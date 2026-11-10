import AVFoundation
import Foundation

struct VisionFailure: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

struct CameraDevice: Identifiable {
    let id: String
    let name: String
}

protocol StillImageSource: Sendable {
    func stillImage() async throws -> Data
}

struct ImportedImage: StillImageSource {
    let url: URL
    func stillImage() async throws -> Data {
        try await VisionImage.importFile(url)
    }
}

protocol CameraSource: StillImageSource {
    var session: AVCaptureSession { get }
    func devices() -> [CameraDevice]
    func start(deviceID: String?) async throws
    func stop()
}

final class VisionCamera: CameraSource, @unchecked Sendable {
    let session = AVCaptureSession()
    // All session configuration and photo submissions run on this queue.
    private let queue = DispatchQueue(label: "HomeRobot.vision.camera")
    private let output = AVCapturePhotoOutput()
    private var rotation: AVCaptureDevice.RotationCoordinator?
    private var photos: [Int64: Photo] = [:]

    func devices() -> [CameraDevice] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera, .external, .continuityCamera],
            mediaType: .video, position: .unspecified).devices.map {
                CameraDevice(id: $0.uniqueID, name: $0.localizedName)
            }
    }

    static func requireCamera(authorized: Bool, deviceID: String?) throws {
        guard authorized else {
            throw VisionFailure("Povolte kameru pro HomeRobot v Nastavení systému → Soukromí a zabezpečení → Kamera.")
        }
        guard deviceID != nil else { throw VisionFailure("Není dostupná žádná kamera.") }
    }

    func start(deviceID: String?) async throws {
        let status = AVCaptureDevice.authorizationStatus(for: .video)
        let allowed = if status == .notDetermined {
            await AVCaptureDevice.requestAccess(for: .video)
        } else { status == .authorized }
        try Task.checkCancellation()
        try Self.requireCamera(authorized: allowed, deviceID: deviceID)
        try await withCheckedThrowingContinuation { (result: CheckedContinuation<Void, Error>) in
            queue.async { [self] in
                do {
                    guard let deviceID, let device = AVCaptureDevice(uniqueID: deviceID) else {
                        throw VisionFailure("Vybraná kamera není dostupná.")
                    }
                    let input = try AVCaptureDeviceInput(device: device)
                    reset()
                    session.beginConfiguration()
                    if session.canSetSessionPreset(.photo) { session.sessionPreset = .photo }
                    guard session.canAddInput(input), session.canAddOutput(output) else {
                        session.commitConfiguration()
                        throw VisionFailure("Kameru se nepodařilo nastavit.")
                    }
                    session.addInput(input)
                    session.addOutput(output)
                    rotation = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: nil)
                    session.commitConfiguration()
                    session.startRunning()
                    guard session.isRunning else {
                        reset()
                        throw VisionFailure("Kameru se nepodařilo spustit.")
                    }
                    result.resume()
                } catch { result.resume(throwing: error) }
            }
        }
    }

    func stop() {
        queue.async { [self] in reset() }
    }

    private func reset() {
        if session.isRunning { session.stopRunning() }
        rotation = nil
        for photo in photos.values { photo.complete(.failure(CancellationError())) }
        photos.removeAll()
        session.beginConfiguration()
        session.inputs.forEach { session.removeInput($0) }
        session.outputs.forEach { session.removeOutput($0) }
        session.commitConfiguration()
    }

    func stillImage() async throws -> Data {
        try Task.checkCancellation()
        let photo: Data = try await withCheckedThrowingContinuation { result in
            queue.async { [self] in
                guard session.isRunning, let connection = output.connection(with: .video), connection.isActive else {
                    result.resume(throwing: VisionFailure("Kamera není dostupná."))
                    return
                }
                if let angle = rotation?.videoRotationAngleForHorizonLevelCapture,
                   connection.isVideoRotationAngleSupported(angle) {
                    connection.videoRotationAngle = angle
                }
                let settings = AVCapturePhotoSettings()
                let id = settings.uniqueID
                let delegate = Photo(result) { [weak self] in
                    self?.queue.async { [weak self] in self?.photos.removeValue(forKey: id) }
                }
                photos[id] = delegate
                output.capturePhoto(with: settings, delegate: delegate)
            }
        }
        try Task.checkCancellation()
        return photo
    }

    private final class Photo: NSObject, AVCapturePhotoCaptureDelegate {
        private let lock = NSLock()
        private var result: CheckedContinuation<Data, Error>?
        var data: Data?
        var processingError: Error?
        let finished: @Sendable () -> Void
        init(_ result: CheckedContinuation<Data, Error>, finished: @escaping @Sendable () -> Void) {
            self.result = result
            self.finished = finished
        }

        func photoOutput(_ output: AVCapturePhotoOutput, didFinishProcessingPhoto photo: AVCapturePhoto, error: Error?) {
            data = photo.fileDataRepresentation()
            processingError = error
        }

        func photoOutput(_ output: AVCapturePhotoOutput, didFinishCaptureFor settings: AVCaptureResolvedPhotoSettings, error: Error?) {
            defer { finished() }
            if let error = error ?? processingError { complete(.failure(error)) }
            else if let data, !data.isEmpty { complete(.success(data)) }
            else { complete(.failure(VisionFailure("Kamera nevrátila snímek."))) }
        }

        func complete(_ value: Result<Data, Error>) {
            lock.lock()
            let pending = result
            result = nil
            lock.unlock()
            pending?.resume(with: value)
        }
    }
}
