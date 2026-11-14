using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Device.Firmware;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins <see cref="PostRestartFirmwareVersion.ReadAsync"/>: the firmware version comes from a fresh
/// status reply, never from <c>Metadata.FirmwareVersion</c>, and "not known" is <c>null</c>, never a
/// guess (review of PR #574, issue #566).
/// </summary>
public class PostRestartFirmwareVersionTests : IDisposable
{
    /// <summary>Long enough for a reply that is coming; the no-reply case sets its own short wait.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    private readonly Daqifi.Avalonia.Tests.Device.CapturingTransport _transport = new();
    private readonly StatusReplyingCoreDevice _core;

    public PostRestartFirmwareVersionTests()
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

    [Fact]
    public async Task Returns_the_version_in_the_reply_not_the_stored_one()
    {
        _core.Metadata.FirmwareVersion = "3.8.0";
        var responder = _core.AnswerNextStatusRequestWith(new DaqifiOutMessage { DeviceFwRev = "3.9.0" });

        var version = await PostRestartFirmwareVersion.ReadAsync(_core, ReplyTimeout);

        await responder;
        Assert.Equal("3.9.0", version);
        Assert.Equal(1, _core.RepliesDelivered);
    }

    [Fact]
    public async Task Trims_the_reported_version()
    {
        var responder = _core.AnswerNextStatusRequestWith(new DaqifiOutMessage { DeviceFwRev = " 3.9.0 " });

        var version = await PostRestartFirmwareVersion.ReadAsync(_core, ReplyTimeout);

        await responder;
        Assert.Equal("3.9.0", version);
    }

    /// <summary>
    /// The case in the review: a reply without a version leaves Core's stored value in place, and
    /// that value can be the one from before the write. The reader must say "unknown", not repeat it.
    /// </summary>
    [Fact]
    public async Task A_reply_without_a_version_is_unknown_even_when_metadata_has_one()
    {
        _core.Metadata.FirmwareVersion = "3.8.0";
        var responder = _core.AnswerNextStatusRequestWith(new DaqifiOutMessage { DevicePn = "Nq1" });

        var version = await PostRestartFirmwareVersion.ReadAsync(_core, ReplyTimeout);

        await responder;
        Assert.Null(version);
        Assert.Equal("3.8.0", _core.Metadata.FirmwareVersion);
    }

    [Fact]
    public async Task No_reply_in_time_is_unknown()
    {
        _core.Metadata.FirmwareVersion = "3.9.0";

        var version = await PostRestartFirmwareVersion.ReadAsync(_core, TimeSpan.FromMilliseconds(200));

        Assert.Null(version);
    }

    [Fact]
    public async Task A_device_that_is_not_connected_is_unknown()
    {
        using var transport = new Daqifi.Avalonia.Tests.Device.CapturingTransport();
        using var neverConnected = new DaqifiStreamingDevice("idle", transport, NullLogger.Instance);
        neverConnected.Metadata.FirmwareVersion = "3.9.0";

        var version = await PostRestartFirmwareVersion.ReadAsync(neverConnected, ReplyTimeout);

        Assert.Null(version);
    }

    /// <summary>A cancellation is not a timeout: it propagates, so Cancel ends the firmware run.</summary>
    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cancel = new CancellationTokenSource();

        var read = PostRestartFirmwareVersion.ReadAsync(_core, TimeSpan.FromSeconds(30), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(ReplyTimeout));
    }
}
