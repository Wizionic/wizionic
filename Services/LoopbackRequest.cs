using System.Net;

namespace App.Services;

public static class LoopbackRequest
{
    public static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null)
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }
}
