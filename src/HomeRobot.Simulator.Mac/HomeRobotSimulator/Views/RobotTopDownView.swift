import SwiftUI

struct RobotTopDownView: View {
    let engine: RobotSimulationEngine

    var body: some View {
        VStack(spacing: 8) {
            Canvas { context, size in
                guard size.width > 0, size.height > 0 else { return }
                let scale = min(size.width, size.height) / 270
                let center = CGPoint(x: size.width / 2, y: size.height / 2)
                let position = engine.position
                let offset = CGPoint(x: center.x - position.x * scale, y: center.y + position.y * scale)
                let gridSpacing = 25 * scale
                var grid = Path()
                for x in stride(from: offset.x.truncatingRemainder(dividingBy: gridSpacing),
                                through: size.width, by: gridSpacing) {
                    grid.move(to: CGPoint(x: x, y: 0))
                    grid.addLine(to: CGPoint(x: x, y: size.height))
                }
                for y in stride(from: offset.y.truncatingRemainder(dividingBy: gridSpacing),
                                through: size.height, by: gridSpacing) {
                    grid.move(to: CGPoint(x: 0, y: y))
                    grid.addLine(to: CGPoint(x: size.width, y: y))
                }
                context.stroke(grid, with: .color(.white.opacity(0.12)), lineWidth: 1)
                context.fill(Path(ellipseIn: CGRect(x: offset.x - 3, y: offset.y - 3, width: 6, height: 6)),
                             with: .color(.orange))

                var robot = context
                robot.translateBy(x: center.x, y: center.y)
                robot.rotate(by: .degrees(position.heading))
                drawWheel(robot, x: -31, travel: engine.leftWheelTravel, speed: engine.movement.leftWheel)
                drawWheel(robot, x: 21, travel: engine.rightWheelTravel, speed: engine.movement.rightWheel)
                let body = Path(roundedRect: CGRect(x: -22, y: -25, width: 44, height: 50), cornerRadius: 9)
                robot.fill(body, with: .color(.cyan.opacity(0.45)))
                robot.stroke(body, with: .color(.cyan), lineWidth: 1.5)
                arrow(robot, x: 0, start: 6, end: -17, color: .white)
            }
            .background(.black.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
            .clipShape(RoundedRectangle(cornerRadius: 12))
            Text("X \(engine.position.x, format: .number.precision(.fractionLength(1))) cm  ·  Y \(engine.position.y, format: .number.precision(.fractionLength(1))) cm  ·  \(engine.position.heading, format: .number.precision(.fractionLength(1)))°")
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .frame(height: 204)
        .accessibilityLabel("Top-down robot position and heading")
    }

    private func drawWheel(_ context: GraphicsContext, x: Double, travel: Double, speed: Double) {
        let wheel = Path(roundedRect: CGRect(x: x, y: -16, width: 10, height: 32), cornerRadius: 3)
        context.fill(wheel, with: .color(.gray))
        var tread = context
        tread.clip(to: wheel)
        for index in -1...4 {
            let y = Double(index) * 8 - 16 - (travel * 2).truncatingRemainder(dividingBy: 8)
            let mark = Path(CGRect(x: x, y: y, width: 10, height: 2))
            tread.fill(mark, with: .color(.white.opacity(0.6)))
        }
        if speed != 0 {
            arrow(context, x: x + 5, start: speed > 0 ? -21 : 21,
                  end: speed > 0 ? -36 : 36, color: .mint)
        }
    }

    private func arrow(_ context: GraphicsContext, x: Double, start: Double, end: Double, color: Color) {
        let back = end + (end < start ? 5 : -5)
        var path = Path()
        path.move(to: CGPoint(x: x, y: start))
        path.addLine(to: CGPoint(x: x, y: end))
        path.move(to: CGPoint(x: x - 4, y: back))
        path.addLine(to: CGPoint(x: x, y: end))
        path.addLine(to: CGPoint(x: x + 4, y: back))
        context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: 2, lineCap: .round))
    }
}
