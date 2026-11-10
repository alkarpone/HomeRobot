import Foundation

enum RobotExpression: String, CaseIterable {
    case idle, listening, thinking, speaking, happy, sad, error, executing
}

enum SimulationError: LocalizedError {
    case invalidParameters, unsupportedExpression, movementInProgress, unknownCommand

    var errorDescription: String? {
        switch self {
        case .invalidParameters: "Invalid command parameters."
        case .unsupportedExpression: "Unsupported expression."
        case .movementInProgress: "Movement is already in progress."
        case .unknownCommand: "Unknown command."
        }
    }
}

struct RobotSimulationEngine {
    struct Configuration {
        var wheelbaseCm = 100 / Double.pi
        var maxWheelSpeedCmPerSecond = 100.0
        var maxDistanceCm = 100.0
        var maxRotationDegrees = 180.0
        var maxSpeedPercent = 50.0
    }

    struct Motion {
        let commandId: String
        let direction: CommandName
        var remaining: Double
    }

    let configuration: Configuration
    private(set) var position = RobotPosition()
    private(set) var movement = RobotMovement()
    private(set) var expression = RobotExpression.idle
    private(set) var motion: Motion?
    private(set) var leftWheelTravel = 0.0
    private(set) var rightWheelTravel = 0.0

    init(configuration: Configuration = .init()) {
        self.configuration = configuration
    }

    mutating func start(_ command: CommandName, parameters: CommandParameters, id: String) throws {
        let isMove = command == .forward || command == .backward
        switch command {
        case .stop:
            guard parameters.distanceCm == nil, parameters.angleDegrees == nil,
                  parameters.speed == nil, parameters.expression == nil else {
                throw SimulationError.invalidParameters
            }
            stop()
        case .expression:
            guard parameters.distanceCm == nil, parameters.angleDegrees == nil, parameters.speed == nil,
                  let value = parameters.expression, RobotProtocol.isIdentifier(value) else {
                throw SimulationError.invalidParameters
            }
            guard let expression = RobotExpression(rawValue: value), expression != .executing else {
                throw SimulationError.unsupportedExpression
            }
            self.expression = expression
        default:
            guard let amount = isMove ? parameters.distanceCm : parameters.angleDegrees,
                  amount > 0, amount <= (isMove ? configuration.maxDistanceCm : configuration.maxRotationDegrees),
                  (isMove ? parameters.angleDegrees : parameters.distanceCm) == nil,
                  let speed = parameters.speed, speed > 0, speed <= configuration.maxSpeedPercent,
                  parameters.expression == nil else { throw SimulationError.invalidParameters }
            guard motion == nil else { throw SimulationError.movementInProgress }
            motion = Motion(commandId: id, direction: command, remaining: amount)
            movement = RobotMovement(
                speed: speed,
                leftWheel: command == .backward || command == .left ? -speed : speed,
                rightWheel: command == .backward || command == .right ? -speed : speed
            )
        }
    }

    @discardableResult
    mutating func stop() -> String? {
        let id = motion?.commandId
        motion = nil
        movement = RobotMovement()
        return id
    }

    mutating func advance(seconds: Double) -> String? {
        guard var motion, seconds > 0 else { return nil }
        let linearSpeed = configuration.maxWheelSpeedCmPerSecond * movement.speed / 100
        let turning = motion.direction == .left || motion.direction == .right
        let degreesPerCm = 360 / (Double.pi * configuration.wheelbaseCm)
        let rate = turning ? linearSpeed * degreesPerCm : linearSpeed
        let amount = min(motion.remaining, rate * seconds)
        let wheelDistance = turning ? amount / degreesPerCm : amount

        leftWheelTravel += movement.leftWheel.sign == .minus ? -wheelDistance : wheelDistance
        rightWheelTravel += movement.rightWheel.sign == .minus ? -wheelDistance : wheelDistance
        if turning {
            let heading = position.heading + (motion.direction == .left ? -amount : amount)
            let wrapped = heading.truncatingRemainder(dividingBy: 360)
            position.heading = (wrapped + 360).truncatingRemainder(dividingBy: 360)
        } else {
            let heading = position.heading * .pi / 180
            let distance = motion.direction == .backward ? -amount : amount
            position.x += sin(heading) * distance
            position.y += cos(heading) * distance
        }

        motion.remaining -= amount
        if motion.remaining == 0 { return stop() }
        self.motion = motion
        return nil
    }

    func telemetry(robotId: String) -> Telemetry {
        Telemetry(robotId: robotId, state: motion == nil ? "idle" : "moving",
                  position: position, movement: movement)
    }
}
