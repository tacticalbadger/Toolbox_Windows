// Draws the Toolbox app icon: a blue squircle holding a dark terminal window
// split into two panes, a green prompt on the left and text lines on the
// right. Usage: swift make-icon.swift <output.png> (1024x1024)
import AppKit

let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon-1024.png"
let px = 1024

guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                                 samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                 colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
      let context = NSGraphicsContext(bitmapImageRep: rep) else {
    fatalError("could not create bitmap")
}
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = context
let cg = context.cgContext
cg.clear(CGRect(x: 0, y: 0, width: px, height: px))

func rgb(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: r / 255, green: g / 255, blue: b / 255, alpha: a)
}

// Squircle on Apple's 1024 grid: 824 pt centred, with a soft drop shadow.
let plate = NSRect(x: 100, y: 100, width: 824, height: 824)
let plateShape = NSBezierPath(roundedRect: plate, xRadius: 186, yRadius: 186)
cg.saveGState()
cg.setShadow(offset: CGSize(width: 0, height: -14), blur: 36, color: NSColor.black.withAlphaComponent(0.35).cgColor)
rgb(20, 60, 120).setFill()
plateShape.fill()
cg.restoreGState()
NSGradient(colors: [rgb(64, 140, 236), rgb(22, 70, 150), rgb(14, 40, 92)], atLocations: [0, 0.6, 1], colorSpace: .sRGB)!
    .draw(in: plateShape, angle: -90)

// Terminal window.
let win = NSRect(x: 176, y: 196, width: 672, height: 588)
let winShape = NSBezierPath(roundedRect: win, xRadius: 44, yRadius: 44)
cg.saveGState()
cg.setShadow(offset: CGSize(width: 0, height: -10), blur: 30, color: NSColor.black.withAlphaComponent(0.45).cgColor)
rgb(14, 18, 26).setFill()
winShape.fill()
cg.restoreGState()

cg.saveGState()
winShape.addClip()
// Title bar.
let barHeight: CGFloat = 76
let bar = NSRect(x: win.minX, y: win.maxY - barHeight, width: win.width, height: barHeight)
rgb(38, 44, 56).setFill()
bar.fill()
// Traffic lights.
for (index, color) in [rgb(255, 95, 87), rgb(254, 188, 46), rgb(40, 200, 64)].enumerated() {
    let dot = NSRect(x: win.minX + 40 + CGFloat(index) * 44, y: bar.midY - 15, width: 30, height: 30)
    color.setFill()
    NSBezierPath(ovalIn: dot).fill()
}
// Pane divider.
let body = NSRect(x: win.minX, y: win.minY, width: win.width, height: win.height - barHeight)
let dividerX = win.minX + win.width * 0.5
rgb(60, 70, 90).setFill()
NSRect(x: dividerX - 3, y: body.minY, width: 6, height: body.height).fill()

// Left pane: green prompt.
let prompt = ">_" as NSString
let font = NSFont(name: "Menlo-Bold", size: 200) ?? NSFont.monospacedSystemFont(ofSize: 200, weight: .bold)
let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: rgb(57, 255, 122)]
let textSize = prompt.size(withAttributes: attrs)
let leftPane = NSRect(x: win.minX, y: body.minY, width: dividerX - win.minX, height: body.height)
prompt.draw(at: NSPoint(x: leftPane.midX - textSize.width / 2, y: leftPane.midY - textSize.height / 2 + 10), withAttributes: attrs)

// Right pane: text lines.
let rightPane = NSRect(x: dividerX, y: body.minY, width: win.maxX - dividerX, height: body.height)
let lineWidths: [CGFloat] = [0.62, 0.78, 0.46, 0.70, 0.54, 0.66]
let lineHeight: CGFloat = 26
let gap: CGFloat = 50
let totalHeight = CGFloat(lineWidths.count) * gap
var y = rightPane.midY + totalHeight / 2 - lineHeight
for (index, fraction) in lineWidths.enumerated() {
    let color = index == 0 ? rgb(120, 180, 255) : rgb(150, 160, 180)
    color.withAlphaComponent(index == 0 ? 0.95 : 0.55).setFill()
    let line = NSRect(x: rightPane.minX + 52, y: y, width: (rightPane.width - 104) * fraction, height: lineHeight)
    NSBezierPath(roundedRect: line, xRadius: 13, yRadius: 13).fill()
    y -= gap
}
cg.restoreGState()

NSGraphicsContext.restoreGraphicsState()
guard let png = rep.representation(using: .png, properties: [:]) else { fatalError("png") }
try! png.write(to: URL(fileURLWithPath: output))
print("wrote \(output)")
