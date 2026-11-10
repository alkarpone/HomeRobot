import AVFoundation
import Foundation
import ImageIO
import XCTest
@testable import HomeRobotSimulator

final class VisionTests: XCTestCase {
    func testCameraPermissionDenied() {
        XCTAssertThrowsError(try VisionCamera.requireCamera(authorized: false, deviceID: "camera")) {
            XCTAssertTrue($0.localizedDescription.contains("Nastavení systému"))
        }
    }

    func testCameraUnavailable() {
        XCTAssertThrowsError(try VisionCamera.requireCamera(authorized: true, deviceID: nil)) {
            XCTAssertTrue($0.localizedDescription.contains("Není dostupná žádná kamera"))
        }
        XCTAssertNoThrow(try VisionCamera.requireCamera(authorized: true, deviceID: "camera"))
    }

    @MainActor
    func testNativeImageImportResizesAndRejectsInvalidData() async throws {
        let input = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".png")
        defer { try? FileManager.default.removeItem(at: input) }
        let context = try XCTUnwrap(CGContext(data: nil, width: 1400, height: 700,
            bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        context.fill(CGRect(x: 0, y: 0, width: 1400, height: 700))
        let writer = try XCTUnwrap(CGImageDestinationCreateWithURL(input as CFURL, "public.png" as CFString, 1, nil))
        CGImageDestinationAddImage(writer, try XCTUnwrap(context.makeImage()), nil)
        XCTAssertTrue(CGImageDestinationFinalize(writer))
        let jpeg = try await ImportedImage(url: input).stillImage()
        let source = try XCTUnwrap(CGImageSourceCreateWithData(jpeg as CFData, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(CGImageSourceGetType(source) as String?, "public.jpeg")
        XCTAssertEqual(image.width, 1280)
        XCTAssertEqual(image.height, 640)
        do {
            _ = try await VisionImage.prepare(Data([1, 2, 3]))
            XCTFail("Invalid image was accepted")
        } catch is VisionFailure {}
    }

    @MainActor
    func testCameraStartCaptureRetakeDisconnectAndStop() async throws {
        let camera = StubCamera()
        let vision = VisionInteraction(camera: camera)
        vision.startCamera()
        try await wait { !vision.preparing }
        XCTAssertTrue(vision.cameraActive)
        XCTAssertEqual(camera.started, ["camera"])

        vision.capture()
        try await wait { !vision.preparing }
        XCTAssertEqual(vision.image, camera.photo)
        XCTAssertTrue(vision.cameraActive)

        camera.photo = try png()
        vision.capture()
        try await wait { !vision.preparing }
        XCTAssertEqual(vision.image, camera.photo)

        camera.available = false
        vision.refreshDevices()
        XCTAssertFalse(vision.cameraActive)
        XCTAssertTrue(vision.devices.isEmpty)
        XCTAssertNotNil(vision.error)
        XCTAssertEqual(camera.stops, 1)

        camera.available = true
        vision.startCamera()
        try await wait { !vision.preparing }
        vision.stopCamera()
        XCTAssertFalse(vision.cameraActive)
        XCTAssertEqual(camera.stops, 2)
        XCTAssertEqual(vision.image, camera.photo)
        vision.clear()
        XCTAssertNil(vision.image)
    }

    @MainActor
    func testAnalysisRequestAnswerAndOptInSpeech() async throws {
        let camera = StubCamera(photo: try png())
        let vision = VisionInteraction(camera: camera) { request in
            XCTAssertEqual(request.url?.path, "/api/vision/analyze")
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
            let body = try XCTUnwrap(request.httpBody)
            let payload = try JSONDecoder().decode(Upload.self, from: body)
            XCTAssertEqual(payload.question, "Co vidíš?")
            let image = try XCTUnwrap(CGImageSourceCreateWithData(XCTUnwrap(payload.image) as CFData, nil))
            XCTAssertEqual(CGImageSourceGetType(image) as String?, "public.jpeg")
            XCTAssertLessThanOrEqual(try XCTUnwrap(payload.image).count, 2 * 1024 * 1024)
            let data = Data("""
                {"response":"Vidím kruh.","expression":"happy","sessionId":"\(payload.sessionId)","cached":false,"inferenceMs":10,"processingMs":12}
                """.utf8)
            return (data, HTTPURLResponse(url: try XCTUnwrap(request.url), statusCode: 200,
                httpVersion: nil, headerFields: nil)!)
        }
        try await capture(vision)
        let robot = RobotConnectionService()
        let previous = robot.serverURL
        defer { robot.serverURL = previous }
        robot.serverURL = "ws://localhost:5080/ws/robot"
        var speechRequests = 0
        let voice = VoiceInteraction(vision: vision) { request in
            speechRequests += 1
            XCTAssertEqual(request.url?.path, "/api/voice/synthesize")
            let body = try XCTUnwrap(request.httpBody)
            XCTAssertEqual(try JSONDecoder().decode([String: String].self, from: body)["text"], "Vidím kruh.")
            return self.wav()
        }
        voice.analyzeImage("  ", robot: robot, followUp: false)
        try await wait { !voice.busy }
        XCTAssertEqual(vision.answer?.response, "Vidím kruh.")
        XCTAssertEqual(vision.answer?.inferenceMs, 10)
        XCTAssertEqual(vision.image, camera.photo)
        XCTAssertEqual(speechRequests, 0)

        voice.speakVisionAnswer(robot: robot)
        voice.speakVisionAnswer(robot: robot)
        try await wait { voice.phase == .speaking || voice.phase == .error }
        XCTAssertEqual(voice.phase, .speaking, voice.errorMessage ?? "")
        XCTAssertEqual(speechRequests, 1)
        voice.cancel()
        XCTAssertEqual(voice.phase, .idle)
        XCTAssertEqual(voice.mouthOpenness, 0)
    }

    @MainActor
    func testInvalidImageDoesNotReachServer() async throws {
        let vision = VisionInteraction(camera: StubCamera()) { _ in
            XCTFail("Invalid image reached the transport")
            throw URLError(.badServerResponse)
        }
        try await capture(vision)
        do {
            _ = try await vision.analyze("Co vidíš?", address: address, query: .selected) {}
            XCTFail("Invalid image was accepted")
        } catch is VisionFailure {}
        XCTAssertFalse(vision.analyzing)
    }

    @MainActor
    func testAnalysisTimeout() async throws {
        let vision = VisionInteraction(camera: StubCamera(photo: try png())) { _ in throw URLError(.timedOut) }
        try await capture(vision)
        do {
            _ = try await vision.analyze("Co vidíš?", address: address, query: .selected) {}
            XCTFail("Timeout was ignored")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("časový limit"))
        }
        XCTAssertFalse(vision.analyzing)
        XCTAssertNil(vision.answer)
    }

    @MainActor
    func testAnalysisCancellationAndDuplicatePrevention() async throws {
        var requests = 0
        let vision = VisionInteraction(camera: StubCamera(photo: try png())) { _ in
            requests += 1
            try await Task.sleep(for: .seconds(30))
            throw URLError(.badServerResponse)
        }
        try await capture(vision)
        let task = Task { try await vision.analyze("Co vidíš?", address: address, query: .selected) {} }
        defer { task.cancel() }
        try await wait { requests == 1 }
        do {
            _ = try await vision.analyze("Duplicate", address: address, query: .selected) {}
            XCTFail("Duplicate was accepted")
        } catch is VisionFailure {}
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("Cancelled analysis completed")
        } catch is CancellationError {}
        XCTAssertEqual(requests, 1)
        XCTAssertFalse(vision.analyzing)
        XCTAssertNil(vision.answer)
    }

    @MainActor
    func testFreshVoiceCaptureAndFollowUpExpiry() async throws {
        var now = Date()
        let camera = StubCamera(photo: try png())
        var requests: [Upload] = []
        let vision = VisionInteraction(camera: camera, clock: { now }) { request in
            let upload = try JSONDecoder().decode(Upload.self, from: XCTUnwrap(request.httpBody))
            requests.append(upload)
            return try self.result(for: upload, request: request)
        }
        _ = try await vision.analyze("Co vidíš?", address: address, query: .fresh) {}
        let follow = try await vision.analyze("Jakou má barvu?", address: address, query: .followUp) {}
        XCTAssertTrue(follow.cached)
        XCTAssertTrue(vision.cameraActive)
        XCTAssertEqual(camera.started, ["camera"])
        XCTAssertEqual(camera.captures, 1)
        XCTAssertEqual(requests.map(\.sessionId), [requests[0].sessionId, requests[0].sessionId])

        now.addTimeInterval(120)
        XCTAssertFalse(vision.hasContext)
        do {
            _ = try await vision.analyze("Co jsi viděl?", address: address, query: .past) {}
            XCTFail("Expired historical question was accepted")
        } catch is VisionFailure {}
        XCTAssertEqual(camera.captures, 1)

        _ = try await vision.analyze("Jakou má barvu?", address: address, query: .followUp) {}
        _ = try await vision.analyze("Co vidíš teď?", address: address, query: .fresh) {}
        XCTAssertEqual(camera.captures, 3)
        XCTAssertEqual(requests.map { $0.image != nil }, [true, false, true, true])
    }

    @MainActor
    func testFreshVisionReportsCameraErrorsBeforeSending() async throws {
        for authorized in [false, true] {
            let camera = StubCamera()
            camera.authorized = authorized
            camera.available = false
            let vision = VisionInteraction(camera: camera) { _ in
                XCTFail("Vision called the server without a camera")
                throw URLError(.badServerResponse)
            }
            do {
                _ = try await vision.analyze("Co vidíš?", address: address, query: .fresh) {}
                XCTFail("Camera failure was ignored")
            } catch {
                XCTAssertTrue(error.localizedDescription.contains(authorized ? "Není dostupná žádná kamera" : "Nastavení systému"))
            }
            XCTAssertFalse(vision.cameraActive)
            XCTAssertFalse(vision.analyzing)
        }
    }

    @MainActor
    func testAutoScanSkipsBusyAndUnchangedTicksAndExplicitCaptureBypassesDetection() async throws {
        let first = try png()
        let second = try png(gray: 1)
        let camera = StubCamera(photo: first)
        var requests: [Upload] = []
        let vision = VisionInteraction(camera: camera) { request in
            let upload = try JSONDecoder().decode(Upload.self, from: XCTUnwrap(request.httpBody))
            requests.append(upload)
            return try self.result(for: upload, request: request)
        }
        XCTAssertFalse(vision.autoScan)
        XCTAssertEqual(vision.scanInterval, 30)
        XCTAssertTrue(vision.detectChanges)
        await vision.runAutoScan(address: address, busy: { false }, sleep: { _ in XCTFail("Scan starts disabled") })
        vision.autoScan = true
        vision.scanInterval = 15
        var tick = 0
        var monotonic = ContinuousClock.now
        await vision.runAutoScan(address: address, busy: { tick == 2 }, monotonic: { monotonic }) { delay in
            XCTAssertEqual(delay, .seconds(15))
            monotonic += delay
            tick += 1
            if tick == 4 { camera.photo = second }
            if tick == 5 { vision.autoScan = false }
        }
        XCTAssertFalse(vision.autoScan)
        XCTAssertEqual(camera.captures, 3)
        XCTAssertEqual(vision.analyses, 2)
        XCTAssertEqual(vision.unchanged, 1)
        XCTAssertGreaterThan(vision.changeScore ?? 0, vision.changeThreshold)
        XCTAssertNotNil(vision.lastScanAt)
        XCTAssertFalse(vision.hasContext)
        _ = try await vision.analyze("Co vidíš?", address: address, query: .fresh) {}
        XCTAssertEqual(vision.analyses, 3)
        XCTAssertEqual(requests.map(\.scan), [true, true, false])
        XCTAssertTrue(vision.hasContext)
    }

    @MainActor
    func testAutoScanCancellationStopsInFlightAnalysis() async throws {
        let camera = StubCamera(photo: try png())
        var sent = false
        var cancelled = false
        let vision = VisionInteraction(camera: camera) { _ in
            sent = true
            defer { cancelled = Task.isCancelled }
            try await Task.sleep(for: .seconds(30))
            throw URLError(.badServerResponse)
        }
        vision.autoScan = true
        let scan = Task { await vision.runAutoScan(address: address, busy: { false }, sleep: { _ in }) }
        defer { scan.cancel() }
        try await wait { sent }
        vision.autoScan = false
        scan.cancel()
        await scan.value
        XCTAssertTrue(cancelled)
        XCTAssertFalse(vision.analyzing)
        XCTAssertNil(vision.scanAnswer)
        XCTAssertEqual(vision.analyses, 0)
        XCTAssertEqual(camera.captures, 1)
    }

    func testChangeScoreIgnoresSmallGlobalLightingAndNoise() {
        let baseline = [UInt8](repeating: 100, count: 32)
        let noisy = (0..<32).map { $0.isMultiple(of: 2) ? UInt8(108) : UInt8(112) }
        XCTAssertEqual(VisionImage.changeScore(baseline, noisy), 0)
        XCTAssertGreaterThan(VisionImage.changeScore(baseline, .init(repeating: 170, count: 32)), 0.1)
    }

    private func result(for upload: Upload, request: URLRequest) throws -> (Data, URLResponse) {
        let data = Data("""
            {"response":"Vidím kruh.","sessionId":"\(upload.sessionId)","cached":\(upload.image == nil),"inferenceMs":10,"processingMs":12,"contextLifetimeSeconds":120}
            """.utf8)
        return (data, try XCTUnwrap(HTTPURLResponse(url: XCTUnwrap(request.url), statusCode: 200,
                                                   httpVersion: nil, headerFields: nil)))
    }

    private let address = "ws://localhost:5080/ws/robot"

    @MainActor
    private func capture(_ vision: VisionInteraction) async throws {
        vision.startCamera()
        try await wait { !vision.preparing }
        vision.capture()
        try await wait { !vision.preparing }
        XCTAssertNotNil(vision.image)
    }

    @MainActor
    private func wait(_ ready: () -> Bool) async throws {
        let deadline = ContinuousClock.now + .seconds(3)
        while !ready() {
            guard .now < deadline else { throw URLError(.timedOut) }
            try await Task.sleep(for: .milliseconds(10))
        }
    }

    private func png(gray: CGFloat = 0) throws -> Data {
        let data = NSMutableData()
        let writer = try XCTUnwrap(CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil))
        let bitmap = try XCTUnwrap(CGContext(data: nil, width: 8, height: 4, bitsPerComponent: 8,
            bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        bitmap.setFillColor(gray: gray, alpha: 1)
        bitmap.fill(CGRect(x: 0, y: 0, width: 8, height: 4))
        CGImageDestinationAddImage(writer, try XCTUnwrap(bitmap.makeImage()), nil)
        XCTAssertTrue(CGImageDestinationFinalize(writer))
        return data as Data
    }

    private func wav() -> Data {
        let format = AVAudioFormat(standardFormatWithSampleRate: 8_000, channels: 1)!
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 8_000)!
        buffer.frameLength = 8_000
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
        defer { try? FileManager.default.removeItem(at: url) }
        do {
            let file = try AVAudioFile(forWriting: url, settings: format.settings)
            try file.write(from: buffer)
        } catch { XCTFail(error.localizedDescription) }
        return (try? Data(contentsOf: url)) ?? Data()
    }

    private struct Upload: Decodable {
        let image: Data?
        let scan: Bool
        let question: String
        let sessionId: UUID
    }

    final class StubCamera: CameraSource, @unchecked Sendable {
        let session = AVCaptureSession()
        var available = true
        var authorized = true
        var captures = 0
        var photo: Data
        var started: [String] = []
        var stops = 0

        init(photo: Data = Data([1, 2, 3])) { self.photo = photo }
        func devices() -> [CameraDevice] { available ? [.init(id: "camera", name: "Test Camera")] : [] }
        func start(deviceID: String?) async throws {
            try VisionCamera.requireCamera(authorized: authorized, deviceID: deviceID)
            if let deviceID { started.append(deviceID) }
        }
        func stop() { stops += 1 }
        func stillImage() async throws -> Data { captures += 1; return photo }
    }
}
