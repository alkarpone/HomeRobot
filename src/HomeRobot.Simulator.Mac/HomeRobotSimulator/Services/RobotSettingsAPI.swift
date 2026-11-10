import Foundation
import Darwin

enum MemoryCategory: String, CaseIterable, Identifiable, Codable, Sendable {
    case personal, preferences, peoplePets = "people_pets", projects, routines, other
    var id: Self { self }
    var label: String {
        self == .peoplePets ? "People & Pets" : rawValue.capitalized
    }
}

struct StoredMemory: Decodable, Identifiable, Sendable {
    let id: UUID
    let content: String
    let category: MemoryCategory
    let createdAt: Date
    let updatedAt: Date?
}

struct MemoryDraft: Encodable, Sendable {
    var content = ""
    var category = MemoryCategory.other

    var isValid: Bool {
        content.utf16.count <= 500 &&
            content.utf16.contains { unit in
                UnicodeScalar(unit).map { CharacterSet.alphanumerics.contains($0) } ?? false
            } &&
            !containsControlCharacters(content)
    }
}

struct PersonalityProfile: Codable, Equatable, Sendable {
    var name: String
    var identity: String
    var description: String
    var language: String
    var communicationStyle: String
    var humorLevel: String
    var verbosity: String
    var defaultExpression: String
    var systemInstructions: String

    static let expressions = ["idle", "happy", "sad", "thinking", "error"]

    var isValid: Bool {
        let fields = [(name, 80), (identity, 200), (description, 300), (language, 80),
                      (communicationStyle, 200), (humorLevel, 100), (verbosity, 100), (systemInstructions, 600)]
        return fields.allSatisfy { $0.0.utf16.count <= $0.1 && !containsControlCharacters($0.0) } &&
            [identity, language, communicationStyle].allSatisfy { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } &&
            Self.expressions.contains(defaultExpression)
    }
}

private func containsControlCharacters(_ text: String) -> Bool {
    text.unicodeScalars.contains { $0.properties.generalCategory == .control }
}

enum PersonalityPreset: String, CaseIterable, Identifiable {
    case friendly = "Friendly", professional = "Professional", playful = "Playful", minimal = "Minimal"
    var id: Self { self }

    func apply(to profile: inout PersonalityProfile) {
        switch self {
        case .friendly:
            (profile.communicationStyle, profile.humorLevel, profile.verbosity, profile.defaultExpression) =
                ("Warm and approachable", "Occasional light humor", "Conversational and concise", "happy")
        case .professional:
            (profile.communicationStyle, profile.humorLevel, profile.verbosity, profile.defaultExpression) =
                ("Professional and precise", "None", "Detailed when useful", "idle")
        case .playful:
            (profile.communicationStyle, profile.humorLevel, profile.verbosity, profile.defaultExpression) =
                ("Playful and upbeat", "Frequent light humor", "Conversational and concise", "happy")
        case .minimal:
            (profile.communicationStyle, profile.humorLevel, profile.verbosity, profile.defaultExpression) =
                ("Direct and simple", "None", "Very brief", "idle")
        }
    }
}

enum SettingsAPIError: LocalizedError, Equatable {
    case invalidAddress, localOnly, network, invalidResponse
    case rejected(Int, String?)

    var errorDescription: String? {
        switch self {
        case .invalidAddress: return "Enter a valid WebSocket server URL on the Robot screen."
        case .localOnly, .rejected(403, _): return "Robot Settings requires a direct connection to the server on this Mac."
        case .network: return "Could not reach the server or the request timed out. Check the server and try again."
        case .invalidResponse: return "The server returned an unexpected response."
        case let .rejected(code, detail):
            let fallback = switch code {
            case 400: "The server rejected the input. Check the field limits and avoid credentials in memories."
            case 404: "The server does not have this item or endpoint. Refresh or check the server version."
            case 409: "The server reported a conflict. The memory may already exist or storage may be full."
            default: "The server returned HTTP \(code)."
            }
            return detail ?? fallback
        }
    }
}

struct RobotSettingsAPI {
    typealias Transport = @Sendable (URLRequest) async throws -> (Data, URLResponse)

    private let base: Result<URL, SettingsAPIError>
    private let transport: Transport

    init(serverURL: String, transport: @escaping Transport = { request in
        try await SettingsSession.shared.data(for: request)
    }) {
        base = Self.apiBase(serverURL)
        self.transport = transport
    }

    static func apiBase(_ address: String) -> Result<URL, SettingsAPIError> {
        guard var components = RobotConnectionService.serverComponents(address),
              let host = components.host?.lowercased() else { return .failure(.invalidAddress) }
        let address = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        var ipv4 = in_addr()
        var ipv6 = in6_addr()
        let localIPv4 = inet_pton(AF_INET, address, &ipv4) == 1 && UInt32(bigEndian: ipv4.s_addr) >> 24 == 127
        let localIPv6 = inet_pton(AF_INET6, address, &ipv6) == 1 && withUnsafeBytes(of: ipv6) { bytes in
            (bytes.prefix(15).allSatisfy { $0 == 0 } && bytes[15] == 1) ||
            (bytes.prefix(10).allSatisfy { $0 == 0 } && bytes[10] == 255 && bytes[11] == 255 && bytes[12] == 127)
        }
        guard host == "localhost" || localIPv4 || localIPv6 else { return .failure(.localOnly) }
        components.scheme = components.scheme?.lowercased() == "wss" ? "https" : "http"
        components.path = "/api"
        components.query = nil
        guard let url = components.url else { return .failure(.invalidAddress) }
        return .success(url)
    }

    func memories(category: MemoryCategory?) async throws -> [StoredMemory] {
        try await decode([StoredMemory].self, from: request("memory", query: category.map {
            URLQueryItem(name: "category", value: $0.rawValue)
        }))
    }

    func saveMemory(_ draft: MemoryDraft, id: UUID?) async throws {
        let body = try JSONEncoder().encode(draft)
        if let id {
            _ = try await request("memory", id: id, method: "PUT", body: body)
        } else {
            _ = try await request("memory", method: "POST", body: body)
        }
    }

    func deleteMemory(_ id: UUID) async throws {
        _ = try await request("memory", id: id, method: "DELETE")
    }

    func deleteAllMemories() async throws {
        _ = try await request("memory", method: "DELETE", confirmDeletion: true)
    }

    func personality() async throws -> PersonalityProfile {
        try await decode(PersonalityProfile.self, from: request("personality"))
    }

    func savePersonality(_ profile: PersonalityProfile) async throws -> PersonalityProfile {
        try await decode(PersonalityProfile.self, from: request("personality", method: "PUT",
            body: JSONEncoder().encode(profile)))
    }

    func preview(profile: PersonalityProfile, message: String) async throws -> String {
        struct Payload: Encodable { let profile: PersonalityProfile; let message: String }
        struct Reply: Decodable { let response: String }
        return try await decode(Reply.self, from: request("personality/preview", method: "POST",
            body: JSONEncoder().encode(Payload(profile: profile, message: message)), timeout: 150)).response
    }

    private func request(_ path: String, id: UUID? = nil, method: String = "GET", body: Data? = nil,
                         query: URLQueryItem? = nil, confirmDeletion: Bool = false,
                         timeout: TimeInterval = 15) async throws -> Data {
        var url = try base.get().appendingPathComponent(path)
        if let id { url.appendPathComponent(id.uuidString) }
        if let query { url.append(queryItems: [query]) }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpMethod = method
        request.httpBody = body
        if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if confirmDeletion { request.setValue("delete-all", forHTTPHeaderField: "X-Confirm-Memory-Deletion") }

        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await transport(request)
            try Task.checkCancellation()
        } catch {
            if Task.isCancelled || error is CancellationError { throw CancellationError() }
            throw SettingsAPIError.network
        }
        guard let http = response as? HTTPURLResponse else { throw SettingsAPIError.invalidResponse }
        guard (200..<300).contains(http.statusCode) else {
            struct Problem: Decodable { let title: String?; let detail: String? }
            let problem = try? JSONDecoder().decode(Problem.self, from: data)
            let detail = (problem?.detail ?? problem?.title).map { String($0.prefix(300)) }
            throw SettingsAPIError.rejected(http.statusCode, detail)
        }
        return data
    }

    private func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let value = try decoder.singleValueContainer().decode(String.self)
            for options: ISO8601DateFormatter.Options in [
                [.withInternetDateTime, .withFractionalSeconds], [.withInternetDateTime]
            ] {
                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = options
                if let date = formatter.date(from: value) { return date }
            }
            throw SettingsAPIError.invalidResponse
        }
        do { return try decoder.decode(type, from: data) }
        catch { throw SettingsAPIError.invalidResponse }
    }
}

private final class SettingsSession: NSObject, URLSessionTaskDelegate {
    static let shared: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        return URLSession(configuration: configuration, delegate: SettingsSession(), delegateQueue: nil)
    }()

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
