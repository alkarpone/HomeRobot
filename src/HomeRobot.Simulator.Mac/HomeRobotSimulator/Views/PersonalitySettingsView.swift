import SwiftUI

struct PersonalitySettingsView: View {
    @ObservedObject var model: RobotSettingsModel
    @State private var question = ""
    @State private var previewRequest: String?
    @State private var confirmingDiscard = false

    var body: some View {
        Form {
            if let profile = Binding<PersonalityProfile>($model.draftProfile) {
                editor(profile)
                    .disabled(model.personalityBusy || model.previewBusy || previewRequest != nil)
                Section {
                    HStack {
                        Button("Reload") { Task { await model.loadPersonality() } }
                            .disabled(model.hasUnsavedChanges)
                        if model.hasUnsavedChanges {
                            Button("Discard Changes…") { confirmingDiscard = true }
                        }
                        Spacer()
                        Button("Save") { Task { await model.savePersonality() } }
                            .buttonStyle(.borderedProminent)
                            .disabled(!model.hasUnsavedChanges || !profile.wrappedValue.isValid)
                    }
                    .disabled(model.personalityBusy || model.previewBusy || previewRequest != nil)
                    if model.personalityBusy { ProgressView().controlSize(.small) }
                    if model.hasUnsavedChanges {
                        Text(profile.wrappedValue.isValid ? "Unsaved changes" : "Check required fields and character limits. Line breaks are not supported.")
                            .font(.callout).foregroundStyle(.secondary)
                    }
                    SettingsFeedback(error: model.personalityError, notice: model.personalityNotice)
                }
                previewSection(profile.wrappedValue)
            } else {
                Section("Personality") {
                    if model.personalityBusy {
                        ProgressView("Loading personality…")
                    } else {
                        SettingsFeedback(error: model.personalityError)
                        Button("Load Personality") { Task { await model.loadPersonality() } }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .alert("Discard Unsaved Changes?", isPresented: $confirmingDiscard) {
            Button("Cancel", role: .cancel) {}
            Button("Discard Changes", role: .destructive) { model.discardPersonalityChanges() }
        } message: {
            Text("Restore the last loaded personality. Changes you made in this window will be lost.")
        }
        .task(id: previewRequest) {
            guard let request = previewRequest else { return }
            await model.preview(request)
            previewRequest = nil
        }
        .onDisappear { previewRequest = nil }
    }

    @ViewBuilder
    private func editor(_ profile: Binding<PersonalityProfile>) -> some View {
        Section("Identity") {
            TextField("Name", text: profile.name, prompt: Text("Optional · up to 80 characters"))
            TextField("Identity", text: profile.identity, prompt: Text("Required · up to 200 characters"))
            longField("Description", value: profile.description, limit: 300)
        }
        Section("Communication") {
            choices("Language", options: ["Czech", "English", "Slovak", "German"], value: profile.language)
            choices("Style", options: ["Friendly, natural and honest", "Warm and approachable", "Professional and precise",
                                       "Playful and upbeat", "Direct and simple"], value: profile.communicationStyle)
            choices("Humor", options: ["None", "Occasional light humor", "Frequent light humor"], value: profile.humorLevel)
            choices("Verbosity", options: ["Very brief", "Brief, with technical detail only when requested",
                                          "Conversational and concise", "Detailed when useful"], value: profile.verbosity)
            Picker("Default Expression", selection: profile.defaultExpression) {
                ForEach(PersonalityProfile.expressions, id: \.self) { Text($0.capitalized).tag($0) }
            }
            Menu("Apply Preset") {
                ForEach(PersonalityPreset.allCases) { preset in
                    Button(preset.rawValue) { model.applyPreset(preset) }
                }
            }
            Text("Presets fill in style, humor, verbosity and expression. Click Save to apply them to the robot.")
                .font(.callout).foregroundStyle(.secondary)
        }
        Section("Custom Instructions") {
            longField("System Instructions", value: profile.systemInstructions, limit: 600)
            Text("Optional. The server’s safety rules always take priority.")
                .font(.callout).foregroundStyle(.secondary)
        }
    }

    private func longField(_ title: String, value: Binding<String>, limit: Int) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
            TextEditor(text: value)
                .accessibilityLabel(title)
                .font(.body)
                .frame(minHeight: 64)
                .border(.quaternary)
            Text("\(value.wrappedValue.utf16.count) / \(limit) · No line breaks")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func choices(_ title: String, options: [String], value: Binding<String>) -> some View {
        let isCustom = !options.contains(value.wrappedValue)
        return VStack(alignment: .leading) {
            Picker(title, selection: Binding<String?>(
                get: { options.contains(value.wrappedValue) ? value.wrappedValue : nil },
                set: { value.wrappedValue = $0 ?? "" }
            )) {
                ForEach(options, id: \.self) { Text($0).tag(Optional($0)) }
                Text("Custom").tag(nil as String?)
            }
            if isCustom {
                TextField("Custom \(title)", text: value)
            }
        }
    }

    private func previewSection(_ profile: PersonalityProfile) -> some View {
        Section("Text Preview") {
            Text("Try the current draft. The preview does not use saved memories or conversation history.")
                .font(.callout).foregroundStyle(.secondary)
            TextField("Sample Question", text: $question, prompt: Text("Introduce yourself in Czech."))
                .disabled(model.previewBusy || previewRequest != nil)
            HStack {
                Button("Preview Response") { previewRequest = question }
                    .disabled(model.previewBusy || previewRequest != nil || model.personalityBusy ||
                              !profile.isValid || question.utf16.count > 800 ||
                              question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                if model.previewBusy { ProgressView().controlSize(.small) }
            }
            if question.utf16.count > 800 { Text("Use at most 800 characters.").font(.caption) }
            if let reply = model.previewReply {
                Text(reply).textSelection(.enabled)
            }
            SettingsFeedback(error: model.previewError)
        }
        .onChange(of: question) { model.clearPreview() }
    }
}
