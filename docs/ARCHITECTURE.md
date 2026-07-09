# Architecture

墨声当前采用轻量 Windows 常驻 EXE 架构。

```text
Mosheng.exe
  SettingsForm
    - API Key
    - shortcut

  OverlayForm
    - calm waveform
    - fade in/out
    - success/error state

  AppController
    - global keyboard hook
    - recording lifecycle
    - transcription lifecycle

  Audio
    - winmm waveIn recorder
    - PCM buffer
    - 28s chunking
    - temporary wav writer

  Speech
    - Zhipu GLM-ASR-2512 client
    - multipart upload
    - prompt handoff between chunks

  Smart formatting
    - local trigger detector
    - Zhipu GLM-5.2 formatter
    - validator and raw-text fallback

  Windows integration
    - foreground window detection
    - clipboard insert
    - DPAPI secret storage
```

## Why chunking exists

智谱 `audio/transcriptions` 当前单个上传文件限制为音频时长不超过 30 秒。墨声录音结束后会把 PCM 数据切成 28 秒左右的小段，逐段转写，并把前文作为 prompt 传给下一段，减少长语音上下文断裂。

## API call policy

墨声只要求用户填写一个智谱 API Key。后台按能力调用不同模型：

1. **ASR 调用**
   - 触发时机：用户按一次快捷键开始录音，再按一次结束收音并进入 Thinking Mode。
   - 调用接口：`POST https://open.bigmodel.cn/api/paas/v4/audio/transcriptions`
   - 调用模型：`glm-asr-2512`
   - 调用策略：录音有效且检测到声音信号后必调；长音频会拆成 28 秒分片逐段调用。

2. **文本整理调用**
   - 触发时机：ASR 返回原始文字后，根据用户按下的快捷键选择输入模式。
   - 调用接口：`POST https://open.bigmodel.cn/api/paas/v4/chat/completions`
   - 调用模型：`glm-5.2`
   - 调用策略：
     - 整理输入：默认调用日常整理 prompt，把口语变成可发送文本。
     - 邮件输入：默认调用邮件 prompt，把口语要点变成更正式、礼貌、分段清楚的正文。
   - 回退策略：文本模型超时、报错、输出过长、输出包含模型解释语，会回退到本地轻清理或分点兜底。

## Why not TSF yet

Windows TSF 可以做真正系统输入法，但它要求更重的 COM/C++ 输入服务开发。墨声先通过常驻 EXE 验证真实体验，再决定是否进入系统级输入法路线。
