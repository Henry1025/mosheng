#!/usr/bin/env bash
# Starts the real compiled app. This is not a sign-in, audio, or end-to-end test.
set -euo pipefail
ios_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ios_root"
command -v xcrun >/dev/null || { echo 'Simulator launch requires a Mac with Xcode.' >&2; exit 2; }
app_path="$ios_root/build/DerivedData/Build/Products/Debug-iphonesimulator/Komorebi.app"
test -d "$app_path" || { echo 'Compile the simulator application first.' >&2; exit 2; }
output="$ios_root/build/Smoke"
mkdir -p "$output"
xcrun simctl list --json > "$output/simulators.json"
python3 - "$output/simulators.json" > "$output/selection.txt" <<'PY'
import json, sys
from pathlib import Path
inventory = json.loads(Path(sys.argv[1]).read_text())
runtimes = [r for r in inventory['runtimes'] if r.get('isAvailable') and r['identifier'].startswith('com.apple.CoreSimulator.SimRuntime.iOS-') and int(r['version'].split('.')[0]) >= 17]
if not runtimes:
    raise SystemExit('No available iOS 17+ simulator runtime; install one in Xcode before retrying.')
runtime = max(runtimes, key=lambda r: tuple(map(int, r['version'].split('.'))))
version_parts = list(map(int, runtime['version'].split('.')))
version_parts += [0] * (3 - len(version_parts))
version_code = (version_parts[0] << 16) | (version_parts[1] << 8) | version_parts[2]
types = [d for d in inventory['devicetypes'] if d.get('productFamily') == 'iPhone' and d.get('minRuntimeVersion', 0) <= version_code <= d.get('maxRuntimeVersion', 0xFFFFFFFF)]
if not types:
    raise SystemExit('No compatible iPhone simulator device type.')
print(runtime['identifier'])
print(types[-1]['identifier'])
PY
runtime=$(sed -n '1p' "$output/selection.txt")
device_type=$(sed -n '2p' "$output/selection.txt")
udid=$(xcrun simctl create 'Komorebi build verification' "$device_type" "$runtime")
cleanup() {
    xcrun simctl shutdown "$udid" >/dev/null 2>&1 || true
    xcrun simctl delete "$udid" >/dev/null 2>&1 || true
}
trap cleanup EXIT
xcrun simctl boot "$udid"
xcrun simctl bootstatus "$udid" -b
# Ad-hoc local signing is sufficient for this simulator launch; no Apple account is used.
codesign --force --sign - "$app_path"
xcrun simctl install "$udid" "$app_path"
bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app_path/Info.plist")
launch_result=$(xcrun simctl launch --terminate-running-process "$udid" "$bundle_id")
printf '%s\n' "$launch_result"
app_pid=${launch_result##*: }
[[ $app_pid =~ ^[0-9]+$ ]] || { echo 'Could not verify the launched process ID.' >&2; exit 1; }
sleep 5
kill -0 "$app_pid" || { echo 'The application exited shortly after launch.' >&2; exit 1; }
xcrun simctl io "$udid" screenshot "$output/launch.png"
echo 'PASS: compiled app installed, launched and remained running for five seconds; startup screenshot saved locally. Live sign-in and physical-device audio are untested.'
