using System.Diagnostics;
using System.Text.Json;
using HomeRobot.Server.AI;
using HomeRobot.Server.Dashboard;
using HomeRobot.Server.Memory;
using HomeRobot.Server.Vision;
using Microsoft.AspNetCore.Mvc;

namespace HomeRobot.Server.Controllers;

[Route("api/vision")]
public sealed class VisionController(VisionService vision, VisionContext context, TimeProvider clock,
    ConversationStore conversations, DashboardDiagnostics diagnostics) : ControllerBase
{
    internal const int MaxImage = 2 * 1024 * 1024;
    internal const int MaxRequest = ((MaxImage + 2) / 3) * 4 + 8192;
    private static readonly SemaphoreSlim Slots = new(1);

    public sealed record AnalyzeRequest(byte[]? Image, string? Question, Guid? SessionId, DateTimeOffset? CapturedAt,
        bool Scan = false, Guid? ConversationSessionId = null);

    [HttpPost("analyze")]
    [RequestSizeLimit(MaxRequest)]
    public async Task<IActionResult> Analyze(CancellationToken cancellationToken)
    {
        if (!MemoryAccess.IsLocal(HttpContext))
            return Error(403, "local_only", "Analýza obrazu je dostupná jen na počítači se serverem.");
        if (Request.ContentLength > MaxRequest)
            return Error(413, "image_too_large", "Obrázek je příliš velký.");
        if (!Request.HasJsonContentType())
            return Error(415, "invalid_request", "Očekávám požadavek ve formátu JSON.");
        if (!Slots.Wait(0))
            return Error(429, "vision_busy", "Analýza obrazu je právě vytížená.");

        diagnostics.RecordVisionRequest();
        try
        {
            var started = Stopwatch.GetTimestamp();
            var received = clock.GetTimestamp();
            var input = await Request.ReadFromJsonAsync<AnalyzeRequest>(cancellationToken);
            if (input is null || input.Question is { Length: > 500 } || input.SessionId == Guid.Empty ||
                (input.Scan && input.Image is null))
                return Error(400, "invalid_request", "Dotaz smí mít nejvýše 500 znaků a relace musí být platná.");

            var session = input.SessionId ?? Guid.NewGuid();
            var question = string.IsNullOrWhiteSpace(input.Question) ? "Co je na obrázku?" : input.Question.Trim();
            var cached = input.Image is null && input.SessionId.HasValue;
            if (!cached && !input.Scan) context.Begin(session);
            byte[] jpeg;
            var now = clock.GetUtcNow();
            var capturedAt = input.CapturedAt is { } supplied && supplied < now ? supplied : now;
            (string Question, string Answer)? previous = null;
            if (cached)
            {
                if (context.Get(session) is not { } entry)
                    return Error(404, "image_context_missing", "Předchozí snímek už není dostupný. Nejdřív analyzujte nový.");
                jpeg = entry.Jpeg;
                capturedAt = entry.CapturedAt;
                previous = (entry.Question, entry.Answer);
            }
            else
            {
                if (input.Image is not { Length: > 0 } bytes)
                    return Error(400, "image_required", "Chybí obrázek.");
                if (bytes.Length > MaxImage)
                    return Error(413, "image_too_large", "Obrázek smí mít nejvýše 2 MiB.");
                jpeg = VisionService.Normalize(bytes, cancellationToken);
            }

            ChatMessage[] recent = input.Scan ? [] :
                await conversations.RecentLocalTurnsAsync(input.ConversationSessionId, cancellationToken);
            var (response, expression, inferenceMs) = await vision.DescribeAsync(jpeg, question, previous, recent, cancellationToken);
            if (!cached && !input.Scan)
                context.Set(session, jpeg, received, capturedAt, now - capturedAt, question, response, cancellationToken);
            diagnostics.RecordVisionAnalysis(inferenceMs);
            return Ok(new { response, expression, sessionId = session, cached, inferenceMs, contextLifetimeSeconds = (int)context.Lifetime.TotalSeconds,
                processingMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
        catch (JsonException)
        {
            return Error(400, "invalid_request", "Neplatný požadavek nebo kódování obrázku.");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            return Error(413, "image_too_large", "Požadavek je příliš velký.");
        }
        catch (InvalidDataException)
        {
            return Error(415, "invalid_image", "Očekávám platný obrázek JPEG nebo PNG do 4096 × 4096 bodů.");
        }
        catch (AIProviderException ex)
        {
            return ex.StatusCode switch
            {
                503 => Error(503, "vision_unavailable", "Ollama nebo model robot-ai není dostupný."),
                504 => Error(504, "vision_timeout", "Analýza obrázku překročila časový limit."),
                _ => Error(502, "vision_failed", "Model obrázek nezpracoval. Ověřte podporu obrazu v robot-ai.")
            };
        }
        finally { context.End(); Slots.Release(); }
    }

    [HttpDelete("{session:guid}")]
    public IActionResult Clear(Guid session)
    {
        if (!MemoryAccess.IsLocal(HttpContext))
            return Error(403, "local_only", "Analýza obrazu je dostupná jen na počítači se serverem.");
        context.Remove(session);
        return NoContent();
    }

    private ObjectResult Error(int status, string code, string message) =>
        StatusCode(status, new { code, message });
}
