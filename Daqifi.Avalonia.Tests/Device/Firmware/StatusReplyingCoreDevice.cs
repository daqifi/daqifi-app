using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// A real, connected <see cref="DaqifiStreamingDevice"/> that answers a status request
/// (<c>SYSTem:SYSInfoPB?</c>) with a status reply the test chooses, through Core's own status
/// handler.
/// </summary>
/// <remarks>
/// <para>
/// Core routes inbound protobuf to <see cref="DaqifiDevice.OnStatusMessageReceived"/> only after
/// <c>InitializeAsync</c> has set up its protocol handler, and the real initialization is a SCPI
/// conversation with a board. So the reply is delivered by calling that protected handler directly,
/// as the consumer thread would. Everything after that is Core's: the metadata update, the waiter
/// <c>RefreshDeviceStatusAsync</c> registered, and the <c>StatusMessageReceived</c> event.
/// </para>
/// <para>
/// The reply is sent only when the request is on the wire, so a caller that never asks never gets
/// one — which is what lets a test tell "read a fresh reply" from "read the stored metadata".
/// </para>
/// </remarks>
internal sealed class StatusReplyingCoreDevice : DaqifiStreamingDevice
{
    private static readonly string StatusRequest = ScpiMessageProducer.GetDeviceInfo.Data;

    private readonly Daqifi.Avalonia.Tests.Device.CapturingTransport _transport;
    private readonly CancellationTokenSource _stop = new();

    public StatusReplyingCoreDevice(Daqifi.Avalonia.Tests.Device.CapturingTransport transport)
        : base("core", transport, NullLogger.Instance)
    {
        _transport = transport;
    }

    /// <summary>How many replies were delivered.</summary>
    public int RepliesDelivered { get; private set; }

    /// <summary>
    /// Waits, on a background task, for the next status request to reach the wire, then delivers
    /// <paramref name="reply"/>. Returns the task so a test can observe that it ran.
    /// </summary>
    public Task AnswerNextStatusRequestWith(DaqifiOutMessage reply)
    {
        _transport.ClearSent();
        return Task.Run(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_transport.WaitForSentText(StatusRequest, TimeSpan.FromMilliseconds(50)))
                {
                    RepliesDelivered++;
                    OnStatusMessageReceived(reply);
                    return;
                }
            }
        });
    }

    /// <summary>Stops a responder that is still waiting, so it cannot outlive the test.</summary>
    public void StopAnswering() => _stop.Cancel();
}
