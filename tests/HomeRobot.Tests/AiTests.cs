using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeRobot.Server.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeRobot.Tests;

public sealed class AiTests : IDisposable
{
    private readonly TestClock clock = new();
    private readonly OllamaHandler ollama = new();
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public AiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Ollama:BaseUrl"] = "http://ollama.test:11434" }))
            .ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddHttpClient<OllamaProvider>().ConfigurePrimaryHttpMessageHandler(() => ollama);
            }));
        client = factory.CreateClient();
    }

    [Fact]
    public async Task StructuredRequestUsesConfiguredRemoteProvider()
    {
        var provider = factory.Services.GetRequiredService<IAIProvider>();
        await provider.GenerateJsonAsync([new("user", "Turn right 90 degrees.")], CancellationToken.None);

        Assert.Equal("json", ollama.Format);
    }

    [Fact]
    public async Task ChatCreatesSessionAndReusesContext()
    {
        var first = await ChatAsync("Ahoj");
        Assert.NotEqual(Guid.Empty, first.SessionId);
        Assert.Equal("Hello!", first.Response);
        Assert.Equal("robot-ai", first.Model);
        Assert.Null(ollama.Format);
        Assert.Equal(new[] { "system", "user" }, ollama.Messages.Select(message => message.Role));

        var second = await ChatAsync("Continue", first.SessionId);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(new[] { "Ahoj", "Hello!", "Continue" }, ollama.Messages.Skip(1).Select(message => message.Content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task EmptyMessageReturnsBadRequest(string? message)
    {
        using var response = await client.PostAsJsonAsync("/api/ai/chat", new { message });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ollama.Messages);
    }

    [Fact]
    public async Task HistoryRetainsLatestTwentyMessagesAndSystemPrompt()
    {
        var session = await ChatAsync("0");
        for (var index = 1; index <= 11; index++)
            await ChatAsync(index.ToString(), session.SessionId);

        Assert.Equal(22, ollama.Messages.Count);
        Assert.Equal("system", ollama.Messages[0].Role);
        Assert.Equal("1", ollama.Messages[1].Content);
        Assert.Equal("11", ollama.Messages[^1].Content);
    }

    [Fact]
    public async Task InactiveSessionExpiresAfterThirtyMinutes()
    {
        var first = await ChatAsync("Old context");
        clock.Now += TimeSpan.FromMinutes(29);
        Assert.Equal(first.SessionId, (await ChatAsync("Refresh", first.SessionId)).SessionId);
        clock.Now += TimeSpan.FromMinutes(30);
        var second = await ChatAsync("New context", first.SessionId);

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(new[] { "system", "user" }, ollama.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task ConnectionFailureReturnsServiceUnavailableAndDisconnectedStatus()
    {
        ollama.Disconnected = true;
        using var response = await client.PostAsJsonAsync("/api/ai/chat", new { message = "Hello" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new OllamaStatus(false, "robot-ai", false), await StatusAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsReportWhetherTheConfiguredModelIsInstalled(bool installed)
    {
        ollama.Installed = installed;
        Assert.Equal(new OllamaStatus(installed, "robot-ai", true), await StatusAsync());
    }

    [Fact]
    public async Task InvalidUpstreamResponseDoesNotEnterHistory()
    {
        var first = await ChatAsync("First");
        ollama.InvalidReply = true;
        using var response = await client.PostAsJsonAsync("/api/ai/chat",
            new { message = "Failed turn", first.SessionId });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        ollama.InvalidReply = false;
        await ChatAsync("Next", first.SessionId);
        Assert.Equal(new[] { "First", "Hello!", "Next" }, ollama.Messages.Skip(1).Select(message => message.Content));
    }

    private Task<OllamaStatus?> StatusAsync() => client.GetFromJsonAsync<OllamaStatus>("/api/ai/status");

    private async Task<ChatResponse> ChatAsync(string message, Guid? sessionId = null)
    {
        using var response = await client.PostAsJsonAsync("/api/ai/chat", new { message, sessionId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private sealed record ChatResponse(Guid SessionId, string Response, string Model);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class OllamaHandler : HttpMessageHandler
    {
        public bool Disconnected { get; set; }
        public bool Installed { get; set; }
        public bool InvalidReply { get; set; }
        public string? Format { get; private set; }
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("http://ollama.test:11434", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            if (Disconnected)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Test connection refused.");

            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                var name = Installed ? "robot-ai:latest" : "another-model:latest";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { models = new[] { new { name } } })
                };
            }

            Assert.Equal("/api/chat", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("robot-ai", body.RootElement.GetProperty("model").GetString());
            Format = body.RootElement.TryGetProperty("format", out var format) ? format.GetString() : null;
            Messages = body.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => new ChatMessage(
                    message.GetProperty("role").GetString()!, message.GetProperty("content").GetString()!))
                .ToArray();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = InvalidReply
                    ? new StringContent("{")
                    : JsonContent.Create(new { message = new { role = "assistant", content = "Hello!" } })
            };
        }
    }
}
