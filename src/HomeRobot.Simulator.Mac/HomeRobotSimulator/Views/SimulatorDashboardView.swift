import SwiftUI

struct SimulatorDashboardView: View {
    @Environment(\.openWindow) private var openWindow
    @ObservedObject var robot: RobotConnectionService
    @StateObject private var voice = VoiceInteraction()

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                ConnectionStatusView(robot: robot)
                panel("Voice") { voiceControls }
                panel("Vision") { VisionView(vision: voice.vision, voice: voice, robot: robot) }
                HStack(spacing: 16) {
                    panel("Robot Face") {
                        RobotFaceView(
                            expression: VoiceInteraction.faceExpression(phase: voice.phase,
                                isMoving: robot.engine.motion != nil, commanded: robot.engine.expression,
                                conversational: voice.conversationalExpression),
                            mouthOpenness: { voice.mouthOpenness }
                        )
                    }
                    panel("Movement Monitor") {
                        RobotTopDownView(engine: robot.engine)
                    }
                }
                panel("Manual Controls") { MovementControlsView(robot: robot) }.disabled(voice.busy)
                panel("AI Commands") { AIControlsView(robot: robot) }.disabled(voice.busy)
                panel("Telemetry & Commands") { TelemetryView(robot: robot) }
            }
            .padding(20)
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            HStack {
                Button {
                    openWindow(id: "robot-settings")
                } label: {
                    Label("Robot Settings", systemImage: "gearshape")
                }
                Spacer()
                CameraIndicator(vision: voice.vision)
                Button(role: .destructive) { voice.stop(robot: robot) } label: {
                    Label("Stop", systemImage: "stop.fill")
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
            }
            .padding(12)
            .background(.regularMaterial)
        }
        .onChange(of: robot.connectionState) { _, state in
            if state != .connected { voice.disconnected() }
        }
        .onChange(of: robot.lastCommand?.id) {
            if robot.lastCommand?.name == "set_expression", robot.lastCommand?.phase == .completed { voice.clearConversationalExpression() }
        }
        .onDisappear {
            voice.cancel()
            voice.vision.close()
            robot.disconnect()
        }
    }

    private var voiceControls: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Button {
                    if voice.phase == .listening {
                        voice.finish(robot: robot)
                    } else {
                        voice.start(robot: robot)
                    }
                } label: {
                    Label(voice.phase == .listening ? "Finish recording" : "Start recording",
                          systemImage: voice.phase == .listening ? "record.circle.fill" : "mic.fill")
                }
                .buttonStyle(.borderedProminent)
                .tint(voice.phase == .listening ? .red : .accentColor)
                .disabled(voice.phase != .listening && (voice.busy || robot.voiceAPI == nil))
                Text(voice.requestingPermission ? "Microphone permission" : voice.phase.rawValue)
                if voice.phase == .speaking { Button("Cancel playback") { voice.cancel() } }
            }
            if !voice.transcription.isEmpty { Text("Raw Whisper: \(voice.transcription)").textSelection(.enabled) }
            if !voice.correctedText.isEmpty { Text("Corrected: \(voice.correctedText)").textSelection(.enabled) }
            if let intent = voice.intent { Text("Intent: \(intent)") }
            if let status = voice.correctionStatus { Text("Correction: \(status)") }
            if let validation = voice.validation { Text("Validation: \(validation)") }
            if let plan = voice.plan { AIPlanView(plan: plan) }
            if !voice.reply.isEmpty { Text("Reply: \(voice.reply)").textSelection(.enabled) }
            if let error = voice.errorMessage { Text(error).foregroundStyle(.orange).textSelection(.enabled) }
            Text("Recording \(voice.recordingSeconds, specifier: "%.1f") s  ·  STT \(timing(voice.sttMs))  ·  Analysis \(timing(voice.analysisMs))  ·  LLM \(timing(voice.llmMs))  ·  TTS \(timing(voice.ttsMs))  ·  Total \(timing(voice.totalMs))")
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
        }
    }

    private func timing(_ ms: Int?) -> String {
        ms.map { "\($0) ms" } ?? "—"
    }

    private func panel<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.headline)
            content()
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
    }
}

struct ConnectionStatusView: View {
    @ObservedObject var robot: RobotConnectionService

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("HomeRobot Simulator").font(.title.bold())
                Spacer()
                Label(robot.connectionState.rawValue, systemImage: "circle.fill")
                    .foregroundStyle(statusColor)
            }
            HStack {
                TextField("WebSocket URL", text: $robot.serverURL)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { robot.connect() }
                Button(robot.connectionState == .disconnected ? "Connect" : "Reconnect") {
                    robot.connect()
                }
                .buttonStyle(.borderedProminent)
                Button("Disconnect") { robot.disconnect() }
                    .disabled(robot.connectionState == .disconnected)
            }
            if let error = robot.errorMessage {
                Label(error, systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.orange)
                    .textSelection(.enabled)
            }
        }
    }

    private var statusColor: Color {
        switch robot.connectionState {
        case .connected: .green
        case .connecting: .yellow
        case .disconnected: .secondary
        }
    }
}

struct MovementControlsView: View {
    @ObservedObject var robot: RobotConnectionService
    @State private var distance = 30.0
    @State private var angle = 45.0
    @State private var speed = 30.0

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                movementButton("Forward", icon: "arrow.up", command: .forward)
                movementButton("Backward", icon: "arrow.down", command: .backward)
                movementButton("Rotate Left", icon: "arrow.counterclockwise", command: .left)
                movementButton("Rotate Right", icon: "arrow.clockwise", command: .right)

            }
            HStack(spacing: 16) {
                input("Distance (cm)", value: $distance)
                input("Rotation (°)", value: $angle)
                input("Speed (%)", value: $speed)
            }
            .disabled(robot.connectionState != .connected)
        }
    }

    private func movementButton(_ title: String, icon: String, command: CommandName) -> some View {
        let translation = command == .forward || command == .backward
        return Button {
            robot.submit(command, parameters: CommandParameters(
                distanceCm: translation ? distance : nil,
                angleDegrees: translation ? nil : angle,
                speed: speed
            ))
        } label: {
            Label(title, systemImage: icon)
        }
        .disabled(!robot.canMove || !speed.isFinite || !(translation ? distance : angle).isFinite)
    }

    private func input(_ label: String, value: Binding<Double>) -> some View {
        HStack {
            Text(label)
            TextField(label, value: value, format: .number)
                .textFieldStyle(.roundedBorder)
                .frame(width: 62)
        }
    }
}

struct TelemetryView: View {
    @ObservedObject var robot: RobotConnectionService

    var body: some View {
        let telemetry = robot.engine.telemetry(robotId: robot.robotId)
        Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 6) {
            GridRow {
                Text("Position  \(number(telemetry.position.x)), \(number(telemetry.position.y)) cm")
                Text("Heading  \(number(telemetry.position.heading))°")
                Text("Speed  \(number(telemetry.movement.speed))%")
            }
            GridRow {
                Text("Left wheel  \(number(telemetry.movement.leftWheel))%")
                Text("Right wheel  \(number(telemetry.movement.rightWheel))%")
                Text("State  \(telemetry.state)")
            }
            GridRow {
                Text("Last command  \(robot.lastCommand?.name ?? "—")")
                Text("Execution  \(robot.lastCommand?.phase.rawValue ?? "—")")
                Text("REST status  \(robot.manualStatus?.status.rawValue ?? "—")")
            }
            Divider()
            HStack {
                Text("Robot ID  \(robot.robotId)").textSelection(.enabled)
                Spacer()
                if let time = robot.lastCommunication {
                    Text("Last communication  \(time, style: .time)")
                } else {
                    Text("Last communication  —")
                }
            }
            .gridCellColumns(3)
        }
        .font(.caption.monospacedDigit())
        .foregroundStyle(.secondary)
    }

    private func number(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(1)))
    }
}

private struct AIControlsView: View {
    @ObservedObject var robot: RobotConnectionService
    @State private var instruction = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                TextField("Robot instruction", text: $instruction)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit(send)
                Button("Send", action: send)
                    .disabled(!robot.canMove || instruction.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            if let plan = robot.aiPlan {
                AIPlanView(plan: plan)
            } else if robot.aiPending {
                ProgressView("Interpreting instruction…")
            }
            if let error = robot.aiError { Text(error).foregroundStyle(.orange) }
        }
    }

    private func send() {
        robot.submitAI(instruction)
    }
}

private struct AIPlanView: View {
    let plan: AIPlan

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Plan status: \(plan.status)")
            ForEach(Array(plan.actions.enumerated()), id: \.offset) { index, action in
                Text("\(index + 1). \(action.command) \(parameters(action.parameters)) — \(action.status)")
                if let message = action.message { Text(message).foregroundStyle(.orange) }
            }
            ForEach(Array(plan.validationErrors.enumerated()), id: \.offset) { _, error in
                Text(error).foregroundStyle(.orange)
            }
            if let message = plan.message { Text(message).foregroundStyle(.orange) }
        }
    }

    private func parameters(_ values: CommandParameters) -> String {
        var parts: [String] = []
        if let distance = values.distanceCm { parts.append("\(distance.formatted()) cm") }
        if let angle = values.angleDegrees { parts.append("\(angle.formatted())°") }
        if let speed = values.speed { parts.append("\(speed.formatted())%") }
        if let expression = values.expression { parts.append(expression) }
        return parts.joined(separator: ", ")
    }
}


private struct CameraIndicator: View {
    @ObservedObject var vision: VisionInteraction

    var body: some View {
        if vision.cameraActive {
            Label("Camera active", systemImage: "video.fill")
                .foregroundStyle(.red)
        }
    }
}
