using System.Buffers.Binary;
using System.Globalization;
using HomeRobot.Server.AI;
using HomeRobot.Server.Communication;
using HomeRobot.Server.Dashboard;
using HomeRobot.Server.Speech;
using HomeRobot.Server.Vision;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromServices] DashboardDiagnostics diagnostics,
        [FromServices] OllamaProvider ollama, [FromServices] RobotRegistry registry, CancellationToken token)
    {
        var status = await diagnostics.CheckOllamaAsync(ollama, refresh: false, token);
        var robots = registry.List();
        return Ok(new
        {
            service = "HomeRobot.Server",
            status = "ok",
            ollama = status,
            connectedRobots = robots.Count,
            connectedSimulators = robots.Count(robot => robot.DeviceType == "simulator"),
            diagnostics = diagnostics.Snapshot()
        });
    }

    [HttpGet("config")]
    public IActionResult Config([FromServices] IServiceProvider services)
    {
        var store = services.GetRequiredService<DashboardSettings>();
        var saved = store.Snapshot();
        var ollama = services.GetRequiredService<IOptions<OllamaOptions>>().Value;
        var whisper = services.GetRequiredService<IOptions<WhisperOptions>>().Value;
        var piper = services.GetRequiredService<IOptions<PiperOptions>>().Value;
        var conversation = services.GetRequiredService<IOptions<ConversationOptions>>().Value;
        var vision = services.GetRequiredService<VisionContext>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var effective = new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = DashboardSettings.SafeUrl(ollama.BaseUrl) ? ollama.BaseUrl : null,
            ["Ollama:Model"] = ollama.Model,
            ["Ollama:TimeoutSeconds"] = Number(ollama.TimeoutSeconds),
            ["Ollama:Temperature"] = ollama.Temperature?.ToString(CultureInfo.InvariantCulture),
            ["Ollama:MaxResponseTokens"] = ollama.MaxResponseTokens?.ToString(CultureInfo.InvariantCulture),
            ["Conversation:MaxHistoryMessages"] = Number(conversation.MaxHistoryMessages),
            ["Speech:STT:Language"] = whisper.Language,
            ["Speech:STT:ModelPath"] = PublicModelPath("Speech:STT:ModelPath", whisper.ModelPath),
            ["Speech:STT:TimeoutSeconds"] = Number(whisper.TimeoutSeconds),
            ["Speech:TTS:VoiceModelPath"] = PublicModelPath("Speech:TTS:VoiceModelPath", piper.VoiceModelPath),
            ["Speech:TTS:TimeoutSeconds"] = Number(piper.TimeoutSeconds),
            ["Vision:ContextLifetimeSeconds"] = Number((int)vision.Lifetime.TotalSeconds)
        };

        var higherPriority = ((IConfigurationRoot)configuration).Providers.Reverse()
            .TakeWhile(provider => provider is not DashboardSettings.SettingsProvider).ToArray();
        return Ok(new
        {
            saved, effective,
            overridden = effective.Keys.Where(key => higherPriority.Any(provider => provider.TryGet(key, out _))),
            restartRequired = !saved.OrderBy(pair => pair.Key).SequenceEqual(store.Startup.OrderBy(pair => pair.Key)),
            lanEnabled = configuration.GetValue<bool>("Dashboard:AllowLan")
        });
    }

    [HttpPut("config")]
    [HttpPatch("config")]
    [RequestSizeLimit(8192)]
    public IActionResult SaveConfig(Dictionary<string, string?> values, [FromServices] IServiceProvider services,
        [FromServices] ILogger<SystemController> logger)
    {
        var merge = HttpMethods.IsPatch(Request.Method);
        var invalid = DashboardSettings.Validate(values, allowRemoval: merge);
        if (invalid.Length != 0)
            return BadRequest(new { title = "Invalid settings.", fields = invalid });
        services.GetRequiredService<DashboardSettings>().Save(values, merge);
        logger.LogInformation("Dashboard settings saved.");
        return Config(services);
    }

    [HttpPost("test-ollama")]
    public Task<OllamaStatus> TestOllama([FromServices] DashboardDiagnostics diagnostics,
        [FromServices] OllamaProvider provider, CancellationToken token) =>
        diagnostics.CheckOllamaAsync(provider, refresh: true, token);

    [HttpPost("test-stt")]
    public async Task<IActionResult> TestStt([FromServices] ISpeechToTextService stt,
        [FromServices] DashboardDiagnostics diagnostics, [FromServices] ILogger<SystemController> logger,
        CancellationToken token)
    {
        // One second of silent mono PCM exercises the same worker without using the microphone.
        var wave = new byte[44 + 32000];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), wave.Length - 44);

        try
        {
            using var stream = new MemoryStream(wave);
            await stt.TranscribeAsync(stream, token);
            diagnostics.RecordSpeech(true, "ready");
            logger.LogInformation("Whisper health check completed.");
            return Ok(new { status = "ready" });
        }
        catch (SpeechException ex)
        {
            diagnostics.RecordSpeech(true, "failed", ex.Message);
            logger.LogWarning("Whisper health check failed.");
            return Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    [HttpGet("logs")]
    public IActionResult Logs([FromServices] IServiceProvider services, [FromQuery] string? query, [FromQuery] LogLevel? minimum)
    {
        if (query is { Length: > 120 } || minimum is not null && !Enum.IsDefined(minimum.Value))
            return BadRequest();
        return Ok(services.GetRequiredService<DashboardLogs>().Read(query, minimum));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? PublicModelPath(string key, string value) =>
        DashboardSettings.Validate(new() { [key] = value }).Length == 0 ? value : null;
}
