using System.Net;
using System.Security.Cryptography;

namespace HomeRobot.Server.Dashboard;

internal sealed class DashboardAccess(IConfiguration configuration)
{
    private const string ManagementKey = "Dashboard.Management";
    private readonly string? passwordHash = configuration["Dashboard:AdminPasswordHash"];
    private readonly bool allowLan = configuration.GetValue<bool>("Dashboard:AllowLan");

    internal static bool AllowsManagement(HttpContext context) => context.Items.ContainsKey(ManagementKey);

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
            address = address.MapToIPv4();
        var host = request.Host.Host.Trim('[', ']');
        var loopback = (address is null || IPAddress.IsLoopback(address)) &&
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress));

        var origin = request.Headers.Origin;
        var site = request.Headers["Sec-Fetch-Site"];
        var expectedOrigin = $"{request.Scheme}://{request.Host.ToUriComponent()}";
        if (request.Headers.ContainsKey("Forwarded") ||
            request.Headers.Keys.Any(key => key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)) ||
            site == "cross-site" || site == "same-site" ||
            HttpMethods.IsPost(request.Method) && request.Path.StartsWithSegments("/api/robots") &&
                (request.HasFormContentType || request.ContentType?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) == true) ||
            origin.Count > 0 && (origin.Count != 1 ||
                !string.Equals(origin[0], expectedOrigin, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 403;
            return;
        }

        if (!loopback)
        {
            if (!allowLan || !request.IsHttps || passwordHash is not { Length: 64 } || !passwordHash.All(Uri.IsHexDigit))
            {
                context.Response.StatusCode = 403;
                return;
            }

            if (!Authenticated(request.Headers.Authorization.ToString(), passwordHash))
            {
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"HomeRobot\", charset=\"UTF-8\"";
                context.Response.StatusCode = 401;
                return;
            }
        }

        if (!loopback || origin.Count > 0 && address is not null)
            context.Items[ManagementKey] = true;

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; media-src blob:; " +
            "base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
        await next(context);
    }

    private static bool Authenticated(string authorization, string hash)
    {
        if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) || authorization.Length > 512)
            return false;
        Span<byte> credentials = stackalloc byte[384];
        if (!Convert.TryFromBase64String(authorization[6..], credentials, out var length) ||
            !credentials[..length].StartsWith("admin:"u8))
            return false;

        var password = credentials[6..length];
        return password.Length >= 24 &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(password), Convert.FromHexString(hash));
    }
}
