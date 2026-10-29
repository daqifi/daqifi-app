using Daqifi.Avalonia.Views;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// The mobile shell's "Or connect by IP" port box must default to the port Core connects on,
/// read from <see cref="DaqifiDeviceFactory.DefaultTcpDataPort"/> rather than re-typed as a
/// literal — the last copy of <c>9760</c> on the mobile path (#429, following #418 on the
/// desktop dialog's <c>MANUAL_WIFI_DATA_PORT</c>).
/// </summary>
/// <remarks>
/// Guards the value, not the spelling: if Core ever changes its default, this test keeps the
/// mobile box agreeing with it instead of silently offering a stale port, and it fails if
/// someone types the literal back in and Core has moved on.
/// </remarks>
public sealed class MobileManualPortDefaultTests
{
    [Fact]
    public void Manual_port_defaults_to_Cores_default_tcp_data_port()
    {
        using var shell = new MobileShellViewModel();

        Assert.Equal(DaqifiDeviceFactory.DefaultTcpDataPort.ToString(), shell.ManualPort);
    }
}
