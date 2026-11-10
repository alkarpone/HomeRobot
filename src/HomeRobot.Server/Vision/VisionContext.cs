namespace HomeRobot.Server.Vision;

public sealed class VisionContext : IDisposable
{
    internal TimeSpan Lifetime { get; }
    private Guid? pending;
    private readonly Dictionary<Guid, Entry> images = new();
    internal sealed record Entry(byte[] Jpeg, long Created, TimeSpan InitialAge, DateTimeOffset CapturedAt, string Question, string Answer);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly ITimer timer;

    public VisionContext(TimeProvider clock, IConfiguration configuration)
    {
        this.clock = clock;
        Lifetime = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Vision:ContextLifetimeSeconds", 120), 1, 600));
        var interval = TimeSpan.FromSeconds(15);
        timer = clock.CreateTimer(_ => { lock (gate) RemoveExpired(); }, null, interval, interval);
    }

    internal Entry? Get(Guid session)
    {
        lock (gate)
        {
            RemoveExpired();
            return images.GetValueOrDefault(session);
        }
    }

    // The Vision endpoint permits only one analysis at a time.
    internal void Begin(Guid session)
    {
        lock (gate) pending = session;
    }

    internal void End()
    {
        lock (gate) pending = null;
    }

    internal void Set(Guid session, byte[] jpeg, long received, DateTimeOffset capturedAt, TimeSpan initialAge, string question,
        string answer, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (pending != session) return;
            images.Remove(session);
            if (initialAge + clock.GetElapsedTime(received) >= Lifetime) return;
            RemoveExpired();
            if (images.Count >= 8)
                images.Remove(images.MinBy(pair => pair.Value.CapturedAt).Key);
            images[session] = new(jpeg, received, initialAge, capturedAt, question, answer);
        }
    }

    internal void Remove(Guid session)
    {
        lock (gate)
        {
            if (pending == session) pending = null;
            images.Remove(session);
        }
    }

    private void RemoveExpired()
    {
        foreach (var pair in images)
            if (pair.Value.InitialAge + clock.GetElapsedTime(pair.Value.Created) >= Lifetime)
                images.Remove(pair.Key);
    }

    public void Dispose() => timer.Dispose();
}
