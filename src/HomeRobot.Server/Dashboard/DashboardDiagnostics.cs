using HomeRobot.Server.AI;

namespace HomeRobot.Server.Dashboard;

public sealed class DashboardDiagnostics(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly long started = clock.GetTimestamp();
    private readonly SemaphoreSlim ollamaProbe = new(1);
    private (OllamaStatus Status, long CheckedAt)? ollama;
    private DateTimeOffset? lastAiRequest;
    private long visionRequests;
    private VisionAnalysis? lastVisionAnalysis;
    private ServiceCheck? whisper;
    private ServiceCheck? piper;

    internal void RecordAiRequest()
    {
        lock (gate)
            lastAiRequest = clock.GetUtcNow();
    }

    internal void RecordVisionRequest()
    {
        lock (gate)
            visionRequests++;
    }

    internal void RecordVisionAnalysis(long inferenceMs)
    {
        lock (gate)
            lastVisionAnalysis = new(clock.GetUtcNow(), inferenceMs);
    }

    internal void RecordSpeech(bool isWhisper, string status, string? error = null)
    {
        lock (gate)
        {
            var check = new ServiceCheck(clock.GetUtcNow(), status, error);
            if (isWhisper)
                whisper = check;
            else
                piper = check;
        }
    }

    internal object Snapshot()
    {
        lock (gate)
            return new
            {
                uptimeSeconds = (long)clock.GetElapsedTime(started).TotalSeconds,
                lastAiRequest, visionRequests, lastVisionAnalysis, whisper, piper
            };
    }

    internal async Task<OllamaStatus> CheckOllamaAsync(OllamaProvider provider, bool refresh, CancellationToken token)
    {
        await ollamaProbe.WaitAsync(token);
        try
        {
            if (!refresh && ollama is { } cached && clock.GetElapsedTime(cached.CheckedAt) < TimeSpan.FromSeconds(15))
                return cached.Status;
            var status = await provider.GetStatusAsync(token);
            ollama = (status, clock.GetTimestamp());
            return status;
        }
        finally { ollamaProbe.Release(); }
    }

    internal sealed record VisionAnalysis(DateTimeOffset At, long InferenceMs);
    internal sealed record ServiceCheck(DateTimeOffset At, string Status, string? Error);
}
