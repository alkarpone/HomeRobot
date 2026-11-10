import Foundation

struct SpeechEnvelope {
    struct Configuration {
        var noiseGate = 0.012
        var wideOpenRMS = 0.22
        var attackSeconds = 0.025
        var releaseSeconds = 0.055

        func openness(for rms: Double) -> Double {
            guard rms > noiseGate else { return 0 }
            return sqrt(min(1, (rms - noiseGate) / max(0.001, wideOpenRMS - noiseGate)))
        }

        func smooth(_ target: Double, from previous: Double, interval: Double) -> Double {
            let response = target > previous ? attackSeconds : releaseSeconds
            guard response > 0 else { return target }
            return target + (previous - target) * exp(-interval / response)
        }
    }

    private let frames: [Double]
    private let interval: TimeInterval
    private let duration: TimeInterval

    // Piper returns little-endian 16-bit PCM WAV. Skip ancillary RIFF chunks.
    init?(wav: Data, configuration: Configuration = .init()) {
        guard let pcm = wav.withUnsafeBytes(Self.readPCM) else { return nil }
        let frameInterval = Double(pcm.windowFrames) / pcm.sampleRate
        interval = frameInterval
        duration = Double(pcm.totalFrames) / pcm.sampleRate

        var previous = 0.0
        frames = pcm.rms.map { rms in
            previous = configuration.smooth(configuration.openness(for: rms),
                                            from: previous, interval: frameInterval)
            return previous < 0.015 ? 0 : previous
        }
    }

    func openness(at time: TimeInterval) -> Double {
        guard time >= 0, time < duration else { return 0 }
        let position = max(0, time / interval - 0.5)
        let lower = min(Int(position), frames.count - 1)
        let upper = min(lower + 1, frames.count - 1)
        return frames[lower] + (frames[upper] - frames[lower]) * (position - Double(lower))
    }

    private static func readPCM(_ bytes: UnsafeRawBufferPointer)
        -> (rms: [Double], windowFrames: Int, totalFrames: Int, sampleRate: Double)? {
        func u16(_ offset: Int) -> UInt16 {
            UInt16(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: UInt16.self))
        }
        func u32(_ offset: Int) -> UInt32 {
            UInt32(littleEndian: bytes.loadUnaligned(fromByteOffset: offset, as: UInt32.self))
        }

        guard bytes.count >= 12, u32(0) == 0x46464952, u32(8) == 0x45564157 else { return nil } // RIFF, WAVE
        let end = min(bytes.count, 8 + Int(u32(4)))
        var offset = 12
        var sampleRate = 0
        var channels = 0
        var samples: Range<Int>?
        while offset + 8 <= end {
            let start = offset + 8
            let size = Int(u32(offset + 4))
            guard size <= end - start else { return nil }
            switch u32(offset) {
            case 0x20746d66: // fmt
                guard size >= 16, u16(start) == 1, u16(start + 14) == 16 else { return nil }
                channels = Int(u16(start + 2))
                sampleRate = Int(u32(start + 4))
            case 0x61746164: // data
                samples = start..<(start + size)
            default: break
            }
            offset = start + size + size % 2
        }

        guard let samples, channels > 0, sampleRate > 0 else { return nil }
        let samplesPerFrame = channels * 2
        let totalFrames = samples.count / samplesPerFrame
        guard totalFrames > 0 else { return nil }
        let windowFrames = max(1, sampleRate / 50)
        let windowBytes = windowFrames * samplesPerFrame
        let sampleEnd = samples.lowerBound + totalFrames * samplesPerFrame
        let rms = stride(from: samples.lowerBound, to: sampleEnd, by: windowBytes).map { start in
            let end = min(start + windowBytes, sampleEnd)
            var sum = 0.0
            for index in stride(from: start, to: end, by: 2) {
                let sample = Double(Int16(bitPattern: u16(index))) / 32_768
                sum += sample * sample
            }
            return sqrt(sum / Double((end - start) / 2))
        }
        return (rms, windowFrames, totalFrames, Double(sampleRate))
    }
}
