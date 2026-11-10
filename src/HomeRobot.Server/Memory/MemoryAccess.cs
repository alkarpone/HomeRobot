using System.Net;

namespace HomeRobot.Server.Memory;

internal static class MemoryAccess
{
    public static bool IsLocal(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
            address = address.MapToIPv4();
        var host = context.Request.Host.Host.Trim('[', ']');
        return address is not null && IPAddress.IsLoopback(address) &&
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress)) &&
            !context.Request.Headers.ContainsKey("Origin") &&
            !context.Request.Headers.ContainsKey("Forwarded") &&
            !context.Request.Headers.Keys.Any(key => key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));
    }
}
