using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeRobot.Server.AI;
using HomeRobot.Server.Communication;
using HomeRobot.Server.Dashboard;
using HomeRobot.Server.Speech;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HomeRobot.Tests;

public sealed class DashboardTests : IDisposable
{
    private const string Password = "dashboard-test-password-only-241";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "home-robot-dashboard-" + Guid.NewGuid());
    private readonly FakeOllama ollama = new();

    [Fact]
    public async Task DashboardServesAssetsAndRealStatusWithStubOllama()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        using var page = await client.GetAsync("/");
        page.EnsureSuccessStatusCode();
        Assert.Contains("Home Robot", await page.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(page.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard.js")).StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.Equal("ok", status.GetProperty("status").GetString());
        Assert.Equal(0, status.GetProperty("connectedRobots").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("diagnostics").GetProperty("lastVisionAnalysis").ValueKind);
        Assert.True(status.GetProperty("ollama").GetProperty("available").GetBoolean());
        Assert.Equal(1, ollama.Checks);

        ollama.Installed = false;
        status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.True(status.GetProperty("ollama").GetProperty("available").GetBoolean());
        using var test = await client.PostAsync("/api/system/test-ollama", null);
        var model = (await test.Content.ReadFromJsonAsync<OllamaStatus>())!;
        Assert.True(model.OllamaConnected);
        Assert.False(model.Available);
        Assert.Equal(2, ollama.Checks);

        using var whisper = await client.PostAsync("/api/system/test-stt", null);
        whisper.EnsureSuccessStatusCode();
        status = await client.GetFromJsonAsync<JsonElement>("/api/system/status");
        Assert.Equal("ready", status.GetProperty("diagnostics").GetProperty("whisper").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ConfigValidatesAndPersistsAcrossHostsWithDeploymentPrecedence()
    {
        var values = new Dictionary<string, string>
        {
            ["Ollama:BaseUrl"] = "http://localhost:11435",
            ["Ollama:Temperature"] = "0.5",
            ["Ollama:MaxResponseTokens"] = "320",
            ["Conversation:MaxHistoryMessages"] = "8"
        };
        await using (var factory = Host())
        {
            using var client = factory.CreateClient();
            foreach (var invalid in new Dictionary<string, string>
            {
                ["Ollama:BaseUrl"] = "https://name:private@localhost/",
                ["Conversation:MaxHistoryMessages"] = "7",
                ["Speech:STT:ModelPath"] = "models/stt/../secret",
                ["Speech:STT:PythonPath"] = "/bin/sh",
                ["Ollama:Temperature"] = "9"
            })
            {
                using var rejected = await client.PutAsJsonAsync("/api/system/config",
                    new Dictionary<string, string> { [invalid.Key] = invalid.Value });
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }

            using var response = await client.PutAsJsonAsync("/api/system/config", values);
            response.EnsureSuccessStatusCode();
            var config = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(config.GetProperty("restartRequired").GetBoolean());
            Assert.Equal("320", config.GetProperty("saved").GetProperty("Ollama:MaxResponseTokens").GetString());
            Assert.Equal(JsonValueKind.Null, config.GetProperty("effective").GetProperty("Ollama:MaxResponseTokens").ValueKind);
            using var patch = await client.PatchAsJsonAsync("/api/system/config", new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = null,
                ["Speech:STT:Language"] = "cs"
            });
            patch.EnsureSuccessStatusCode();
            var updated = (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("saved");
            Assert.False(updated.TryGetProperty("Ollama:BaseUrl", out _));
            Assert.Equal("320", updated.GetProperty("Ollama:MaxResponseTokens").GetString());
        }

        await using (var factory = Host(deploymentOverride: true))
        {
            using var client = factory.CreateClient();
            var config = await client.GetFromJsonAsync<JsonElement>("/api/system/config");
            Assert.False(config.GetProperty("restartRequired").GetBoolean());
            Assert.Equal("12", config.GetProperty("effective").GetProperty("Conversation:MaxHistoryMessages").GetString());
            Assert.Contains("Conversation:MaxHistoryMessages", config.GetProperty("overridden").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("320", config.GetProperty("effective").GetProperty("Ollama:MaxResponseTokens").GetString());

            using var chat = await client.PostAsJsonAsync("/api/ai/chat", new { message = "Hello" });
            chat.EnsureSuccessStatusCode();
            Assert.Equal(0.5, ollama.Options.GetProperty("temperature").GetDouble());
            Assert.Equal(320, ollama.Options.GetProperty("num_predict").GetInt32());
        }
    }

    [Fact]
    public async Task LanRequiresHttpsAndValidCredentialsAndBlocksCrossOriginMutations()
    {
        await using var disabled = Host();
        using var blocked = disabled.CreateClient(new() { BaseAddress = new Uri("https://robot.test") });
        Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync("/")).StatusCode);

        await using var enabled = Host(lan: true);
        using var insecure = enabled.CreateClient(new() { BaseAddress = new Uri("http://robot.test") });
        using var client = enabled.CreateClient(new() { BaseAddress = new Uri("https://robot.test") });
        Assert.Equal(HttpStatusCode.Forbidden, (await insecure.GetAsync("/")).StatusCode);
        using var challenge = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Equal("Basic", challenge.Headers.WwwAuthenticate.Single().Scheme);
        using var audio = new HttpRequestMessage(HttpMethod.Get, "/api/audio/resources/unknown");
        audio.Headers.Add("X-Audio-Token", new string('A', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(audio)).StatusCode);
        Authenticate(client, "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/memory")).StatusCode);
        Authenticate(client, Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        using var chat = await client.PostAsJsonAsync("/api/ai/chat", new { message = "Ulož si, že mám psa." });
        chat.EnsureSuccessStatusCode();
        Assert.Contains("jen přímo", (await chat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("response").GetString());

        using var csrf = new HttpRequestMessage(HttpMethod.Post, "/api/robots/robot-test/stop");
        csrf.Headers.Add("Origin", "https://other.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(csrf)).StatusCode);
        using var legacyForm = new StringContent("stop=true", Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/robots/robot-test/stop", legacyForm)).StatusCode);
        using var rebinding = disabled.CreateClient(new() { BaseAddress = new Uri("http://arbitrary.test") });
        Assert.Equal(HttpStatusCode.Forbidden, (await rebinding.GetAsync("/api/system/status")).StatusCode);
    }

    [Fact]
    public async Task AuthenticatedBrowserCanEditPersonalityAndManageMemory()
    {
        await using var factory = Host(lan: true);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://robot.test") });
        Authenticate(client, Password);
        client.DefaultRequestHeaders.Add("Origin", "https://robot.test");
        var personality = (await client.GetFromJsonAsync<PersonalityProfile>("/api/personality"))!;
        using var badProfile = await client.PutAsJsonAsync("/api/personality", personality with { DefaultExpression = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, badProfile.StatusCode);
        using var goodProfile = await client.PutAsJsonAsync("/api/personality", personality with { Name = "Karel" });
        goodProfile.EnsureSuccessStatusCode();
        Assert.Equal("Karel", (await client.GetFromJsonAsync<PersonalityProfile>("/api/personality"))!.Name);

        using var create = await client.PostAsJsonAsync("/api/memory", new { content = "Likes green tea", category = "preferences" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var entry = await create.Content.ReadFromJsonAsync<JsonElement>();
        var path = "/api/memory/" + entry.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new { content = "Likes oolong", category = "preferences" })).StatusCode);
        var records = await client.GetFromJsonAsync<JsonElement[]>("/api/memory?category=preferences");
        Assert.Equal("Likes oolong", Assert.Single(records!).GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        (await client.PostAsJsonAsync("/api/memory", new { content = "Likes jasmine tea" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/memory")).StatusCode);
        using var purge = new HttpRequestMessage(HttpMethod.Delete, "/api/memory");
        purge.Headers.Add("X-Confirm-Memory-Deletion", "delete-all");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(purge)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/memory"))!);
    }

    [Fact]
    public async Task LogsFilterAndExcludePrivateValuesAndExceptions()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var logger = factory.Services.GetRequiredService<ILogger<RobotRegistry>>();
        logger.LogInformation("Robot {RobotId} registered ({DeviceType})", "private-memory-token", "simulator");
        logger.LogWarning(new Exception("private-memory-token"), "Ollama model status check failed.");
        logger.LogError("Unexpected private-memory-token");
        var raw = await client.GetStringAsync("/api/system/logs");
        Assert.Contains("Robot registered.", raw);
        Assert.Contains("Ollama model status check failed.", raw);
        Assert.DoesNotContain("private-memory-token", raw);
        var filtered = await client.GetFromJsonAsync<JsonElement[]>("/api/system/logs?query=ollama&minimum=Warning");
        Assert.Equal("Warning", Assert.Single(filtered!).GetProperty("severity").GetString());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/system/logs?query=token"))!);
    }

    private WebApplicationFactory<Program> Host(bool lan = false, bool deploymentOverride = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Dashboard:SettingsPath", Path.Combine(directory, "settings.json"));
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Memory:DatabasePath"] = Path.Combine(directory, "memory.sqlite3"),
                    ["Dashboard:AllowLan"] = lan.ToString(),
                    ["Dashboard:AdminPasswordHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password))),
                    ["Ollama:Model"] = "dashboard-test"
                };
                if (deploymentOverride)
                    values["Conversation:MaxHistoryMessages"] = "12";
                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<OllamaProvider>().ConfigurePrimaryHttpMessageHandler(() => ollama);
                services.AddSingleton<ISpeechToTextService, FakeWhisper>();
            });
        });

    private static void Authenticate(HttpClient client, string password) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + password)));

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeWhisper : ISpeechToTextService
    {
        public async Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await audio.CopyToAsync(buffer, cancellationToken);
            Assert.True(WavAudio.TryValidate(buffer.ToArray(), out var silent));
            Assert.True(silent);
            return "";
        }
    }

    private sealed class FakeOllama : HttpMessageHandler
    {
        public bool Installed { get; set; } = true;
        public int Checks { get; private set; }
        public JsonElement Options { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                Checks++;
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(Installed ? """{"models":[{"name":"dashboard-test:latest"}]}""" : """{"models":[]}""")
                };
            }

            var chat = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Options = chat.GetProperty("options");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"role":"assistant","content":"Hello"}}""")
            };
        }

        protected override void Dispose(bool disposing) { }
    }
}
