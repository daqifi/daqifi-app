// Downstream-only: upstream has no self-update and no UPDATES section (#346).

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Common;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.UpdateVersion;

namespace Daqifi.Desktop.ViewModels;

/// <summary>
/// The UPDATES section of the settings drawer (#346): the version of this copy, the state of the
/// update, and a button that checks now. It also runs the automatic checks.
/// </summary>
/// <remarks>
/// The state of an update has one source: the notification that <see cref="AppUpdateNotifier"/>
/// shows (#503). This view model keeps that notification as <see cref="Notice"/> and gives the same
/// object to the bell, so the drawer and the bell always show the same text and the same button.
/// The view model adds only what the notifier does not keep: whether a check runs now, when the last
/// check ended, and whether it failed.
/// </remarks>
public sealed partial class AppUpdateViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long after start-up the first check waits, so that it does not run at the same time as
    /// device discovery.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>How often the app checks again while it stays open and no update is offered.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IAppUpdateService _service;
    private readonly AppUpdateNotifier _notifier;
    private readonly Action<Notifications?> _showInBell;
    private readonly Action<Action> _post;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _watching;

    /// <param name="service">The update service for this head.</param>
    /// <param name="blocker">What, if anything, installing would interrupt now.</param>
    /// <param name="confirm">Asks a yes/no question (message, title); true means go ahead.</param>
    /// <param name="showInBell">Puts the update's notification in the bell in place of the previous one; <c>null</c> removes it. Called on the UI thread.</param>
    /// <param name="logger">Where failures are recorded.</param>
    /// <param name="attempts">Where the version being installed is remembered across the restart.</param>
    /// <param name="post">Runs an action on the UI thread. The notifier reports download progress from another thread. Runs the action at once when not given.</param>
    /// <param name="now">The local time; <see cref="DateTime.Now"/> unless a test supplies one.</param>
    /// <param name="delay">Waits between checks; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test supplies one.</param>
    public AppUpdateViewModel(
        IAppUpdateService service,
        Func<UpdateBlocker> blocker,
        Func<string, string, Task<bool>> confirm,
        Action<Notifications?> showInBell,
        IAppLogger logger,
        UpdateAttemptMarker? attempts = null,
        Action<Action>? post = null,
        Func<DateTime>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _service = service;
        _showInBell = showInBell;
        _post = post ?? (action => action());
        _now = now ?? (() => DateTime.Now);
        _delay = delay ?? Task.Delay;
        _notifier = new AppUpdateNotifier(service, blocker, confirm, ShowNotice, logger, _delay, attempts);
    }

    /// <summary>
    /// The version of this copy, for example <c>3.3.0</c>. A Debug build adds the short commit,
    /// as the mobile shell does. A Release build does not: the commit is in <see cref="BuildVersion"/>,
    /// and the captured screens of the settings drawer stay the same from one commit to the next.
    /// </summary>
#if DEBUG
    public string Version { get; } = FormatVersion(AppVersion.Semantic, AppVersion.ShortBuildMetadata);
#else
    public string Version { get; } = FormatVersion(AppVersion.Semantic, null);
#endif

    /// <summary>The full version with the commit it was built from, shown as the tooltip of <see cref="Version"/>.</summary>
    public string BuildVersion { get; } = AppVersion.Informational ?? "dev";

    /// <summary>
    /// True when the section shows an update state. False only on a platform that does not update
    /// itself (Android, iOS), where the section shows the version and nothing else.
    /// </summary>
    public bool HasStatus => _service.Support != AppUpdateSupport.PlatformUnsupported;

    /// <summary>The update's notification, the same object the bell shows, or <c>null</c> when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(ActionText), nameof(ActionCommand), nameof(HasAction), nameof(CanCheckNow))]
    private Notifications? _notice;

    /// <summary>True while a check runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isChecking;

    /// <summary>When the last check that got an answer ended, or <c>null</c> before that.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private DateTime? _lastChecked;

    /// <summary>True when the last check did not get an answer, for example with no network.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _lastCheckFailed;

    /// <summary>One line that tells the state of updates for this copy.</summary>
    public string Status => _service.Support switch
    {
        AppUpdateSupport.Supported => Notice is { } notice ? notice.Message
            : IsChecking ? "Checking for updates..."
            : LastCheckFailed ? "Couldn't check for updates."
            : LastChecked is { } at ? $"Up to date. Checked at {at:t}."
            : "Not checked yet.",
        AppUpdateSupport.NotInstalled => "This copy was not installed with the DAQiFi installer, so it does not update itself.",
        AppUpdateSupport.OwnedExternally => "This copy gets its updates from the store it came from.",
        AppUpdateSupport.DisabledByPolicy => "An administrator turned off updates on this PC.",
        _ => string.Empty,
    };

    /// <summary>The label of the notification's button: install, try again or cancel. <c>null</c> when it has none.</summary>
    public string? ActionText => Notice?.ActionText;

    /// <summary>What the notification's button runs.</summary>
    public ICommand? ActionCommand => Notice?.Action;

    /// <summary>True when the notification has a button.</summary>
    public bool HasAction => ActionText is not null;

    /// <summary>
    /// True when "Check now" is shown: this copy can update and no update is offered yet. Once an
    /// update is offered, its own button takes this place, and a new check would only replace it.
    /// </summary>
    public bool CanCheckNow => _service.Support == AppUpdateSupport.Supported && Notice is null;

    /// <summary>
    /// Checks shortly after start-up (<see cref="StartupDelay"/>), then each <see cref="CheckInterval"/>
    /// until an update is offered. Returns at once when this copy cannot update. Never throws.
    /// </summary>
    public async Task RunAsync()
    {
        if (_service.Support != AppUpdateSupport.Supported) { return; }

        var token = _lifetime.Token;
        try
        {
            await _delay(StartupDelay, token);
            while (Notice is null)
            {
                await CheckAsync();
                await _delay(CheckInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private Task CheckNowAsync() => CheckAsync();

    private bool CanRunCheck() => !IsChecking;

    /// <summary>
    /// Runs one check. When it finds an update, the notifier shows it, and its watch starts, so that
    /// the button follows what installing would cut off (#348). A failed check is only a line in this
    /// section and in the log: no network is normal for a bench PC.
    /// </summary>
    private async Task CheckAsync()
    {
        if (IsChecking || !CanCheckNow) { return; }

        IsChecking = true;
        Notifications? offered = null;
        try
        {
            offered = await _notifier.CheckAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            // Through the same queue as the notification, so that the offer is in Notice before
            // the end of the check is shown, and "Up to date" never shows for a moment first.
            var failed = _notifier.LastCheckFailed;
            var endedAt = _now();
            _post(() =>
            {
                LastCheckFailed = failed;
                if (!failed) { LastChecked = endedAt; }
                IsChecking = false;
            });
        }

        if (offered is not null && !_watching)
        {
            _watching = true;
            _ = _notifier.WatchAsync(_lifetime.Token);
        }
    }

    // The notifier calls this from any thread; the drawer and the bell are changed on the UI thread.
    private void ShowNotice(Notifications? notice) => _post(() =>
    {
        Notice = notice;
        _showInBell(notice);
    });

    /// <summary>The version as this section shows it: <c>3.3.0</c>, or <c>3.3.0 (abc1234)</c> with a commit.</summary>
    internal static string FormatVersion(string? semantic, string? shortCommit)
    {
        var version = semantic ?? "dev";
        return shortCommit is null ? version : $"{version} ({shortCommit})";
    }

    /// <summary>Stops the automatic checks and the watch of an offered update.</summary>
    public void Dispose() => _lifetime.Cancel();
}
