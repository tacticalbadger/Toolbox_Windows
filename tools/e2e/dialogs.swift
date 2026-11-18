import CoreGraphics
import Foundation
guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] else { exit(1) }
var found = 0
for w in list {
    let owner = w[kCGWindowOwnerName as String] as? String ?? ""
    let name = w[kCGWindowName as String] as? String ?? ""
    let layer = w[kCGWindowLayer as String] as? Int ?? 0
    let o = owner.lowercased()
    if o.contains("securityagent") || o.contains("auth") || o.contains("usernotification") {
        print("DIALOG owner=\(owner) name=\(name) layer=\(layer)")
        found += 1
    }
}
print("dialogs=\(found)")
