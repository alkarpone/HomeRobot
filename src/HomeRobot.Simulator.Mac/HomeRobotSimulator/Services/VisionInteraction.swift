import AVFoundation
import Combine
import Foundation

@MainActor
final class VisionInteraction: ObservableObject {
    enum Query { case selected, fresh, followUp, past, scan }

    struct Answer: Decodable {
        let response: String
        let expression: String?
        let sessionId: UUID
        let cached: Bool
        let inferenceMs: Int
        let processingMs: Int
        let contextLifetimeSeconds: Int?
    }

    private struct Request: Encodable {
        let image: Data?
        let question: String
        let sessionId: UUID
        let capturedAt: Date?
        let scan: Bool
        let conversationSessionId: UUID?
    }

    private struct Problem: Decodable { let message: String }

    private final class LocalTransport: NSObject, URLSessionTaskDelegate {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                        newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
            completionHandler(nil)
        }
    }

    private static let transport = URLSession(configuration: .ephemeral, delegate: LocalTransport(), delegateQueue: nil)
    typealias Transport = (URLRequest) async throws -> (Data, URLResponse)
    let camera: any CameraSource
    private let send: Transport

    convenience init(camera: any CameraSource = VisionCamera(), send: Transport? = nil) {
        self.init(camera: camera, clock: { Date() }, send: send)
    }

    init(camera: any CameraSource, clock: @escaping () -> Date, send: Transport?) {
        self.camera = camera
        self.clock = clock
        self.send = send ?? { try await Self.transport.data(for: $0) }
    }
    @Published var deviceID = ""
    @Published private(set) var devices: [CameraDevice] = []
    @Published private(set) var cameraActive = false
    @Published private(set) var preparing = false
    @Published private(set) var cameraStatus = "Camera stopped"
    @Published private(set) var image: Data?
    @Published private(set) var source = ""
    @Published private(set) var answer: Answer?
    @Published private(set) var analyzing = false
    @Published private(set) var lastCaptureAt: Date?
    @Published private(set) var lastAnalysisAt: Date?
    @Published private(set) var lastScanAt: Date?
    @Published private(set) var scanAnswer: Answer?
    @Published private(set) var analyses = 0
    @Published private(set) var unchanged = 0
    @Published private(set) var changeScore: Double?
    @Published private(set) var scanning = false
    @Published var autoScan = false {
        didSet {
            if !autoScan {
                scanBaseline = nil
                scanSession = UUID()
            }
        }
    }
    @Published var scanInterval = 30
    @Published var detectChanges = true
    @Published var changeThreshold = 0.035
    var hasContext: Bool { contextAge != nil }
    var contextAge: Int? {
        guard let context, clock() < context.expires else { return nil }
        return Int(max(0, clock().timeIntervalSince(context.captured)))
    }
    @Published private(set) var captureMs: Int?
    @Published private(set) var requestMs: Int?
    @Published var error: String?
    @Published private var context: (base: URL, id: UUID, captured: Date, expires: Date)?
    private var session = UUID()
    private var sessionBase: URL?
    private var cameraGeneration = UUID()
    private var scanSession = UUID()
    private var scanBaseline: [UInt8]?
    private var scanBase: URL?
    private var selectedAt: Date?
    private let clock: () -> Date
    private var operation: Task<Void, Never>?

    func refreshDevices() {
        devices = camera.devices()
        if cameraActive, !devices.contains(where: { $0.id == deviceID }) {
            cameraFailed("Vybraná kamera byla odpojena.")
        }
        if !devices.contains(where: { $0.id == deviceID }) { deviceID = devices.first?.id ?? "" }
    }

    func startCamera() {
        guard !cameraActive, !preparing, !analyzing else { return }
        prepare(status: "Starting camera…") { try await self.ensureCamera() }
    }

    func stopCamera() {
        autoScan = false
        cameraGeneration = UUID()
        operation?.cancel()
        camera.stop()
        cameraActive = false
        cameraStatus = "Camera stopped"
    }

    func cameraFailed(_ message: String) {
        guard cameraActive || preparing || analyzing else { return }
        stopCamera()
        error = message
    }

    func capture() {
        guard cameraActive, !analyzing else { return }
        prepare(status: "Capturing image…") { try await self.select(self.camera, label: "Camera still") }
    }

    func importImage(_ url: URL) {
        guard !preparing, !analyzing else { return }
        stopCamera()
        prepare(status: "Importing image…") { try await self.select(ImportedImage(url: url), label: "Imported image") }
    }

    func close() {
        stopCamera()
        clear()
    }

    func clear() {
        forgetContext()
        image = nil
        selectedAt = nil
        source = ""
        answer = nil
        captureMs = nil
        requestMs = nil
        error = nil
    }

    private func prepare(status: String, _ action: @escaping @MainActor () async throws -> Void) {
        guard operation == nil else { return }
        preparing = true
        cameraStatus = status
        error = nil
        operation = Task {
            defer {
                operation = nil
                preparing = false
                cameraStatus = cameraActive ? "Camera running" : "Camera stopped"
            }
            do { try await action() }
            catch { if !Task.isCancelled { self.error = error.localizedDescription } }
        }
    }

    private func ensureCamera() async throws {
        if cameraActive { return }
        refreshDevices()
        cameraStatus = "Starting camera…"
        let generation = cameraGeneration
        do {
            try await camera.start(deviceID: deviceID.isEmpty ? nil : deviceID)
            try Task.checkCancellation()
            guard cameraGeneration == generation else { throw CancellationError() }
            cameraActive = true
            cameraStatus = "Camera running"
        } catch {
            if Task.isCancelled || cameraGeneration != generation { camera.stop() }
            cameraStatus = "Camera stopped"
            throw error
        }
    }

    private func acquire(_ input: any StillImageSource) async throws -> (Data, Date) {
        let started = ContinuousClock.now
        let generation = cameraGeneration
        let original = try await input.stillImage()
        try Task.checkCancellation()
        guard generation == cameraGeneration else { throw CancellationError() }
        let captured = clock()
        captureMs = Self.elapsed(started)
        if input is any CameraSource { lastCaptureAt = captured }
        return (original, captured)
    }

    private func select(_ input: any StillImageSource, label: String) async throws {
        let (original, captured) = try await acquire(input)
        image = original
        selectedAt = captured
        source = label
    }

    // Owned by the panel's lifecycle task. A busy tick is dropped.
    func runAutoScan(address: String, busy: () -> Bool,
                     monotonic: () -> ContinuousClock.Instant = { .now },
                     sleep: (Duration) async throws -> Void = { try await Task.sleep(for: $0) }) async {
        let generation = scanSession
        let interval = Duration.seconds(scanInterval)
        var deadline = monotonic() + interval
        while autoScan && generation == scanSession && !Task.isCancelled {
            let now = monotonic()
            if deadline <= now { deadline = now + interval }
            do { try await sleep(now.duration(to: deadline)) } catch { return }
            deadline += interval
            guard autoScan, generation == scanSession, !Task.isCancelled else { return }
            lastScanAt = clock()
            guard !busy(), !analyzing, !preparing else { continue }
            do { _ = try await perform("Stručně popiš, co je na snímku.", address: address, query: .scan) {} }
            catch {
                guard !(error is CancellationError), !Task.isCancelled, autoScan, generation == scanSession else { return }
                self.error = error.localizedDescription
                if !cameraActive { autoScan = false }
            }
        }
    }

    func analyze(_ question: String, address: String, query: Query,
                 conversationSessionId: UUID? = nil, thinking: () -> Void) async throws -> Answer {
        if let answer = try await perform(question, address: address, query: query, conversationSessionId: conversationSessionId, thinking: thinking) {
            return answer
        }
        // A follow-up can outlive the server context or its bounded cache.
        if query == .followUp, let answer = try await perform(question, address: address, query: .fresh, conversationSessionId: conversationSessionId, thinking: thinking) {
            return answer
        }
        throw VisionFailure("Předchozí snímek už není dostupný.")
    }

    private func perform(_ question: String, address: String, query: Query,
                         conversationSessionId: UUID? = nil, thinking: () -> Void) async throws -> Answer? {
        try Task.checkCancellation()
        guard !analyzing, !preparing else { throw VisionFailure("Zpracování obrázku už probíhá.") }
        let trimmed = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.utf16.count <= 500 else { throw VisionFailure("Dotaz smí mít nejvýše 500 znaků.") }
        let base: URL
        do { base = try RobotSettingsAPI.apiBase(address).get().appendingPathComponent("vision") }
        catch { throw VisionFailure("Pro analýzu obrazu použijte server na tomto Macu (localhost).") }

        analyzing = true
        let scan = query == .scan
        scanning = scan
        defer { analyzing = false; scanning = false }
        self.error = nil
        requestMs = nil
        if !scan, let sessionBase, sessionBase != base { forgetContext() }
        let id = scan ? scanSession : session
        let generation = cameraGeneration
        let jpeg: Data?
        let capturedAt: Date?
        var signature: [UInt8]?
        switch query {
        case .followUp, .past:
            guard hasContext, let context, context.base == base else { return nil }
            capturedAt = nil
            jpeg = nil
        case .fresh, .scan:
            try await ensureCamera()
            if scan {
                let (original, captured) = try await acquire(camera)
                let sample = try await VisionImage.signature(original)
                let score = scanBase == base ? scanBaseline.map { VisionImage.changeScore($0, sample) } : nil
                changeScore = score
                if detectChanges, let score, score < changeThreshold {
                    unchanged += 1
                    return nil
                }
                signature = sample
                jpeg = try await VisionImage.prepare(original)
                capturedAt = captured
            } else {
                let (original, captured) = try await acquire(camera)
                image = original
                selectedAt = captured
                source = "Camera still"
                capturedAt = captured
                jpeg = try await VisionImage.prepare(original)
            }
        case .selected:
            guard let image else {
                throw VisionFailure("Nemám žádný obrázek. Pořiďte nebo importujte snímek.")
            }
            capturedAt = selectedAt
            jpeg = try await VisionImage.prepare(image)
        }
        try Task.checkCancellation()
        guard generation == cameraGeneration, id == (scan ? scanSession : session) else { throw CancellationError() }

        thinking()
        var request = URLRequest(url: base.appendingPathComponent("analyze"), timeoutInterval: 150)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        request.httpBody = try encoder.encode(Request(image: jpeg, question: trimmed.isEmpty ? "Co vidíš?" : trimmed,
                                                      sessionId: id, capturedAt: capturedAt, scan: scan,
                                                      conversationSessionId: conversationSessionId))
        if !scan { sessionBase = base }
        let started = ContinuousClock.now
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await send(request) }
        catch {
            try Task.checkCancellation()
            if (error as? URLError)?.code == .timedOut {
                throw VisionFailure("Analýza obrázku překročila časový limit.")
            }
            throw VisionFailure("Spojení se serverem pro analýzu selhalo: \(error.localizedDescription)")
        }
        try Task.checkCancellation()
        guard generation == cameraGeneration, id == (scan ? scanSession : session) else { throw CancellationError() }
        requestMs = Self.elapsed(started)
        let status = (response as? HTTPURLResponse)?.statusCode
        guard status == 200 else {
            if status == 404, jpeg == nil {
                context = nil
                return nil
            }
            throw VisionFailure((try? JSONDecoder().decode(Problem.self, from: data).message)
                ?? "Server nezpracoval obrázek (HTTP \(status ?? 0)).")
        }
        guard let result = try? JSONDecoder().decode(Answer.self, from: data) else {
            throw VisionFailure("Server vrátil neplatnou odpověď na analýzu obrázku.")
        }
        lastAnalysisAt = clock()
        analyses += 1
        if scan {
            scanBaseline = signature
            scanBase = base
            scanAnswer = result
        } else {
            if let capturedAt {
                context = (base, result.sessionId, capturedAt,
                           capturedAt.addingTimeInterval(Double(result.contextLifetimeSeconds ?? 120)))
            }
            answer = result
        }
        return result
    }

    private func forgetContext() {
        let id = session
        let base = sessionBase
        session = UUID()
        sessionBase = nil
        context = nil
        guard let base else { return }
        Task { [send] in
            var request = URLRequest(url: base.appendingPathComponent(id.uuidString), timeoutInterval: 5)
            request.httpMethod = "DELETE"
            _ = try? await send(request)
        }
    }

    private static func elapsed(_ start: ContinuousClock.Instant) -> Int {
        let value = start.duration(to: .now).components
        return Int(value.seconds * 1000 + value.attoseconds / 1_000_000_000_000_000)
    }
}
