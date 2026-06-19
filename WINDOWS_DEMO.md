# 墨声 Windows Demo

这是一个真正在 Windows 本地运行的语音输入 demo。

- 双击 `Launch Mosheng Windows Demo.cmd` 启动。
- 输入智谱 API Key 后点击「保存并开始使用」。
- 默认长按左 Shift 录音，松开后上传音频转写，并把文字粘贴到当前光标位置。
- API Key 使用 Windows 当前用户 DPAPI 加密后保存在 `%APPDATA%\Mosheng\settings.json`。
- 录音只写入临时 wav 文件用于转写，请求结束后立即删除。

当前版本是可用原型，不是最终产品安装包。后续可以换成正式 .NET/WPF 或 Tauri Windows 应用，并增加自动更新、安装器、权限引导和更完整的错误状态。

