using System.Security.Cryptography;
using HomeRobot.Server.Communication;

namespace HomeRobot.Server.Speech;

// Resources are scoped to a single command, bounded in RAM and never persisted.
public sealed class AudioPlaybackService(RobotRegistry robots, TimeProvider clock) : BackgroundService
{
    private sealed record Clip(string RobotId, string CommandId, string Token, byte[] Pcm, DateTimeOffset Expires);
    private readonly object gate = new();
    private readonly Dictionary<string, Clip> clips = new();
    private readonly Dictionary<(string RobotId, string PlaybackId), DateTimeOffset> cancelled = new();

    public (string? PlaybackId, string? Error) Play(string robotId, byte[] wav, string? playbackId = null)
    {
        var pcm = Esp32SpeakerClient.ToPcm(wav);
        lock (gate)
        {
            Prune();
            if (clips.Count >= 16 || clips.Values.Sum(clip => clip.Pcm.Length) + pcm.Length > 16 * 960000)
                return (null, "audio_capacity");
            var id = playbackId ?? Guid.NewGuid().ToString("N");
            if (cancelled.ContainsKey((robotId, id))) return (null, "playback_cancelled");
            if (!RobotProtocol.IsIdentifier(id) || clips.ContainsKey(id)) return (null, "invalid_playback_id");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var command = new RobotCommand("audio_play", new AudioPlayParameters(id,
                $"/api/audio/resources/{id}", token, 16000, 1, 16, pcm.Length));
            var submitted = robots.Submit(robotId, command);
            if (submitted.Command is null)
                return (null, submitted.Error);
            clips[id] = new(robotId, submitted.Command.CommandId, token, pcm, clock.GetUtcNow().AddSeconds(45));
            return (id, null);
        }
    }

    internal byte[]? Read(string id, string token)
    {
        lock (gate)
        {
            Prune();
            return clips.TryGetValue(id, out var clip) && token.Length == clip.Token.Length &&
                CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token),
                    System.Text.Encoding.ASCII.GetBytes(clip.Token)) ? clip.Pcm : null;
        }
    }

    public (int Status, RobotCommandStatus? Command, string? Error) Stop(string robotId, string playbackId)
    {
        lock (gate)
        {
            Prune();
            var result = robots.Submit(robotId, new RobotCommand("audio_stop", new AudioStopParameters(playbackId)));
            if (result.Error != "playback_not_active") return result;
            // A client can cancel while Piper is still synthesizing. Never dispatch that late result.
            if (cancelled.Count >= 256) return (429, null, "audio_capacity");
            cancelled[(robotId, playbackId)] = clock.GetUtcNow().AddSeconds(90);
            return (202, null, null);
        }
    }

    private void Prune()
    {
        foreach (var (key, expires) in cancelled.ToArray())
            if (clock.GetUtcNow() >= expires) cancelled.Remove(key);
        foreach (var (id, clip) in clips.ToArray())
            if (clock.GetUtcNow() >= clip.Expires || robots.FindCommand(clip.RobotId, clip.CommandId)?.Status
                is null or "completed" or "failed" or "cancelled")
                clips.Remove(id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            lock (gate) Prune();
    }
}
