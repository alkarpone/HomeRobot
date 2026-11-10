import SwiftUI

@main
struct HomeRobotSimulatorApp: App {
    @StateObject private var robot = RobotConnectionService()

    var body: some Scene {
        Window("HomeRobot Simulator", id: "simulator") {
            SimulatorDashboardView(robot: robot)
                .preferredColorScheme(.dark)
                .frame(minWidth: 620, minHeight: 520)
        }
        .defaultSize(width: 960, height: 720)

        Window("Robot Settings", id: "robot-settings") {
            RobotSettingsView(robot: robot)
                .frame(minWidth: 500, minHeight: 480)
        }
        .defaultSize(width: 680, height: 680)
    }
}
