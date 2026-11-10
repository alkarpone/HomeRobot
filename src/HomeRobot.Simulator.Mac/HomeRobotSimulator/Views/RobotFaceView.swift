import SwiftUI

struct RobotFaceView: View {
    let expression: RobotExpression
    let mouthOpenness: () -> Double
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(spacing: 8) {
            TimelineView(.animation(minimumInterval: 1.0 / 30,
                                    paused: reduceMotion && expression != .speaking)) { timeline in
                let time = timeline.date.timeIntervalSinceReferenceDate
                let cycle = time.truncatingRemainder(dividingBy: 17.9)
                let blinkDistance = min(abs(cycle - 2.2), abs(cycle - 6.7), abs(cycle - 12.1), abs(cycle - 16.8))
                let blink = reduceMotion ? 1 : 1 - 0.94 * pow(max(0, 1 - blinkDistance / 0.11), 1.5)
                let openness = expression == .speaking ? mouthOpenness() : 0
                VStack(spacing: 12) {
                    HStack(spacing: 28) {
                        eye(side: -1, blink: blink, time: time)
                        eye(side: 1, blink: blink, time: time)
                    }
                    MouthShape(openness: openness, smile: pose.smile)
                        .fill(color.opacity(0.9))
                        .frame(width: 29 + 8 * openness + 10 * max(0, pose.smile), height: 32)
                        .frame(width: 48)
                        .animation(nil, value: openness)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 12))
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.24), value: expression)
            }
            Text(expression.rawValue.capitalized)
                .font(.caption.weight(.medium))
                .foregroundStyle(color)
        }
        .frame(height: 204)
        .accessibilityLabel("Robot face, \(expression.rawValue)")
    }

    private func eye(side: Double, blink: Double, time: Double) -> some View {
        let lookX = expression == .thinking ? 4.5 + 2 * sin(time * 1.2) : 2 * sin(time * 0.9)
        let lookY = expression == .thinking ? -3.0 : cos(time * 1.1)
        let tilt = side * pose.tilt
        return VStack(spacing: 5) {
            Capsule()
                .fill(color.opacity(0.75))
                .frame(width: 21, height: 3)
                .rotationEffect(.degrees(tilt))
                .offset(y: pose.brow)
            RoundedRectangle(cornerRadius: expression == .error ? 5 : 14)
                .fill(color.gradient)
                .frame(width: pose.width, height: pose.height)
                .scaleEffect(x: 1, y: blink)
                .rotationEffect(.degrees(tilt * 0.55))
                .frame(height: 47)
        }
        .offset(x: reduceMotion ? 0 : lookX, y: reduceMotion ? 0 : lookY)
        .frame(width: 44, height: 61)
    }

    private var pose: (width: Double, height: Double, tilt: Double, brow: Double, smile: Double) {
        switch expression {
        case .idle: (30, 34, 0, 0, 0)
        case .listening: (31, 44, 0, -4, 0.15)
        case .thinking: (29, 30, -10, -2, 0)
        case .speaking: (31, 36, 0, -1, 0)
        case .happy: (35, 19, -18, -3, 1)
        case .sad: (30, 23, 25, 3, -0.8)
        case .error: (31, 22, -24, 2, -0.4)
        case .executing: (33, 26, -17, 3, 0)
        }
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

private struct MouthShape: Shape {
    var openness: Double
    var smile: Double

    var animatableData: AnimatablePair<Double, Double> {
        get { AnimatablePair(openness, smile) }
        set { (openness, smile) = (newValue.first, newValue.second) }
    }

    func path(in rect: CGRect) -> Path {
        let center = rect.midY
        let corners = center - 5 * smile
        let radius = 2 + 20 * openness
        let curve = center + 5 * smile
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: corners))
        path.addQuadCurve(to: CGPoint(x: rect.maxX, y: corners),
                          control: CGPoint(x: rect.midX, y: curve - radius))
        path.addQuadCurve(to: CGPoint(x: rect.minX, y: corners),
                          control: CGPoint(x: rect.midX, y: curve + radius + 6 * max(0, smile)))
        path.closeSubpath()
        return path
    }
}
