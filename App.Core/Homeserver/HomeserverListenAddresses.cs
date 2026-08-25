using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace App.Core.Homeserver;

/// <summary>
/// URLs a Home Server is reachable at: localhost for this PC, hostname.local and LAN IPv4 for other devices.
/// </summary>
public sealed class HomeserverListenAddresses
{
    public string Port { get; init; } = HomeserverPaths.DefaultPort;
    public string LocalUrl { get; init; } = HomeserverPaths.DefaultBaseUrl;
    public string HostName { get; init; } = "";
    public string? HostNameUrl { get; init; }
    public string? IPv4 { get; init; }
    public string? IPv4Url { get; init; }

    public static HomeserverListenAddresses LocalOnly(string? port = null)
    {
        var p = string.IsNullOrWhiteSpace(port) ? HomeserverPaths.DefaultPort : port.Trim();
        return new HomeserverListenAddresses
        {
            Port = p,
            LocalUrl = $"http://localhost:{p}"
        };
    }

    public static HomeserverListenAddresses Detect(string? port = null)
    {
        var p = string.IsNullOrWhiteSpace(port) ? HomeserverPaths.DefaultPort : port.Trim();
        var host = SanitizeDnsLabel(TryGetHostLabel());
        var ipv4 = TryGetLanIPv4();
        return new HomeserverListenAddresses
        {
            Port = p,
            LocalUrl = $"http://localhost:{p}",
            HostName = host,
            HostNameUrl = string.IsNullOrEmpty(host) ? null : $"http://{host}.local:{p}",
            IPv4 = ipv4,
            IPv4Url = string.IsNullOrEmpty(ipv4) ? null : $"http://{ipv4}:{p}"
        };
    }

    public bool MatchesLoginServer(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!int.TryParse(Port, out var port))
            port = 5150;
        if (uri.Port != port)
            return false;

        var host = uri.IdnHost.Trim().TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(HostName) &&
            (host.Equals(HostName, StringComparison.OrdinalIgnoreCase) ||
             host.Equals(HostName + ".local", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrEmpty(IPv4) && host.Equals(IPv4, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool TryTcpConnect(string? url, int timeoutMs = 800)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(uri.Host, uri.Port);
            if (!task.Wait(timeoutMs))
                return false;
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    internal static string TryGetHostLabel()
    {
        try
        {
            var dns = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(dns))
            {
                var label = dns.Split('.')[0];
                if (!string.IsNullOrWhiteSpace(label))
                    return label;
            }
        }
        catch
        {
            // fall through
        }

        return Environment.MachineName;
    }

    internal static string SanitizeDnsLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var chars = name.Trim().Select(ch =>
            char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) :
            ch is '_' or ' ' ? '-' :
            ch == '-' ? '-' :
            '\0').Where(ch => ch != '\0').ToArray();
        var s = new string(chars).Trim('-');
        while (s.Contains("--", StringComparison.Ordinal))
            s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Length > 63 ? s[..63].TrimEnd('-') : s;
    }

    internal static string? TryGetLanIPv4()
    {
        try
        {
            string? fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var preferred = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;
                    var bytes = addr.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                        continue;

                    var ip = addr.Address.ToString();
                    if (preferred)
                        return ip;
                    fallback ??= ip;
                }
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }
}
