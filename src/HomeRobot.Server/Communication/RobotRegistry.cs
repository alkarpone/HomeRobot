using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Communication;

internal sealed class RobotSession(long connectedAt)
{
    public RobotRegistration? Registration { get; set; }
    public long LastActivity { get; set; } = connectedAt;
    public DateTimeOffset LastSeen { get; set; }
    public RobotTelemetry? Telemetry { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public DateTimeOffset? LastTelemetry { get; set; }
    public RobotCommandStatus? LastExecutedCommand { get; set; }
    public bool Closed { get; set; }
    public AudioPlaybackStatus? Audio { get; set; }
    public RobotPlanLease? Plan { get; set; }
    public Guid VoiceTurn { get; set; } = Guid.NewGuid();
    public (Guid Id, string Text, long IssuedAt)? VoiceInstruction { get; set; }
    public Dictionary<string, TrackedCommand> Commands { get; } = new(StringComparer.Ordinal);
    public LinkedList<OutboundMessage> Outbound { get; } = new();
    public Channel<bool> Ready { get; } = Channel.CreateBounded<bool>(1);
}

internal sealed record OutboundMessage(object Body, TrackedCommand? Command = null, bool RegistrationAck = false);

internal sealed class TrackedCommand(RobotCommand command, RobotCommandStatus status, long startedAt)
{
    public RobotCommand Command { get; } = command;
    public RobotCommandStatus Status { get; set; } = status;
    public long StartedAt { get; } = startedAt;
    public bool Dispatched { get; set; }
}

internal sealed class RobotPlanLease
{
    // Accessed under the registry gate.
    public string? CancellationReason { get; set; }
    public TrackedCommand? ActiveCommand { get; set; }
    public TaskCompletionSource<RobotCommandStatus>? Completion { get; set; }
}

public sealed class RobotRegistry(TimeProvider clock, IOptions<RobotProtocolOptions> configuration,
    ILogger<RobotRegistry> logger)
{
    private const int QueueLimit = 64;
    private const int HistoryLimit = 256;
    private readonly object gate = new();
    private readonly Dictionary<string, RobotSession> robots = new(StringComparer.Ordinal);
    private readonly Dictionary<(string RobotId, string CommandId), RobotCommandStatus> history = new();
    private readonly Queue<(string RobotId, string CommandId)> historyOrder = new();
    private readonly RobotProtocolOptions options = configuration.Value;

    internal RobotSession Connect() => new(clock.GetTimestamp());

    internal object Message<T>(string type, T payload) where T : notnull =>
        new RobotMessage<T>(RobotProtocol.NewId(), type, clock.GetUtcNow(), payload);

    internal string? Register(RobotSession session, RobotRegistration registration)
    {
        lock (gate)
        {
            if (!CheckDeadlines(session))
                return "connection_closed";
            if (session.Registration is not null)
                return "already_registered";
            if (robots.ContainsKey(registration.RobotId))
                return "duplicate_robot_id";

            var ack = Message("register_ack", new RobotRegistrationAck(registration.RobotId,
                RobotProtocol.Version, options.HeartbeatIntervalSeconds, options.InactivityTimeoutSeconds));
            if (!Enqueue(session, new(ack, RegistrationAck: true)))
                return "connection_busy";

            session.Registration = registration;
            Touch(session);
            robots.Add(registration.RobotId, session);
            logger.LogInformation("Robot {RobotId} registered ({DeviceType})", registration.RobotId, registration.DeviceType);
            return null;
        }
    }

    internal string? UpdateDevice(RobotSession session, string robotId, RobotTelemetry? telemetry = null)
    {
        lock (gate)
        {
            if (session.Closed || session.Registration is null)
                return "registration_required";
            if (!CheckDeadlines(session))
                return "connection_closed";
            if (session.Registration.RobotId != robotId)
                return "robot_id_mismatch";

            Touch(session);
            if (telemetry is null)
                session.LastHeartbeat = session.LastSeen;
            else
            {
                session.Telemetry = telemetry;
                session.LastTelemetry = session.LastSeen;
            }
            return null;
        }
    }

    public IReadOnlyList<RobotDetails> List()
    {
        lock (gate)
            return robots.Values.Select(Details).ToArray();
    }

    public RobotDetails? Find(string robotId)
    {
        lock (gate)
            return robots.TryGetValue(robotId, out var session) ? Details(session) : null;
    }

    public RobotCommandStatus? FindCommand(string robotId, string commandId)
    {
        lock (gate)
        {
            if (robots.TryGetValue(robotId, out var session) && session.Commands.TryGetValue(commandId, out var command))
                return command.Status;
            return history.GetValueOrDefault((robotId, commandId));
        }
    }

    public IReadOnlyList<RobotCommandStatus> CommandHistory(string robotId)
    {
        lock (gate)
        {
            var active = robots.TryGetValue(robotId, out var session)
                ? session.Commands.Values.Select(command => command.Status) : [];
            return active.Concat(history.Where(item => item.Key.RobotId == robotId).Select(item => item.Value))
                .OrderByDescending(command => command.CreatedAt).ToArray();
        }
    }

    internal Guid? GetVoiceTurn(string robotId)
    {
        lock (gate)
            return robots.TryGetValue(robotId, out var session) && CheckDeadlines(session) &&
                !session.Commands.Values.Any(item => item.Command.IsStop) ? session.VoiceTurn : null;
    }

    internal bool TryVoiceStop(string robotId, Guid turn)
    {
        lock (gate)
            return GetVoiceTurn(robotId) == turn &&
                Submit(robotId, new RobotCommand("stop", new EmptyParameters())).Command is not null;
    }

    internal Guid? AuthorizeVoiceInstruction(string robotId, Guid turn, string text)
    {
        lock (gate)
        {
            if (GetVoiceTurn(robotId) != turn)
                return null;
            var id = Guid.NewGuid();
            robots[robotId].VoiceInstruction = (id, text, clock.GetTimestamp());
            return id;
        }
    }

    internal (RobotPlanLease? Lease, string? Error) ReservePlan(
        string robotId, Guid? voiceTurn = null, string? voiceText = null)
    {
        lock (gate)
        {
            if (!robots.TryGetValue(robotId, out var session) || !CheckDeadlines(session))
                return (null, "robot_not_connected");
            if (session.Plan is not null || session.Commands.Values.Any(item => item.Command.IsStop))
                return (null, "plan_busy");

            if (voiceTurn is { } expected)
            {
                if (session.VoiceInstruction is not { } instruction || instruction.Id != expected ||
                    instruction.Text != voiceText || clock.GetElapsedTime(instruction.IssuedAt) > TimeSpan.FromSeconds(15))
                    return (null, "voice_turn_expired");
                session.VoiceInstruction = null;
            }

            session.Plan = new RobotPlanLease();
            return (session.Plan, null);
        }
    }

    internal string? ReleasePlan(string robotId, RobotPlanLease lease)
    {
        lock (gate)
        {
            if (robots.TryGetValue(robotId, out var session) && session.Plan == lease)
                session.Plan = null;
            return lease.CancellationReason;
        }
    }

    internal void StopPlan(string robotId, RobotPlanLease lease)
    {
        lock (gate)
        {
            if (robots.TryGetValue(robotId, out var session) && session.Plan == lease &&
                lease.ActiveCommand is not null)
                Submit(robotId, new RobotCommand("stop", new EmptyParameters()));
        }
    }

    internal (RobotCommandStatus? Command, Task<RobotCommandStatus>? Completion, string? Error) SubmitPlanAction(
        string robotId, RobotPlanLease lease, RobotCommand command)
    {
        lock (gate)
        {
            if (lease.CancellationReason is { } reason)
                return (null, null, reason);

            var result = Submit(robotId, command, lease);
            return (result.Command, lease.Completion?.Task, result.Error);
        }
    }

    internal (int HttpStatus, RobotCommandStatus? Command, string? Error) Submit(
        string robotId, RobotCommand command, RobotPlanLease? lease = null)
    {
        lock (gate)
        {
            if (!robots.TryGetValue(robotId, out var session) || !CheckDeadlines(session))
                return (404, null, "robot_not_connected");

            if (command.Command.StartsWith("audio_", StringComparison.Ordinal) &&
                (session.Registration?.DeviceType != "esp32" ||
                 !new[] { "audio_output", "tts_playback", "audio_stop" }.All(session.Registration.Capabilities.Contains)))
                return (400, null, "audio_unsupported");
            if (command.Command == "audio_play" && session.Commands.Values.Any(item => item.Command.Command == "audio_play"))
                return (409, null, "audio_busy");
            if (command.Parameters is AudioStopParameters target)
            {
                var playback = session.Commands.Values.FirstOrDefault(item =>
                    item.Command.Parameters is AudioPlayParameters audio && audio.PlaybackId == target.PlaybackId);
                if (playback is null) return (409, null, "playback_not_active");
                var pendingStop = session.Commands.Values.FirstOrDefault(item =>
                    item.Command.Parameters is AudioStopParameters audio && audio.PlaybackId == target.PlaybackId);
                if (pendingStop is not null) return (202, pendingStop.Status, null);
                if (!playback.Dispatched)
                {
                    // Never send a priority stop ahead of a play command that has not left the queue.
                    Finish(session, playback, "cancelled", "audio_stopped");
                    return (202, playback.Status, null);
                }
            }

            var stop = session.Commands.Values.FirstOrDefault(item => item.Command.IsStop);
            if (command.IsStop && lease is null)
            {
                session.VoiceTurn = Guid.NewGuid();
                session.VoiceInstruction = null;
                CancelPlan(session, "emergency_stop");
            }
            if (command.IsStop && stop is not null)
                return (202, stop.Status, null);

            if (command.IsMovement && (stop is not null || session.Commands.Values.Any(item => item.Command.IsMovement)))
                return (409, null, "movement_busy");

            if (!command.IsStop && (session.Commands.Count >= QueueLimit || session.Outbound.Count >= QueueLimit))
                return (429, null, "robot_busy");

            if (command.IsStop)
            {
                foreach (var movement in session.Commands.Values.Where(item => item.Command.IsMovement).ToArray())
                    Finish(session, movement, "cancelled", "emergency_stop");
            }

            var now = clock.GetUtcNow();
            var status = new RobotCommandStatus(RobotProtocol.NewId(), command.Command, "pending", now, now, null);
            var tracked = new TrackedCommand(command, status, clock.GetTimestamp());
            session.Commands.Add(status.CommandId, tracked);
            UpdateAudio(session, tracked);
            if (lease is not null)
            {
                lease.ActiveCommand = tracked;
                lease.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var message = new OutboundMessage(new RobotMessage<RobotCommand>(status.CommandId, "command", now, command), tracked);
            if (command.IsStop || command.Command == "audio_stop")
            {
                // Registration must reach the device before its first command.
                if (session.Outbound.First is { Value.RegistrationAck: true } registration)
                    session.Outbound.AddAfter(registration, message);
                else
                    session.Outbound.AddFirst(message);
                session.Ready.Writer.TryWrite(true);
            }
            else
            {
                Enqueue(session, message);
            }

            return (202, status, null);
        }
    }

    internal string? Transition(RobotSession session, string id, string state, bool result, string? message)
    {
        lock (gate)
        {
            if (session.Closed || session.Registration is null)
                return "registration_required";
            if (!CheckDeadlines(session))
                return "connection_closed";
            if (!session.Commands.TryGetValue(id, out var command))
                return history.ContainsKey((session.Registration.RobotId, id))
                    ? "invalid_command_transition" : "unknown_command";

            var current = command.Status.Status;
            if (!command.Dispatched)
                return "invalid_command_transition";
            if (result)
            {
                var valid = state switch
                {
                    "completed" => current is "accepted" or "executing",
                    "failed" or "cancelled" => true,
                    _ => false
                };
                if (!valid)
                    return "invalid_command_transition";

                Finish(session, command, state, message);
                if (command.Command.IsStop && state != "completed")
                    Disconnect(session, "stop_unconfirmed");
            }
            else
            {
                var valid = (current, state) is ("pending", "accepted") or ("accepted", "executing");
                if (!valid)
                    return "invalid_command_transition";
                command.Status = command.Status with { Status = state, UpdatedAt = clock.GetUtcNow() };
                UpdateAudio(session, command);
            }

            return null;
        }
    }

    internal bool TryQueue(RobotSession session, object message)
    {
        lock (gate)
            return !session.Closed && Enqueue(session, new(message));
    }

    internal object? TakeMessage(RobotSession session)
    {
        lock (gate)
        {
            if (!CheckDeadlines(session))
                return null;

            while (session.Outbound.First is { } first)
            {
                session.Outbound.RemoveFirst();
                var command = first.Value.Command;
                if (command is not null)
                {
                    if (!session.Commands.ContainsKey(command.Status.CommandId))
                        continue;
                    command.Dispatched = true;
                }

                return first.Value.Body;
            }

            return null;
        }
    }

    internal bool CheckConnection(RobotSession session)
    {
        lock (gate)
            return CheckDeadlines(session);
    }

    internal void Remove(RobotSession session)
    {
        lock (gate)
            Disconnect(session, "disconnected");
    }

    private bool CheckDeadlines(RobotSession session)
    {
        if (session.Closed)
            return false;

        var now = clock.GetTimestamp();
        if (clock.GetElapsedTime(session.LastActivity, now).TotalSeconds >= options.InactivityTimeoutSeconds)
        {
            Disconnect(session, session.Registration is null ? "registration_timeout" : "heartbeat_timeout");
            return false;
        }

        foreach (var command in session.Commands.Values.ToArray())
        {
            if (clock.GetElapsedTime(command.StartedAt, now).TotalSeconds < (command.Command.Command.StartsWith("audio_", StringComparison.Ordinal) ? 45 : options.CommandTimeoutSeconds))
                continue;

            Finish(session, command, "failed", "command_timeout");
            if (command.Command.IsStop || (command.Command.IsMovement && command.Dispatched))
            {
                Disconnect(session, "command_timeout");
                return false;
            }
        }

        return true;
    }

    private void Disconnect(RobotSession session, string reason)
    {
        if (session.Closed)
            return;

        session.Closed = true;
        session.Outbound.Clear();
        session.Ready.Writer.TryComplete();
        if (session.Registration is not { } registration)
            return;

        robots.Remove(registration.RobotId);
        CancelPlan(session, reason);
        foreach (var command in session.Commands.Values.ToArray())
            Finish(session, command, "cancelled", reason);
        logger.LogInformation("Robot {RobotId} disconnected ({Reason})", registration.RobotId, reason);
    }

    private void CancelPlan(RobotSession session, string reason)
    {
        if (session.Plan is not { } lease)
            return;

        lease.CancellationReason ??= reason;
        if (lease.ActiveCommand is { } active && !active.Command.IsStop)
            Finish(session, active, "cancelled", reason);
    }

    private void Finish(RobotSession session, TrackedCommand command, string state, string? message)
    {
        var id = command.Status.CommandId;
        session.Commands.Remove(id);
        var key = (session.Registration!.RobotId, id);
        var status = command.Status with { Status = state, UpdatedAt = clock.GetUtcNow(), Message = message };
        command.Status = status;
        UpdateAudio(session, command);
        history.Add(key, status);
        if (state == "completed")
            session.LastExecutedCommand = status;
        if (session.Plan is { } lease && lease.ActiveCommand == command)
        {
            lease.ActiveCommand = null;
            lease.Completion!.SetResult(status);
        }
        historyOrder.Enqueue(key);
        if (historyOrder.Count > HistoryLimit)
            history.Remove(historyOrder.Dequeue());
    }

    private void Touch(RobotSession session)
    {
        session.LastActivity = clock.GetTimestamp();
        session.LastSeen = clock.GetUtcNow();
    }

    private static void UpdateAudio(RobotSession session, TrackedCommand command)
    {
        if (command.Command.Parameters is not AudioPlayParameters audio) return;
        var state = command.Status.Status switch
        {
            "pending" or "accepted" => "buffering",
            "executing" => "playing",
            "failed" => "error",
            "cancelled" => "idle",
            _ => command.Status.Status
        };
        session.Audio = new(audio.PlaybackId, state, state == "error" ? command.Status.Message : null);
    }

    private static bool Enqueue(RobotSession session, OutboundMessage message)
    {
        if (session.Outbound.Count >= QueueLimit)
            return false;

        session.Outbound.AddLast(message);
        session.Ready.Writer.TryWrite(true);
        return true;
    }

    private static RobotDetails Details(RobotSession session)
    {
        var registration = session.Registration!;
        return new(registration.RobotId, registration.Name, registration.DeviceType, registration.ProtocolVersion,
            registration.Capabilities, session.LastSeen, "connected", session.Telemetry)
        {
            LastHeartbeat = session.LastHeartbeat,
            LastTelemetry = session.LastTelemetry,
            LastExecutedCommand = session.LastExecutedCommand,
            Audio = session.Audio
        };
    }
}
