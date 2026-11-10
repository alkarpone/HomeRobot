using HomeRobot.Server.Memory;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.AI;

public sealed class ConversationStore(TimeProvider clock, IOptions<ConversationOptions> options,
    ConversationPrompt prompt, MemoryCommands commands, IMemoryRetrieval retrieval)
{
    private const int SessionLimit = 256;
    private const int HistoryCharacters = 6000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    private readonly Dictionary<Guid, Session> sessions = new();

    public async Task<(Guid SessionId, string Response, string Expression)?> ReplyAsync(
        Guid? sessionId, ChatMessage message, IAIProvider provider, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? beforeCommit = null, bool briefCzech = false,
        bool localMemory = false, bool clearSpeech = true)
    {
        Session session;
        Guid id;
        lock (sessions)
        {
            var now = clock.GetUtcNow();
            foreach (var (key, value) in sessions)
            {
                if (value.ActiveRequests == 0 && now - value.LastActivity >= SessionLifetime)
                    sessions.Remove(key);
            }

            if (sessionId is { } existingId && sessions.TryGetValue(existingId, out var existing) &&
                existing.LocalMemory == localMemory)
            {
                id = existingId;
                session = existing;
            }
            else
            {
                if (sessions.Count >= SessionLimit)
                    return null;

                id = Guid.NewGuid();
                session = new Session { LocalMemory = localMemory };
                sessions.Add(id, session);
            }

            session.ActiveRequests++;
        }

        try
        {
            await session.Gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = prompt.Profile;
                var response = commands.Handle(message.Content, id, localMemory, clearSpeech, ref session.PendingDeletion);
                if (response is null && session.History.Count == 0)
                    response = commands.Recall(message.Content, localMemory);
                if (response is not null)
                {
                    session.HasReply = true;
                    if (beforeCommit is not null)
                        await beforeCommit(response, cancellationToken);
                    return (id, response, profile.DefaultExpression);
                }

                var relevant = localMemory ? retrieval.Retrieve(message.Content) : [];
                var messages = prompt.Compose(profile, session.History, message, relevant, briefCzech);
                var (reply, expression) = ConversationPrompt.ReadReply(
                    await provider.GenerateAsync(messages, cancellationToken), profile.DefaultExpression);
                if (beforeCommit is not null)
                    await beforeCommit(reply, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                session.HasReply = true;
                session.History.Add(message);
                session.History.Add(new("assistant", reply));
                while (session.History.Count > options.Value.MaxHistoryMessages ||
                    session.History.Sum(item => item.Content.Length) > HistoryCharacters)
                    session.History.RemoveRange(0, 2);

                return (id, reply, expression);
            }
            finally
            {
                session.Gate.Release();
            }
        }
        finally
        {
            lock (sessions)
            {
                session.LastActivity = clock.GetUtcNow();
                session.ActiveRequests--;
                if (session.ActiveRequests == 0 && !session.HasReply)
                    sessions.Remove(id);
            }
        }
    }

    internal async Task<ChatMessage[]> RecentLocalTurnsAsync(Guid? id, CancellationToken token)
    {
        Session? session;
        lock (sessions)
        {
            if (id is null || !sessions.TryGetValue(id.Value, out session) || !session.LocalMemory ||
                (session.ActiveRequests == 0 && clock.GetUtcNow() - session.LastActivity >= SessionLifetime))
                return [];
        }

        await session.Gate.WaitAsync(token);
        try
        {
            return session.History.TakeLast(2)
                .Select(turn => new ChatMessage(turn.Role, turn.Content[..Math.Min(turn.Content.Length, 500)]))
                .ToArray();
        }
        finally { session.Gate.Release(); }
    }

    private sealed class Session
    {
        public SemaphoreSlim Gate { get; } = new(1);
        public List<ChatMessage> History { get; } = [];
        public DateTimeOffset LastActivity { get; set; }
        public MemoryCommands.PendingDeletion? PendingDeletion;
        public bool LocalMemory { get; init; }
        public bool HasReply { get; set; }
        public int ActiveRequests { get; set; }
    }
}
