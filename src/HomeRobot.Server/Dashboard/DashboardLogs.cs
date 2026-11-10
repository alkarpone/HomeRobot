namespace HomeRobot.Server.Dashboard;

internal sealed class DashboardLogs(TimeProvider clock) : ILoggerProvider
{
    // Only exact, reviewed templates enter the dashboard. Values and exceptions are never formatted.
    private static readonly Dictionary<string, string> Messages = new()
    {
        ["Robot {RobotId} registered ({DeviceType})"] = "Robot registered.",
        ["Robot {RobotId} disconnected ({Reason})"] = "Robot disconnected.",
        ["Robot WebSocket opened ({ConnectionId})"] = "Robot connection opened.",
        ["Robot WebSocket closed ({ConnectionId})"] = "Robot connection closed.",
        ["Robot action plan {PlanId} failed"] = "Robot action plan failed.",
        ["Ollama model status check failed."] = "Ollama model status check failed.",
        ["{OllamaFailure}"] = "Ollama inference failed.",
        ["Whisper health check completed."] = "Whisper health check completed.",
        ["Whisper health check failed."] = "Whisper health check failed.",
        ["Piper sample synthesis failed."] = "Piper sample synthesis failed.",
        ["Piper sample synthesis completed."] = "Piper sample synthesis completed.",
        ["Dashboard settings saved."] = "Dashboard settings saved."
    };

    private readonly Queue<LogEntry> entries = new();
    private readonly TimeProvider time = clock;

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose() { }

    public IReadOnlyList<LogEntry> Read(string? query, LogLevel? minimum)
    {
        lock (entries)
            return entries.Reverse().Where(entry => (minimum is null || entry.Level >= minimum) &&
                (string.IsNullOrEmpty(query) || entry.Component.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private sealed class Sink(DashboardLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level is >= LogLevel.Information and < LogLevel.None &&
            category.StartsWith("HomeRobot.Server.", StringComparison.Ordinal);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || state is not IReadOnlyList<KeyValuePair<string, object?>> properties ||
                properties.LastOrDefault().Value is not string template || !Messages.TryGetValue(template, out var message))
                return;

            lock (owner.entries)
            {
                if (owner.entries.Count >= 250)
                    owner.entries.Dequeue();
                owner.entries.Enqueue(new(owner.time.GetUtcNow(), level, category, message));
            }
        }
    }

    internal sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Component, string Message)
    {
        public string Severity => Level.ToString();
    }
}
