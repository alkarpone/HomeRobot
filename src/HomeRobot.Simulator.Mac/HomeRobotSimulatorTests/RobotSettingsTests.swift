import Foundation
import XCTest
@testable import HomeRobotSimulator

@MainActor
final class RobotSettingsTests: XCTestCase {
    private static let id = UUID(uuidString: "96872ae4-d7b5-4c56-bd23-547ac47a0b68")!
    private static let memory = """
        {"id":"\(id.uuidString)","content":"Gessa likes walks","category":"people_pets",
         "createdAt":"2026-11-05T08:30:00.1234567+00:00","updatedAt":"2026-11-05T08:31:00Z"}
        """
    private static let profile = PersonalityProfile(
        name: "Robot", identity: "Home assistant", description: "Local robot", language: "Czech",
        communicationStyle: "Custom style", humorLevel: "", verbosity: "", defaultExpression: "idle",
        systemInstructions: "Use metric units."
    )

    func testLoadsMemoryDatesAndCombinesServerCategoryWithTextSearch() async {
        let stub = Stub([.init(200, "[\(Self.memory)]")])
        let model = makeModel(stub)
        await model.refreshMemories(category: .peoplePets)

        XCTAssertEqual(model.memories.first?.id, Self.id)
        XCTAssertEqual(model.memories.first?.category, .peoplePets)
        XCTAssertNotNil(model.memories.first?.updatedAt)
        XCTAssertNotNil(model.lastRefresh)
        XCTAssertEqual(model.memoryAvailable, true)
        XCTAssertEqual(model.matchingMemories("  GESSA ").count, 1)
        XCTAssertTrue(model.matchingMemories("cat").isEmpty)
        let requests = await stub.requests
        XCTAssertEqual(requests.first?.url?.path, "/api/memory")
        XCTAssertEqual(requests.first?.url?.query, "category=people_pets")
    }

    func testCreateAndEditSendCorrectPayloadAndRefresh() async throws {
        let edited = Self.memory.replacingOccurrences(of: "Gessa likes walks", with: "Gessa likes parks")
        let stub = Stub([.init(201, Self.memory), .init(200, "[\(Self.memory)]"),
                         .init(204), .init(200, "[\(edited)]")])
        let model = makeModel(stub)
        try await model.saveMemory(.init(content: "Gessa likes walks", category: .peoplePets), id: nil)
        XCTAssertEqual(model.memories.first?.content, "Gessa likes walks")
        try await model.saveMemory(.init(content: "Gessa likes parks", category: .peoplePets), id: Self.id)
        XCTAssertEqual(model.memories.first?.content, "Gessa likes parks")
        let requests = await stub.requests
        XCTAssertEqual(requests.compactMap(\.httpMethod), ["POST", "GET", "PUT", "GET"])
        XCTAssertEqual(requests[2].url?.path, "/api/memory/\(Self.id.uuidString)")
        let payload = try JSONDecoder().decode([String: String].self, from: XCTUnwrap(requests[2].httpBody))
        XCTAssertEqual(payload, ["category": "people_pets", "content": "Gessa likes parks"])
    }

    func testIndividualDeleteKeepsRowOnFailureAndRemovesItOnSuccess() async throws {
        let stub = Stub([.init(200, "[\(Self.memory)]"), .init(409), .init(204)])
        let model = makeModel(stub)
        await model.refreshMemories(category: nil)
        let row = try XCTUnwrap(model.memories.first)
        model.requestDeletion(.one(row))
        await model.confirmDeletion()
        XCTAssertNotNil(model.memoryError)
        XCTAssertEqual(model.memories.count, 1)
        model.requestDeletion(.one(row))
        await model.confirmDeletion()
        XCTAssertTrue(model.memories.isEmpty)
        let requests = await stub.requests
        XCTAssertEqual(requests.last?.httpMethod, "DELETE")
        XCTAssertEqual(requests.last?.url?.path, "/api/memory/\(row.id.uuidString)")
        XCTAssertNil(requests.last?.value(forHTTPHeaderField: "X-Confirm-Memory-Deletion"))
    }

    func testBulkDeletionOnlySendsRequestAfterConfirmation() async {
        let stub = Stub([.init(200, "[\(Self.memory)]"), .init(204)])
        let model = makeModel(stub)
        await model.refreshMemories(category: .peoplePets)
        model.requestDeletion(.all)
        model.cancelDeletion()
        await model.confirmDeletion()
        let before = await stub.requests
        XCTAssertEqual(before.count, 1)
        XCTAssertEqual(model.memories.count, 1)

        model.requestDeletion(.all)
        await model.confirmDeletion()
        await model.confirmDeletion()
        let requests = await stub.requests
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests.last?.httpMethod, "DELETE")
        XCTAssertEqual(requests.last?.url?.path, "/api/memory")
        XCTAssertNil(requests.last?.url?.query)
        XCTAssertEqual(requests.last?.value(forHTTPHeaderField: "X-Confirm-Memory-Deletion"), "delete-all")
        XCTAssertTrue(model.memories.isEmpty)
    }

    func testPersonalityDraftPresetPreviewDiscardAndExplicitSave() async throws {
        let original = Self.profile
        var saved = original
        saved.name = "New Name"
        let stub = Stub([try .encoded(original), .init(200, #"{"response":"Hello!"}"#), try .encoded(saved)])
        let model = makeModel(stub)
        await model.loadPersonality()
        XCTAssertEqual(model.savedProfile, original)
        model.applyPreset(.playful)
        XCTAssertTrue(model.hasUnsavedChanges)
        XCTAssertEqual(model.draftProfile?.communicationStyle, "Playful and upbeat")
        XCTAssertEqual(model.draftProfile?.systemInstructions, original.systemInstructions)
        await model.loadPersonality()
        XCTAssertEqual(model.draftProfile?.communicationStyle, "Playful and upbeat")
        await model.preview("Introduce yourself in Czech.")
        XCTAssertEqual(model.previewReply, "Hello!")
        XCTAssertEqual(model.savedProfile, original)
        let before = await stub.requests
        XCTAssertEqual(before.compactMap(\.httpMethod), ["GET", "POST"])
        XCTAssertEqual(before.last?.url?.path, "/api/personality/preview")
        struct Preview: Decodable { let profile: PersonalityProfile; let message: String }
        let preview = try JSONDecoder().decode(Preview.self, from: XCTUnwrap(before.last?.httpBody))
        XCTAssertEqual(preview.profile, model.draftProfile)
        XCTAssertEqual(preview.message, "Introduce yourself in Czech.")
        model.discardPersonalityChanges()
        XCTAssertFalse(model.hasUnsavedChanges)
        XCTAssertNil(model.previewReply)
        model.draftProfile?.name = saved.name
        await model.savePersonality()
        XCTAssertEqual(model.savedProfile, saved)
        XCTAssertFalse(model.hasUnsavedChanges)
        let requests = await stub.requests
        XCTAssertEqual(requests.last?.httpMethod, "PUT")
        XCTAssertEqual(try JSONDecoder().decode(PersonalityProfile.self, from: XCTUnwrap(requests.last?.httpBody)), saved)
    }

    func testNetworkInvalidResponseAndServerValidationErrors() async throws {
        let stub = Stub([.init(nil), .init(200, #"{"unexpected":true}"#),
                         .init(400, #"{"title":"Validation failed","detail":"Memory rejected."}"#)])
        let model = makeModel(stub)
        await model.refreshMemories(category: nil)
        XCTAssertEqual(model.memoryError, SettingsAPIError.network.localizedDescription)
        await model.refreshMemories(category: nil)
        XCTAssertEqual(model.memoryError, SettingsAPIError.invalidResponse.localizedDescription)
        let draft = MemoryDraft(content: "Gessa likes walks")
        do {
            try await model.saveMemory(draft, id: nil)
            XCTFail("Expected server validation error")
        } catch {
            XCTAssertEqual(error as? SettingsAPIError, .rejected(400, "Memory rejected."))
        }
        XCTAssertFalse(model.memoryBusy)
        let requests = await stub.requests
        XCTAssertEqual(requests.count, 3)
        XCTAssertFalse(MemoryDraft(content: String(repeating: "a", count: 501)).isValid)
        XCTAssertFalse(MemoryDraft(content: " \n ").isValid)
    }

    func testSettingsNeverSendsRequestsToRemoteHost() async {
        let stub = Stub([])
        let api = RobotSettingsAPI(serverURL: "ws://robot.example:5000/ws/robot") { try await stub.send($0) }
        do {
            _ = try await api.memories(category: nil)
            XCTFail("Expected local-only error")
        } catch {
            XCTAssertEqual(error as? SettingsAPIError, .localOnly)
        }
        let requests = await stub.requests
        XCTAssertTrue(requests.isEmpty)
    }

    private func makeModel(_ stub: Stub) -> RobotSettingsModel {
        RobotSettingsModel(api: RobotSettingsAPI(serverURL: "ws://localhost:5080/ws/robot") {
            try await stub.send($0)
        })
    }

    private actor Stub {
        struct Reply: Sendable {
            let status: Int?
            let data: Data
            init(_ status: Int?, _ body: String = "") {
                self.status = status
                data = Data(body.utf8)
            }
            static func encoded(_ profile: PersonalityProfile) throws -> Self {
                try .init(200, String(decoding: JSONEncoder().encode(profile), as: UTF8.self))
            }
        }

        private var replies: [Reply]
        private(set) var requests: [URLRequest] = []

        init(_ replies: [Reply]) { self.replies = replies }

        func send(_ request: URLRequest) throws -> (Data, URLResponse) {
            requests.append(request)
            guard !replies.isEmpty, let url = request.url else { throw URLError(.badURL) }
            let reply = replies.removeFirst()
            guard let status = reply.status else { throw URLError(.notConnectedToInternet) }
            return (reply.data, HTTPURLResponse(url: url, statusCode: status, httpVersion: nil, headerFields: nil)!)
        }
    }
}
