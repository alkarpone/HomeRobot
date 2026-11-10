import Foundation
import XCTest
@testable import HomeRobotSimulator

final class FaceTests: XCTestCase {
    func testPCMEnvelopeUsesRMSAndInterpolates() throws {
        let config = SpeechEnvelope.Configuration(noiseGate: 0, wideOpenRMS: 1,
                                                   attackSeconds: 0, releaseSeconds: 0)
        let envelope = try XCTUnwrap(SpeechEnvelope(wav: wav(windows: [0, 8192, 0]), configuration: config))
        XCTAssertEqual(envelope.openness(at: 0.01), 0)
        XCTAssertEqual(envelope.openness(at: 0.02), 0.25, accuracy: 0.001)
        XCTAssertEqual(envelope.openness(at: 0.03), 0.5, accuracy: 0.001)
        XCTAssertEqual(envelope.openness(at: 0.06), 0)
        XCTAssertNil(SpeechEnvelope(wav: Data([0, 1, 2])))
    }

    func testNoiseGateAndMouthMapping() throws {
        let config = SpeechEnvelope.Configuration(noiseGate: 0.02)
        let quiet = try XCTUnwrap(SpeechEnvelope(wav: wav(windows: [0, 600, 500]), configuration: config))
        XCTAssertEqual(quiet.openness(at: 0.03), 0)
        XCTAssertEqual(config.openness(for: 0.02), 0)
        XCTAssertGreaterThan(config.openness(for: 0.03), 0)
        XCTAssertGreaterThan(config.openness(for: 0.1), config.openness(for: 0.03))
        XCTAssertEqual(config.openness(for: 0.5), 1)
    }

    func testAttackAndReleaseSmoothWithoutHoldingMouthOpen() throws {
        let envelope = try XCTUnwrap(SpeechEnvelope(wav: wav(windows:
            [0, 12_000, 12_000] + Array(repeating: 0, count: 20))))
        let attack = envelope.openness(at: 0.03)
        let peak = envelope.openness(at: 0.05)
        let release = envelope.openness(at: 0.07)
        XCTAssertGreaterThan(attack, 0)
        XCTAssertGreaterThan(peak, attack)
        XCTAssertLessThan(release, peak)
        XCTAssertGreaterThan(release, 0)
        XCTAssertEqual(envelope.openness(at: 0.43), 0)
    }

    @MainActor
    func testExpressionPriorityRestoresCommandedFace() throws {
        var robot = RobotSimulationEngine()
        try robot.start(.expression, parameters: .init(expression: "happy"), id: "smile")
        func face(_ phase: VoiceInteraction.Phase, moving: Bool = false,
                  conversational: RobotExpression? = nil) -> RobotExpression {
            VoiceInteraction.faceExpression(phase: phase, isMoving: moving, commanded: robot.expression,
                                            conversational: conversational)
        }
        XCTAssertEqual(face(.idle), .happy)
        XCTAssertEqual(face(.idle, moving: true), .executing)
        XCTAssertEqual(face(.listening, moving: true), .listening)
        XCTAssertEqual(face(.transcribing), .thinking)
        XCTAssertEqual(face(.thinking), .thinking)
        XCTAssertEqual(face(.executing), .executing)
        XCTAssertEqual(face(.speaking), .speaking)
        XCTAssertEqual(face(.speaking, conversational: .sad), .speaking)
        XCTAssertEqual(face(.idle, moving: true, conversational: .sad), .executing)
        XCTAssertEqual(face(.idle, conversational: .sad), .sad)
        XCTAssertEqual(face(.error), .error)
        XCTAssertEqual(face(.idle), .happy)
        XCTAssertThrowsError(try robot.start(.expression, parameters: .init(expression: "executing"), id: "focus"))
    }

    @MainActor
    func testPlaybackCompletionClosesMouth() async throws {
        let voice = VoiceInteraction()
        try await voice.playReply(wav(windows: Array(repeating: 8_000, count: 20)))
        XCTAssertEqual(voice.phase, .speaking)
        XCTAssertGreaterThan(voice.mouthOpenness, 0)
        for _ in 0..<60 where voice.busy {
            try await Task.sleep(for: .milliseconds(25))
        }
        XCTAssertEqual(voice.phase, .idle)
        XCTAssertEqual(voice.mouthOpenness, 0)
    }

    @MainActor
    func testPlaybackCancellationClosesMouthAndAllowsAnotherReply() async throws {
        let voice = VoiceInteraction()
        let audio = wav(windows: Array(repeating: 8_000, count: 50))
        try await voice.playReply(audio)
        XCTAssertGreaterThan(voice.mouthOpenness, 0)
        voice.cancel()
        XCTAssertEqual(voice.phase, .idle)
        XCTAssertEqual(voice.mouthOpenness, 0)

        try await voice.playReply(audio)
        XCTAssertEqual(voice.phase, .speaking)
        XCTAssertGreaterThan(voice.mouthOpenness, 0)
        voice.cancel()
    }

    @MainActor
    func testCzechPiperPlaybackFollowsSpeechAndPauses() async throws {
        guard let path = ProcessInfo.processInfo.environment["HOMEROBOT_PIPER_SAMPLE"] else {
            throw XCTSkip("Set HOMEROBOT_PIPER_SAMPLE to a short Czech PCM WAV with a sentence pause.")
        }
        let voice = VoiceInteraction()
        defer { voice.cancel() }
        try await voice.playReply(Data(contentsOf: URL(fileURLWithPath: path)))

        var heardSpeech = false
        var foundPause = false
        var heardSpeechAfterPause = false
        for _ in 0..<600 where voice.busy {
            let opening = voice.mouthOpenness
            if opening > 0.15 {
                heardSpeech = true
                if foundPause { heardSpeechAfterPause = true }
            } else if heardSpeech, opening == 0 {
                foundPause = true
            }
            try await Task.sleep(for: .milliseconds(20))
        }
        XCTAssertTrue(heardSpeechAfterPause)
        XCTAssertEqual(voice.phase, .idle)
        XCTAssertEqual(voice.mouthOpenness, 0)
    }

    private func wav(windows: [Int16]) -> Data {
        let sampleRate: UInt32 = 8_000
        let frames = Int(sampleRate) / 50
        let bytes = UInt32(windows.count * frames * 2)
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) {
            withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
        }
        data.append(contentsOf: "RIFF".utf8)
        append(36 + bytes)
        data.append(contentsOf: "WAVEfmt ".utf8)
        append(UInt32(16))
        append(UInt16(1))
        append(UInt16(1))
        append(sampleRate)
        append(sampleRate * 2)
        append(UInt16(2))
        append(UInt16(16))
        data.append(contentsOf: "data".utf8)
        append(bytes)
        for window in windows {
            for frame in 0..<frames { append(frame.isMultiple(of: 2) ? window : -window) }
        }
        return data
    }
}
