// Renders the branding the macOS disk image wears: slaw.icns for the app
// bundle, and the DMG window background (a turntable to drop the needle on,
// an arrow, and the Applications slot), at 1x and 2x.
// Nothing here is checked in, for the same reason as New-InstallerAssets.ps1.
//
//   swift installer/macos/render-assets.swift out/macos-assets
//
// Writes slaw.icns, background.png and background@2x.png. The layout constants
// below must agree with the icon positions in dmg-settings.py.

import AppKit
import Foundation

// Studio palette (see src/Slaw/Desktop/StudioTheme.cs)
func rgb(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(
        srgbRed: CGFloat((hex >> 16) & 0xff) / 255,
        green: CGFloat((hex >> 8) & 0xff) / 255,
        blue: CGFloat(hex & 0xff) / 255,
        alpha: alpha)
}

let background = rgb(0x0b0b10)
let surface = rgb(0x13131b)
let surfaceRaised = rgb(0x1c1c28)
let border = rgb(0x2a2a3a)
let text = rgb(0xededf2)
let textMuted = rgb(0x8a8aa3)
let accent = rgb(0xd946ef)
let gradientTop = rgb(0xa21caf)
let gradientBottom = rgb(0x6d28d9)

// A waveform that has never been mastered (same bars as the Windows installer)
let barHeights: [CGFloat] = [0.30, 0.55, 0.85, 0.50, 1.00, 0.70, 0.40, 0.90, 0.60, 0.35, 0.75, 0.45, 0.95, 0.55, 0.30]
let markBars: [CGFloat] = [0.35, 0.70, 1.00, 0.55, 0.80]

// DMG window content, in points. Icon centres are shared with dmg-settings.py.
// Finder shows 420 points of it (less when the user keeps the tab bar open, so
// nothing that matters sits below y = 380); the rest is spare floor.
let windowWidth: CGFloat = 660
let windowHeight: CGFloat = 440
let appIconCentre = CGPoint(x: 170, y: 205)
let applicationsCentre = CGPoint(x: 500, y: 205)

/// A bitmap context with a top-left origin, measured in points.
func makeCanvas(width: CGFloat, height: CGFloat, scale: CGFloat) -> CGContext {
    let context = CGContext(
        data: nil,
        width: Int(width * scale),
        height: Int(height * scale),
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.translateBy(x: 0, y: height * scale)
    context.scaleBy(x: scale, y: -scale)
    context.setShouldAntialias(true)
    context.interpolationQuality = .high
    return context
}

func savePNG(_ context: CGContext, to path: String) {
    let image = context.makeImage()!
    let destination = CGImageDestinationCreateWithURL(
        URL(fileURLWithPath: path) as CFURL, "public.png" as CFString, 1, nil)!
    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        fatalError("could not write \(path)")
    }
}

func fillLinearGradient(_ context: CGContext, in rect: CGRect, from start: CGColor, to end: CGColor, diagonal: Bool) {
    let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!, colors: [start, end] as CFArray, locations: [0, 1])!
    let endPoint = diagonal ? CGPoint(x: rect.maxX, y: rect.maxY) : CGPoint(x: rect.minX, y: rect.maxY)
    context.drawLinearGradient(gradient, start: CGPoint(x: rect.minX, y: rect.minY), end: endPoint, options: [])
}

/// Vertically centred rounded bars across a band of the canvas.
func drawWaveform(_ context: CGContext, left: CGFloat, centreY: CGFloat, width: CGFloat, maxHeight: CGFloat, heights: [CGFloat], color: CGColor) {
    let slot = width / CGFloat(heights.count)
    let barWidth = max(1.5, slot * 0.55)
    context.setFillColor(color)
    for (index, height) in heights.enumerated() {
        let barHeight = max(barWidth, height * maxHeight)
        let bar = CGRect(x: left + CGFloat(index) * slot + (slot - barWidth) / 2, y: centreY - barHeight / 2, width: barWidth, height: barHeight)
        context.addPath(CGPath(roundedRect: bar, cornerWidth: barWidth / 2, cornerHeight: barWidth / 2, transform: nil))
        context.fillPath()
    }
}

/// The app mark: a rounded gradient tile carrying a waveform.
func drawAppMark(_ context: CGContext, in tile: CGRect) {
    context.saveGState()
    let radius = tile.width * 0.225
    context.addPath(CGPath(roundedRect: tile, cornerWidth: radius, cornerHeight: radius, transform: nil))
    context.clip()
    fillLinearGradient(context, in: tile, from: gradientTop, to: gradientBottom, diagonal: true)
    context.restoreGState()
    drawWaveform(context, left: tile.minX + tile.width * 0.18, centreY: tile.midY, width: tile.width * 0.64, maxHeight: tile.height * 0.56, heights: markBars, color: rgb(0xffffff))
}

func drawText(_ context: CGContext, _ string: String, at origin: CGPoint, font: NSFont, color: CGColor, centred: Bool = false) {
    let attributed = NSAttributedString(string: string, attributes: [.font: font, .foregroundColor: NSColor(cgColor: color)!])
    let size = attributed.size()
    let drawOrigin = centred ? CGPoint(x: origin.x - size.width / 2, y: origin.y) : origin
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
    attributed.draw(at: drawOrigin)
    NSGraphicsContext.restoreGraphicsState()
}

// MARK: - Icon

func renderIcon(into directory: String) {
    let iconset = (directory as NSString).appendingPathComponent("slaw.iconset")
    try? FileManager.default.removeItem(atPath: iconset)
    try! FileManager.default.createDirectory(atPath: iconset, withIntermediateDirectories: true)
    for points in [16, 32, 128, 256, 512] {
        for scale in [1, 2] {
            let pixels = CGFloat(points * scale)
            let context = makeCanvas(width: pixels, height: pixels, scale: 1)
            // macOS icon grid: an 824/1024 tile, centred, leaving room for the system shadow
            let inset = pixels * (100.0 / 1024.0)
            drawAppMark(context, in: CGRect(x: inset, y: inset, width: pixels - inset * 2, height: pixels - inset * 2))
            let name = scale == 1 ? "icon_\(points)x\(points).png" : "icon_\(points)x\(points)@2x.png"
            savePNG(context, to: (iconset as NSString).appendingPathComponent(name))
        }
    }
    let iconutil = Process()
    iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
    iconutil.arguments = ["-c", "icns", iconset, "-o", (directory as NSString).appendingPathComponent("slaw.icns")]
    try! iconutil.run()
    iconutil.waitUntilExit()
    guard iconutil.terminationStatus == 0 else {
        fatalError("iconutil failed with exit code \(iconutil.terminationStatus)")
    }
    try? FileManager.default.removeItem(atPath: iconset)
}

// MARK: - DMG background

func drawTurntable(_ context: CGContext) {
    let centre = appIconCentre

    // Plinth
    let plinth = CGRect(x: centre.x - 120, y: centre.y - 100, width: 250, height: 200)
    context.addPath(CGPath(roundedRect: plinth, cornerWidth: 18, cornerHeight: 18, transform: nil))
    context.setFillColor(surfaceRaised)
    context.setStrokeColor(border)
    context.setLineWidth(1)
    context.drawPath(using: .fillStroke)

    // Platter and record, grooves included
    context.setFillColor(rgb(0x050507))
    context.fillEllipse(in: CGRect(x: centre.x - 86, y: centre.y - 86, width: 172, height: 172))
    context.setStrokeColor(rgb(0xffffff, 0.05))
    context.setLineWidth(0.75)
    var radius: CGFloat = 80
    while radius > 46 {
        context.strokeEllipse(in: CGRect(x: centre.x - radius, y: centre.y - radius, width: radius * 2, height: radius * 2))
        radius -= 3.2
    }
    // A sheen across the vinyl
    context.saveGState()
    context.addEllipse(in: CGRect(x: centre.x - 82, y: centre.y - 82, width: 164, height: 164))
    context.clip()
    context.move(to: centre)
    context.addArc(center: centre, radius: 90, startAngle: -.pi * 0.80, endAngle: -.pi * 0.62, clockwise: false)
    context.closePath()
    context.setFillColor(rgb(0xffffff, 0.06))
    context.fillPath()
    context.restoreGState()

    // Label, glowing in the accent gradient behind where the app icon lands
    context.saveGState()
    let label = CGRect(x: centre.x - 44, y: centre.y - 44, width: 88, height: 88)
    context.addEllipse(in: label)
    context.clip()
    fillLinearGradient(context, in: label, from: gradientTop, to: gradientBottom, diagonal: true)
    context.restoreGState()

    // Tonearm: pivot top right, arm resting on the outer grooves
    let pivot = CGPoint(x: centre.x + 104, y: centre.y - 74)
    let elbow = CGPoint(x: centre.x + 96, y: centre.y + 22)
    let headshell = CGPoint(x: centre.x + 62, y: centre.y + 52)
    context.setStrokeColor(rgb(0xc9c9d6))
    context.setLineCap(.round)
    context.setLineWidth(4)
    context.move(to: pivot)
    context.addLine(to: elbow)
    context.addLine(to: headshell)
    context.strokePath()
    context.setFillColor(rgb(0xc9c9d6))
    context.saveGState()
    context.translateBy(x: headshell.x, y: headshell.y)
    context.rotate(by: .pi * 0.22)
    context.fill(CGRect(x: -7, y: -4, width: 14, height: 13))
    context.restoreGState()
    context.setFillColor(surface)
    context.setStrokeColor(border)
    context.setLineWidth(1)
    context.addEllipse(in: CGRect(x: pivot.x - 11, y: pivot.y - 11, width: 22, height: 22))
    context.drawPath(using: .fillStroke)
    context.setFillColor(rgb(0xc9c9d6))
    context.fillEllipse(in: CGRect(x: pivot.x - 4, y: pivot.y - 4, width: 8, height: 8))

    // Pitch fader, because it is a deck, not a coaster
    context.setFillColor(rgb(0x050507))
    context.addPath(CGPath(roundedRect: CGRect(x: centre.x + 112, y: centre.y + 6, width: 6, height: 70), cornerWidth: 3, cornerHeight: 3, transform: nil))
    context.fillPath()
    context.setFillColor(accent)
    context.addPath(CGPath(roundedRect: CGRect(x: centre.x + 108, y: centre.y + 36, width: 14, height: 7), cornerWidth: 2, cornerHeight: 2, transform: nil))
    context.fillPath()
}

func drawArrow(_ context: CGContext) {
    let startX = appIconCentre.x + 142
    let endX = applicationsCentre.x - 82
    let y = appIconCentre.y
    context.setStrokeColor(accent)
    context.setLineWidth(3)
    context.setLineCap(.round)
    context.setLineDash(phase: 0, lengths: [2, 9])
    context.move(to: CGPoint(x: startX, y: y))
    context.addLine(to: CGPoint(x: endX - 6, y: y))
    context.strokePath()
    context.setLineDash(phase: 0, lengths: [])
    context.setLineJoin(.round)
    context.move(to: CGPoint(x: endX - 14, y: y - 10))
    context.addLine(to: CGPoint(x: endX, y: y))
    context.addLine(to: CGPoint(x: endX - 14, y: y + 10))
    context.strokePath()
}

func drawApplicationsSlot(_ context: CGContext) {
    let centre = applicationsCentre
    context.setFillColor(rgb(0xd946ef, 0.07))
    context.fillEllipse(in: CGRect(x: centre.x - 70, y: centre.y - 70, width: 140, height: 140))
    context.setStrokeColor(rgb(0xd946ef, 0.55))
    context.setLineWidth(1.5)
    context.setLineDash(phase: 0, lengths: [6, 6])
    context.strokeEllipse(in: CGRect(x: centre.x - 70, y: centre.y - 70, width: 140, height: 140))
    context.setLineDash(phase: 0, lengths: [])
}

func drawNameplate(_ context: CGContext, under iconCentre: CGPoint, width: CGFloat) {
    let plate = CGRect(x: iconCentre.x - width / 2, y: iconCentre.y + 56, width: width, height: 19)
    context.addPath(CGPath(roundedRect: plate, cornerWidth: 9.5, cornerHeight: 9.5, transform: nil))
    context.setFillColor(rgb(0xc026d3))
    context.fillPath()
}

func renderBackground(scale: CGFloat, to path: String) {
    let context = makeCanvas(width: windowWidth, height: windowHeight, scale: scale)
    fillLinearGradient(context, in: CGRect(x: 0, y: 0, width: windowWidth, height: windowHeight), from: surface, to: background, diagonal: false)

    // Faint waveform along the floor
    drawWaveform(context, left: 0, centreY: windowHeight - 20, width: windowWidth, maxHeight: 64, heights: barHeights + barHeights, color: rgb(0xd946ef, 0.07))

    drawText(context, "slaw", at: CGPoint(x: 36, y: 22), font: .systemFont(ofSize: 28, weight: .bold), color: text)
    drawText(context, "Beyond Deterministic Production", at: CGPoint(x: 38, y: 58), font: NSFontManager.shared.convert(.systemFont(ofSize: 12), toHaveTrait: .italicFontMask), color: textMuted)

    drawTurntable(context)
    drawArrow(context)
    drawApplicationsSlot(context)

    // Finder draws each icon's name below it in the system label colour: black
    // in Light Mode, white in Dark Mode. This fuchsia reads at about 4.5:1 for both.
    drawNameplate(context, under: appIconCentre, width: 58)
    drawNameplate(context, under: applicationsCentre, width: 104)

    drawText(context, "Drag slaw onto Applications, then drop the needle.", at: CGPoint(x: windowWidth / 2, y: 342), font: .systemFont(ofSize: 14, weight: .medium), color: text, centred: true)
    drawText(context, "First launch: System Settings → Privacy & Security → Open Anyway", at: CGPoint(x: windowWidth / 2, y: 366), font: .systemFont(ofSize: 11), color: textMuted, centred: true)

    savePNG(context, to: path)
}

// MARK: - Main

let arguments = CommandLine.arguments
guard arguments.count == 2 else {
    FileHandle.standardError.write("usage: swift render-assets.swift <output-directory>\n".data(using: .utf8)!)
    exit(2)
}
let outputDirectory = (arguments[1] as NSString).standardizingPath
try! FileManager.default.createDirectory(atPath: outputDirectory, withIntermediateDirectories: true)
renderIcon(into: outputDirectory)
renderBackground(scale: 1, to: (outputDirectory as NSString).appendingPathComponent("background.png"))
renderBackground(scale: 2, to: (outputDirectory as NSString).appendingPathComponent("background@2x.png"))
print("[assets] macOS installer artwork written to \(outputDirectory)")
