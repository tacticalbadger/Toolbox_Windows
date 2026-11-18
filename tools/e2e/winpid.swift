import CoreGraphics
import Foundation
let pid = Int32(CommandLine.arguments[1])!
guard let list = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] else { exit(1) }
for w in list where (w[kCGWindowOwnerPID as String] as? Int32) == pid {
    let b = w[kCGWindowBounds as String] as? [String: Any] ?? [:]
    print("id=\(w[kCGWindowNumber as String] ?? 0) \(Int(b["Width"] as? Double ?? 0))x\(Int(b["Height"] as? Double ?? 0)) at \(Int(b["X"] as? Double ?? 0)),\(Int(b["Y"] as? Double ?? 0)) layer=\(w[kCGWindowLayer as String] ?? 0) alpha=\(w[kCGWindowAlpha as String] ?? 0) onscreen=\(w[kCGWindowIsOnscreen as String] ?? false) name=\(w[kCGWindowName as String] ?? "")")
}
