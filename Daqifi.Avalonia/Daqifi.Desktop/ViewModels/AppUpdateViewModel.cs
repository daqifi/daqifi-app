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
/// shows (#503). This view model keeps that notification as <see cref="Notice"/>, and the drawer
/// shows its text and its button. The bell shows the same text, and its button opens this section
/// (#346 review), so a download or a restart is always started from here. The view model adds only
/// what the notifier does not keep: whether a check runs now, when the last check ended, and
/// whether it failed.
/// </remarks>
public sealed partial class AppUpdateViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long after start-up the first check waits, so that it does not run at the same time as
    /// device discovery.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>How often the app checks again while it stays open, including while an update is offered.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>The label of the bell entry's button, which opens this section.</summary>
    public const string OpenSectionText = "Open in Settings";

    private readonly IAppUpdateService _service;
    private readonly AppUpdateNotifier _notifier;
    private readonly Action<Notifications?> _showInBell;
    private readonly Action _openSection;
    private readonly Action<Action> _post;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _watching;

    /// <param name="service">The update service for this head.</param>
    /// <param name="blocker">What, if anything, installing would interrupt now.</param>
    /// <param name="confirm">Asks a yes/no question (message, title); true means go ahead.</param>
    /// <param name="showInBell">Puts the update's entry in the bell in place of the previous one; <c>null</c> removes it. Called on the UI thread.</param>
    /// <param name="openSection">Opens the settings drawer, where this section is. The bell entry's button calls it.</param>
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
        Action openSection,
        IAppLogger logger,
        UpdateAttemptMarker? attempts = null,
        Action<Action>? post = null,
        Func<DateTime>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _service = service;
        _showInBell = showInBell;
        _openSection = openSection;
        _post = post ?? (action => action());
        _now = now ?? (() => DateTime.Now);
        _delay = delay ?? Task.Delay;
        _notifier = new AppUpdateNotifier(service, blocker, confirm, ShowNotice, logger, _delay, attempts);
        // Concurrent runs allowed: the download's own Cancel goes through this same button while
        // the download it started is still running.
        _action = new AsyncRelayCommand(RunNoticeActionAsync, () => !IsChecking, AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    /// <summary>
    /// The version of this copy with the short commit it was built from, for example
    /// <c>3.3.0 (9f1c2ab)</c>, in every build (#346 review): a screenshot of the drawer then tells
    /// two builds of one version apart. The capture harness builds with a fixed commit
    /// (<c>tools/parity-audit/run.sh</c>), so its screens do not change from one commit to the next.
    /// </summary>
    public string Version { get; } = FormatVersion(AppVersion.Semantic, AppVersion.ShortBuildMetadata);

    /// <summary>The full version with the commit it was built from, shown as the tooltip of <see cref="Version"/>.</summary>
    public string BuildVersion { get; } = AppVersion.Informational ?? "dev";

    /// <summary>
    /// True when the section shows an update state. False only on a platform that does not update
    /// itself (Android, iOS), where the section shows the version and nothing else.
    /// </summary>
    public bool HasStatus => _service.Support != AppUpdateSupport.PlatformUnsupported;

    /// <summary>The update's notification as the notifier shows it, or <c>null</c> when there is none.</summary>
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

    // The notification that was shown when the last check failed, or null. While it is still the one
    // shown, the line says that the check failed (#346 review, round 2); once the update moves on,
    // for example to a download, the failure no longer describes what is on screen.
    private Notifications? _noticeAtFailedCheck;

    /// <summary>One line that tells the state of updates for this copy.</summary>
    public string Status => _service.Support switch
    {
        AppUpdateSupport.Supported => Notice is { } notice
                ? ReferenceEquals(notice, _noticeAtFailedCheck) ? $"Couldn't check for updates. {notice.Message}" : notice.Message
            : IsChecking ? "Checking for updates..."
            : LastCheckFailed ? "Couldn't check for updates."
            : LastChecked is { } at ? $"Up to date. Checked at {at:t}."
            : "Not checked yet.",
        AppUpdateSupport.NotInstalled => "This copy was not installed with the DAQiFi installer, so it does not update itself.",
        AppUpdateSupport.OwnedExternally => "This copy gets its updates from the store it came from.",
        AppUpdateSupport.DisabledByPolicy => "An administrator turned off updates on this PC.",
        _ => string.Empty,
    };

    /// <summary>The label of the notification's button: download, restart, try again or cancel. <c>null</c> when it has none.</summary>
    public string? ActionText => Notice?.ActionText;

    /// <summary>
    /// The drawer's button for the notification: it runs the notification's own command. It is
    /// disabled while a check runs, and a click that comes then does nothing (#346 review, round 2).
    /// The check can replace the offer with a newer version, so a download or a restart started
    /// during it could finish for a version that is no longer offered.
    /// </summary>
    public ICommand? ActionCommand => Notice?.Action is null ? null : _action;

    private readonly AsyncRelayCommand _action;

    private Task RunNoticeActionAsync()
    {
        if (IsChecking || Notice?.Action is not { } action) { return Task.CompletedTask; }
        if (action is IAsyncRelayCommand asyncAction) { return asyncAction.ExecuteAsync(null); }
        action.Execute(null);
        return Task.CompletedTask;
    }

    partial void OnIsCheckingChanged(bool value) => _action.NotifyCanExecuteChanged();

    /// <summary>True when the notification has a button.</summary>
    public bool HasAction => ActionText is not null;

    /// <summary>
    /// True when "Check now" is shown: this copy can update, and nothing or an offer waiting for the
    /// user is shown (#346 review). A check then replaces an offer with a newer version, or takes it
    /// down when the feed no longer has it. False while a download runs, which a check would replace.
    /// </summary>
    public bool CanCheckNow => _service.Support == AppUpdateSupport.Supported && (Notice is null || _notifier.HasOffer);

    /// <summary>
    /// Checks shortly after start-up (<see cref="StartupDelay"/>), then each <see cref="CheckInterval"/>
    /// for as long as the app runs, so that an app left open across a release hears of it. A timed
    /// check is skipped while a download runs. Returns at once when this copy cannot update. Never throws.
    /// </summary>
    public async Task RunAsync()
    {
        if (_service.Support != AppUpdateSupport.Supported) { return; }

        var token = _lifetime.Token;
        try
        {
            await _delay(StartupDelay, token);
            while (true)
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
    /// Runs one check, unless one runs already or a download is under way. When it finds an update,
    /// the notifier shows it, and its watch starts, so that "Restart to update" follows what
    /// installing would cut off (#348). A failed check is only a line in this section and in the
    /// log: no network is normal for a bench PC.
    /// </summary>
    private async Task CheckAsync()
    {
        if (IsChecking || !CanCheckNow) { return; }

        IsChecking = true;
        Notifications? offered = null;
        var answered = false;
        try
        {
            offered = await _notifier.CheckAsync(_lifetime.Token);
            answered = !_notifier.LastCheckFailed;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The app is closing. The check has no answer, and nothing more is shown.
        }
        finally
        {
            // Through the same queue as the notification, so that the offer is in Notice before
            // the end of the check is shown, and "Up to date" never shows for a moment first.
            var failed = !answered;
            var endedAt = _now();
            _post(() =>
            {
                // A failed check leaves an offer on screen as it was, so the line has to say that
                // the feed was not reached; see Status. Set before IsChecking, whose change (always
                // true to false here) tells the view to read Status again.
                _noticeAtFailedCheck = failed ? Notice : null;
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
        _showInBell(notice is null ? null : BellEntry(notice));
    });

    /// <summary>
    /// The bell's copy of <paramref name="notice"/>: the same text, with a button that opens this
    /// section instead of the notice's own button (#346 review). It starts no download or restart.
    /// </summary>
    private Notifications BellEntry(Notifications notice) => new()
    {
        Message = notice.Message,
        ActionText = OpenSectionText,
        Action = new RelayCommand(_openSection),
    };

    /// <summary>The version as this section shows it: <c>3.3.0</c>, or <c>3.3.0 (abc1234)</c> with a commit.</summary>
    internal static string FormatVersion(string? semantic, string? shortCommit)
    {
        var version = semantic ?? "dev";
        return shortCommit is null ? version : $"{version} ({shortCommit})";
    }

    /// <summary>Stops the automatic checks and the watch of an offered update.</summary>
    public void Dispose() => _lifetime.Cancel();
}
