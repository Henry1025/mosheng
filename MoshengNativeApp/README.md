# 墨声 Windows Native App

这是墨声的 Windows 本地 EXE 原型。

## 运行

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Run
```

构建产物会生成在：

```text
MoshengNativeApp\dist\Mosheng.exe
```

## 当前能力

- 智谱 `GLM-ASR-2512` 语音转文字
- Windows 托盘常驻
- 长按快捷键录音，松开后自动输入到当前光标
- API Key 使用 Windows DPAPI 保存在本机
- 黑白极简悬浮输入胶囊
- 长音频自动按 28 秒分段识别，再合并文本

## 注意

当前版本使用 .NET Framework 自带编译器构建，方便在没有 .NET SDK 的机器上快速生成 EXE。正式版本建议迁移到 .NET/WPF 或 WinUI，以获得更细腻的动画、字体渲染和窗口边缘质量。
