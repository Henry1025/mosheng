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

  Windows integration
    - foreground window detection
    - clipboard insert
    - DPAPI secret storage
```

## Why chunking exists

智谱 `audio/transcriptions` 当前单个上传文件限制为音频时长不超过 30 秒。墨声录音结束后会把 PCM 数据切成 28 秒左右的小段，逐段转写，并把前文作为 prompt 传给下一段，减少长语音上下文断裂。

## Why not TSF yet

Windows TSF 可以做真正系统输入法，但它要求更重的 COM/C++ 输入服务开发。墨声先通过常驻 EXE 验证真实体验，再决定是否进入系统级输入法路线。
