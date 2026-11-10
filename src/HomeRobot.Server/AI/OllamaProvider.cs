using System.Net;
using System.Text.Json.Serialization;
using HomeRobot.Server.Dashboard;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.AI;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; }
    public double? Temperature { get; set; }
    public int? MaxResponseTokens { get; set; }
}

public sealed class OllamaProvider(
    HttpClient client, IOptions<OllamaOptions> options, ILogger<OllamaProvider> logger, DashboardDiagnostics diagnostics) : IAIProvider
{
    public string Model => options.Value.Model;

    public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
        GenerateAsync(messages, json: false, cancellationToken);

    public Task<string> GenerateJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
        GenerateAsync(messages, json: true, cancellationToken);

    private async Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages, bool json, CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));

        diagnostics.RecordAiRequest();
        var parameters = new GenerationOptions(options.Value.Temperature, options.Value.MaxResponseTokens);
        var request = new ChatRequest(Model, messages, false, json ? "json" : null,
            parameters.Temperature.HasValue || parameters.MaxTokens.HasValue ? parameters : null);

        try
        {
            using var response = await client.PostAsJsonAsync("/api/chat", request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw Failure(503, "Configured Ollama model is unavailable.");
            if (!response.IsSuccessStatusCode)
                throw Failure(502, "Ollama rejected the chat request.");

            var result = await response.Content.ReadFromJsonAsync<ChatResult>(cancellationToken);
            if (result?.Message is not { Role: "assistant", Content: { } content } ||
                string.IsNullOrWhiteSpace(content))
                throw Failure(502, "Ollama returned an invalid chat response.");

            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(504, "Ollama chat request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                ? Failure(503, "Could not connect to Ollama.")
                : Failure(502, "Ollama HTTP request failed.");
        }
        catch (JsonException)
        {
            throw Failure(502, "Ollama returned an invalid chat response.");
        }
    }

    public async Task<OllamaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var connected = false;

        try
        {
            using var response = await client.GetAsync("/api/tags", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            connected = true;
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(client.MaxResponseContentBufferSize, timeout.Token);
            var result = await response.Content.ReadFromJsonAsync<ModelList>(timeout.Token);
            if (result?.Models is null)
                throw new JsonException();

            var modelName = Model[(Model.LastIndexOf('/') + 1)..];
            var installedName = modelName.Contains(':') ? Model : Model + ":latest";
            var installed = result.Models.Any(model => model?.Name == Model || model?.Name == installedName);
            return new OllamaStatus(installed, Model, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Ollama model status check failed.");
            return new OllamaStatus(false, Model, connected);
        }
    }

    private AIProviderException Failure(int statusCode, string message)
    {
        logger.LogWarning("{OllamaFailure}", message);
        return new AIProviderException(statusCode, message);
    }

    private sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, bool Stream,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GenerationOptions? Options);
    private sealed record GenerationOptions(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature,
        [property: JsonPropertyName("num_predict"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxTokens);

    private sealed record ChatResult(Reply? Message);
    private sealed record Reply(string? Role, string? Content);
    private sealed record ModelList(InstalledModel?[]? Models);
    private sealed record InstalledModel(string? Name);
}

public sealed record OllamaStatus(bool Available, string Model, bool OllamaConnected);

public sealed class AIProviderException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
