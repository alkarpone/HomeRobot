namespace HomeRobot.Server.Communication;

internal static class FaceExpressions
{
    internal static readonly string[] Supported = ["idle", "listening", "thinking", "speaking", "happy", "sad", "error", "angry", "surprised", "eyes_closed", "blink"];
    internal static bool IsValid(string expression) => Supported.Contains(expression, StringComparer.Ordinal);
}

public sealed record RobotFaceState(string Expression, long Revision, string? LastCommand,
    DateTimeOffset? BlinkAt, string[] DisplayClients);

public sealed partial class RobotRegistry
{
    private readonly Dictionary<string, RobotFaceState> faces = new(StringComparer.Ordinal);

    public RobotFaceState GetFace(string robotId)
    {
        lock (gate)
        {
            var state = faces.GetValueOrDefault(robotId) ?? new("idle", 0, null, null, []);
            return state with { DisplayClients = robots.Values.Where(session =>
                session.Registration is { } registration && registration.Capabilities.Contains("face_sync") &&
                (registration.FaceRobotId ?? registration.RobotId) == robotId)
                .Select(session => session.Registration!.RobotId).ToArray() };
        }
    }

    internal bool SetFace(string robotId, string expression)
    {
        if (!RobotProtocol.IsIdentifier(robotId) || !FaceExpressions.IsValid(expression)) return false;
        lock (gate)
        {
            var previous = GetFace(robotId);
            if (expression != "blink" && previous.Expression == expression) return true;
            // Bound runtime state without evicting a robot's saved expression silently.
            if (!faces.ContainsKey(robotId) && faces.Count >= 256) return false;
            var state = previous with {
                Expression = expression == "blink" ? previous.Expression : expression,
                Revision = previous.Revision + 1, LastCommand = expression,
                BlinkAt = expression == "blink" ? clock.GetUtcNow() : null
            };
            faces[robotId] = state;
            foreach (var session in robots.Values.Where(session => session.Registration is { } registration &&
                registration.Capabilities.Contains("face_sync") && (registration.FaceRobotId ?? registration.RobotId) == robotId).ToArray())
            {
                // One coalescing slot preserves command/stop priority under queue pressure.
                session.PendingFace = state;
                session.Ready.Writer.TryWrite(true);
            }
            logger.LogInformation("Robot {RobotId} face changed to {Expression}", robotId, expression);
            return true;
        }
    }
}
