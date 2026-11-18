# Toolbox build entry points.
#   make core     build Rust core → Swift bindings + static library (App/Vendor)
#   make app      build the app with SwiftPM and wrap it in build/Toolbox.app
#   make run      build and launch the app
#   make test     run the Rust core tests
#   make icon     re-render the app icon (icns + asset catalog)
#   make signing  one-time: create a local code-signing identity so the Keychain stops re-prompting
#   make project  regenerate App/Toolbox.xcodeproj from App/project.yml
#   make xcode    build the Xcode project (needs the Xcode license accepted)
export PATH := $(HOME)/.cargo/bin:/opt/homebrew/bin:$(PATH)
XCODE_DEV := /Applications/Xcode.app/Contents/Developer

.PHONY: all core app run test project xcode icon signing clean

all: core app

core:
	bridge/build-core.sh

app:
	bridge/bundle-app.sh

run: app
	open build/Toolbox.app

test:
	cd core && cargo test --features test-support

project:
	cd App && xcodegen generate

icon:
	bridge/make-icon.sh

signing:
	bridge/signing-identity.sh

xcode: project
	DEVELOPER_DIR=$(XCODE_DEV) xcodebuild -project App/Toolbox.xcodeproj -scheme Toolbox \
	  -configuration Debug -derivedDataPath build/xcode -quiet build

clean:
	rm -rf core/target build App/.build App/Sources/Generated App/Vendor App/Toolbox.xcodeproj App/Info.plist
