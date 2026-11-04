using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeRobot.Server.Communication;

public sealed class RobotProtocolOptions
{
    public int MaxMessageBytes { get; set; } = 4096;
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int InactivityTimeoutSeconds { get; set; } = 15;
    public int CommandTimeoutSeconds { get; set; } = 10;
    public double MaxDistanceCm { get; set; } = 100;
    public double MaxSpeed { get; set; } = 50;
    public double MaxRotationDegrees { get; set; } = 180;

    internal bool IsValid() =>
        MaxMessageBytes is >= 512 and <= 65536 &&
        HeartbeatIntervalSeconds > 0 && InactivityTimeoutSeconds > HeartbeatIntervalSeconds &&
        CommandTimeoutSeconds > 0 &&
        double.IsFinite(MaxDistanceCm) && MaxDistanceCm > 0 &&
        MaxSpeed is > 0 and <= 100 && MaxRotationDegrees is > 0 and <= 360;
}

public sealed record RobotRegistration(
    string RobotId, string Name, string DeviceType, int ProtocolVersion, string[] Capabilities);

public sealed record RobotTelemetry(
    string RobotId, string State, RobotPosition Position, RobotMovement Movement, double BatteryPercent);

public sealed record RobotPosition(double X, double Y, double Heading);
public sealed record RobotMovement(double Speed, double LeftWheel, double RightWheel);

public sealed record RobotDetails(
    string RobotId, string Name, string DeviceType, int ProtocolVersion, string[] Capabilities,
    DateTimeOffset LastSeen, string Status, RobotTelemetry? Telemetry);

public sealed record RobotCommandStatus(
    string CommandId, string Command, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? Message);

internal sealed record RobotMessage<T>(string Id, string Type, DateTimeOffset Timestamp, T Payload);
internal sealed record RobotHeartbeat(string RobotId);
internal sealed record RobotRegistrationAck(string RobotId, int ProtocolVersion, int HeartbeatIntervalSeconds,
    int InactivityTimeoutSeconds);
internal sealed record RobotError(string Code, string? ReplyTo);
internal sealed record RobotCommandRequest(string Command, JsonElement Parameters);
internal sealed record RobotCommandAck(string CommandId, string Status);
internal sealed record RobotCommandResult(
    string CommandId, string Status,
    string? Message = null);

internal sealed record MoveParameters(double DistanceCm, double Speed);
internal sealed record RotateParameters(double AngleDegrees, double Speed);
internal sealed record ExpressionParameters(string Expression);
internal sealed record EmptyParameters;

internal sealed record RobotCommand(string Command, object Parameters)
{
    internal bool IsMovement => Command is "move_forward" or "move_backward" or "rotate_left" or "rotate_right";
    internal bool IsStop => Command == "stop";

    public static RobotCommand? Parse(JsonElement body, RobotProtocolOptions limits)
    {
        try
        {
            var request = body.Deserialize<RobotCommandRequest>(RobotProtocol.Json);
            if (request?.Parameters.ValueKind != JsonValueKind.Object)
                return null;

            var parameters = request.Parameters;
            return request.Command switch
            {
                "move_forward" or "move_backward"
                    when parameters.Deserialize<MoveParameters>(RobotProtocol.Json) is { } move &&
                         move.DistanceCm > 0 && move.DistanceCm <= limits.MaxDistanceCm &&
                         move.Speed > 0 && move.Speed <= limits.MaxSpeed => new(request.Command, move),
                "rotate_left" or "rotate_right"
                    when parameters.Deserialize<RotateParameters>(RobotProtocol.Json) is { } rotate &&
                         rotate.AngleDegrees > 0 && rotate.AngleDegrees <= limits.MaxRotationDegrees &&
                         rotate.Speed > 0 && rotate.Speed <= limits.MaxSpeed => new(request.Command, rotate),
                "set_expression"
                    when parameters.Deserialize<ExpressionParameters>(RobotProtocol.Json) is { } expression &&
                         RobotProtocol.IsIdentifier(expression.Expression) => new(request.Command, expression),
                "stop" when parameters.Deserialize<EmptyParameters>(RobotProtocol.Json) is { } stop =>
                    new(request.Command, stop),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class RobotProtocol
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static bool IsValid(RobotRegistration registration) =>
        IsIdentifier(registration.RobotId) &&
        !string.IsNullOrWhiteSpace(registration.Name) && registration.Name.Length <= 100 &&
        registration.DeviceType is "esp32" or "simulator" &&
        registration.Capabilities.Length <= 32 && registration.Capabilities.All(IsIdentifier) &&
        registration.Capabilities.Distinct(StringComparer.Ordinal).Count() == registration.Capabilities.Length;

    public static bool IsValid(RobotTelemetry telemetry) =>
        IsIdentifier(telemetry.State) &&
        double.IsFinite(telemetry.Position.X) && double.IsFinite(telemetry.Position.Y) &&
        telemetry.Position.Heading is >= 0 and < 360 &&
        telemetry.Movement.Speed is >= 0 and <= 100 &&
        telemetry.Movement.LeftWheel is >= -100 and <= 100 &&
        telemetry.Movement.RightWheel is >= -100 and <= 100 &&
        telemetry.BatteryPercent is >= 0 and <= 100;
}
