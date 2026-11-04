import Foundation
import Combine

@MainActor
final class RobotConnectionService: ObservableObject {
    enum ConnectionState: String {
        case disconnected = "Disconnected"
        case connecting = "Connecting"
        case connected = "Connected"
    }

    @Published var serverURL: String {
        didSet { UserDefaults.standard.set(serverURL, forKey: "serverURL") }
    }
    let robotId: String
    @Published private(set) var connectionState = ConnectionState.disconnected
    @Published private(set) var lastCommunication: Date?
    @Published private(set) var errorMessage: String?
    @Published private(set) var engine: RobotSimulationEngine
    @Published private(set) var lastCommand: CommandExecution?
    @Published private(set) var manualStatus: CommandStatus?
    @Published private(set) var movementRequested = false
    @Published private(set) var stopRequested = false

    private let session: URLSession
    private let clock = ContinuousClock()
    private var socket: URLSessionWebSocketTask?
    private var apiBase: URL?
    private var outgoing: AsyncStream<String>.Continuation?
    private var reader: Task<Void, Never>?
    private var writer: Task<Void, Never>?
    private var heartbeat: Task<Void, Never>?
    private var registrationTimeout: Task<Void, Never>?
    private var motionTask: Task<Void, Never>?
    private var manualMovement: Task<Void, Never>?
    private var manualStop: Task<Void, Never>?
    private var activeMovement: CommandExecution?
    private var manualRequestID: UUID?
    private var pendingPing: ContinuousClock.Instant?

    private let telemetryInterval: Duration

    init() {
        telemetryInterval = .seconds(1 / Self.setting("telemetryHz", default: 8, min: 1, max: 30))
        let defaults = UserDefaults.standard
        var simulation = RobotSimulationEngine.Configuration()
        simulation.wheelbaseCm = Self.setting("wheelbaseCm", default: simulation.wheelbaseCm, min: 0.01, max: 10_000)
        simulation.maxWheelSpeedCmPerSecond = Self.setting(
            "maxWheelSpeedCmPerSecond", default: simulation.maxWheelSpeedCmPerSecond, min: 0.01, max: 10_000
        )
        simulation.maxDistanceCm = Self.setting("maxDistanceCm", default: simulation.maxDistanceCm)
        simulation.maxRotationDegrees = Self.setting("maxRotationDegrees", default: simulation.maxRotationDegrees, max: 360)
        simulation.maxSpeedPercent = Self.setting("maxSpeedPercent", default: simulation.maxSpeedPercent, max: 100)
        engine = RobotSimulationEngine(configuration: simulation)
        serverURL = defaults.string(forKey: "serverURL") ?? "ws://localhost:5000/ws/robot"
        robotId = defaults.string(forKey: "simulatorRobotId") ?? "sim-\(UUID().uuidString.lowercased())"
        defaults.set(robotId, forKey: "simulatorRobotId")
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        session = URLSession(configuration: configuration)
    }

    private static func setting(
        _ key: String, default fallback: Double, min: Double = .leastNonzeroMagnitude,
        max: Double = .greatestFiniteMagnitude
    ) -> Double {
        let value = UserDefaults.standard.double(forKey: key)
        return (min...max).contains(value) ? value : fallback
    }

    var canMove: Bool {
        connectionState == .connected && engine.motion == nil && !movementRequested && !stopRequested
    }

    func connect() {
        disconnect()
        errorMessage = nil
        lastCommunication = nil
        manualStatus = nil
        guard var components = URLComponents(string: serverURL.trimmingCharacters(in: .whitespacesAndNewlines)),
              let scheme = components.scheme?.lowercased(), scheme == "ws" || scheme == "wss",
              let host = components.host, !host.isEmpty,
              components.user == nil, components.password == nil, components.fragment == nil,
              let url = components.url else {
            errorMessage = "Enter a valid ws:// or wss:// server URL."
            return
        }
        components.scheme = scheme == "wss" ? "https" : "http"
        components.path = "/api/robots/\(robotId)"
        components.query = nil
        apiBase = components.url

        let socket = session.webSocketTask(with: url)
        socket.maximumMessageSize = 65_536
        self.socket = socket
        connectionState = .connecting
        let (messages, continuation) = AsyncStream<String>.makeStream(bufferingPolicy: .bufferingOldest(64))
        outgoing = continuation
        socket.resume()

        writer = Task { [weak self] in
            do {
                for await message in messages {
                    try Task.checkCancellation()
                    try await socket.send(.string(message))
                    guard let self, self.socket === socket else { return }
                    self.lastCommunication = Date()
                }
            } catch {
                self?.connectionFailed(socket, error.localizedDescription)
            }
        }
        reader = Task { [weak self] in
            do {
                while !Task.isCancelled {
                    let message = try await socket.receive()
                    guard let self, self.socket === socket else { return }
                    self.lastCommunication = Date()
                    switch message {
                    case .string(let text): self.receive(text)
                    default: self.errorMessage = "Unexpected WebSocket frame."
                    }
                }
            } catch {
                self?.connectionFailed(socket, error.localizedDescription)
            }
        }
        registrationTimeout = Task { [weak self] in
            try? await Task.sleep(for: .seconds(10))
            guard !Task.isCancelled else { return }
            self?.connectionFailed(socket, "Registration timed out.")
        }
        send("register", Registration(robotId: robotId))
    }

    func disconnect() {
        let previousSocket = socket
        socket = nil
        apiBase = nil
        connectionState = .disconnected
        outgoing?.finish()
        outgoing = nil
        [reader, writer, heartbeat, registrationTimeout, motionTask, manualMovement, manualStop].forEach {
            $0?.cancel()
        }
        previousSocket?.cancel(with: .goingAway, reason: nil)
        motionTask = nil
        pendingPing = nil
        movementRequested = false
        stopRequested = false
        cancelMotion(reason: "Connection closed.")
    }

    private func connectionFailed(_ socket: URLSessionWebSocketTask, _ message: String) {
        guard self.socket === socket else { return }
        disconnect()
        errorMessage = message
    }

    private func send(_ type: String, _ payload: some Encodable) {
        guard let socket, let outgoing else { return }
        do {
            let message = try RobotProtocol.encode(type, payload)
            if case .dropped = outgoing.yield(message) {
                connectionFailed(socket, "WebSocket send queue is full.")
            }
        } catch {
            connectionFailed(socket, error.localizedDescription)
        }
    }

    private func receive(_ text: String) {
        let data = Data(text.utf8)
        guard let message = try? JSONDecoder().decode(IncomingMessage.self, from: data) else {
            errorMessage = "Malformed server message."
            return
        }
        if message.type == "error",
           let code = message.hasValidEnvelope ? message.payload?.code : message.code {
            if connectionState == .connecting, let socket {
                connectionFailed(socket, "Registration failed: \(code).")
            } else {
                errorMessage = "Server error: \(code)."
            }
            return
        }
        guard message.hasValidEnvelope, let payload = message.payload, let id = message.id else {
            errorMessage = "Invalid protocol envelope."
            return
        }
        switch message.type {
        case "register_ack":
            guard connectionState == .connecting, let socket else { return }
            guard payload.protocolVersion == RobotProtocol.version else {
                connectionFailed(socket, "Unsupported server protocol version.")
                return
            }
            guard payload.robotId == robotId, let interval = payload.heartbeatIntervalSeconds,
                  let timeout = payload.inactivityTimeoutSeconds, interval > 0, timeout > interval else {
                connectionFailed(socket, "Invalid registration acknowledgement.")
                return
            }
            registrationTimeout?.cancel()
            connectionState = .connected
            sendTelemetry()
            heartbeat = Task { [weak self] in
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(interval))
                    guard !Task.isCancelled, let self, self.socket === socket else { return }
                    self.send("heartbeat", Heartbeat(robotId: self.robotId))
                    self.checkConnection(socket, timeout: .seconds(timeout))
                }
            }
        case "command":
            guard connectionState == .connected,
                  let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
                  Set(root.keys) == ["id", "type", "timestamp", "payload"],
                  let command = root["payload"] as? [String: Any] else {
                errorMessage = "Invalid command envelope."
                return
            }
            handleCommand(id: id, payload: command)
        default:
            errorMessage = "Unsupported server message: \(message.type)."
        }
    }

    private func checkConnection(_ socket: URLSessionWebSocketTask, timeout: Duration) {
        guard self.socket === socket else { return }
        if let pendingPing {
            if clock.now - pendingPing >= timeout {
                connectionFailed(socket, "WebSocket peer timed out.")
            }
            return
        }
        pendingPing = clock.now
        socket.sendPing { [weak self] error in
            Task { @MainActor in
                guard let self, self.socket === socket else { return }
                if let error {
                    self.connectionFailed(socket, error.localizedDescription)
                } else {
                    self.pendingPing = nil
                    self.lastCommunication = Date()
                }
            }
        }
    }

    private func handleCommand(id: String, payload: [String: Any]) {
        var candidate = engine
        let execution = candidate.execute(id: id, payload: payload)
        lastCommand = execution
        if execution.phase == .failed {
            errorMessage = execution.message
            report(execution)
            return
        }

        if execution.name == CommandName.stop.rawValue {
            // P4 already cancelled the active movement when it accepted the stop.
            cancelMotion(reason: "Emergency stop.")
        }
        engine = candidate
        report(execution)
        guard connectionState == .connected else { return }
        if engine.motion?.commandId == id {
            activeMovement = execution
            startMotion()
        }
        sendTelemetry()
    }

    private func report(_ execution: CommandExecution) {
        for phase in execution.phases {
            if phase.isTerminal {
                sendResult(execution)
            } else {
                send("command_ack", CommandStatus(commandId: execution.id, status: phase))
            }
        }
    }

    private func sendResult(_ execution: CommandExecution) {
        send("command_result", CommandResult(
            commandId: execution.id, status: execution.phase, message: execution.message
        ))
    }

    private func sendTelemetry() {
        send("telemetry", engine.telemetry(robotId: robotId))
    }

    private func cancelMotion(reason: String) {
        motionTask?.cancel()
        engine.stop()
        if var execution = activeMovement {
            execution.finish(.cancelled, message: reason)
            if lastCommand?.id == execution.id { lastCommand = execution }
        }
        activeMovement = nil
    }

    private func startMotion() {
        motionTask = Task { [weak self, clock, telemetryInterval] in
            var previous = clock.now
            var nextTelemetry = previous + telemetryInterval
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(16))
                guard !Task.isCancelled, let self else { return }
                let now = clock.now
                let elapsed = (now - previous).components
                previous = now
                let seconds = Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1e18
                if self.engine.advance(seconds: seconds) != nil {
                    if var execution = self.activeMovement {
                        execution.finish(.completed)
                        self.activeMovement = nil
                        if self.lastCommand?.id == execution.id { self.lastCommand = execution }
                        self.sendResult(execution)
                    }
                    self.sendTelemetry()
                    return
                }
                if now >= nextTelemetry {
                    self.sendTelemetry()
                    nextTelemetry = now + telemetryInterval
                }
            }
        }
    }

    func submit(_ command: CommandName, parameters: CommandParameters = .init()) {
        let isStop = command == .stop
        guard let socket, let apiBase, isStop ? connectionState == .connected && !stopRequested : canMove else {
            return
        }
        errorMessage = nil
        manualStatus = nil
        let requestID = UUID()
        manualRequestID = requestID
        let url = apiBase.appendingPathComponent(isStop ? "stop" : "commands")
        if isStop { stopRequested = true } else { movementRequested = true }

        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                if self.socket === socket {
                    if isStop { self.stopRequested = false } else { self.movementRequested = false }
                }
            }
            do {
                var request = URLRequest(url: url, timeoutInterval: 10)
                request.httpMethod = "POST"
                if !isStop {
                    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                    request.httpBody = try JSONEncoder().encode(CommandRequest(command: command, parameters: parameters))
                }
                var status = try await self.fetchStatus(request)
                let commandURL = apiBase.appendingPathComponent("commands").appendingPathComponent(status.commandId)
                let deadline = self.clock.now + .seconds(30)
                while self.socket === socket && !Task.isCancelled {
                    if self.manualRequestID == requestID { self.manualStatus = status }
                    if status.status.isTerminal {
                        if status.status == .failed, self.manualRequestID == requestID {
                            self.errorMessage = status.message ?? "Server reported command failure."
                        }
                        return
                    }
                    if self.clock.now >= deadline {
                        if self.manualRequestID == requestID {
                            self.errorMessage = "Timed out waiting for server command status."
                        }
                        return
                    }
                    try await Task.sleep(for: .milliseconds(300))
                    status = try await self.fetchStatus(URLRequest(url: commandURL, timeoutInterval: 10))
                }
            } catch {
                if self.socket === socket, self.manualRequestID == requestID, !Task.isCancelled {
                    self.errorMessage = error.localizedDescription
                }
            }
        }
        if isStop { manualStop = task } else { manualMovement = task }
    }

    private func fetchStatus(_ request: URLRequest) async throws -> CommandStatus {
        let (data, response) = try await session.data(for: request)
        try Task.checkCancellation()
        lastCommunication = Date()
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(code) else {
            let detail = (try? JSONDecoder().decode([String: String].self, from: data))?["code"]
            throw RESTError(code: code, detail: detail)
        }
        return try JSONDecoder().decode(CommandStatus.self, from: data)
    }

    private struct RESTError: LocalizedError {
        let code: Int
        let detail: String?
        var errorDescription: String? { "Server rejected the request (HTTP \(code)): \(detail ?? "request failed")." }
    }
}
