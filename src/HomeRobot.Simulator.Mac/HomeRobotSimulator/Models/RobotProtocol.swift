import Foundation

enum RobotProtocol {
    static let version = 1

    static func isIdentifier(_ value: String) -> Bool {
        (1...64).contains(value.utf8.count) && value.utf8.allSatisfy {
            (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0)
                || [45, 46, 95].contains($0)
        }
    }

    static func encode(_ type: String, _ payload: some Encodable) throws -> String {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return String(decoding: try encoder.encode(Envelope(
            id: UUID().uuidString, type: type, timestamp: Date(), payload: payload
        )), as: UTF8.self)
    }
}

struct Envelope<Payload> {
    let id: String
    let type: String
    let timestamp: Date
    let payload: Payload
}

extension Envelope: Encodable where Payload: Encodable {}

struct IncomingMessage: Decodable {
    let id: String?
    let type: String
    let timestamp: String?
    let code: String?
    let payload: Payload?

    struct Payload: Decodable {
        let robotId: String?
        let protocolVersion: Int?
        let heartbeatIntervalSeconds: Int?
        let inactivityTimeoutSeconds: Int?
        let code: String?
        let replyTo: String?
    }

    var hasValidEnvelope: Bool {
        guard let id, let timestamp, payload != nil, RobotProtocol.isIdentifier(id),
              timestamp.hasSuffix("Z") || timestamp.hasSuffix("+00:00") else { return false }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if formatter.date(from: timestamp) != nil { return true }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: timestamp) != nil
    }
}

struct Registration: Encodable {
    let robotId: String
    let name = "HomeRobot Simulator"
    let deviceType = "simulator"
    let protocolVersion = RobotProtocol.version
    let capabilities = ["movement", "display"]
}

struct Heartbeat: Encodable {
    let robotId: String
}

struct CommandStatus: Codable, Equatable {
    let commandId: String
    let status: CommandPhase
    let message: String?

    init(commandId: String, status: CommandPhase, message: String? = nil) {
        self.commandId = commandId
        self.status = status
        self.message = message
    }
}

enum CommandPhase: String, Codable {
    case pending, accepted, executing, completed, failed, cancelled

    var isTerminal: Bool {
        self == .completed || self == .failed || self == .cancelled
    }
}

struct CommandResult: Encodable {
    let commandId: String
    let status: CommandPhase
    let message: String?
}

enum CommandName: String, Codable {
    case forward = "move_forward"
    case backward = "move_backward"
    case left = "rotate_left"
    case right = "rotate_right"
    case expression = "set_expression"
    case stop
}

struct CommandParameters: Codable {
    var distanceCm: Double?
    var angleDegrees: Double?
    var speed: Double?
    var expression: String?

    init(distanceCm: Double? = nil, angleDegrees: Double? = nil,
         speed: Double? = nil, expression: String? = nil) {
        self.distanceCm = distanceCm
        self.angleDegrees = angleDegrees
        self.speed = speed
        self.expression = expression
    }
}

struct CommandRequest: Encodable {
    let command: CommandName
    let parameters: CommandParameters
}

struct RobotPosition: Encodable {
    var x = 0.0
    var y = 0.0
    var heading = 0.0
}

struct RobotMovement: Encodable {
    var speed = 0.0
    var leftWheel = 0.0
    var rightWheel = 0.0
}

struct Telemetry: Encodable {
    let robotId: String
    let state: String
    let position: RobotPosition
    let movement: RobotMovement
    let batteryPercent = 100
}
