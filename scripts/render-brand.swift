// Render logo Zevn Shell ke PNG.
// Pakai: swift scripts/render-brand.swift icon <out.png> <size>
//        swift scripts/render-brand.swift og <out.png>
import AppKit

func hex(_ s: String, _ a: CGFloat = 1) -> NSColor {
    let v = UInt64(s.dropFirst(), radix: 16)!
    return NSColor(srgbRed: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255, blue: CGFloat(v & 0xFF) / 255, alpha: a)
}

let teal = hex("#3FD0B4"), blue = hex("#5B9CF5"), violet = hex("#9D7CF2")

/// Squircle gaya macOS (superellipse) di dalam rect.
func squircle(_ r: CGRect) -> NSBezierPath {
    let p = NSBezierPath()
    let n: CGFloat = 5, steps = 360
    for i in 0...steps {
        let t = CGFloat(i) / CGFloat(steps) * 2 * .pi
        let c = cos(t), s = sin(t)
        let x = pow(abs(c), 2 / n) * (c < 0 ? -1 : 1), y = pow(abs(s), 2 / n) * (s < 0 ? -1 : 1)
        let pt = NSPoint(x: r.midX + x * r.width / 2, y: r.midY + y * r.height / 2)
        i == 0 ? p.move(to: pt) : p.line(to: pt)
    }
    p.close()
    return p
}

/// Gambar ikon di context saat ini, menempati `rect`.
func drawIcon(in rect: CGRect) {
    let ctx = NSGraphicsContext.current!.cgContext
    let s = rect.width
    let body = rect.insetBy(dx: s * 0.1, dy: s * 0.1) // margin standar ikon macOS
    let shape = squircle(body)

    // Bayangan lembut
    NSGraphicsContext.saveGraphicsState()
    let sh = NSShadow(); sh.shadowBlurRadius = s * 0.04; sh.shadowOffset = NSSize(width: 0, height: -s * 0.015)
    sh.shadowColor = NSColor.black.withAlphaComponent(0.45); sh.set()
    hex("#07090D").setFill(); shape.fill()
    NSGraphicsContext.restoreGraphicsState()

    // Latar: gelap dengan cahaya teal-ungu dari atas
    NSGraphicsContext.saveGraphicsState()
    shape.addClip()
    NSGradient(colors: [hex("#121826"), hex("#080A10")])!.draw(in: body, angle: -90)
    let glow = NSGradient(colors: [teal.withAlphaComponent(0.55), blue.withAlphaComponent(0.25), .clear])!
    glow.draw(fromCenter: NSPoint(x: body.midX - s * 0.12, y: body.maxY + s * 0.02), radius: 0,
              toCenter: NSPoint(x: body.midX - s * 0.12, y: body.maxY + s * 0.02), radius: s * 0.62, options: [])
    let glow2 = NSGradient(colors: [violet.withAlphaComponent(0.35), .clear])!
    glow2.draw(fromCenter: NSPoint(x: body.maxX, y: body.minY), radius: 0,
               toCenter: NSPoint(x: body.maxX, y: body.minY), radius: s * 0.5, options: [])

    // Grid titik halus
    let dot = s * 0.006, gap = s * 0.052
    var y = body.minY + gap / 2
    while y < body.maxY {
        var x = body.minX + gap / 2
        while x < body.maxX {
            NSColor.white.withAlphaComponent(0.05).setFill()
            NSBezierPath(ovalIn: CGRect(x: x - dot, y: y - dot, width: dot * 2, height: dot * 2)).fill()
            x += gap
        }
        y += gap
    }

    // Simbol ">_" bergradien
    let glyph = NSBezierPath()
    let lw = s * 0.075
    let cx = body.minX + body.width * 0.30, cy = body.midY + s * 0.02, arm = s * 0.15
    glyph.move(to: NSPoint(x: cx - arm * 0.55, y: cy + arm))
    glyph.line(to: NSPoint(x: cx + arm * 0.62, y: cy))
    glyph.line(to: NSPoint(x: cx - arm * 0.55, y: cy - arm))
    glyph.lineWidth = lw; glyph.lineCapStyle = .round; glyph.lineJoinStyle = .round
    let under = NSBezierPath(roundedRect: CGRect(x: body.minX + body.width * 0.47, y: cy - arm - lw * 0.5,
                                                width: body.width * 0.25, height: lw), xRadius: lw / 2, yRadius: lw / 2)
    ctx.saveGState()
    let sh2 = NSShadow(); sh2.shadowBlurRadius = s * 0.05; sh2.shadowOffset = .zero; sh2.shadowColor = teal.withAlphaComponent(0.8); sh2.set()
    ctx.beginTransparencyLayer(auxiliaryInfo: nil)
    let stroked = glyph.copy() as! NSBezierPath
    NSColor.white.setStroke(); stroked.stroke(); NSColor.white.setFill(); under.fill()
    // Isi gradien di atas bentuk putih
    ctx.setBlendMode(.sourceIn)
    NSGradient(colors: [teal, blue, violet])!.draw(in: body, angle: -35)
    ctx.endTransparencyLayer()
    ctx.restoreGState()

    // Kilau tepi atas & border gradien tipis
    NSGradient(colors: [NSColor.white.withAlphaComponent(0.10), .clear])!
        .draw(in: CGRect(x: body.minX, y: body.midY, width: body.width, height: body.height / 2), angle: -90)
    NSGraphicsContext.restoreGraphicsState()
    shape.lineWidth = s * 0.008
    NSGraphicsContext.saveGraphicsState()
    shape.addClip()
    ctx.saveGState()
    ctx.setLineWidth(s * 0.012)
    ctx.addPath(shape.cgPathCompat); ctx.replacePathWithStrokedPath(); ctx.clip()
    NSGradient(colors: [teal.withAlphaComponent(0.9), blue.withAlphaComponent(0.5), violet.withAlphaComponent(0.8)])!.draw(in: body, angle: -45)
    ctx.restoreGState()
    NSGraphicsContext.restoreGraphicsState()
}

extension NSBezierPath {
    var cgPathCompat: CGPath {
        let path = CGMutablePath()
        var pts = [NSPoint](repeating: .zero, count: 3)
        for i in 0..<elementCount {
            switch element(at: i, associatedPoints: &pts) {
            case .moveTo: path.move(to: pts[0])
            case .lineTo: path.addLine(to: pts[0])
            case .curveTo, .cubicCurveTo: path.addCurve(to: pts[2], control1: pts[0], control2: pts[1])
            case .quadraticCurveTo: path.addQuadCurve(to: pts[1], control: pts[0])
            case .closePath: path.closeSubpath()
            @unknown default: break
            }
        }
        return path
    }
}

func render(_ size: NSSize, _ draw: () -> Void) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    draw()
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let args = CommandLine.arguments
let out = URL(fileURLWithPath: args[2])
switch args[1] {
case "icon":
    let n = CGFloat(Int(args[3]) ?? 1024)
    try! render(NSSize(width: n, height: n)) { drawIcon(in: CGRect(x: 0, y: 0, width: n, height: n)) }.write(to: out)
case "og":
    // Gambar share 1200x630 untuk media sosial
    let W: CGFloat = 1200, H: CGFloat = 630
    try! render(NSSize(width: W, height: H)) {
        NSGradient(colors: [hex("#0A0C12"), hex("#0E1420")])!.draw(in: CGRect(x: 0, y: 0, width: W, height: H), angle: -90)
        NSGradient(colors: [teal.withAlphaComponent(0.35), .clear])!.draw(fromCenter: NSPoint(x: 220, y: 600), radius: 0, toCenter: NSPoint(x: 220, y: 600), radius: 600, options: [])
        NSGradient(colors: [violet.withAlphaComponent(0.30), .clear])!.draw(fromCenter: NSPoint(x: 1100, y: 80), radius: 0, toCenter: NSPoint(x: 1100, y: 80), radius: 560, options: [])
        drawIcon(in: CGRect(x: 70, y: 175, width: 300, height: 300))
        let title = NSAttributedString(string: "Zevn Shell", attributes: [.font: NSFont.systemFont(ofSize: 88, weight: .heavy), .foregroundColor: NSColor.white])
        title.draw(at: NSPoint(x: 410, y: 340))
        let sub = NSAttributedString(string: "Semua servermu, satu terminal.", attributes: [.font: NSFont.systemFont(ofSize: 38, weight: .semibold), .foregroundColor: teal])
        sub.draw(at: NSPoint(x: 414, y: 280))
        let meta = NSAttributedString(string: "SSH client native untuk Mac · SFTP · Gratis",
                                      attributes: [.font: NSFont.monospacedSystemFont(ofSize: 20, weight: .regular), .foregroundColor: NSColor.white.withAlphaComponent(0.6)])
        meta.draw(at: NSPoint(x: 416, y: 220))
    }.write(to: out)
default: break
}
