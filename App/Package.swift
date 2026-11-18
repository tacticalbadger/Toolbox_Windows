// swift-tools-version: 5.10
// SwiftPM definition of the app. It compiles the same sources as the Xcode
// project (App/project.yml) and lets `make app` work with only the Command
// Line Tools installed. Run bridge/build-core.sh first to produce
// Sources/Generated and Vendor.
import Foundation
import PackageDescription

let appRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent().path

let package = Package(
    name: "Toolbox",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(url: "https://github.com/migueldeicaza/SwiftTerm", exact: "1.99.3"),
        // By commit (tag 1.1.0): the package depends on a branch of CryptoSwift,
        // which SwiftPM only allows for unversioned dependencies.
        .package(url: "https://github.com/royalapplications/royalvnc", revision: "92d4427c73817d8f849bb289ff190aa4b40c44ea"),
    ],
    targets: [
        .systemLibrary(name: "ToolboxCoreFFI", path: "Vendor/ToolboxCoreFFI"),
        .executableTarget(
            name: "Toolbox",
            dependencies: [
                .product(name: "SwiftTerm", package: "SwiftTerm"),
                .product(name: "RoyalVNCKit", package: "royalvnc"),
                "ToolboxCoreFFI",
            ],
            path: "Sources",
            linkerSettings: [
                .unsafeFlags(["-L\(appRoot)/Vendor"]),
                .linkedLibrary("toolbox_core"),
                // Needed by the Rust core's TLS and HTTP (proxy discovery) crates.
                .linkedFramework("Security"),
                .linkedFramework("SystemConfiguration"),
                .linkedFramework("CoreFoundation"),
            ]
        ),
    ]
)
