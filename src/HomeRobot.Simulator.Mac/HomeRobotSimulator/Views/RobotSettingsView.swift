import SwiftUI

struct RobotSettingsView: View {
    @ObservedObject var robot: RobotConnectionService

    var body: some View {
        RobotSettingsTabs(address: robot.serverURL, connection: robot.connectionState)
            .id(robot.serverURL)
    }
}

private struct RobotSettingsTabs: View {
    let connection: RobotConnectionService.ConnectionState
    @StateObject private var model: RobotSettingsModel

    init(address: String, connection: RobotConnectionService.ConnectionState) {
        self.connection = connection
        _model = StateObject(wrappedValue: RobotSettingsModel(api: RobotSettingsAPI(serverURL: address)))
    }

    var body: some View {
        TabView {
            PersonalitySettingsView(model: model)
                .tabItem { Label("Personality", systemImage: "face.smiling") }
            MemorySettingsView(model: model)
                .tabItem { Label("Memory", systemImage: "tray.full") }
            Form {
                Section("Connection & Storage") {
                    LabeledContent("Server connection (WebSocket)", value: connection.rawValue)
                    LabeledContent("Memory API", value: model.memoryAvailable.map { $0 ? "Available" : "Unavailable" } ?? "Not checked")
                    LabeledContent("Loaded memories", value: "\(model.memories.count)")
                    LabeledContent("Last successful refresh",
                                   value: model.lastRefresh?.formatted(date: .abbreviated, time: .shortened) ?? "—")
                    LabeledContent("Saved personality", value: model.savedProfile.map { $0.name.isEmpty ? "Unnamed" : $0.name } ?? "—")
                    Button("Refresh Diagnostics") {
                        Task { await model.refreshMemories(category: model.category) }
                        Task { await model.loadPersonality() }
                    }
                    .disabled(model.memoryBusy || model.personalityBusy || model.previewBusy)
                    if model.hasUnsavedChanges {
                        Text("Personality refresh is paused while you have unsaved changes.")
                            .foregroundStyle(.secondary)
                    }
                    SettingsFeedback(error: model.memoryError ?? model.personalityError)
                }
            }
            .formStyle(.grouped)
            .tabItem { Label("Diagnostics", systemImage: "stethoscope") }
        }
        .padding(12)
        .task {
            async let memory: Void = model.refreshMemories(category: model.category)
            async let personality: Void = model.loadPersonality()
            _ = await (memory, personality)
        }
    }
}

struct SettingsFeedback: View {
    let error: String?
    var notice: String? = nil

    var body: some View {
        if let error {
            Label(error, systemImage: "exclamationmark.circle").foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        } else if let notice {
            Label(notice, systemImage: "checkmark.circle").foregroundStyle(.secondary)
        }
    }
}
