import AVFoundation
import Combine
import Foundation

@MainActor
final class VoiceInteraction: NSObject, ObservableObject, AVAudioPlayerDelegate {
    enum Phase: String {
        case idle, listening, transcribing, capturing, thinking, executing, speaking, error
    }

    @Published private(set) var phase = Phase.idle
    @Published private(set) var conversationalExpression: RobotExpression?
    @Published private(set) var recordingSeconds = 0.0
    @Published private(set) var transcription = ""
    @Published private(set) var correctedText = ""
    @Published private(set) var correctionStatus: String?
    @Published private(set) var intent: String?
    @Published private(set) var validation: String?
    @Published private(set) var analysisMs: Int?
    @Published private(set) var reply = ""
    @Published private(set) var errorMessage: String?
    @Published private(set) var plan: AIPlan?
    @Published private(set) var sttMs: Int?
    @Published private(set) var llmMs: Int?
    @Published private(set) var ttsMs: Int?
    @Published private(set) var totalMs: Int?
    @Published private(set) var requestingPermission = false

    let vision: VisionInteraction
    private let fetchSpeech: ((URLRequest) async throws -> Data)?

    init(vision: VisionInteraction = VisionInteraction(),
         fetchSpeech: ((URLRequest) async throws -> Data)? = nil) {
        self.vision = vision
        self.fetchSpeech = fetchSpeech
        super.init()
    }

    private var recorder: AVAudioRecorder?
    private var player: AVAudioPlayer?
    private var envelope: SpeechEnvelope?
    private var playbackWatch: Task<Void, Never>?
    private var audioFile: URL?
    private var work: Task<Void, Never>?
    private var meter: Task<Void, Never>?
    private var turn = UUID()
    private var sessionID: UUID?
    private var chatServer: URL?
    private let clock = ContinuousClock()

    var busy: Bool { requestingPermission || (phase != .idle && phase != .error) }

    static func faceExpression(phase: Phase, isMoving: Bool, commanded: RobotExpression,
                               conversational: RobotExpression? = nil) -> RobotExpression {
        switch phase {
        case .idle: isMoving ? .executing : conversational ?? commanded
        case .executing: .executing
        case .listening, .capturing: .listening
        case .transcribing, .thinking: .thinking
        case .speaking: .speaking
        case .error: .error
        }
    }

    func start(robot: RobotConnectionService) {
        guard !busy, let api = robot.voiceAPI else { return }
        let current = UUID()
        turn = current
        transcription = ""
        correctedText = ""
        correctionStatus = nil
        intent = nil
        validation = nil
        analysisMs = nil
        reply = ""
        errorMessage = nil
        plan = nil
        recordingSeconds = 0
        sttMs = nil
        llmMs = nil
        ttsMs = nil
        totalMs = nil
        phase = .idle
        requestingPermission = true
        work = Task {
            let allowed = await AVCaptureDevice.requestAccess(for: .audio)
            guard turn == current else { return }
            requestingPermission = false
            work = nil
            guard robot.voiceAPI == api else { return }
            guard allowed else {
                fail("Povolte mikrofon v Nastavení systému → Soukromí a zabezpečení → Mikrofon.")
                return
            }

            let file = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).wav")
            audioFile = file
            do {
                let capture = try AVAudioRecorder(url: file, settings: [
                    AVFormatIDKey: kAudioFormatLinearPCM,
                    AVSampleRateKey: 16_000,
                    AVNumberOfChannelsKey: 1,
                    AVLinearPCMBitDepthKey: 16,
                    AVLinearPCMIsFloatKey: false,
                    AVLinearPCMIsBigEndianKey: false
                ])
                guard capture.record(forDuration: 29) else { throw VoiceFailure("Mikrofon nelze spustit.") }
                recorder = capture
                phase = .listening
                meter = Task { [weak self] in
                    while let self, !Task.isCancelled, turn == current {
                        guard capture.isRecording else {
                            finish(robot: robot, reachedLimit: recordingSeconds >= 28)
                            return
                        }
                        recordingSeconds = capture.currentTime
                        do { try await Task.sleep(for: .milliseconds(100)) } catch { return }
                    }
                }
            } catch {
                removeRecording()
                fail(error.localizedDescription)
            }
        }
    }

    func finish(robot: RobotConnectionService, reachedLimit: Bool = false) {
        guard let recorder, let audioFile, robot.voiceAPI != nil else {
            cancel()
            fail("Simulátor je odpojený.")
            return
        }
        recordingSeconds = reachedLimit ? 29 : max(recordingSeconds, recorder.currentTime)
        recorder.stop()
        self.recorder = nil
        meter?.cancel()
        meter = nil
        let wav = try? Data(contentsOf: audioFile)
        removeRecording()
        guard let wav, wav.count > 44, recordingSeconds >= 0.2 else {
            fail("Nahrávka je prázdná nebo příliš krátká.")
            return
        }

        process(wav: wav, robot: robot)
    }

    func process(wav: Data, robot: RobotConnectionService) {
        guard (phase == .listening && recorder == nil) || !busy, let api = robot.voiceAPI else { return }
        conversationalExpression = nil
        let started = clock.now
        let current = turn
        phase = .transcribing
        work = Task {
            defer {
                if turn == current {
                    work = nil
                    totalMs = milliseconds(since: started)
                }
            }
            do {
                try await respond(wav: wav, api: api, robot: robot, current: current)
            } catch {
                if turn == current, !Task.isCancelled { fail(error.localizedDescription) }
            }
        }
    }

    func analyzeImage(_ question: String, robot: RobotConnectionService, followUp: Bool) {
        guard !busy, !vision.preparing, !vision.analyzing else { return }
        guard followUp ? vision.hasContext : vision.image != nil else { return }
        runVisionAction {
            try await self.answerImage(question, robot: robot, query: followUp ? .past : .selected)
            self.phase = .idle
        }
    }

    func speakVisionAnswer(robot: RobotConnectionService) {
        guard !busy, !vision.preparing, !vision.analyzing, let answer = vision.answer else { return }
        runVisionAction {
            guard let api = try? RobotSettingsAPI.apiBase(robot.serverURL).get().appendingPathComponent("voice")
                else { throw VisionFailure("Server pro hlasovou odpověď není dostupný.") }
            self.reply = answer.response
            self.conversationalExpression = Self.responseExpression(answer.expression)
            try await self.speak(api: api, robot: robot, current: self.turn)
        }
    }

    private func runVisionAction(_ action: @escaping @MainActor () async throws -> Void) {
        turn = UUID()
        let current = turn
        conversationalExpression = nil
        errorMessage = nil
        reply = ""
        phase = .thinking
        vision.error = nil
        work = Task {
            defer { if turn == current { work = nil } }
            do {
                try Task.checkCancellation()
                try await action()
            } catch {
                if turn == current, !Task.isCancelled {
                    vision.error = error.localizedDescription
                    fail(error.localizedDescription)
                }
            }
        }
    }

    private func answerImage(_ question: String, robot: RobotConnectionService, query: VisionInteraction.Query) async throws {
        phase = .capturing
        let answer = try await vision.analyze(question, address: robot.serverURL, query: query,
                                              conversationSessionId: sessionID) {
            phase = .thinking
        }
        conversationalExpression = Self.responseExpression(answer.expression)
        llmMs = answer.inferenceMs
        let label = answer.cached ? "Na předchozím snímku: " : (query == .selected ? "Na vybraném snímku: " : "")
        reply = label + answer.response
    }

    func cancel() {
        turn = UUID()
        conversationalExpression = nil
        work?.cancel()
        work = nil
        meter?.cancel()
        meter = nil
        recorder?.stop()
        recorder = nil
        removeRecording()
        clearPlayback()
        requestingPermission = false
        phase = .idle
        errorMessage = nil
    }

    func clearConversationalExpression() {
        conversationalExpression = nil
    }

    func disconnected() {
        clearConversationalExpression()
        guard busy else { return }
        cancel()
        fail("Simulátor je odpojený.")
    }

    func stop(robot: RobotConnectionService) {
        robot.submit(.stop)
        vision.autoScan = false
        cancel()
        if robot.connectionState != .connected { fail("Simulátor je odpojený.") }
    }

    private func respond(wav: Data, api: URL, robot: RobotConnectionService, current: UUID) async throws {
        if chatServer != api {
            chatServer = api
            sessionID = nil
        }
        let boundary = UUID().uuidString
        var request = URLRequest(url: api.appendingPathComponent("transcribe"), timeoutInterval: 75)
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"robotId\"\r\n\r\n\(robot.robotId)\r\n".utf8)
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"conversation\"\r\n\r\ntrue\r\n".utf8))
        if let sessionID {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"sessionId\"\r\n\r\n\(sessionID.uuidString)\r\n".utf8))
        }
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"voice.wav\"\r\nContent-Type: audio/wav\r\n\r\n".utf8))
        body.append(wav)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        request.httpBody = body
        let result: Transcript = try await robot.fetch(request)
        transcription = result.text
        correctedText = result.correctedText ?? result.text
        correctionStatus = result.correctionStatus
        intent = result.intent
        validation = result.route
        analysisMs = result.analysisMs
        sttMs = result.sttMs
        var failure: String?

        switch result.route {
        case "memory":
            phase = .thinking
            sessionID = result.sessionId
            reply = result.response ?? ""
            conversationalExpression = Self.responseExpression(result.expression)
        case "conversation":
            phase = .thinking
            let started = clock.now
            let chat = try jsonRequest(api.deletingLastPathComponent().appendingPathComponent("ai/chat"),
                ChatRequest(message: correctedText, sessionId: sessionID), timeout: 150)
            let answer: ChatReply = try await robot.fetch(chat)
            llmMs = milliseconds(since: started)
            sessionID = answer.sessionId
            conversationalExpression = Self.responseExpression(answer.expression)
            reply = answer.response.trimmingCharacters(in: .whitespacesAndNewlines)
        case "vision", "vision_followup", "vision_past":
            do {
                try await answerImage(correctedText, robot: robot,
                    query: result.route == "vision_past" ? .past : (result.route == "vision_followup" ? .followUp : .fresh))
            } catch {
                try Task.checkCancellation()
                failure = error.localizedDescription
                vision.error = failure
                reply = error is VisionFailure ? error.localizedDescription : "Analýza obrázku se nezdařila."
            }
        case "vision_unsupported":
            reply = "Kamerou zatím neumím pohybovat."
        case "action":
            guard let voiceTurn = result.voiceTurn else { throw VoiceFailure("Simulátor není připraven k pohybu.") }
            phase = .thinking
            let plan: AIPlan
            do {
                plan = try await robot.runVoiceAction(result.text, turn: voiceTurn) { plan, _ in
                    self.plan = plan
                    self.llmMs = plan.interpretationMs
                    self.correctedText = plan.correctedText ?? self.correctedText
                    self.correctionStatus = plan.correctionStatus ?? self.correctionStatus
                    self.validation = plan.status == "rejected" ? "rejected" : "validated"
                    if !plan.actions.isEmpty { self.phase = .executing }
                }
            } catch {
                if !Task.isCancelled, turn == current { robot.submit(.stop) }
                throw error
            }
            if plan.status == "completed" {
                reply = "Hotovo."
            } else {
                failure = plan.validationErrors.first ?? plan.message ?? plan.actions.compactMap(\.message).first ?? plan.status
                reply = plan.status == "rejected" ? "Povel jsem odmítl." : "Pohyb nebyl potvrzen."
            }
        case "stop":
            if result.stopSubmitted == nil { robot.submit(.stop) }
            if result.stopSubmitted == false {
                throw VoiceFailure("Hlasové zastavení se nepodařilo odeslat.")
            }
            vision.autoScan = false
            validation = "stop_submitted"
            reply = "Požadavek na zastavení byl odeslán."
        case "invalid":
            throw VoiceFailure("Nerozpoznal jsem použitelný text.")
        case "clarification_required":
            failure = result.reason
            switch result.reason {
            case "Missing rotation angle.", "Specify an angle.": reply = "O kolik stupňů se mám otočit?"
            case "Missing movement distance.", "Specify a distance.": reply = "Jak daleko mám jet?"
            case "Specify a direction.": reply = "Kterým směrem mám jet?"
            case "Specify a speed in percent.": reply = "Jakou rychlost mám použít v procentech?"
            default: reply = "Upřesněte prosím směr, úhel a rychlost."
            }
        case "unsupported":
            failure = result.reason
            reply = "Tento povel zatím neumím provést."
        case "ambiguous", "unclear":
            failure = result.reason ?? "Nejednoznačný nebo špatně rozpoznaný povel."
            reply = "Povel jsem nerozpoznal dostatečně přesně. Zopakujte ho prosím."
        default:
            throw VoiceFailure("Server vrátil neznámý typ požadavku.")
        }

        guard !reply.isEmpty else { throw VoiceFailure("Robot nevrátil odpověď.") }
        errorMessage = failure
        try await speak(api: api, robot: robot, current: current)
        if failure != nil, result.route.hasPrefix("vision"), turn == current { phase = .error }
    }

    private func speak(api: URL, robot: RobotConnectionService, current: UUID) async throws {
        phase = .thinking
        let synthesis = clock.now
        do {
            let spoken = String(reply.prefix(500))
            let request = try jsonRequest(api.appendingPathComponent("synthesize"), ["text": spoken], timeout: 75)
            let audio: Data
            if let fetchSpeech { audio = try await fetchSpeech(request) }
            else { audio = try await robot.fetchData(request) }
            try Task.checkCancellation()
            guard turn == current else { throw CancellationError() }
            ttsMs = milliseconds(since: synthesis)
            try await playReply(audio)
        } catch {
            if turn == current, !Task.isCancelled { ttsMs = milliseconds(since: synthesis) }
            throw error
        }
    }

    var mouthOpenness: Double {
        guard let player, player.isPlaying else { return 0 }
        return envelope?.openness(at: player.currentTime) ?? 0
    }

    func playReply(_ audio: Data) async throws {
        let current = turn
        let analyzed = await Self.analyze(audio)
        try Task.checkCancellation()
        guard turn == current else { throw CancellationError() }

        let playback = try AVAudioPlayer(data: audio)
        playback.delegate = self
        guard playback.play() else { throw VoiceFailure("Odpověď nelze přehrát.") }
        envelope = analyzed
        player = playback
        phase = .speaking

        // A stopped output can leave isPlaying false without a finish callback.
        let identifier = ObjectIdentifier(playback)
        playbackWatch = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .milliseconds(200)) } catch { return }
                guard let self, let player, ObjectIdentifier(player) == identifier else { return }
                if !player.isPlaying {
                    completePlayback(identifier, success: true)
                    return
                }
            }
        }
    }

    private nonisolated static func analyze(_ audio: Data) async -> SpeechEnvelope? {
        SpeechEnvelope(wav: audio)
    }

    private static func responseExpression(_ hint: String?) -> RobotExpression? {
        switch hint {
        case "idle": .idle
        case "happy": .happy
        case "sad": .sad
        case "thinking": .thinking
        case "error": .error
        default: nil
        }
    }

    private func clearPlayback() {
        playbackWatch?.cancel()
        playbackWatch = nil
        player?.stop()
        player = nil
        envelope = nil
    }

    nonisolated func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        let identifier = ObjectIdentifier(player)
        Task { @MainActor in completePlayback(identifier, success: flag) }
    }

    nonisolated func audioPlayerDecodeErrorDidOccur(_ player: AVAudioPlayer, error: Error?) {
        let identifier = ObjectIdentifier(player)
        Task { @MainActor in completePlayback(identifier, success: false) }
    }

    private func completePlayback(_ identifier: ObjectIdentifier, success: Bool) {
        guard let player, ObjectIdentifier(player) == identifier else { return }
        clearPlayback()
        if success {
            phase = errorMessage == nil ? .idle : .error
        } else {
            fail("Přehrávání hlasové odpovědi selhalo.")
        }
    }

    private func jsonRequest(_ url: URL, _ body: some Encodable, timeout: TimeInterval) throws -> URLRequest {
        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(body)
        return request
    }

    private func removeRecording() {
        if let audioFile { try? FileManager.default.removeItem(at: audioFile) }
        audioFile = nil
    }

    private func fail(_ message: String) {
        errorMessage = [errorMessage, message].compactMap { $0 }.joined(separator: " ")
        phase = .error
    }

    private func milliseconds(since start: ContinuousClock.Instant) -> Int {
        let duration = (clock.now - start).components
        return Int(duration.seconds * 1_000 + duration.attoseconds / 1_000_000_000_000_000)
    }

    private struct VoiceFailure: LocalizedError {
        let errorDescription: String?
        init(_ message: String) { errorDescription = message }
    }

    private struct Transcript: Decodable {
        let text: String
        let route: String
        let voiceTurn: String?
        let sttMs: Int
        let correctedText: String?
        let correctionStatus: String?
        let intent: String?
        let reason: String?
        let stopSubmitted: Bool?
        let analysisMs: Int?
        let sessionId: UUID?
        let response: String?
        let expression: String?
    }

    private struct ChatRequest: Encodable {
        let message: String
        let sessionId: UUID?
        let briefCzech = true
    }

    private struct ChatReply: Decodable {
        let sessionId: UUID
        let response: String
        let expression: String?
    }
}
