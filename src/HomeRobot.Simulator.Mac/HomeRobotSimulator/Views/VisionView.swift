import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

struct VisionView: View {
    @ObservedObject var vision: VisionInteraction
    @ObservedObject var voice: VoiceInteraction
    let robot: RobotConnectionService
    @State private var importing = false
    @State private var question = "Co vidíš?"

    private var occupied: Bool { vision.preparing || vision.analyzing || voice.busy }

    private struct ScanTask: Equatable {
        let enabled: Bool
        let interval: Int
        let address: String
        let voiceBusy: Bool
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Group {
                if vision.cameraActive {
                    CameraPreview(session: vision.camera.session)
                        .accessibilityLabel("Live camera preview")
                } else {
                    ZStack {
                        Color.black
                        Text("Live camera preview").foregroundStyle(.white)
                    }
                }
            }
            .frame(maxWidth: 420)
            .frame(height: 180)
            .clipShape(RoundedRectangle(cornerRadius: 6))

            Picker("Camera", selection: $vision.deviceID) {
                if vision.devices.isEmpty { Text("No camera available").tag("") }
                ForEach(vision.devices) { device in Text(device.name).tag(device.id) }
            }
            .disabled(vision.cameraActive || occupied || vision.devices.isEmpty)

            HStack {
                Button("Start Camera") { vision.startCamera() }
                    .disabled(vision.cameraActive || occupied || vision.devices.isEmpty)
                Button("Capture Image") { vision.capture() }
                    .disabled(!vision.cameraActive || occupied)
                Button("Stop Camera") {
                    if vision.analyzing && !vision.scanning { voice.cancel() }
                    vision.stopCamera()
                }
                    .disabled(!vision.cameraActive && !vision.preparing)
            }
            Text(vision.cameraStatus).foregroundStyle(.secondary)

            Divider()
            HStack {
                Toggle("Auto Scan", isOn: $vision.autoScan)
                Picker("Interval", selection: $vision.scanInterval) {
                    ForEach([10, 15, 30, 60], id: \.self) { Text("\($0) s").tag($0) }
                }
                .frame(maxWidth: 180)
                Toggle("Detect changes", isOn: $vision.detectChanges)
            }
            HStack {
                Slider(value: $vision.changeThreshold, in: 0.005...0.25, step: 0.005) {
                    Text("Minimum change")
                }
                .frame(maxWidth: 280)
                .disabled(!vision.detectChanges)
                Text(vision.changeThreshold, format: .percent.precision(.fractionLength(1)))
            }
            TimelineView(.periodic(from: .now, by: 1)) { _ in
                diagnostics
            }
            if let scan = vision.scanAnswer {
                Text("Last scan result · \(scan.inferenceMs) ms").font(.headline)
                Text(scan.response).textSelection(.enabled)
            }

            Divider()
            Text("Captured Image").font(.headline)
            if let data = vision.image, let image = NSImage(data: data) {
                Image(nsImage: image).resizable().scaledToFit().frame(maxWidth: 420, maxHeight: 180)
                    .accessibilityLabel("Selected still image")
                Text(vision.source).foregroundStyle(.secondary)
            }
            HStack {
                Button("Import Image…") { importing = true }.disabled(occupied)
                Button("Clear Image") { vision.clear() }.disabled(occupied || vision.image == nil)
            }
            TextField("Question", text: $question)
            HStack {
                Button("Analyze with AI") { voice.analyzeImage(question, robot: robot, followUp: false) }
                    .disabled(occupied || vision.image == nil || question.utf16.count > 500)
                Button("Ask about Previous Image") { voice.analyzeImage(question, robot: robot, followUp: true) }
                    .disabled(occupied || !vision.hasContext || question.utf16.count > 500)
            }
            HStack {
                Button("Speak Answer") { voice.speakVisionAnswer(robot: robot) }
                    .disabled(occupied || vision.answer == nil)
                if voice.phase == .speaking {
                    Button("Stop Speech") { voice.cancel() }
                } else if (vision.analyzing && !vision.scanning) || (voice.busy && vision.answer != nil && voice.phase == .thinking) {
                    Button("Cancel") { voice.cancel() }
                }
            }
            if vision.analyzing {
                ProgressView("Analyzing image…").controlSize(.small)
            } else if let answer = vision.answer {
                Text(answer.cached ? "AI response ready · Previous image" : "AI response ready").font(.headline)
                Text(answer.response).textSelection(.enabled)
                Text("Inference: \(answer.inferenceMs) ms · Server: \(answer.processingMs) ms")
                    .foregroundStyle(.secondary)
            }
            if let error = vision.error { Text(error).foregroundStyle(.orange).textSelection(.enabled) }
        }
        .onAppear { vision.refreshDevices() }
        .task(id: ScanTask(enabled: vision.autoScan, interval: vision.scanInterval,
                           address: robot.serverURL, voiceBusy: voice.busy)) {
            guard vision.autoScan, !voice.busy else { return }
            await vision.runAutoScan(address: robot.serverURL, busy: { voice.busy })
        }
        .onReceive(NotificationCenter.default.publisher(for: AVCaptureDevice.wasConnectedNotification).receive(on: DispatchQueue.main)) { _ in
            vision.refreshDevices()
        }
        .onReceive(NotificationCenter.default.publisher(for: AVCaptureDevice.wasDisconnectedNotification).receive(on: DispatchQueue.main)) { _ in
            vision.refreshDevices()
        }
        .onReceive(NotificationCenter.default.publisher(for: AVCaptureSession.runtimeErrorNotification,
                                                        object: vision.camera.session).receive(on: DispatchQueue.main)) { _ in
            vision.cameraFailed("Kamera přestala fungovat. Zkontrolujte připojení a spusťte ji znovu.")
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.jpeg, .png, .heic, .heif]) { result in
            switch result {
            case .success(let url): vision.importImage(url)
            case .failure(let error): vision.error = error.localizedDescription
            }
        }
    }

    private var diagnostics: some View {
        let score = vision.changeScore?.formatted(.percent.precision(.fractionLength(1))) ?? "—"
        let context = vision.contextAge.map { "Cached (\($0) s old)" } ?? "Unavailable"
        return VStack(alignment: .leading, spacing: 2) {
            Text("Capture: \(time(vision.lastCaptureAt)) · Analysis: \(time(vision.lastAnalysisAt)) · Scan: \(time(vision.lastScanAt))")
            Text("Analyses: \(vision.analyses) · Unchanged: \(vision.unchanged) · Change: \(score)")
            Text("Visual context: \(context)")
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .monospacedDigit()
    }

    private func time(_ date: Date?) -> String {
        date?.formatted(date: .omitted, time: .standard) ?? "—"
    }

}

private struct CameraPreview: NSViewRepresentable {
    let session: AVCaptureSession

    func makeNSView(context: Context) -> PreviewSurface {
        let view = PreviewSurface()
        view.preview.session = session
        return view
    }

    func updateNSView(_ view: PreviewSurface, context: Context) {}
    static func dismantleNSView(_ view: PreviewSurface, coordinator: ()) { view.preview.session = nil }
}

private final class PreviewSurface: NSView {
    let preview = AVCaptureVideoPreviewLayer()

    init() {
        super.init(frame: .zero)
        wantsLayer = true
        preview.videoGravity = .resizeAspect
        preview.backgroundColor = NSColor.black.cgColor
    }

    override func makeBackingLayer() -> CALayer { preview }

    required init?(coder: NSCoder) { nil }
}
