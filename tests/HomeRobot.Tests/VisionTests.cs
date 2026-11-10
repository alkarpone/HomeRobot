using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeRobot.Server.AI;
using HomeRobot.Server.Speech;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace HomeRobot.Tests;

public sealed class VisionTests
{
    private const string Path = "/api/vision/analyze";

    [Fact]
    public async Task UploadIsNormalizedAndFollowUpIsScopedAndExpires()
    {
        var clock = new Clock();
        var ai = new FakeAi();
        using var app = Start(ai, clock);
        using var client = app.CreateClient();
        using var first = await Send(client, Image(), "Co vidíš?");
        first.EnsureSuccessStatusCode();
        var initial = await first.Content.ReadFromJsonAsync<Answer>();
        Assert.NotNull(initial);
        Assert.False(initial.Cached);
        Assert.Equal("Vidím stůl.", initial.Response);
        Assert.Equal("happy", initial.Expression);
        var sent = Assert.Single(ai.Messages[^1].Images!);
        using var input = SKData.CreateCopy(Convert.FromBase64String(sent));
        using var decoded = SKCodec.Create(input);
        Assert.Equal(SKEncodedImageFormat.Jpeg, decoded.EncodedFormat);
        Assert.Equal((1280, 640), (decoded.Info.Width, decoded.Info.Height));

        using var other = await Send(client, null, "Jakou má barvu?", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        using var follow = await Send(client, null, "Jakou má barvu?", initial.SessionId);
        follow.EnsureSuccessStatusCode();
        Assert.True((await follow.Content.ReadFromJsonAsync<Answer>())!.Cached);
        Assert.Equal(sent, Assert.Single(ai.Messages[^1].Images!));
        var quote = ai.Messages[1].Content;
        using var previous = JsonDocument.Parse(quote[quote.IndexOf('{')..]);
        Assert.Equal("Co vidíš?", previous.RootElement.GetProperty("Question").GetString());

        clock.Advance(TimeSpan.FromMinutes(2));
        using var expired = await Send(client, null, "Jakou má barvu?", initial.SessionId);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(2, ai.Calls);
    }

    [Fact]
    public async Task InvalidEmptyAndOversizedUploadsNeverReachAi()
    {
        var ai = new FakeAi();
        using var app = Start(ai);
        using var client = app.CreateClient();
        foreach (var (bytes, expected) in new (byte[]?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.BadRequest), ([], HttpStatusCode.BadRequest),
            ([0xff, 0xd8, 0xff, 0xd9], HttpStatusCode.UnsupportedMediaType),
            (Image()[..50], HttpStatusCode.UnsupportedMediaType),
            (new byte[2 * 1024 * 1024 + 1], HttpStatusCode.RequestEntityTooLarge)
        })
        {
            using var result = await Send(client, bytes);
            Assert.Equal(expected, result.StatusCode);
        }
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task ScanLeavesConversationImageUntouchedAndDeleteClearsIt()
    {
        var ai = new FakeAi();
        using var app = Start(ai);
        using var client = app.CreateClient();
        var sessionId = Guid.NewGuid();
        using var original = await Send(client, Image(), "Původní otázka", sessionId);
        original.EnsureSuccessStatusCode();
        using var scan = await client.PostAsJsonAsync(Path, new { image = Image(), question = "Scan", sessionId, scan = true });
        scan.EnsureSuccessStatusCode();
        using var follow = await Send(client, null, "Co jsi viděl?", sessionId);
        follow.EnsureSuccessStatusCode();
        var quote = ai.Messages[1].Content;
        using var previous = JsonDocument.Parse(quote[quote.IndexOf('{')..]);
        Assert.Equal("Původní otázka", previous.RootElement.GetProperty("Question").GetString());
        using var deleted = await client.DeleteAsync($"/api/vision/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await Send(client, null, session: sessionId);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(503, "vision_unavailable")]
    [InlineData(504, "vision_timeout")]
    [InlineData(502, "vision_failed")]
    public async Task ProviderFailuresAreStructured(int status, string code)
    {
        var ai = new FakeAi { Status = status };
        using var app = Start(ai);
        using var client = app.CreateClient();
        using var result = await Send(client, Image());
        Assert.Equal(status, (int)result.StatusCode);
        Assert.Equal(code, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task BrowserOriginsAreRejectedAndAiOutputCannotDispatch()
    {
        var ai = new FakeAi { Reply = "Expression: move_forward\n{\"command\":\"move_forward\"}" };
        using var app = Start(ai);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var blocked = await Send(client, Image());
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(0, ai.Calls);
        client.DefaultRequestHeaders.Remove("Origin");
        using var result = await Send(client, Image());
        result.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("idle", json.RootElement.GetProperty("expression").GetString());
        Assert.False(json.RootElement.TryGetProperty("command", out _));
        Assert.False(json.RootElement.TryGetProperty("actions", out _));
    }

    [Fact]
    public async Task ClientCancellationReachesProviderAndDoesNotCache()
    {
        var ai = new FakeAi { Block = true };
        using var app = Start(ai);
        using var client = app.CreateClient();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = Guid.NewGuid();
        var request = Send(client, Image(), session: session, token: cancel.Token);
        await ai.Entered.Task.WaitAsync(cancel.Token);
        using var overlapping = await Send(client, Image(), token: cancel.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, overlapping.StatusCode);
        Assert.Equal(1, ai.Calls);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        await ai.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var follow = await Send(client, null, session: session);
        Assert.Equal(HttpStatusCode.NotFound, follow.StatusCode);
    }

    [Theory]
    [InlineData("Co vidíš?", "vision")]
    [InlineData("Co vidíš teď?", "vision")]
    [InlineData("Rozhlédni se.", "vision")]
    [InlineData("Popiš, co je před tebou.", "vision")]
    [InlineData("Přečti text, který vidíš.", "vision")]
    [InlineData("Co jsi viděl předtím?", "vision_past")]
    [InlineData("Podívej se před sebe.", "vision")]
    [InlineData("Co je přede mnou?", "vision")]
    [InlineData("Co mám v ruce?", "vision")]
    [InlineData("Popiš, co vidíš.", "vision")]
    [InlineData("Co je na stole?", "vision")]
    [InlineData("Vidíš nějakého psa?", "vision")]
    [InlineData("Kolik je tam lidí?", "vision")]
    [InlineData("Přečti mi ten text.", "vision")]
    [InlineData("Co je tohle?", "vision")]
    [InlineData("Poznáš ten předmět?", "vision")]
    [InlineData("Je tam nějaká překážka?", "vision")]
    [InlineData("Jakou to má barvu?", "vision_followup")]
    [InlineData("Jakou barvu má ten notebook?", "vision_followup")]
    [InlineData("Otoč kameru doleva.", "vision_unsupported")]
    [InlineData("Vidíš v tom problém?", "conversation")]
    [InlineData("Je tam někde napsáno, kdy přijede vlak?", "conversation")]
    [InlineData("Zastav.", "stop")]
    public void RoutesCzechSpeechWithoutAuthorizingVisionMovement(string text, string route)
    {
        var intent = VoiceIntent.Analyze(new SpeechTranscript(text, true), 30);
        Assert.Equal(route, intent.Route);
        Assert.Null(intent.Actions);
    }

    private static byte[] Image()
    {
        using var bitmap = new SKBitmap(1400, 700);
        bitmap.Erase(SKColors.Red);
        using var output = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return output.ToArray();
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, byte[]? image, string? question = null,
        Guid? session = null, CancellationToken token = default) =>
        client.PostAsJsonAsync(Path, new { image, question, sessionId = session }, token);

    private static WebApplicationFactory<Program> Start(FakeAi ai, Clock? clock = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAIProvider>(ai);
            if (clock is not null) services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IStartupFilter, LocalAddress>();
        }));

    private sealed record Answer(string Response, string Expression, Guid SessionId, bool Cached);

    private sealed class LocalAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, proceed) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return proceed();
            });
            next(app);
        };
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan time) => Interlocked.Add(ref timestamp, time.Ticks);
    }

    private sealed class FakeAi : IAIProvider
    {
        public string Reply { get; init; } = "Expression: happy\nVidím stůl.";
        public int Status { get; init; }
        public bool Block { get; init; }
        public int Calls { get; private set; }
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken token)
        {
            Calls++;
            Messages = messages;
            if (Status != 0) throw new AIProviderException(Status, "Provider error.");
            if (Block)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Cancelled.TrySetResult(); }
            }
            return Reply;
        }
    }
}
