import SwiftUI

struct SimulatorDashboardView: View {
    @StateObject private var robot = RobotConnectionService()

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                ConnectionStatusView(robot: robot)
                HStack(spacing: 16) {
                    panel("Robot Face") {
                        RobotFaceView(expression: robot.engine.expression)
                    }
                    panel("Movement Monitor") {
                        RobotTopDownView(engine: robot.engine)
                    }
                }
                panel("Manual Controls") { MovementControlsView(robot: robot) }
                panel("Telemetry & Commands") { TelemetryView(robot: robot) }
            }
            .padding(20)
        }
        .onDisappear { robot.disconnect() }
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
    let robot: RobotConnectionService
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
                Spacer(minLength: 0)
                Button(role: .destructive) { robot.submit(.stop) } label: {
                    Label("Stop", systemImage: "stop.fill")
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .disabled(robot.connectionState != .connected || robot.stopRequested)
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
    let robot: RobotConnectionService

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
