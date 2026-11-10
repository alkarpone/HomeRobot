import Foundation
import XCTest
@testable import HomeRobotSimulator

final class SimulatorTests: XCTestCase {
    func testForwardIsTimedAndClampedToRequestedDistance() throws {
        var robot = RobotSimulationEngine()
        try robot.start(.forward, parameters: .init(distanceCm: 40, speed: 20), id: "forward")
        XCTAssertNil(robot.advance(seconds: 0.5))
        XCTAssertEqual(robot.position.y, 10)
        XCTAssertEqual(robot.movement.leftWheel, 20)
        XCTAssertEqual(robot.advance(seconds: 5), "forward")
        XCTAssertEqual(robot.position.y, 40)
        XCTAssertEqual(robot.movement.speed, 0)
    }

    func testBackwardUsesCurrentHeading() throws {
        var robot = RobotSimulationEngine()
        try robot.start(.right, parameters: .init(angleDegrees: 90, speed: 30), id: "turn")
        _ = robot.advance(seconds: 1)
        try robot.start(.backward, parameters: .init(distanceCm: 20, speed: 20), id: "backward")
        XCTAssertEqual(robot.movement.rightWheel, -20)
        XCTAssertEqual(robot.advance(seconds: 1), "backward")
        XCTAssertEqual(robot.position.x, -20, accuracy: 1e-8)
        XCTAssertEqual(robot.position.y, 0, accuracy: 1e-8)
    }

    func testRotationAndHeadingNormalization() throws {
        var robot = RobotSimulationEngine()
        try robot.start(.left, parameters: .init(angleDegrees: 90, speed: 25), id: "left")
        XCTAssertEqual(robot.movement.leftWheel, -25)
        XCTAssertEqual(robot.movement.rightWheel, 25)
        XCTAssertEqual(robot.advance(seconds: 1), "left")
        XCTAssertEqual(robot.position.heading, 270, accuracy: 1e-8)
        try robot.start(.right, parameters: .init(angleDegrees: 135, speed: 25), id: "right")
        XCTAssertEqual(robot.advance(seconds: 2), "right")
        XCTAssertEqual(robot.position.heading, 45, accuracy: 1e-8)
        XCTAssertEqual(robot.position.x, 0)
        XCTAssertEqual(robot.position.y, 0)
    }

    func testEmergencyStopPreemptsMovement() {
        var robot = RobotSimulationEngine()
        _ = robot.execute(id: "move", payload: [
            "command": "move_forward", "parameters": ["distanceCm": 20, "speed": 30]
        ])
        _ = robot.advance(seconds: 0.2)
        let stoppedAt = robot.position.y
        let stop = robot.execute(id: "stop", payload: ["command": "stop", "parameters": [:]])
        XCTAssertEqual(stop.phases, [.accepted, .executing, .completed])
        XCTAssertNil(robot.advance(seconds: 5))
        XCTAssertEqual(robot.position.y, stoppedAt)
        XCTAssertEqual(robot.movement.leftWheel, 0)
        XCTAssertEqual(robot.movement.rightWheel, 0)
    }

    func testCommandLifecycleAndConflict() {
        var robot = RobotSimulationEngine()
        let payload: [String: Any] = [
            "command": "move_forward", "parameters": ["distanceCm": 20, "speed": 30]
        ]
        var execution = robot.execute(id: "first", payload: payload)
        XCTAssertEqual(execution.phases, [.accepted, .executing])
        XCTAssertEqual(robot.execute(id: "second", payload: payload).phases, [.failed])
        XCTAssertEqual(robot.advance(seconds: 1), execution.id)
        execution.finish(.completed)
        execution.finish(.cancelled)
        XCTAssertEqual(execution.phases, [.accepted, .executing, .completed])
    }

    func testInvalidCommandsLeaveRobotIdle() {
        var robot = RobotSimulationEngine()
        for payload: [String: Any] in [
            ["command": "fly", "parameters": [:]],
            ["command": "move_forward", "parameters": ["distanceCm": 10, "speed": 51]],
            ["command": "move_forward", "parameters": ["distanceCm": 10, "speed": true]],
            ["command": "move_forward", "parameters": ["distanceCm": 101, "speed": 30]],
            ["command": "rotate_left", "parameters": ["angleDegrees": 181, "speed": 30]],
            ["command": "rotate_left", "parameters": ["angleDegrees": 20, "speed": 30, "extra": 1]],
            ["command": "set_expression", "parameters": ["expression": "unknown"]],
            ["command": "stop", "parameters": ["speed": 0]]
        ] {
            XCTAssertEqual(robot.execute(id: "invalid", payload: payload).phase, .failed)
            XCTAssertNil(robot.motion)
        }
    }

    func testConnectionLossDropsMotionWithoutCompletion() throws {
        var robot = RobotSimulationEngine()
        try robot.start(.forward, parameters: .init(distanceCm: 100, speed: 30), id: "interrupted")
        _ = robot.advance(seconds: 0.5)
        XCTAssertEqual(robot.stop(), "interrupted")
        XCTAssertEqual(robot.telemetry(robotId: "sim").state, "idle")
        XCTAssertEqual(robot.movement.speed, 0)
        XCTAssertNil(robot.advance(seconds: 20))
        XCTAssertEqual(robot.position.y, 15)
    }

    func testProtocolJSONSerialization() throws {
        let frame = try RobotProtocol.encode("register", Registration(robotId: "sim-1"))
        let data = Data(frame.utf8)
        let envelope = try JSONDecoder().decode(IncomingMessage.self, from: data)
        XCTAssertTrue(envelope.hasValidEnvelope)
        XCTAssertEqual(envelope.payload?.protocolVersion, 1)
        XCTAssertEqual(envelope.payload?.robotId, "sim-1")
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(Set(json.keys), ["id", "type", "timestamp", "payload"])
        let payload = try XCTUnwrap(json["payload"] as? [String: Any])
        XCTAssertEqual(payload["deviceType"] as? String, "simulator")
        let command = try RobotProtocol.encode("command_ack", CommandStatus(commandId: "server-id", status: .accepted))
        XCTAssertTrue(command.contains("\"commandId\":\"server-id\""))
        XCTAssertTrue(command.contains("\"status\":\"accepted\""))
    }

    @MainActor
    func testLiveP4RoundTrip() async throws {
        guard let endpoint = ProcessInfo.processInfo.environment["HOMEROBOT_INTEGRATION_WS"] else {
            throw XCTSkip("Set HOMEROBOT_INTEGRATION_WS to opt into live integration.")
        }
        let robot = RobotConnectionService()
        let previousURL = robot.serverURL
        defer {
            robot.disconnect()
            robot.serverURL = previousURL
        }
        robot.serverURL = endpoint
        robot.connect()
        try await waitUntil { robot.connectionState == .connected }
        robot.submit(.forward, parameters: .init(distanceCm: 25, speed: 30))
        try await waitUntil { robot.manualStatus?.status == .completed }
        XCTAssertEqual(robot.lastCommand?.phase, .completed)
        XCTAssertEqual(robot.engine.position.y, 25, accuracy: 0.01)

        var components = try XCTUnwrap(URLComponents(string: endpoint))
        components.scheme = components.scheme == "wss" ? "https" : "http"
        components.path = "/api/robots/\(robot.robotId)"
        let url = try XCTUnwrap(components.url)
        let (data, _) = try await URLSession.shared.data(from: url)
        let details = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let telemetry = try XCTUnwrap(details["telemetry"] as? [String: Any])
        let position = try XCTUnwrap(telemetry["position"] as? [String: Double])
        XCTAssertEqual(try XCTUnwrap(position["y"]), 25, accuracy: 0.01)

        var request = URLRequest(url: url.appendingPathComponent("commands"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(CommandRequest(
            command: .expression, parameters: .init(expression: "happy")
        ))
        let (_, response) = try await URLSession.shared.data(for: request)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 202)
        try await waitUntil { robot.engine.expression == .happy && robot.lastCommand?.phase == .completed }

        robot.submit(.forward, parameters: .init(distanceCm: 100, speed: 20))
        try await waitUntil { robot.engine.motion != nil }
        let interruptedID = try XCTUnwrap(robot.engine.motion?.commandId)
        robot.submit(.stop)
        try await waitUntil { robot.lastCommand?.name == "stop" && robot.manualStatus?.status == .completed }
        XCTAssertNil(robot.engine.motion)
        XCTAssertEqual(robot.engine.movement.speed, 0)

        let interruptedURL = url.appendingPathComponent("commands").appendingPathComponent(interruptedID)
        let (interruptedData, _) = try await URLSession.shared.data(from: interruptedURL)
        XCTAssertEqual(try JSONDecoder().decode(CommandStatus.self, from: interruptedData).status, .cancelled)

        try await waitUntil { robot.canMove }
        robot.submit(.forward, parameters: .init(distanceCm: 100, speed: 20))
        try await waitUntil { robot.engine.motion != nil }
        robot.disconnect()
        XCTAssertNil(robot.engine.motion)
        XCTAssertEqual(robot.engine.movement.speed, 0)
        XCTAssertEqual(robot.lastCommand?.phase, .cancelled)
    }

    @MainActor
    func testLiveAICommands() async throws {
        let environment = ProcessInfo.processInfo.environment
        guard environment["HOMEROBOT_INTEGRATION_AI"] == "1",
              let endpoint = environment["HOMEROBOT_INTEGRATION_WS"] else {
            throw XCTSkip("Set HOMEROBOT_INTEGRATION_AI=1 and HOMEROBOT_INTEGRATION_WS to opt in.")
        }
        let robot = RobotConnectionService()
        let previousURL = robot.serverURL
        defer {
            robot.disconnect()
            robot.serverURL = previousURL
        }
        robot.serverURL = endpoint
        robot.connect()
        try await waitUntil { robot.connectionState == .connected }

        let cases: [(String, [String], [Double])] = [
            ("Otoč se doprava o 90 stupňů.", ["rotate_right"], [90]),
            ("Popojeď dopředu o 50 centimetrů.", ["move_forward"], [50]),
            ("Otoč se doleva o 45 stupňů a potom couvni 20 centimetrů.",
             ["rotate_left", "move_backward"], [45, 20]),
            ("Zastav.", ["stop"], []),
            ("Jeď někam dopředu.", [], []),
            ("Jeď dopředu 100 kilometrů.", [], []),
            ("Turn right 90 degrees and move forward 30 centimeters.", ["rotate_right", "move_forward"], [90, 30])
        ]
        for (instruction, commands, values) in cases {
            robot.submitAI(instruction)
            try await waitUntil(timeout: .seconds(180)) { !robot.aiPending }
            let plan = try XCTUnwrap(robot.aiPlan, robot.aiError ?? instruction)
            XCTAssertEqual(plan.actions.map(\.command), commands, instruction)
            XCTAssertEqual(plan.actions.compactMap { $0.parameters.distanceCm ?? $0.parameters.angleDegrees },
                           values, instruction)
            XCTAssertEqual(plan.status, commands.isEmpty ? "rejected" : "completed", instruction)
            XCTAssertEqual(plan.actions.map(\.status), commands.map { _ in "completed" }, instruction)
            if commands.isEmpty { XCTAssertFalse(plan.validationErrors.isEmpty, instruction) }
        }
        XCTAssertEqual(robot.engine.position.heading, 135, accuracy: 0.01)
        XCTAssertEqual(robot.engine.position.x, 50 + 10 / sqrt(2), accuracy: 0.01)
        XCTAssertEqual(robot.engine.position.y, -50 / sqrt(2), accuracy: 0.01)
        XCTAssertNil(robot.engine.motion)
    }

    @MainActor
    func testLiveVoiceConversation() async throws {
        try await withVoiceFixture("chat.wav") { robot, wav in
            let voice = VoiceInteraction()
            voice.process(wav: wav, robot: robot)
            try await waitUntil(timeout: .seconds(160)) { !voice.busy }
            XCTAssertNil(voice.errorMessage)
            XCTAssertTrue(voice.reply.lowercased().contains("čtyři") || voice.reply.contains("4"))
            XCTAssertNotNil(voice.ttsMs)
            XCTAssertEqual(robot.engine.position.heading, 0, accuracy: 0.01)
        }
    }

    @MainActor
    func testLiveVoiceActionAndStop() async throws {
        try await withVoiceFixture("action.wav") { robot, wav in
            let voice = VoiceInteraction()
            voice.process(wav: wav, robot: robot)
            try await waitUntil(timeout: .seconds(160)) { !voice.busy }
            XCTAssertNil(voice.errorMessage, "Raw: \(voice.transcription), corrected: \(voice.correctedText)")
            XCTAssertEqual(voice.reply, "Hotovo.")
            XCTAssertEqual(voice.plan?.actions.map(\.command), ["rotate_right"])
            XCTAssertEqual(voice.plan?.status, "completed")
            XCTAssertNotNil(voice.ttsMs)
            XCTAssertEqual(robot.engine.position.heading, 90, accuracy: 0.01)

            robot.submit(.forward, parameters: .init(distanceCm: 100, speed: 20))
            try await waitUntil { robot.engine.motion != nil }
            voice.stop(robot: robot)
            try await waitUntil { robot.manualStatus?.status == .completed }
            XCTAssertEqual(robot.lastCommand?.name, "stop")
            XCTAssertNil(robot.engine.motion)
        }
    }

    @MainActor
    func testLiveVoiceEmergencyStop() async throws {
        try await withVoiceFixture("stop.wav") { robot, wav in
            robot.submit(.forward, parameters: .init(distanceCm: 100, speed: 10))
            try await waitUntil { robot.engine.motion != nil }

            let voice = VoiceInteraction()
            voice.process(wav: wav, robot: robot)
            try await waitUntil(timeout: .seconds(75)) { !voice.busy }
            XCTAssertNil(voice.errorMessage)
            XCTAssertEqual(voice.intent, "stop")
            XCTAssertEqual(voice.validation, "stop_submitted")
            XCTAssertNil(voice.llmMs)
            XCTAssertEqual(robot.lastCommand?.name, "stop")
            XCTAssertNil(robot.engine.motion)
        }
    }

    @MainActor
    func testLiveCzechVoiceVision() async throws {
        guard let directory = ProcessInfo.processInfo.environment["HOMEROBOT_INTEGRATION_VISION"] else {
            throw XCTSkip("Set HOMEROBOT_INTEGRATION_VISION to the synthetic image and Czech voice fixtures directory.")
        }
        let fixture = URL(fileURLWithPath: directory, isDirectory: true)
        try await withVoiceFixture("vision.wav") { robot, wav in
            let camera = VisionTests.StubCamera(photo: try Data(contentsOf: fixture.appendingPathComponent("vision.png")))
            let voice = VoiceInteraction(vision: VisionInteraction(camera: camera))
            defer { voice.vision.close() }
            voice.process(wav: wav, robot: robot)
            try await waitUntil(timeout: .seconds(160)) { !voice.busy }
            XCTAssertNil(voice.errorMessage, "Transcript: \(voice.transcription)")
            XCTAssertEqual(voice.validation, "vision")
            XCTAssertEqual(voice.vision.answer?.cached, false)
            XCTAssertNotNil(voice.ttsMs)
            XCTAssertNil(robot.lastCommand)

            let follow = try Data(contentsOf: fixture.appendingPathComponent("vision-follow.wav"))
            voice.process(wav: follow, robot: robot)
            try await waitUntil(timeout: .seconds(160)) { !voice.busy }
            XCTAssertNil(voice.errorMessage, "Transcript: \(voice.transcription)")
            XCTAssertEqual(voice.validation, "vision_followup")
            XCTAssertEqual(voice.vision.answer?.cached, true)
            XCTAssertEqual(camera.captures, 1)
            XCTAssertTrue(voice.reply.lowercased().contains("červen"), voice.reply)
            XCTAssertNotNil(voice.ttsMs)
            XCTAssertNil(robot.lastCommand)

            voice.process(wav: wav, robot: robot)
            try await waitUntil(timeout: .seconds(160)) { !voice.busy }
            XCTAssertNil(voice.errorMessage)
            XCTAssertEqual(voice.vision.answer?.cached, false)
            XCTAssertEqual(camera.captures, 2)
            XCTAssertNil(robot.lastCommand)
        }
    }

    @MainActor
    private func withVoiceFixture(
        _ name: String, run: (RobotConnectionService, Data) async throws -> Void
    ) async throws {
        let environment = ProcessInfo.processInfo.environment
        guard let directory = environment["HOMEROBOT_INTEGRATION_VOICE_WAVS"],
              let endpoint = environment["HOMEROBOT_INTEGRATION_WS"] else {
            throw XCTSkip("Set HOMEROBOT_INTEGRATION_VOICE_WAVS and HOMEROBOT_INTEGRATION_WS.")
        }
        let file = URL(fileURLWithPath: directory, isDirectory: true).appendingPathComponent(name)
        guard FileManager.default.fileExists(atPath: file.path) else {
            throw XCTSkip("Missing optional voice fixture \(name).")
        }
        let robot = RobotConnectionService()
        let previousURL = robot.serverURL
        defer {
            robot.disconnect()
            robot.serverURL = previousURL
        }
        robot.serverURL = endpoint
        robot.connect()
        try await waitUntil { robot.connectionState == .connected }
        try await run(robot, Data(contentsOf: file))
    }

    @MainActor
    private func waitUntil(timeout: Duration = .seconds(5), _ condition: () -> Bool) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now + timeout
        while !condition() {
            guard clock.now < deadline else { throw WaitTimeout() }
            try await Task.sleep(for: .milliseconds(25))
        }
    }

    private struct WaitTimeout: Error {}
}
