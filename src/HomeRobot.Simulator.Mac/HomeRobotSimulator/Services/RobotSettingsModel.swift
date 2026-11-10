import Foundation
import Combine

@MainActor
final class RobotSettingsModel: ObservableObject {
    enum MemoryDeletion {
        case one(StoredMemory), all
    }

    private let api: RobotSettingsAPI
    @Published private(set) var memories: [StoredMemory] = []
    @Published private(set) var category: MemoryCategory?
    @Published private(set) var lastRefresh: Date?
    @Published private(set) var memoryAvailable: Bool?
    @Published private(set) var memoryBusy = false
    @Published private(set) var memoryError: String?
    @Published private(set) var memoryNotice: String?
    @Published private(set) var pendingDeletion: MemoryDeletion?

    @Published private(set) var savedProfile: PersonalityProfile?
    @Published var draftProfile: PersonalityProfile? {
        didSet {
            if oldValue != draftProfile {
                clearPreview()
                personalityNotice = nil
            }
        }
    }
    var hasUnsavedChanges: Bool { draftProfile != savedProfile }
    @Published private(set) var personalityBusy = false
    @Published private(set) var personalityError: String?
    @Published private(set) var personalityNotice: String?
    @Published private(set) var previewReply: String?
    @Published private(set) var previewBusy = false
    @Published private(set) var previewError: String?

    init(api: RobotSettingsAPI) {
        self.api = api
    }

    func matchingMemories(_ query: String) -> [StoredMemory] {
        let term = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return memories.filter { term.isEmpty || $0.content.localizedStandardContains(term) }
    }

    func refreshMemories(category: MemoryCategory?) async {
        guard !memoryBusy else { return }
        memoryBusy = true
        defer { memoryBusy = false }
        memoryNotice = nil
        if self.category != category {
            self.category = category
            memories = []
        }
        await fetchMemories()
    }

    private func fetchMemories() async {
        memoryError = nil
        do {
            let loaded = try await api.memories(category: category)
            memories = loaded.sorted { ($0.updatedAt ?? $0.createdAt) > ($1.updatedAt ?? $1.createdAt) }
            lastRefresh = .now
            memoryAvailable = true
        } catch is CancellationError {
        } catch {
            memoryAvailable = false
            memoryError = error.localizedDescription
        }
    }

    func saveMemory(_ draft: MemoryDraft, id: UUID?) async throws {
        guard !memoryBusy, draft.isValid else {
            throw SettingsAPIError.rejected(400, "Wait for the current operation and check the memory fields.")
        }
        memoryBusy = true
        defer { memoryBusy = false }
        memoryNotice = nil
        try await api.saveMemory(draft, id: id)
        memoryNotice = "Memory saved."
        await fetchMemories()
    }

    func requestDeletion(_ deletion: MemoryDeletion) {
        guard !memoryBusy else { return }
        pendingDeletion = deletion
    }

    func cancelDeletion() {
        pendingDeletion = nil
    }

    func confirmDeletion() async {
        guard !memoryBusy, let deletion = pendingDeletion else { return }
        pendingDeletion = nil
        memoryBusy = true
        defer { memoryBusy = false }
        memoryNotice = nil
        memoryError = nil
        do {
            switch deletion {
            case .one(let memory):
                try await api.deleteMemory(memory.id)
                memories.removeAll { $0.id == memory.id }
                memoryNotice = "Memory deleted."
            case .all:
                try await api.deleteAllMemories()
                memories = []
                memoryNotice = "All memories deleted."
            }
        } catch is CancellationError {
        } catch {
            memoryError = error.localizedDescription
        }
    }

    func loadPersonality() async {
        guard !personalityBusy, !previewBusy, !hasUnsavedChanges else { return }
        personalityBusy = true
        defer { personalityBusy = false }
        personalityError = nil
        personalityNotice = nil
        do {
            let profile = try await api.personality()
            savedProfile = profile
            draftProfile = profile
        } catch is CancellationError {
        } catch {
            personalityError = error.localizedDescription
        }
    }

    func discardPersonalityChanges() {
        guard !personalityBusy, !previewBusy else { return }
        draftProfile = savedProfile
        personalityError = nil
    }

    func applyPreset(_ preset: PersonalityPreset) {
        guard !personalityBusy, !previewBusy, var profile = draftProfile else { return }
        preset.apply(to: &profile)
        draftProfile = profile
    }

    func savePersonality() async {
        guard !personalityBusy, !previewBusy, hasUnsavedChanges, let profile = draftProfile, profile.isValid else { return }
        personalityBusy = true
        defer { personalityBusy = false }
        personalityError = nil
        do {
            let saved = try await api.savePersonality(profile)
            savedProfile = saved
            if draftProfile == profile { draftProfile = saved }
            personalityNotice = "Personality saved."
        } catch is CancellationError {
        } catch {
            personalityError = error.localizedDescription
        }
    }

    func clearPreview() {
        previewReply = nil
        previewError = nil
    }

    func preview(_ message: String) async {
        let question = message.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !previewBusy, !personalityBusy, let profile = draftProfile, profile.isValid,
              !question.isEmpty, question.utf16.count <= 800 else { return }
        previewBusy = true
        defer { previewBusy = false }
        previewReply = nil
        previewError = nil
        do {
            let reply = try await api.preview(profile: profile, message: question)
            if draftProfile == profile { previewReply = reply }
        } catch is CancellationError {
        } catch {
            previewError = error.localizedDescription
        }
    }
}
