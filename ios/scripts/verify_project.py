#!/usr/bin/env python3
"""Portable checks. These do not substitute for a successful Xcode build."""
import json, plistlib, re, xml.etree.ElementTree as ET
from pathlib import Path
root=Path(__file__).resolve().parents[1]
for path in (root/'Komorebi').glob('*.plist'): plistlib.loads(path.read_bytes())
for path in (root/'Komorebi').glob('*.entitlements'): plistlib.loads(path.read_bytes())
plistlib.loads((root/'Komorebi/PrivacyInfo.xcprivacy').read_bytes())
project=(root/'Komorebi.xcodeproj/project.pbxproj').read_text()
for path in (root/'Komorebi').glob('*.swift'): assert path.name in project, path.name
for path in (root/'Komorebi/Assets.xcassets').rglob('Contents.json'):
    data=json.loads(path.read_text())
    for image in data.get('images',[]):
        if 'filename' in image: assert (path.parent/image['filename']).is_file(), path
ET.parse(root/'Komorebi.xcodeproj/xcshareddata/xcschemes/Komorebi.xcscheme')
combined='\n'.join(p.read_text() for p in (root/'Komorebi').glob('*.swift'))
assert 'sk-fish-' not in combined and 'pdl_live_apikey_' not in combined
for forbidden in ('import StoreKit', 'Purchases', 'PaywallView', 'com.komorebichat.plus.monthly', 'api/mobile/billing/'):
    assert forbidden not in combined, f'Paid capability in free target: {forbidden}'
assert 'Purchases.swift' not in project
privacy = plistlib.loads((root/'Komorebi/PrivacyInfo.xcprivacy').read_bytes())
assert all(item['NSPrivacyCollectedDataType'] != 'NSPrivacyCollectedDataTypePurchaseHistory' for item in privacy['NSPrivacyCollectedDataTypes'])
assert 'kSecAttrAccessibleWhenUnlockedThisDeviceOnly' in combined
print('PASS: Xcode source/resource references, property lists, scheme, assets, free target without StoreKit or purchase-history collection, and Keychain storage. Xcode compilation and device tests still required.')
