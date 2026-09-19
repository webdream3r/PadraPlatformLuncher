using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Padra.Core.Networking;

/// <summary>Host name / address helpers shared by the gateway and the panel.</summary>
public static class NetworkHosts
{
    public static readonly IReadOnlySet<string> Loopback =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "::1", "localhost" };

    public static bool IsWildcard(string host) =>
        host is "0.0.0.0" or "::" or "*";

    public static bool IsLoopback(string host) => Loopback.Contains(host);

    /// <summary>Wrap IPv6 literals in brackets so they can go into a URL.</summary>
    public static string ForUrl(string host)
    {
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            return $"[{host}]";
        }

        return host;
    }
}

public sealed record LocalAddress(string Address, string Family, bool Primary);

public sealed record EndpointUrl(string Address, string Family, string Url, bool Primary);

public sealed record EndpointMap(
    bool Shared,
    string Host,
    int Port,
    string LocalUrl,
    IReadOnlyList<EndpointUrl> Lan);

/// <summary>LAN reachability: which addresses this host answers on and the URLs to hand out.</summary>
public static class NetworkInfo
{
    public static string HostName => Dns.GetHostName();

    /// <summary>The address the OS would use to reach the LAN. No packets are sent.</summary>
    public static string? PrimaryAddress()
    {
        foreach (var (family, probe) in new[]
                 {
                     (AddressFamily.InterNetwork, "192.0.2.1"),
                     (AddressFamily.InterNetworkV6, "2001:db8::1"),
                 })
        {
            try
            {
                using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(IPAddress.Parse(probe), 9));
                if (socket.LocalEndPoint is IPEndPoint local && IsUsable(local.Address))
                {
                    return local.Address.ToString();
                }
            }
            catch (SocketException)
            {
                // No route on this family; try the next one.
            }
            catch (PlatformNotSupportedException)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>Every non-loopback address of this host, primary interface first.</summary>
    public static IReadOnlyList<LocalAddress> LocalAddresses()
    {
        var found = new Dictionary<string, LocalAddress>(StringComparer.OrdinalIgnoreCase);
        var primary = PrimaryAddress();
        if (primary is not null)
        {
            found[primary] = new LocalAddress(primary, FamilyOf(primary), true);
        }

        foreach (var nic in SafeInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                var address = info.Address;
                if (!IsUsable(address))
                {
                    continue;
                }

                var text = address.ToString().Split('%')[0];
                if (!found.ContainsKey(text))
                {
                    found[text] = new LocalAddress(text, FamilyOf(text), false);
                }
            }
        }

        return found.Values
            .OrderBy(a => a.Primary ? 0 : 1)
            .ThenBy(a => a.Family == "ipv4" ? 0 : 1)
            .ThenBy(a => a.Address, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Local and LAN URLs for a gateway bound to <paramref name="host"/>.</summary>
    public static EndpointMap EndpointUrls(string host, int port, string path = "/v1")
    {
        var shared = NetworkHosts.IsWildcard(host);
        var local = $"http://127.0.0.1:{port}{path}";
        var lan = new List<EndpointUrl>();

        if (shared)
        {
            foreach (var address in LocalAddresses())
            {
                lan.Add(new EndpointUrl(
                    address.Address,
                    address.Family,
                    $"http://{NetworkHosts.ForUrl(address.Address)}:{port}{path}",
                    address.Primary));
            }

            var name = HostName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                lan.Add(new EndpointUrl(name, "hostname", $"http://{name}:{port}{path}", false));
            }
        }
        else if (!NetworkHosts.IsLoopback(host))
        {
            lan.Add(new EndpointUrl(host, FamilyOf(host),
                $"http://{NetworkHosts.ForUrl(host)}:{port}{path}", true));
        }

        return new EndpointMap(shared, host, port, local, lan);
    }

    /// <summary>Best URL to hand to another machine; falls back to the loopback URL.</summary>
    public static string PreferredLanUrl(string host, int port, string path = "/v1")
    {
        var map = EndpointUrls(host, port, path);
        var best = map.Lan.FirstOrDefault(e => e.Primary && e.Family == "ipv4")
                   ?? map.Lan.FirstOrDefault(e => e.Family == "ipv4")
                   ?? map.Lan.FirstOrDefault();
        return best?.Url ?? map.LocalUrl;
    }

    /// <summary>True when nothing is already listening on <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static bool PortIsFree(string host, int port)
    {
        var address = NetworkHosts.IsWildcard(host) ? IPAddress.Any : ParseOrAny(host);
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static IPAddress ParseOrAny(string host) =>
        IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Any;

    private static string FamilyOf(string address) =>
        address.Contains(':', StringComparison.Ordinal) ? "ipv6" : "ipv4";

    private static bool IsUsable(IPAddress address) =>
        !IPAddress.IsLoopback(address) &&
        !address.IsIPv6LinkLocal &&
        !address.IsIPv6Multicast &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(IPAddress.IPv6Any) &&
        !IsIPv4LinkLocal(address);

    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NetworkInterface>();
        }
    }
}

public sealed record FirewallResult(bool Ok, bool Elevated, string Command, string Output);

/// <summary>Opening the gateway port for other machines on the network.</summary>
public static class FirewallHelper
{
    public const string RuleName = "Padra Platform Launcher";

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>The command that opens <paramref name="port"/> on this platform.</summary>
    public static string Command(int port)
    {
        if (IsWindows)
        {
            return $"netsh advfirewall firewall add rule name=\"{RuleName}\" " +
                   $"dir=in action=allow protocol=TCP localport={port} profile=private";
        }

        if (File.Exists("/usr/sbin/ufw") || File.Exists("/usr/bin/ufw"))
        {
            return $"sudo ufw allow {port}/tcp";
        }

        return $"sudo firewall-cmd --add-port={port}/tcp --permanent && sudo firewall-cmd --reload";
    }

    /// <summary>Add the inbound rule. On Windows this raises the UAC prompt.</summary>
    public static async Task<FirewallResult> OpenAsync(int port, CancellationToken token = default)
    {
        var command = Command(port);
        if (!IsWindows)
        {
            return new FirewallResult(false, false, command, string.Empty);
        }

        var arguments =
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
            $"protocol=TCP localport={port} profile=private";

        var start = new ProcessStartInfo("netsh", arguments)
        {
            UseShellExecute = true,   // required for the UAC prompt
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return new FirewallResult(false, true, command, "the process did not start");
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return new FirewallResult(process.ExitCode == 0, true, command, string.Empty);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new FirewallResult(false, true, command, ex.Message);
        }
    }
}
