using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using Daqifi.Desktop.Device.Firmware;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins <see cref="PostRestartNetworkSettings"/> (issue #642): the WiFi settings after an update come
/// from a fresh status reply that the device sends once it has completed its start, never from
/// Core's metadata, and an absent security field in a full status is an open network
/// (the workaround for daqifi/daqifi-core#986).
/// </summary>
public class PostRestartNetworkSettingsTests : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The access-point SSID the bench Nyquist 1 reported after the 3.9.1 update.</summary>
    private const string AccessPointSsid = "DAQiFi-95AC";

    private readonly Daqifi.Avalonia.Tests.Device.CapturingTransport _transport = new();
    private readonly StatusReplyingCoreDevice _core;

    public PostRestartNetworkSettingsTests()
    {
        _core = new StatusReplyingCoreDevice(_transport);
        _core.Connect();
    }

    public void Dispose()
    {
        _core.StopAnswering();
        _transport.CloseStream();
        _core.Dispose();
        _transport.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The status the bench board sent after the update: a full status, its own access point, open.
    /// Open is 0, and proto3 does not send a 0, so field 60 is absent.
    /// </summary>
    private static DaqifiOutMessage OpenAccessPointStatus(string ssid = AccessPointSsid) => new()
    {
        DeviceSn = 9090539562006014104,
        DevicePn = "Nq1",
        WifiInfMode = (uint)WifiMode.SelfHosted,
        Ssid = ssid,
    };

    #region FromStatus
    [Fact]
    public void An_absent_security_field_in_a_full_status_is_open()
    {
        var settings = PostRestartNetworkSettings.FromStatus(OpenAccessPointStatus());

        Assert.NotNull(settings);
        Assert.Equal(WifiSecurityType.None, settings.SecurityType);
        Assert.Equal(WifiMode.SelfHosted, settings.Mode);
        Assert.Equal(AccessPointSsid, settings.Ssid);
    }

    [Fact]
    public void A_reported_security_mode_is_kept()
    {
        var status = OpenAccessPointStatus();
        status.WifiInfMode = (uint)WifiMode.ExistingNetwork;
        status.WifiSecurityMode = (uint)WifiSecurityType.WpaPskPhrase;
        status.Ssid = "SiteNetwork";

        var settings = PostRestartNetworkSettings.FromStatus(status);

        Assert.Equal(new ReportedNetworkSettings(WifiMode.ExistingNetwork, "SiteNetwork", WifiSecurityType.WpaPskPhrase), settings);
    }

    /// <summary>
    /// A status that does not identify the device is not the full reply to <c>SYSInfoPB?</c>: its
    /// absent fields say nothing, so it gives no settings at all and no "open".
    /// </summary>
    [Fact]
    public void A_status_that_is_not_full_gives_nothing()
    {
        var status = new DaqifiOutMessage { WifiInfMode = (uint)WifiMode.SelfHosted, Ssid = AccessPointSsid };

        Assert.Null(PostRestartNetworkSettings.FromStatus(status));
    }

    /// <summary>A full status with no WiFi section gives nothing: there is no network to call open.</summary>
    [Fact]
    public void A_full_status_without_wifi_settings_gives_nothing()
    {
        var status = new DaqifiOutMessage { DeviceSn = 1, DevicePn = "Nq1" };

        Assert.Null(PostRestartNetworkSettings.FromStatus(status));
    }

    /// <summary>A mode the app does not know is not known; the SSID still counts.</summary>
    [Fact]
    public void An_unknown_mode_is_null()
    {
        var status = OpenAccessPointStatus();
        status.WifiInfMode = 7;

        var settings = PostRestartNetworkSettings.FromStatus(status);

        Assert.NotNull(settings);
        Assert.Null(settings.Mode);
        Assert.Equal(AccessPointSsid, settings.Ssid);
    }
    #endregion

    #region ReadAsync
    /// <summary>
    /// The regression of issue #642 at the reader: Core's metadata kept "WPA" from before the update
    /// and the short SSID of the first status, and the reply says open and the full SSID. The reader
    /// returns the reply's values.
    /// </summary>
    [Fact]
    public async Task Returns_the_settings_in_the_reply_not_the_stored_ones()
    {
        _core.Metadata.WifiSecurityMode = (uint)WifiSecurityType.WpaPskPhrase;
        _core.Metadata.Ssid = "DAQiFi";
        var responder = _core.AnswerNextStatusRequestWith(OpenAccessPointStatus());

        var settings = await PostRestartNetworkSettings.ReadAsync(_core, TimeSpan.Zero, ReplyTimeout);

        await responder;
        Assert.Equal(new ReportedNetworkSettings(WifiMode.SelfHosted, AccessPointSsid, WifiSecurityType.None), settings);
        Assert.Equal(1, _core.RepliesDelivered);
    }

    /// <summary>
    /// The firmware reports the short SSID for its first seconds (firmware#1266), so the reader must
    /// not ask before its settle delay ends. A Cancel during the delay ends it, and nothing was asked.
    /// </summary>
    [Fact]
    public async Task Nothing_is_asked_during_the_settle_delay()
    {
        using var cancel = new CancellationTokenSource();
        _transport.ClearSent();

        var read = PostRestartNetworkSettings.ReadAsync(_core, TimeSpan.FromSeconds(30), ReplyTimeout, cancel.Token);
        var askedEarly = _transport.WaitForSentText(ScpiMessageProducer.GetDeviceInfo.Data, TimeSpan.FromMilliseconds(300));
        await cancel.CancelAsync();

        Assert.False(askedEarly);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(ReplyTimeout));
    }

    [Fact]
    public async Task No_reply_in_time_is_unknown()
    {
        var settings = await PostRestartNetworkSettings.ReadAsync(_core, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));

        Assert.Null(settings);
    }

    /// <summary>A device that is not connected is unknown at once, without the settle delay.</summary>
    [Fact]
    public async Task A_device_that_is_not_connected_is_unknown_without_the_delay()
    {
        using var transport = new Daqifi.Avalonia.Tests.Device.CapturingTransport();
        using var neverConnected = new DaqifiStreamingDevice("idle", transport, NullLogger.Instance);

        var settings = await PostRestartNetworkSettings
            .ReadAsync(neverConnected, TimeSpan.FromSeconds(30), ReplyTimeout)
            .WaitAsync(ReplyTimeout);

        Assert.Null(settings);
    }
    #endregion
}
