import Foundation

struct CommandExecution {
    let id: String
    let name: String
    private(set) var phases: [CommandPhase]
    private(set) var message: String?

    init(id: String, name: String, error: String? = nil) {
        self.id = id
        self.name = name
        phases = error == nil ? [.accepted, .executing] : [.failed]
        message = error
    }

    var phase: CommandPhase { phases.last! }

    mutating func finish(_ phase: CommandPhase, message: String? = nil) {
        guard !self.phase.isTerminal, phase.isTerminal else { return }
        phases.append(phase)
        self.message = message
    }
}

extension RobotSimulationEngine {
    mutating func execute(id: String, payload: [String: Any]) -> CommandExecution {
        let name = payload["command"] as? String ?? "unknown"
        do {
            guard let command = CommandName(rawValue: name) else { throw SimulationError.unknownCommand }
            guard Set(payload.keys) == ["command", "parameters"],
                  let parameters = payload["parameters"] as? [String: Any] else {
                throw SimulationError.invalidParameters
            }
            let keys: Set<String>
            switch command {
            case .forward, .backward: keys = ["distanceCm", "speed"]
            case .left, .right: keys = ["angleDegrees", "speed"]
            case .expression: keys = ["expression"]
            case .stop: keys = []
            }
            guard Set(parameters.keys) == keys else { throw SimulationError.invalidParameters }
            let data = try JSONSerialization.data(withJSONObject: parameters)
            guard let decoded = try? JSONDecoder().decode(CommandParameters.self, from: data) else {
                throw SimulationError.invalidParameters
            }
            try start(command, parameters: decoded, id: id)
            var execution = CommandExecution(id: id, name: name)
            if command == .stop || command == .expression { execution.finish(.completed) }
            return execution
        } catch {
            return CommandExecution(id: id, name: name, error: error.localizedDescription)
        }
    }
}
