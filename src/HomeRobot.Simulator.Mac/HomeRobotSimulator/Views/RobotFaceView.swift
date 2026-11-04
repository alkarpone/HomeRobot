import SwiftUI

struct RobotFaceView: View {
    let expression: RobotExpression
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(spacing: 8) {
            TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { timeline in
                let time = reduceMotion ? 1 : timeline.date.timeIntervalSinceReferenceDate
                let blinkPhase = time.truncatingRemainder(dividingBy: 4.3)
                let blink = blinkPhase < 0.22 ? 1 - 0.92 * sin(.pi * blinkPhase / 0.22) : 1
                VStack(spacing: 18) {
                    HStack(spacing: 30) {
                        eye(blink: blink, side: -1, time: time)
                        eye(blink: blink, side: 1, time: time)
                    }
                    Capsule()
                        .fill(color.opacity(0.85))
                        .frame(width: expression == .happy ? 42 : 26, height: mouthHeight(time))
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 12))
                .animation(.easeInOut(duration: 0.2), value: expression)
            }
            Text(expression.rawValue.capitalized)
                .font(.caption.weight(.medium))
                .foregroundStyle(color)
        }
        .frame(height: 204)
        .accessibilityLabel("Robot face, \(expression.rawValue)")
    }

    private func eye(blink: Double, side: Double, time: Double) -> some View {
        let shape: (width: Double, height: Double, tilt: Double) = switch expression {
        case .idle: (30, 42, 0)
        case .listening: (33, 52, 0)
        case .thinking: (30, side < 0 ? 31 : 43, 0)
        case .speaking: (33, 38, 0)
        case .happy: (37, 19, -side * 10)
        case .sad: (33, 27, -side * 20)
        case .error: (32, 21, side * 24)
        }
        return RoundedRectangle(cornerRadius: expression == .error ? 4 : 14)
            .fill(color.gradient)
            .frame(width: shape.width, height: shape.height)
            .rotationEffect(.degrees(shape.tilt))
            .scaleEffect(x: 1, y: blink)
            .offset(x: reduceMotion ? 0 : sin(time * (expression == .thinking ? 3 : 0.8)) * 5,
                    y: reduceMotion ? 0 : cos(time * 1.7) * 2)
            .frame(width: 45, height: 53)
    }

    private func mouthHeight(_ time: Double) -> Double {
        expression == .speaking && !reduceMotion ? 6 + 10 * abs(sin(time * 15)) : 5
    }

    private var color: Color {
        switch expression {
        case .happy: .mint
        case .sad: .blue
        case .error: .red
        case .thinking: .purple
        default: .cyan
        }
    }
}
