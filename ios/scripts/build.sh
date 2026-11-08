#!/usr/bin/env bash
set -euo pipefail
ios_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ios_root"
mode=${1:-simulator}
case "$mode" in simulator|archive) ;; *) echo 'Usage: build.sh [simulator|archive]' >&2; exit 2 ;; esac
command -v xcodebuild >/dev/null || { echo 'A Mac with Xcode is required. Simulator builds do not require a paid Apple membership. No iOS binary has been built.' >&2; exit 2; }
python3 scripts/generate_project.py
python3 scripts/verify_project.py
xcode_version=$(xcodebuild -version | awk '/^Xcode / {print $2}')
if [[ $mode == archive ]]; then
  [[ ${xcode_version%%.*} -ge 26 ]] || { echo 'App Store uploads require Xcode 26 or later.' >&2; exit 2; }
  test -f Local.xcconfig || { echo 'Configure the real DEVELOPMENT_TEAM in Local.xcconfig before archiving.' >&2; exit 2; }
  xcodebuild -project Komorebi.xcodeproj -scheme Komorebi -configuration Release -destination 'generic/platform=iOS' -archivePath build/Komorebi.xcarchive -allowProvisioningUpdates archive
else
  [[ ${xcode_version%%.*} -ge 15 ]] || { echo 'The iOS 17 simulator target requires Xcode 15 or later.' >&2; exit 2; }
  xcodebuild -project Komorebi.xcodeproj -scheme Komorebi -configuration Debug -destination 'generic/platform=iOS Simulator' -derivedDataPath build/DerivedData CODE_SIGNING_ALLOWED=NO build
fi
