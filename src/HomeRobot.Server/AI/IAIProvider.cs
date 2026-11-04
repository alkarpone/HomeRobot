namespace HomeRobot.Server.AI;

public interface IAIProvider
{
    Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}

public sealed record ChatMessage
{
    public ChatMessage(string role, string content)
    {
        if (role is not ("system" or "user" or "assistant"))
            throw new ArgumentException("Invalid chat role.", nameof(role));
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        Role = role;
        Content = content;
    }

    public string Role { get; }
    public string Content { get; }
}
