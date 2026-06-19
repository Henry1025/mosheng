# 墨声

墨声是一个 Windows-first 的 AI 语音输入项目。它的目标不是做一个聊天助手，而是做一个很轻的本地输入工具：按住快捷键说话，松开后把文字输入到当前光标。

当前主线是 **Windows 本地 EXE**，GitHub 仓库用于开源分发、收集反馈和发布安装包。浏览器页面保留为早期 UI 评审稿。

## 当前能力

- Windows 本地运行，不依赖浏览器页面
- 托盘常驻
- 默认长按左 Shift 录音
- 调用智谱 `GLM-ASR-2512` 做语音转文字
- 自动输入到当前光标位置
- API Key 使用 Windows DPAPI 保存在本机
- 悬浮输入条采用黑白极简胶囊 UI
- 长语音自动切成 28 秒小段识别，再合并文本，绕开单段 30 秒限制

## 快速开始

Windows 上双击：

```text
Launch Local Demo.cmd
```

或者手动运行：

```powershell
cd MoshengNativeApp
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Run
```

构建产物：

```text
MoshengNativeApp\dist\Mosheng.exe
```

> 当前版本使用 Windows 自带 .NET Framework 编译器构建，方便没有 .NET SDK 的机器也能生成 EXE。正式产品建议迁移到 WPF / WinUI，以获得更好的窗口边缘、字体和动效。

## 项目结构

```text
MoshengNativeApp/
  src/Mosheng.cs        Windows 本地 EXE 源码
  build.ps1            编译脚本
  run.cmd              构建并运行

docs/
  ARCHITECTURE.md      产品和技术架构
  PRIVACY.md           隐私和 API Key 说明
  ROADMAP.md           路线图

index.html             早期 UI 评审稿
app.js
styles.css
MoshengWindowsDemo.ps1 PowerShell 原型，保留作参考
```

## 为什么不是完整输入法

真正接入 Windows 输入法列表需要开发 TSF/Text Services Framework 组件，复杂度高很多。墨声当前先走更轻的本地常驻 EXE 路线：覆盖大多数 Windows 应用，快速验证真实输入体验。

长期可以拆成三条线：

- Browser 插件：最快传播，覆盖网页输入
- Windows EXE：覆盖桌面应用，适合重度用户
- Windows IME：系统级输入法，后续再做

## 许可证

MIT
