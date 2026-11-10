import SwiftUI

struct MemorySettingsView: View {
    @ObservedObject var model: RobotSettingsModel
    @State private var query = ""
    @State private var editor: Editor?
    @State private var confirmingDeletion = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                TextField("Search memory contents", text: $query)
                    .textFieldStyle(.roundedBorder)
                Picker("Category", selection: Binding(
                    get: { model.category },
                    set: { category in Task { await model.refreshMemories(category: category) } }
                )) {
                    Text("All Categories").tag(nil as MemoryCategory?)
                    ForEach(MemoryCategory.allCases) { Text($0.label).tag(Optional($0)) }
                }
                .labelsHidden()
                .accessibilityLabel("Filter by category")
                .disabled(model.memoryBusy)
            }
            HStack {
                Button { editor = Editor() } label: { Label("Add Memory", systemImage: "plus") }
                Button { Task { await model.refreshMemories(category: model.category) } } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                Spacer()
                if model.memoryBusy { ProgressView().controlSize(.small).accessibilityLabel("Updating memories") }
            }
            .disabled(model.memoryBusy)

            if model.memoryError != nil || model.memoryNotice != nil {
                SettingsFeedback(error: model.memoryError, notice: model.memoryNotice)
            }

            let results = model.matchingMemories(query)
            if results.isEmpty {
                if model.memoryBusy {
                    Text("Updating memories…").frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if model.memoryError != nil {
                    ContentUnavailableView("Could Not Load Memories", systemImage: "exclamationmark.triangle")
                } else {
                    ContentUnavailableView(
                        query.isEmpty && model.category == nil ? "No Memories Yet" : "No Matching Memories",
                        systemImage: "tray",
                        description: Text(query.isEmpty && model.category == nil
                                          ? "Add a memory to keep it on the robot server."
                                          : "Try a different search or category.")
                    )
                }
            } else {
                List(results) { memory in
                    HStack(alignment: .top, spacing: 12) {
                        VStack(alignment: .leading, spacing: 5) {
                            Text(memory.content).font(.body).textSelection(.enabled)
                            Text(memory.category.label).font(.subheadline).foregroundStyle(.secondary)
                            Text("Created: \(memory.createdAt.formatted(date: .abbreviated, time: .shortened))")
                            if let updated = memory.updatedAt {
                                Text("Updated: \(updated.formatted(date: .abbreviated, time: .shortened))")
                            }
                        }
                        .font(.caption)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        HStack {
                            Button {
                                editor = Editor(memory: memory)
                            } label: {
                                Label("Edit memory", systemImage: "pencil").labelStyle(.iconOnly)
                            }
                            .help("Edit memory")
                            Button(role: .destructive) {
                                confirm(.one(memory))
                            } label: {
                                Label("Delete memory", systemImage: "trash").labelStyle(.iconOnly)
                            }
                            .help("Delete memory")
                        }
                        .buttonStyle(.borderless)
                        .disabled(model.memoryBusy)
                    }
                    .padding(.vertical, 5)
                }
                .listStyle(.inset)
            }

            Divider()
            VStack(alignment: .leading, spacing: 6) {
                Text("Destructive Actions").font(.headline)
                Text("Delete every stored memory, including records outside the current filter.")
                    .font(.callout).foregroundStyle(.secondary)
                Button("Delete All Memories…", role: .destructive) { confirm(.all) }
                    .tint(.red)
                    .disabled(model.memoryBusy || model.memoryAvailable != true)
            }
        }
        .padding()
        .sheet(item: $editor) { item in
            MemoryEditorView(memory: item.memory, category: model.category, model: model)
        }
        .alert("Delete Memories?", isPresented: $confirmingDeletion, presenting: model.pendingDeletion) { target in
            Button("Cancel", role: .cancel) { model.cancelDeletion() }
            Button(deleteTitle(target), role: .destructive) {
                Task { await model.confirmDeletion() }
            }
        } message: { target in
            switch target {
            case .one(let memory): Text("Permanently delete “\(memory.content)”?")
            case .all: Text("Permanently delete all memories from the server, regardless of the current filter? This cannot be undone.")
            }
        }
    }

    private func confirm(_ target: RobotSettingsModel.MemoryDeletion) {
        model.requestDeletion(target)
        confirmingDeletion = true
    }

    private func deleteTitle(_ target: RobotSettingsModel.MemoryDeletion) -> String {
        switch target {
        case .one: "Delete Memory"
        case .all: "Delete All Memories"
        }
    }

    private struct Editor: Identifiable {
        let id = UUID()
        var memory: StoredMemory?
    }
}

private struct MemoryEditorView: View {
    @Environment(\.dismiss) private var dismiss
    let memory: StoredMemory?
    @ObservedObject var model: RobotSettingsModel
    @State private var draft: MemoryDraft
    @State private var error: String?
    @State private var submitting = false

    init(memory: StoredMemory?, category: MemoryCategory?, model: RobotSettingsModel) {
        self.memory = memory
        self.model = model
        _draft = State(initialValue: MemoryDraft(content: memory?.content ?? "",
                                                category: memory?.category ?? category ?? .other))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(memory == nil ? "Add Memory" : "Edit Memory").font(.title2.bold())
            Text("Content").font(.headline)
            TextEditor(text: $draft.content)
                .accessibilityLabel("Memory content")
                .font(.body)
                .frame(minHeight: 110)
                .border(.quaternary)
                .disabled(submitting)
            Text("\(draft.content.utf16.count) / 500 · Use one line with at least one letter or number.")
                .font(.caption).foregroundStyle(.secondary)
            Picker("Category", selection: $draft.category) {
                ForEach(MemoryCategory.allCases) { Text($0.label).tag($0) }
            }
            .disabled(submitting)
            SettingsFeedback(error: error)
            HStack {
                if submitting { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(submitting)
                Button("Save") {
                    submitting = true
                    error = nil
                    Task {
                        defer { submitting = false }
                        do {
                            try await model.saveMemory(draft, id: memory?.id)
                            dismiss()
                        } catch is CancellationError {
                        } catch {
                            self.error = error.localizedDescription
                        }
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!draft.isValid || submitting || model.memoryBusy)
            }
        }
        .padding(20)
        .frame(width: 460)
        .interactiveDismissDisabled(submitting)
    }
}
