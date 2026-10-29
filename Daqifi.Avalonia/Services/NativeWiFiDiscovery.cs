using System.Net;
using System.Net.Sockets;
using Daqifi.Core.Device;

namespace Daqifi.Avalonia.Services;

/// <summary>A DAQiFi device found by <see cref="NativeWiFiDiscovery"/>.</summary>
public sealed record DiscoveredDevice(IPAddress Ip, int Port, string? Name);

/// <summary>
/// Self-contained UDP broadcast discovery for the mobile heads, because Core's
/// <c>WiFiDeviceFinder</c> enumerates no usable interface on Android.
/// </summary>
/// <remarks>
/// This comment used to say Core's finder sends a limited <c>255.255.255.255</c>
/// broadcast that Android drops. That was never true of any Core version this repo
/// has pinned — Core has computed a subnet-directed broadcast per NIC since v0.21.0,
/// three minor versions before the workaround landed (#429).
/// <para>
/// The real reason, measured on a Galaxy A16 5G (Android 15 / API 35, .NET 10) against
/// a Nyquist 1 on the same /24 on 2026-10-29: Core requires each interface to be
/// <c>Ethernet</c> or <c>Wireless80211</c>, and Android's <c>wlan0</c> reports
/// <c>NetworkInterfaceType.Unknown</c> — so it is filtered out and discovery finds
/// nothing. Everything else about the interface passes, <c>IPv4Mask</c> included
/// (<c>255.255.255.0</c>, not the missing mask the <c>/24</c> fallback below guards
/// against). Logged on the phone:
/// <code>
/// NIC429 name=wlan0 desc=wlan0 type=Unknown status=Up ipv4=True virtual=False filterPass=False
/// NIC429   addr=192.168.1.145 mask=255.255.255.0
/// </code>
/// Two controls ran on the same phone in the same scan: Core's own
/// <c>WiFiDeviceFinder.ProbeTcpDataPortAsync</c> unicast the query to the board and
/// read back port 9760, and Core's sweep replicated WITHOUT the type filter — same
/// per-NIC bind to <c>(wlan0 address, 30303)</c>, same subnet-directed broadcast —
/// got <c>hostName=DAQiFi-95AC sn=… fw=3.8.0 pn=Nq1 devicePort=9760</c>. So Core's
/// wire format, socket strategy and protobuf parse all work on Android; the interface
/// filter alone is what excludes the phone's WiFi. Filed upstream as daqifi-core#846;
/// when that lands, this file is replaced by Core's finder, which also returns the
/// real host name, serial number, firmware revision and ADVERTISED data port rather
/// than the placeholder name and assumed port below.
/// </para>
/// <para>
/// What this does: send the DAQiFi finder queries to the subnet-directed broadcast
/// (e.g. 192.168.1.255, computed from the phone's own WiFi address) plus limited
/// broadcast as a fallback, then take each responder's IP straight from the reply
/// packet's source address (no protobuf parse; the SN + firmware come back on
/// connect). The caller holds the WiFi MulticastLock
/// (<see cref="NetworkDiscoveryScope"/>) for the sweep — that half of the original
/// workaround is load-bearing and independent of which finder runs: Android's
/// power-save filter drops inbound broadcast without it.
/// </para>
/// </remarks>
public static class NativeWiFiDiscovery
{
    private const int DiscoveryPort = 30303;
    // The port Core connects on by default, not a local literal — this path cannot read the
    // device's advertised DevicePort without parsing the reply, so it assumes the default.
    private const int DefaultDataPort = DaqifiDeviceFactory.DefaultTcpDataPort;
    private static readonly byte[] DaqifiQuery = "DAQiFi?\r\n"u8.ToArray();
    private static readonly byte[] NativeQuery = "Discovery: Who is out there?\r\n"u8.ToArray();

    /// <summary>
    /// Broadcast, collect responders until <paramref name="window"/>
    /// elapses, and report each unique device via <paramref name="onFound"/>
    /// (invoked on a background thread — the caller marshals to the UI).
    /// </summary>
    public static async Task DiscoverAsync(
        TimeSpan window, Action<DiscoveredDevice> onFound, CancellationToken token)
    {
        using var sock = new Socket(AddressFamily.InterNetwork,
            SocketType.Dgram, ProtocolType.Udp);
        sock.EnableBroadcast = true;
        sock.SetSocketOption(SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress, true);
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));

        foreach (var target in BroadcastTargets())
        {
            var ep = new IPEndPoint(target, DiscoveryPort);
            try
            {
                await sock.SendToAsync(DaqifiQuery, SocketFlags.None, ep, token);
                await sock.SendToAsync(NativeQuery, SocketFlags.None, ep, token);
            }
            catch (SocketException) { /* one target unreachable — try the rest */ }
        }

        var seen = new HashSet<string>();
        var buffer = new byte[2048];
        var deadline = DateTime.UtcNow + window;
        while (!token.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) { break; }
            var from = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult result;
            try
            {
                using var timeoutCts = CancellationTokenSource
                    .CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(remaining);
                result = await sock.ReceiveFromAsync(
                    buffer, SocketFlags.None, from, timeoutCts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            if (result.ReceivedBytes <= 0) { continue; }
            var ip = ((IPEndPoint)result.RemoteEndPoint).Address;
            if (!seen.Add(ip.ToString())) { continue; }
            onFound(new DiscoveredDevice(ip, DefaultDataPort, $"DAQiFi @ {ip}"));
        }
    }

    /// <summary>
    /// Subnet-directed broadcast(s) for every up IPv4 interface, plus the
    /// limited-broadcast fallback. Subnet-directed is what Android actually
    /// delivers; limited broadcast covers hosts that only answer it.
    /// </summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress>();
        foreach (var (addr, mask) in LocalIPv4Interfaces())
        {
            var a = addr.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (var i = 0; i < 4; i++) { b[i] = (byte)(a[i] | (~m[i] & 0xFF)); }
            var bcast = new IPAddress(b);
            if (!targets.Contains(bcast)) { targets.Add(bcast); }
        }
        targets.Add(IPAddress.Broadcast);   // 255.255.255.255 fallback
        return targets;
    }

    private static IEnumerable<(IPAddress Addr, IPAddress Mask)> LocalIPv4Interfaces()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface
                     .GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation
                    .OperationalStatus.Up)
            {
                continue;
            }
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) { continue; }
                if (IPAddress.IsLoopback(ua.Address)) { continue; }
                var mask = ua.IPv4Mask;
                if (mask == null || Equals(mask, IPAddress.Any))
                {
                    // Kept for the platforms this has NOT been measured on — Android 15's
                    // wlan0 does report 255.255.255.0 (#429), and iOS WiFi discovery has
                    // never been validated on a real device (#104).
                    mask = IPAddress.Parse("255.255.255.0"); // sane /24 default
                }
                yield return (ua.Address, mask);
            }
        }
    }
}
