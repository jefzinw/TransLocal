using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TransLocal;

internal sealed record LocalAddress(IPAddress Address, IPAddress Mask, string InterfaceName, string Kind, bool IsCable);

internal sealed record ReceiverInfo(string Name, IPAddress Address, int Port, string Via, bool IsCable, bool IsThisComputer, bool Busy)
{
    public string Endpoint => $"{Address}:{Port}";
}

internal static class NetInfo
{
    /// <summary>Lista os endereços IPv4 ativos, classificando o tipo de ligação.</summary>
    public static List<LocalAddress> GetLocalAddresses()
    {
        var list = new List<LocalAddress>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return list; }

        foreach (var ni in nics)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }

            var (kind, cable) = Classify(ni);
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = ua.IPv4Mask is { } m && !m.Equals(IPAddress.Any) ? m : IPAddress.Parse("255.255.0.0");
                list.Add(new LocalAddress(ua.Address, mask, ni.Name, kind, cable));
            }
        }
        // Ligações por cabo primeiro.
        return list.OrderByDescending(a => a.IsCable).ThenBy(a => a.InterfaceName).ToList();
    }

    private static (string kind, bool cable) Classify(NetworkInterface ni)
    {
        var d = (ni.Description + " " + ni.Name).ToLowerInvariant();
        if (d.Contains("usb4") || d.Contains("p2p network") || d.Contains("thunderbolt"))
            return ("cabo USB4/Thunderbolt", true);
        if (d.Contains("rndis") || d.Contains("prolific") || d.Contains("pl27a1") || d.Contains("bridge") && d.Contains("usb") || d.Contains("usb link") || d.Contains("easy transfer"))
            return ("cabo USB (bridge)", true);
        if (d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("virtualbox") || d.Contains("vpn") || d.Contains("tap-") || d.Contains("wsl"))
            return ("rede virtual", false);
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || d.Contains("wi-fi") || d.Contains("wireless") || d.Contains("wlan"))
            return ("Wi-Fi", false);
        if (d.Contains("usb"))
            return ("cabo de rede (adaptador USB)", true);
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT)
            return ("cabo de rede", true);
        return ("rede", false);
    }

    public static IPAddress BroadcastOf(IPAddress addr, IPAddress mask)
    {
        var a = addr.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    public static void DisableUdpConnReset(Socket s)
    {
        // Evita que um ICMP "porta inalcançável" derrube o ReceiveAsync no Windows.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { s.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
    }
}

/// <summary>Responde às procuras de transmissores na rede local (porta UDP 52021).</summary>
internal sealed class DiscoveryResponder : IDisposable
{
    private readonly int _port;
    private readonly int _videoPort;
    private readonly Func<string> _name;
    private readonly Func<bool> _busy;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public DiscoveryResponder(int port, int videoPort, Func<string> name, Func<bool> busy)
    {
        _port = port;
        _videoPort = videoPort;
        _name = name;
        _busy = busy;
    }

    public void Start()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        NetInfo.DisableUdpConnReset(udp.Client);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _udp = udp;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(udp, _cts.Token));
    }

    private async Task LoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            try
            {
                if (Encoding.ASCII.GetString(r.Buffer) != Proto.DiscoverRequest) continue;
                var reply = new DiscoveryReply { Name = _name(), Port = _videoPort, Busy = _busy() };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, Proto.Json);
                await udp.SendAsync(bytes, r.RemoteEndPoint, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Falha ao responder descoberta", ex);
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _cts?.Dispose();
    }
}

internal static class DiscoveryScanner
{
    /// <summary>
    /// Envia uma procura por broadcast em cada interface (inclusive cabos com endereço 169.254.x.x)
    /// e junta as respostas.
    /// </summary>
    public static async Task<List<ReceiverInfo>> ScanAsync(int discoveryPort, TimeSpan timeout, bool includeLoopback, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<string, ReceiverInfo>();
        var locals = NetInfo.GetLocalAddresses();
        var localSet = new HashSet<IPAddress>(locals.Select(l => l.Address)) { IPAddress.Loopback };
        var request = Encoding.ASCII.GetBytes(Proto.DiscoverRequest);
        var tasks = new List<Task>();

        foreach (var local in locals)
            tasks.Add(ProbeAsync(local.Address, new[] { NetInfo.BroadcastOf(local.Address, local.Mask), IPAddress.Broadcast }, local.Kind, local.IsCable));
        if (includeLoopback)
            tasks.Add(ProbeAsync(IPAddress.Loopback, new[] { IPAddress.Loopback }, "este computador", false));

        await Task.WhenAll(tasks);

        return found.Values
            .OrderBy(r => r.IsThisComputer)
            .ThenByDescending(r => r.IsCable)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        async Task ProbeAsync(IPAddress bindTo, IPAddress[] targets, string kind, bool cable)
        {
            UdpClient udp;
            try
            {
                udp = new UdpClient(new IPEndPoint(bindTo, 0)) { EnableBroadcast = true };
                NetInfo.DisableUdpConnReset(udp.Client);
            }
            catch (Exception ex)
            {
                Log.Warn($"Descoberta: não foi possível usar {bindTo}", ex);
                return;
            }
            using (udp)
            {
                foreach (var t in targets.Distinct())
                {
                    try { await udp.SendAsync(request, new IPEndPoint(t, discoveryPort)); }
                    catch (Exception ex) { Log.Warn($"Descoberta: falha ao enviar para {t} via {bindTo}", ex); }
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await udp.ReceiveAsync(cts.Token);
                        var reply = JsonSerializer.Deserialize<DiscoveryReply>(r.Buffer, Proto.Json);
                        if (reply is null || reply.App != Proto.AppId) continue;
                        var ip = r.RemoteEndPoint.Address;
                        bool self = localSet.Contains(ip);
                        found[$"{ip}:{reply.Port}"] = new ReceiverInfo(reply.Name, ip, reply.Port, kind, cable, self, reply.Busy);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { }
                    catch (JsonException) { }
                }
            }
        }
    }
}
