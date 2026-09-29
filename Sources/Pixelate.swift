import CoreGraphics

/// Mosaic for hiding content. Pixels inside the area are never read: each block is filled with
/// a blend of two pixels from the border around the area, picked by a hash of the block coordinates.
/// The hidden content can't be recovered from the mosaic, and the image stays the same between redraws.
enum Pixelate {
    /// - Parameters:
    ///   - rect: area in screenshot pixels (origin at the top-left corner);
    ///   - block: mosaic block size in pixels.
    static func image(from screenshot: CGImage, rect: CGRect, block: Int) -> CGImage? {
        let bounds = CGRect(x: 0, y: 0, width: screenshot.width, height: screenshot.height)
        let area = rect.integral.intersection(bounds)
        guard !area.isEmpty else { return nil }
        // A 2-pixel border just outside the area, clipped to the screenshot.
        let outer = area.insetBy(dx: -2, dy: -2).intersection(bounds)
        var border = [
            CGRect(x: outer.minX, y: outer.minY, width: outer.width, height: area.minY - outer.minY),
            CGRect(x: outer.minX, y: area.maxY, width: outer.width, height: outer.maxY - area.maxY),
            CGRect(x: outer.minX, y: area.minY, width: area.minX - outer.minX, height: area.height),
            CGRect(x: area.maxX, y: area.minY, width: outer.maxX - area.maxX, height: area.height),
        ].filter { !$0.isEmpty }.flatMap { pixels(screenshot, $0) }
        if border.isEmpty {
            // The area covers the whole screenshot, so there is no outer border: use the area's top row.
            border = pixels(screenshot, CGRect(x: area.minX, y: area.minY, width: area.width, height: 1))
        }
        guard !border.isEmpty else { return nil }

        let side = max(block, 1)
        let columns = max(Int(area.width) / side, 1), rows = max(Int(area.height) / side, 1)
        var out = [UInt8](repeating: 255, count: columns * rows * 4)
        for row in 0..<rows {
            for column in 0..<columns {
                let a = border[hash(column, row, 1) % border.count], b = border[hash(column, row, 2) % border.count]
                let offset = (row * columns + column) * 4
                for c in 0..<3 {
                    out[offset + c] = UInt8((UInt16(a[c]) + UInt16(b[c])) / 2)
                }
            }
        }
        let space = screenshot.colorSpace ?? CGColorSpaceCreateDeviceRGB()
        return out.withUnsafeMutableBytes { buffer in
            CGContext(data: buffer.baseAddress, width: columns, height: rows, bitsPerComponent: 8, bytesPerRow: columns * 4,
                      space: space, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)?.makeImage()
        }
    }

    /// RGB pixels of a screenshot rectangle.
    private static func pixels(_ image: CGImage, _ rect: CGRect) -> [SIMD3<UInt8>] {
        guard let crop = image.cropping(to: rect) else { return [] }
        let (w, h) = (crop.width, crop.height)
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let space = image.colorSpace ?? CGColorSpaceCreateDeviceRGB()
        data.withUnsafeMutableBytes { buffer in
            CGContext(data: buffer.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                      space: space, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)?
                .draw(crop, in: CGRect(x: 0, y: 0, width: w, height: h))
        }
        return stride(from: 0, to: data.count, by: 4).map { SIMD3(data[$0], data[$0 + 1], data[$0 + 2]) }
    }

    /// Mixes block coordinates into a non-negative pseudo-random number (SplitMix64 finalizer).
    private static func hash(_ x: Int, _ y: Int, _ salt: UInt64) -> Int {
        var z = UInt64(truncatingIfNeeded: x) &* 0x9E37_79B9_7F4A_7C15 ^ UInt64(truncatingIfNeeded: y) &* 0xD1B5_4A32_D192_ED03 ^ salt
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return Int(truncatingIfNeeded: (z ^ (z >> 31)) >> 1)
    }
}
