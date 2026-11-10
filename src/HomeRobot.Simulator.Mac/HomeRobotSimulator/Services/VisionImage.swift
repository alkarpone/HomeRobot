import Foundation
import ImageIO
import UniformTypeIdentifiers

enum VisionImage {
    private static let maxInput = 25 * 1024 * 1024

    static func importFile(_ url: URL) async throws -> Data {
        try await background {
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            let file = try FileHandle(forReadingFrom: url)
            defer { try? file.close() }
            let data = try file.read(upToCount: maxInput + 1) ?? Data()
            return try jpeg(data)
        }
    }

    static func prepare(_ data: Data) async throws -> Data {
        try await background { try jpeg(data) }
    }

    private static func background<T: Sendable>(_ operation: @escaping @Sendable () throws -> T) async throws -> T {
        let task = Task.detached(priority: .userInitiated, operation: operation)
        return try await withTaskCancellationHandler {
            let data = try await task.value
            try Task.checkCancellation()
            return data
        } onCancel: { task.cancel() }
    }

    static func signature(_ data: Data) async throws -> [UInt8] {
        try await background {
            let side = 32
            guard let source = CGImageSourceCreateWithData(data as CFData, nil),
                  let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 64
                  ] as CFDictionary),
                  let gray = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8,
                    bytesPerRow: side, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: 0),
                  let pixels = gray.data else {
                throw VisionFailure("Snímek se nepodařilo porovnat.")
            }
            gray.interpolationQuality = .medium
            gray.draw(thumbnail, in: CGRect(x: 0, y: 0, width: side, height: side))
            return Array(UnsafeBufferPointer(start: pixels.assumingMemoryBound(to: UInt8.self), count: side * side))
        }
    }

    static func changeScore(_ previous: [UInt8], _ current: [UInt8]) -> Double {
        let differences = zip(previous, current).map { Int($1) - Int($0) }
        let mean = Double(differences.reduce(0, +)) / Double(differences.count)
        // Compensate for small global lighting shifts and discard residual pixel noise.
        let lighting = min(12, max(-12, mean))
        let difference = differences.reduce(0.0) { $0 + max(0, abs(Double($1) - lighting) - 4) }
        return difference / (Double(differences.count) * 255)
    }

    private static func jpeg(_ data: Data) throws -> Data {
        try Task.checkCancellation()
        guard data.count <= maxInput else { throw VisionFailure("Soubor smí mít nejvýše 25 MiB.") }
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let type = CGImageSourceGetType(source) as String?,
              [UTType.jpeg.identifier, UTType.png.identifier, UTType.heic.identifier, UTType.heif.identifier].contains(type),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: 1280
              ] as CFDictionary) else { throw VisionFailure("Obrázek JPEG, PNG nebo HEIC se nepodařilo načíst.") }
        let output = NSMutableData()
        guard let encoder = CGImageDestinationCreateWithData(output, UTType.jpeg.identifier as CFString, 1, nil) else {
            throw VisionFailure("Nelze připravit náhled obrázku.")
        }
        CGImageDestinationAddImage(encoder, image, [kCGImageDestinationLossyCompressionQuality: 0.78] as CFDictionary)
        guard CGImageDestinationFinalize(encoder), output.length <= 2 * 1024 * 1024 else {
            throw VisionFailure("Obrázek se nepodařilo zmenšit na 2 MiB.")
        }
        return output as Data
    }
}
