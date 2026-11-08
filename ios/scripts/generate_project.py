#!/usr/bin/env python3
"""Generate a dependency-free Xcode project from tracked Swift sources."""
from pathlib import Path
import hashlib, json, plistlib
from xml.sax.saxutils import escape

root = Path(__file__).resolve().parents[1]
def uid(value): return hashlib.sha1(value.encode()).hexdigest()[:24].upper()
def quote(value): return json.dumps(str(value))
objects = []
def obj(name, value):
    ident = uid(name)
    objects.append(f'{ident} = {{ {value} }};')
    return ident
swift = sorted((root / 'Komorebi').glob('*.swift'))
sources, files = [], []
for path in swift:
    rel = path.relative_to(root).as_posix()
    ref = obj(rel, f'isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = {quote(rel)}; sourceTree = "<group>";')
    files.append(ref)
    sources.append(obj('build:'+rel, f'isa = PBXBuildFile; fileRef = {ref};'))
resources = []
for rel, kind in [('Komorebi/Assets.xcassets','folder.assetcatalog'),('Komorebi/PrivacyInfo.xcprivacy','text.xml')]:
    ref = obj(rel, f'isa = PBXFileReference; lastKnownFileType = {kind}; path = {quote(rel)}; sourceTree = "<group>";')
    files.append(ref); resources.append(obj('build:'+rel, f'isa = PBXBuildFile; fileRef = {ref};'))
config = obj('config', 'isa = PBXFileReference; lastKnownFileType = text.xcconfig; path = Config.xcconfig; sourceTree = "<group>";')
files.append(config)
product = obj('product', 'isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = Komorebi.app; sourceTree = BUILT_PRODUCTS_DIR;')
products = obj('products', f'isa = PBXGroup; children = ({product},); name = Products; sourceTree = "<group>";')
group = obj('main', f'isa = PBXGroup; children = ({",".join(files)},{products},); sourceTree = "<group>";')
src = obj('sources', f'isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = ({",".join(sources)},); runOnlyForDeploymentPostprocessing = 0;')
res = obj('resources', f'isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = ({",".join(resources)},); runOnlyForDeploymentPostprocessing = 0;')
frameworks = obj('frameworks', 'isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = (); runOnlyForDeploymentPostprocessing = 0;')
target_cfgs, project_cfgs = [], []
for mode in ['Debug','Release']:
    common = 'ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon; CODE_SIGN_ENTITLEMENTS = Komorebi/Komorebi.entitlements; GENERATE_INFOPLIST_FILE = NO; INFOPLIST_FILE = Komorebi/Info.plist; SDKROOT = iphoneos; TARGETED_DEVICE_FAMILY = 1; PRODUCT_NAME = "$(TARGET_NAME)"; SWIFT_EMIT_LOC_STRINGS = YES; ENABLE_PREVIEWS = YES; '
    common += 'SWIFT_OPTIMIZATION_LEVEL = "-Onone"; DEBUG_INFORMATION_FORMAT = dwarf; SWIFT_ACTIVE_COMPILATION_CONDITIONS = DEBUG;' if mode == 'Debug' else 'SWIFT_OPTIMIZATION_LEVEL = "-O"; DEBUG_INFORMATION_FORMAT = "dwarf-with-dsym"; VALIDATE_PRODUCT = YES;'
    target_cfgs.append(obj('target:'+mode, f'isa = XCBuildConfiguration; baseConfigurationReference = {config}; buildSettings = {{ {common} }}; name = {mode};'))
    project_cfgs.append(obj('project:'+mode, f'isa = XCBuildConfiguration; buildSettings = {{ CLANG_ENABLE_MODULES = YES; CLANG_ENABLE_OBJC_ARC = YES; }}; name = {mode};'))
target_list = obj('target-list', f'isa = XCConfigurationList; buildConfigurations = ({",".join(target_cfgs)},); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
project_list = obj('project-list', f'isa = XCConfigurationList; buildConfigurations = ({",".join(project_cfgs)},); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
target = obj('target', f'isa = PBXNativeTarget; buildConfigurationList = {target_list}; buildPhases = ({src},{frameworks},{res},); buildRules = (); dependencies = (); name = Komorebi; productName = Komorebi; productReference = {product}; productType = "com.apple.product-type.application";')
project = obj('project', f'isa = PBXProject; attributes = {{ LastUpgradeCheck = 2600; TargetAttributes = {{ {target} = {{ CreatedOnToolsVersion = 26.0; SystemCapabilities = {{ com.apple.SignInWithApple = {{ enabled = 1; }}; }}; }}; }}; }}; buildConfigurationList = {project_list}; compatibilityVersion = "Xcode 14.0"; developmentRegion = en; hasScannedForEncodings = 0; knownRegions = (en, "zh-Hans", Base); mainGroup = {group}; productRefGroup = {products}; projectDirPath = ""; projectRoot = ""; targets = ({target},);')
proj = root / 'Komorebi.xcodeproj'
(proj / 'xcshareddata/xcschemes').mkdir(parents=True,exist_ok=True)
(proj / 'project.pbxproj').write_text('// !$*UTF8*$!\n{ archiveVersion = 1; classes = {}; objectVersion = 56; objects = {\n'+'\n'.join(objects)+f'\n}}; rootObject = {project}; }}\n')
ref = f'<BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{target}" BuildableName="Komorebi.app" BlueprintName="Komorebi" ReferencedContainer="container:Komorebi.xcodeproj"/>'
scheme = f'''<?xml version="1.0" encoding="UTF-8"?><Scheme LastUpgradeVersion="2600" version="1.3"><BuildAction parallelizeBuildables="YES" buildImplicitDependencies="YES"><BuildActionEntries><BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES">{ref}</BuildActionEntry></BuildActionEntries></BuildAction><TestAction buildConfiguration="Debug" shouldUseLaunchSchemeArgsEnv="YES"/><LaunchAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" launchStyle="0" useCustomWorkingDirectory="NO" ignoresPersistentStateOnLaunch="NO" debugDocumentVersioning="YES" debugServiceExtension="internal" allowLocationSimulation="YES"><BuildableProductRunnable runnableDebuggingMode="0">{ref}</BuildableProductRunnable></LaunchAction><ProfileAction buildConfiguration="Release" shouldUseLaunchSchemeArgsEnv="YES" savedToolIdentifier="" useCustomWorkingDirectory="NO" debugDocumentVersioning="YES"><BuildableProductRunnable runnableDebuggingMode="0">{ref}</BuildableProductRunnable></ProfileAction><AnalyzeAction buildConfiguration="Debug"/><ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES"/></Scheme>'''
(proj / 'xcshareddata/xcschemes/Komorebi.xcscheme').write_text(scheme)
print(f'Generated {proj.name}: {len(swift)} Swift files, {len(resources)} resource bundles.')
