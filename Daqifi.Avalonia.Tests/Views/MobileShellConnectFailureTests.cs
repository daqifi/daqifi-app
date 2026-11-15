using System.Net;
using System.Net.Sockets;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Transport;
using Xunit;
using DaqifiStreamingDevice = Daqifi.Desktop.Device.WiFiDevice.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Issue #593: a failed connect on the phone said "see device log". The phone has no screen that
/// shows the app log, and DEVICE LOGS lists the SD card, so the message sent the user to nothing
/// and dropped the reason. The status must give the reason instead.
/// </summary>
/// <remarks>
/// The end-to-end test connects to a closed port on the loopback address, which refuses at once
/// on every platform. It reaches no device and sends nothing.
/// </remarks>
public sealed class MobileShellConnectFailureTests : IDisposable
{
    private readonly MobileShellViewModel _shell = new();

    public void Dispose() => _shell.Dispose();

    [Fact]
    public async Task A_refused_manual_connect_says_refused_and_not_see_device_log()
    {
        var port = ClosedLoopbackPort();
        _shell.ManualIp = "127.0.0.1";
        _shell.ManualPort = port.ToString();

        await _shell.ManualConnectCommand.ExecuteAsync(null);

        Assert.False(_shell.IsConnected);
        Assert.Equal(
            $"Could not connect to 127.0.0.1:{port} — connection refused. " +
            "Make sure that the phone and the device are on the same WiFi network.",
            _shell.Status);
        Assert.DoesNotContain("log", _shell.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The device keeps the reason that it used to send only to the log.</summary>
    [Fact]
    public void A_failed_connect_keeps_its_reason_on_the_device()
    {
        var device = new DaqifiStreamingDevice(IPAddress.Loopback, ClosedLoopbackPort(), "Refusing");

        Assert.False(device.Connect());

        var socket = Assert.IsType<SocketException>(device.LastConnectError, exactMatch: false);
        Assert.Equal(SocketError.ConnectionRefused, socket.SocketErrorCode);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, "connection refused")]
    [InlineData(SocketError.TimedOut, "no reply (timeout)")]
    [InlineData(SocketError.HostUnreachable, "the address cannot be reached")]
    [InlineData(SocketError.HostDown, "the address cannot be reached")]
    [InlineData(SocketError.NetworkUnreachable, "the network cannot be reached")]
    [InlineData(SocketError.AddressNotAvailable, "network error (AddressNotAvailable)")]
    public void A_socket_error_is_described_by_its_code(SocketError code, string expected)
    {
        Assert.Equal(expected, MobileShellViewModel.DescribeConnectFailure(new SocketException((int)code)));
    }

    /// <summary>Core reports an exhausted connect timeout as a <see cref="TimeoutException"/>.</summary>
    [Fact]
    public void A_connect_timeout_is_described_as_no_reply()
    {
        Assert.Equal(
            "no reply (timeout)",
            MobileShellViewModel.DescribeConnectFailure(new TimeoutException("TCP connect timed out after 5s.")));
    }

    [Fact]
    public void A_wrapped_socket_error_is_still_found()
    {
        var wrapped = new IOException("outer", new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal("connection refused", MobileShellViewModel.DescribeConnectFailure(wrapped));
    }

    [Fact]
    public void A_link_that_drops_during_setup_is_described_as_such()
    {
        Assert.Equal(
            "the connection closed during setup",
            MobileShellViewModel.DescribeConnectFailure(new TransportNotConnectedException("gone")));
    }

    [Fact]
    public void An_unknown_error_falls_back_to_its_message_and_no_error_to_a_plain_reason()
    {
        Assert.Equal("Something odd", MobileShellViewModel.DescribeConnectFailure(new InvalidOperationException("Something odd")));
        Assert.Equal("no connection was made", MobileShellViewModel.DescribeConnectFailure(null));
    }

    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
