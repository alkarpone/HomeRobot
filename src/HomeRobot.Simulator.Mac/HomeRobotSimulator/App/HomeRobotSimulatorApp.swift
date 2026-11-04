import SwiftUI

@main
struct HomeRobotSimulatorApp: App {
    var body: some Scene {
        Window("HomeRobot Simulator", id: "simulator") {
            SimulatorDashboardView()
                .preferredColorScheme(.dark)
                .frame(minWidth: 620, minHeight: 520)
        }
        .defaultSize(width: 960, height: 720)
    }
}
