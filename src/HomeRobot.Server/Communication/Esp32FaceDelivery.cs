using System.Net.Http.Json;
using System.Text.Json;

namespace HomeRobot.Server.Communication;

// One explicitly configured HTTP display belongs to one logical robot.
public sealed class Esp32FaceDelivery(IHttpClientFactory clients, IConfiguration configuration,
    RobotRegistry registry, ILogger<Esp32FaceDelivery> logger) : BackgroundService
{
    public string Status { get; private set; } = "disabled";
    public string? RobotId => configuration["Face:Esp32:RobotId"];

    internal static async Task DeliverAsync(HttpClient http, Uri address, string expression, CancellationToken token)
    {
        // ESP-IDF requires a fixed Content-Length, not chunked JSON transfer.
        using var body = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { expression }));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync(new Uri(address, "/face"), body, token);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Face:Esp32:Enabled")) return;
        if (!RobotProtocol.IsIdentifier(RobotId) ||
            !Uri.TryCreate(configuration["Face:Esp32:BaseUrl"] ?? configuration["Speech:Esp32:BaseUrl"], UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo))
            throw new InvalidOperationException("Face:Esp32 requires RobotId and an HTTP(S) BaseUrl.");
        using var http = clients.CreateClient("esp32-face");
        long revision = -1;
        string? deliveredExpression = null;
        var probeAt = DateTimeOffset.MinValue;
        var retryAt = DateTimeOffset.MinValue;
        Status = "connecting";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var state = registry.GetFace(RobotId!);
                if (now >= retryAt && (state.Revision != revision || now >= probeAt))
                {
                    bool restore = false;
                    if (now >= probeAt)
                    {
                        var remote = await http.GetFromJsonAsync<JsonElement>(new Uri(address, "/face"), stoppingToken);
                        restore = remote.GetProperty("expression").GetString() != state.Expression;
                        probeAt = now.AddSeconds(5);
                    }
                    if (restore || state.Revision != revision)
                    {
                        if (restore || deliveredExpression != state.Expression)
                            await DeliverAsync(http, address, state.Expression, stoppingToken);
                        deliveredExpression = state.Expression;
                        // Blink is an event; reconnects restore only the underlying expression.
                        if (revision >= 0 && state.Revision != revision && state.BlinkAt is { } blink && now - blink < TimeSpan.FromMilliseconds(500))
                            await DeliverAsync(http, address, "blink", stoppingToken);
                        revision = state.Revision;
                    }
                    if (Status != "delivered") logger.LogInformation("ESP32 face delivery available");
                    Status = "delivered";
                }
                await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                if (Status != "error") logger.LogWarning("ESP32 face delivery failed ({ErrorType})", ex.GetType().Name);
                Status = "error";
                revision = -1;
                deliveredExpression = null;
                retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                probeAt = DateTimeOffset.MinValue;
            }
        }
    }
}
