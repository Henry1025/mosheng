param(
  [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Net.Http

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetUnhandledExceptionMode([System.Windows.Forms.UnhandledExceptionMode]::CatchException)
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

Add-Type @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class MoshengNative {
  public delegate void KeyboardEvent(int vkCode, bool isDown);
  public static event KeyboardEvent KeyboardAction;

  private const int WH_KEYBOARD_LL = 13;
  private const int WM_KEYDOWN = 0x0100;
  private const int WM_KEYUP = 0x0101;
  private const int WM_SYSKEYDOWN = 0x0104;
  private const int WM_SYSKEYUP = 0x0105;

  private static LowLevelKeyboardProc _proc = HookCallback;
  private static IntPtr _hookId = IntPtr.Zero;

  public static void StartHook() {
    if (_hookId == IntPtr.Zero) _hookId = SetHook(_proc);
  }

  public static void StopHook() {
    if (_hookId != IntPtr.Zero) {
      UnhookWindowsHookEx(_hookId);
      _hookId = IntPtr.Zero;
    }
  }

  private static IntPtr SetHook(LowLevelKeyboardProc proc) {
    using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
    using (System.Diagnostics.ProcessModule module = process.MainModule) {
      return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
    }
  }

  private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

  private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
    if (nCode >= 0) {
      int message = wParam.ToInt32();
      if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN || message == WM_KEYUP || message == WM_SYSKEYUP) {
        int vkCode = Marshal.ReadInt32(lParam);
        bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
        if (KeyboardAction != null) KeyboardAction(vkCode, isDown);
      }
    }
    return CallNextHookEx(_hookId, nCode, wParam, lParam);
  }

  [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
  private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

  [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool UnhookWindowsHookEx(IntPtr hhk);

  [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
  private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

  [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
  private static extern IntPtr GetModuleHandle(string lpModuleName);

  [DllImport("user32.dll")]
  public static extern IntPtr GetForegroundWindow();

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool SetForegroundWindow(IntPtr hWnd);

  [DllImport("gdi32.dll")]
  public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

  [DllImport("user32.dll")]
  public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
}

public sealed class MoshengWaveRecorder : IDisposable {
  private const int WAVE_MAPPER = -1;
  private const uint CALLBACK_FUNCTION = 0x00030000;
  private const uint WIM_DATA = 0x3C0;
  private const ushort WAVE_FORMAT_PCM = 1;

  [StructLayout(LayoutKind.Sequential)]
  private struct WaveFormatEx {
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct WaveHdr {
    public IntPtr lpData;
    public uint dwBufferLength;
    public uint dwBytesRecorded;
    public IntPtr dwUser;
    public uint dwFlags;
    public uint dwLoops;
    public IntPtr lpNext;
    public IntPtr reserved;
  }

  private sealed class BufferState {
    public byte[] Data;
    public GCHandle DataHandle;
    public IntPtr HeaderPtr;
  }

  private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

  [DllImport("winmm.dll")]
  private static extern uint waveInGetNumDevs();

  [DllImport("winmm.dll")]
  private static extern int waveInOpen(out IntPtr hWaveIn, int uDeviceID, ref WaveFormatEx lpFormat, WaveInProc dwCallback, IntPtr dwInstance, uint dwFlags);

  [DllImport("winmm.dll")]
  private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

  [DllImport("winmm.dll")]
  private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

  [DllImport("winmm.dll")]
  private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

  [DllImport("winmm.dll")]
  private static extern int waveInStart(IntPtr hWaveIn);

  [DllImport("winmm.dll")]
  private static extern int waveInStop(IntPtr hWaveIn);

  [DllImport("winmm.dll")]
  private static extern int waveInReset(IntPtr hWaveIn);

  [DllImport("winmm.dll")]
  private static extern int waveInClose(IntPtr hWaveIn);

  private readonly object _sync = new object();
  private readonly List<BufferState> _buffers = new List<BufferState>();
  private readonly int _sampleRate = 16000;
  private readonly short _channels = 1;
  private readonly short _bitsPerSample = 16;
  private IntPtr _handle = IntPtr.Zero;
  private WaveInProc _callback;
  private MemoryStream _stream;
  private bool _recording;
  private int _headerSize;

  public void Start() {
    if (_handle != IntPtr.Zero) throw new InvalidOperationException("Recorder is already running.");
    if (waveInGetNumDevs() == 0) throw new InvalidOperationException("No recording device was found.");

    _stream = new MemoryStream();
    _callback = Callback;
    _headerSize = Marshal.SizeOf(typeof(WaveHdr));

    WaveFormatEx format = new WaveFormatEx();
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = (ushort)_channels;
    format.nSamplesPerSec = (uint)_sampleRate;
    format.wBitsPerSample = (ushort)_bitsPerSample;
    format.nBlockAlign = (ushort)(_channels * (_bitsPerSample / 8));
    format.nAvgBytesPerSec = (uint)(_sampleRate * format.nBlockAlign);
    format.cbSize = 0;

    int result = waveInOpen(out _handle, WAVE_MAPPER, ref format, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
    Check(result, "waveInOpen");

    _recording = true;
    int bufferSize = _sampleRate * format.nBlockAlign / 5;
    for (int i = 0; i < 4; i++) AddBuffer(bufferSize);
    Check(waveInStart(_handle), "waveInStart");
  }

  public string StopToFile(string path) {
    byte[] data;
    lock (_sync) {
      _recording = false;
      data = _stream == null ? new byte[0] : _stream.ToArray();
    }

    if (_handle != IntPtr.Zero) {
      waveInStop(_handle);
      waveInReset(_handle);
      foreach (BufferState buffer in _buffers) {
        waveInUnprepareHeader(_handle, buffer.HeaderPtr, _headerSize);
      }
      waveInClose(_handle);
      _handle = IntPtr.Zero;
    }

    foreach (BufferState buffer in _buffers) {
      if (buffer.HeaderPtr != IntPtr.Zero) Marshal.FreeHGlobal(buffer.HeaderPtr);
      if (buffer.DataHandle.IsAllocated) buffer.DataHandle.Free();
    }
    _buffers.Clear();

    if (_stream != null) {
      _stream.Dispose();
      _stream = null;
    }

    if (!String.IsNullOrEmpty(path)) WriteWav(path, data);
    return path;
  }

  public void Dispose() {
    try { StopToFile(null); } catch { }
  }

  private void AddBuffer(int bufferSize) {
    BufferState state = new BufferState();
    state.Data = new byte[bufferSize];
    state.DataHandle = GCHandle.Alloc(state.Data, GCHandleType.Pinned);
    state.HeaderPtr = Marshal.AllocHGlobal(_headerSize);

    WaveHdr header = new WaveHdr();
    header.lpData = state.DataHandle.AddrOfPinnedObject();
    header.dwBufferLength = (uint)bufferSize;
    Marshal.StructureToPtr(header, state.HeaderPtr, false);

    Check(waveInPrepareHeader(_handle, state.HeaderPtr, _headerSize), "waveInPrepareHeader");
    Check(waveInAddBuffer(_handle, state.HeaderPtr, _headerSize), "waveInAddBuffer");
    _buffers.Add(state);
  }

  private void Callback(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2) {
    if (uMsg != WIM_DATA || dwParam1 == IntPtr.Zero) return;

    WaveHdr header = (WaveHdr)Marshal.PtrToStructure(dwParam1, typeof(WaveHdr));
    lock (_sync) {
      if (_stream != null && header.dwBytesRecorded > 0) {
        byte[] chunk = new byte[header.dwBytesRecorded];
        Marshal.Copy(header.lpData, chunk, 0, (int)header.dwBytesRecorded);
        _stream.Write(chunk, 0, chunk.Length);
      }
      if (_recording && _handle != IntPtr.Zero) {
        header.dwBytesRecorded = 0;
        Marshal.StructureToPtr(header, dwParam1, false);
        waveInAddBuffer(_handle, dwParam1, _headerSize);
      }
    }
  }

  private void WriteWav(string path, byte[] data) {
    using (FileStream fs = File.Create(path))
    using (BinaryWriter writer = new BinaryWriter(fs)) {
      short blockAlign = (short)(_channels * (_bitsPerSample / 8));
      int byteRate = _sampleRate * blockAlign;
      writer.Write(Encoding.ASCII.GetBytes("RIFF"));
      writer.Write(36 + data.Length);
      writer.Write(Encoding.ASCII.GetBytes("WAVE"));
      writer.Write(Encoding.ASCII.GetBytes("fmt "));
      writer.Write(16);
      writer.Write((short)1);
      writer.Write(_channels);
      writer.Write(_sampleRate);
      writer.Write(byteRate);
      writer.Write(blockAlign);
      writer.Write(_bitsPerSample);
      writer.Write(Encoding.ASCII.GetBytes("data"));
      writer.Write(data.Length);
      writer.Write(data);
    }
  }

  private void Check(int result, string action) {
    if (result != 0) throw new InvalidOperationException(action + " failed with code " + result + ".");
  }
}
"@

if ($SelfTest) {
  Write-Host "Mosheng Windows demo self-test OK"
  exit 0
}

$script:Ink = [System.Drawing.Color]::FromArgb(12, 12, 12)
$script:Muted = [System.Drawing.Color]::FromArgb(112, 112, 112)
$script:Line = [System.Drawing.Color]::FromArgb(210, 210, 210)
$script:Paper = [System.Drawing.Color]::FromArgb(245, 245, 243)
$script:White = [System.Drawing.Color]::White
$script:Black = [System.Drawing.Color]::Black
$script:ConfigDir = Join-Path $env:APPDATA "Mosheng"
$script:ConfigPath = Join-Path $script:ConfigDir "settings.json"
$script:LogPath = Join-Path $script:ConfigDir "mosheng-demo.log"
$script:TempDir = Join-Path $env:TEMP "Mosheng"
$script:ProviderName = "智谱"
$script:TranscriptionUrl = "https://open.bigmodel.cn/api/paas/v4/audio/transcriptions"
$script:TranscriptionModel = "glm-asr-2512"
$script:ProtectedApiKey = ""
$script:SavedKeyTail = ""
$script:ShortcutVk = 0xA0
$script:ShortcutLabel = "长按左 Shift"
$script:ShortcutIdleLabel = "按住左 Shift"
$script:CapturingShortcut = $false
$script:ShortcutHeld = $false
$script:IsRecording = $false
$script:IsProcessing = $false
$script:Recorder = $null
$script:ActiveWindowHandle = [IntPtr]::Zero
$script:PreviousClipboard = $null
$script:RestoreClipboard = $true
$script:IsExiting = $false
$script:MeterTick = 0
$script:KeyboardQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[object]'
$script:PillMode = "idle"
$script:PillShowActions = $false

function New-Font {
  param(
    [float]$Size,
    [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular,
    [string]$Family = "Microsoft YaHei UI"
  )
  return New-Object System.Drawing.Font -ArgumentList $Family, $Size, $Style
}

function Write-Log {
  param([string]$Message)
  try {
    if (-not (Test-Path $script:ConfigDir)) {
      [void](New-Item -ItemType Directory -Path $script:ConfigDir -Force)
    }
    $safeMessage = [string]$Message
    try {
      $currentKey = Get-ApiKey
      if (-not [string]::IsNullOrWhiteSpace($currentKey) -and $currentKey.Length -ge 6) {
        $safeMessage = $safeMessage.Replace($currentKey, "[REDACTED_API_KEY]")
      }
    } catch { }
    $safeMessage = [regex]::Replace($safeMessage, "(?i)(Bearer\s+)[^,\s`"']+", '$1[REDACTED]')
    $safeMessage = [regex]::Replace($safeMessage, "(?i)sk-[A-Za-z0-9._-]{8,}", "[REDACTED_API_KEY]")
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $safeMessage"
    Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8
  } catch { }
}

function Set-Bounds {
  param($Control, [int]$X, [int]$Y, [int]$Width, [int]$Height)
  $Control.Location = New-Object System.Drawing.Point -ArgumentList $X, $Y
  $Control.Size = New-Object System.Drawing.Size -ArgumentList $Width, $Height
}

function New-Label {
  param(
    [string]$Text,
    [int]$X,
    [int]$Y,
    [int]$Width,
    [int]$Height,
    [float]$Size = 10,
    [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular,
    $Color = $script:Ink
  )
  $label = New-Object System.Windows.Forms.Label
  $label.Text = $Text
  $label.Font = New-Font $Size $Style
  $label.ForeColor = $Color
  $label.AutoSize = $false
  Set-Bounds $label $X $Y $Width $Height
  return $label
}

function Set-FlatButton {
  param($Button, $BackColor, $ForeColor)
  $Button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
  $Button.FlatAppearance.BorderSize = 1
  $Button.FlatAppearance.BorderColor = $script:Ink
  $Button.BackColor = $BackColor
  $Button.ForeColor = $ForeColor
  $Button.Cursor = [System.Windows.Forms.Cursors]::Hand
}

function Protect-Secret {
  param([string]$Value)
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
  $protected = [System.Security.Cryptography.ProtectedData]::Protect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
  return [Convert]::ToBase64String($protected)
}

function Unprotect-Secret {
  param([string]$Value)
  if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
  try {
    $bytes = [Convert]::FromBase64String($Value)
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [System.Text.Encoding]::UTF8.GetString($plain)
  } catch {
    return ""
  }
}

function Get-KeyLabel {
  param([int]$Vk)
  switch ($Vk) {
    0xA0 { return "长按左 Shift" }
    0xA1 { return "长按右 Shift" }
    0xA2 { return "长按左 Ctrl" }
    0xA3 { return "长按右 Ctrl" }
    0xA4 { return "长按左 Alt" }
    0xA5 { return "长按右 Alt" }
    0x20 { return "长按 Space" }
    default { return "长按 VK $Vk" }
  }
}

function Get-IdleLabel {
  param([string]$Label)
  return $Label.Replace("长按", "按住")
}

function Save-Settings {
  if (-not (Test-Path $script:ConfigDir)) {
    [void](New-Item -ItemType Directory -Path $script:ConfigDir -Force)
  }
  $data = [ordered]@{
    protectedApiKey = $script:ProtectedApiKey
    keyTail = $script:SavedKeyTail
    shortcutVk = $script:ShortcutVk
    shortcutLabel = $script:ShortcutLabel
  }
  $data | ConvertTo-Json | Set-Content -Path $script:ConfigPath -Encoding UTF8
}

function Load-Settings {
  if (-not (Test-Path $script:ConfigPath)) { return }
  try {
    $data = Get-Content -Path $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($data.protectedApiKey) { $script:ProtectedApiKey = [string]$data.protectedApiKey }
    if ($data.keyTail) { $script:SavedKeyTail = [string]$data.keyTail }
    if ($data.shortcutVk) { $script:ShortcutVk = [int]$data.shortcutVk }
    if ($data.shortcutLabel) { $script:ShortcutLabel = [string]$data.shortcutLabel }
    $script:ShortcutIdleLabel = Get-IdleLabel $script:ShortcutLabel
  } catch {
    $script:ProtectedApiKey = ""
    $script:SavedKeyTail = ""
  }
}

function Get-ApiKey {
  $draft = $script:ApiKeyBox.Text.Trim()
  if (-not [string]::IsNullOrWhiteSpace($draft)) { return $draft }
  return Unprotect-Secret $script:ProtectedApiKey
}

function Set-Status {
  param([string]$Text)
  if ($script:StatusLabel) { $script:StatusLabel.Text = $Text }
  if ($script:TrayIcon) { $script:TrayIcon.Text = "墨声 - $Text" }
}

[System.Windows.Forms.Application]::add_ThreadException({
  param($sender, $eventArgs)
  Write-Log ("UI exception: " + $eventArgs.Exception.ToString())
  Set-Status "运行异常，已写入日志"
})

[AppDomain]::CurrentDomain.add_UnhandledException({
  param($sender, $eventArgs)
  if ($eventArgs.ExceptionObject) {
    Write-Log ("Unhandled exception: " + $eventArgs.ExceptionObject.ToString())
  }
})

function Sync-SettingsUi {
  if ($script:ShortcutButton) {
    $script:ShortcutButton.Text = "快捷键        $script:ShortcutLabel"
  }
  if ($script:KeyState) {
    if ($script:SavedKeyTail) { $script:KeyState.Text = "已保存 · $script:SavedKeyTail" }
    else { $script:KeyState.Text = "未保存" }
  }
}

function Save-ApiKey {
  $value = $script:ApiKeyBox.Text.Trim()
  if ([string]::IsNullOrWhiteSpace($value)) {
    if ($script:ProtectedApiKey) {
      Set-Status "已保存智谱 API Key"
      return $true
    }
    Set-Status "请输入智谱 API Key"
    return $false
  }
  $script:ProtectedApiKey = Protect-Secret $value
  $script:SavedKeyTail = $value.Substring([Math]::Max(0, $value.Length - [Math]::Min(4, $value.Length)))
  $script:ApiKeyBox.Clear()
  Save-Settings
  Sync-SettingsUi
  Set-Status "智谱 API Key 已保存"
  return $true
}

function Clear-SavedApiKey {
  $script:ProtectedApiKey = ""
  $script:SavedKeyTail = ""
  if ($script:ApiKeyBox) { $script:ApiKeyBox.Clear() }
  Save-Settings
  Sync-SettingsUi
}

function Begin-ShortcutCapture {
  $script:CapturingShortcut = $true
  $script:ShortcutButton.Text = "快捷键        按下想用的键"
  $script:ShortcutButton.BackColor = $script:Black
  $script:ShortcutButton.ForeColor = $script:White
  Set-Status "等待新的快捷键"
}

function Complete-ShortcutCapture {
  param([int]$Vk)
  $script:ShortcutVk = $Vk
  $script:ShortcutLabel = Get-KeyLabel $Vk
  $script:ShortcutIdleLabel = Get-IdleLabel $script:ShortcutLabel
  $script:CapturingShortcut = $false
  $script:ShortcutButton.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 250)
  $script:ShortcutButton.ForeColor = $script:Ink
  Save-Settings
  Sync-SettingsUi
  Set-Status "快捷键已更新"
}

function Enter-InputMode {
  if (-not (Save-ApiKey)) { return }
  $script:SettingsForm.Hide()
  Set-Status "运行中 · $script:ShortcutLabel"
  $script:TrayIcon.ShowBalloonTip(1800, "墨声正在运行", "$script:ShortcutLabel 录音，松开后转写并输入。", [System.Windows.Forms.ToolTipIcon]::Info)
}

function Show-Settings {
  Sync-SettingsUi
  $script:SettingsForm.Show()
  $script:SettingsForm.Activate()
}

function Set-PillRegion {
  if (-not $script:PillForm -or -not $script:PillForm.IsHandleCreated) { return }
  $region = [MoshengNative]::CreateRoundRectRgn(0, 0, $script:PillForm.Width, $script:PillForm.Height, $script:PillForm.Height, $script:PillForm.Height)
  [void][MoshengNative]::SetWindowRgn($script:PillForm.Handle, $region, $true)
}

function New-RoundedPath {
  param([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius)

  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $diameter = [single]($Radius * 2)
  [void]$path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
  [void]$path.AddArc(($X + $Width - $diameter), $Y, $diameter, $diameter, 270, 90)
  [void]$path.AddArc(($X + $Width - $diameter), ($Y + $Height - $diameter), $diameter, $diameter, 0, 90)
  [void]$path.AddArc($X, ($Y + $Height - $diameter), $diameter, $diameter, 90, 90)
  [void]$path.CloseFigure()
  return $path
}

function Update-PillSize {
  if (-not $script:PillForm) { return }
  if ($script:PillShowActions) {
    $script:PillForm.Size = New-Object System.Drawing.Size -ArgumentList 132, 34
  } else {
    $script:PillForm.Size = New-Object System.Drawing.Size -ArgumentList 92, 34
  }
}

function Paint-Pill {
  param([System.Drawing.Graphics]$Graphics)

  $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $Graphics.Clear($script:Black)

  $width = [single]$script:PillForm.Width
  $height = [single]$script:PillForm.Height
  $bodyBrush = New-Object System.Drawing.SolidBrush $script:Black
  $body = New-RoundedPath 0 0 $width $height ($height / 2)
  $Graphics.FillPath($bodyBrush, $body)
  $body.Dispose()
  $bodyBrush.Dispose()

  if ($script:PillShowActions) {
    $leftBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 34, 34))
    $leftPath = New-RoundedPath 4 4 28 26 13
    $Graphics.FillPath($leftBrush, $leftPath)
    $leftPath.Dispose()
    $leftBrush.Dispose()

    $xPen = New-Object System.Drawing.Pen $script:White, 2.1
    $xPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $xPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $Graphics.DrawLine($xPen, 14, 13, 22, 21)
    $Graphics.DrawLine($xPen, 22, 13, 14, 21)
    $xPen.Dispose()

    $rightFill = $script:White
    if ($script:PillMode -eq "error") { $rightFill = [System.Drawing.Color]::FromArgb(222, 222, 222) }
    $rightBrush = New-Object System.Drawing.SolidBrush $rightFill
    $rightPath = New-RoundedPath ($width - 34) 4 30 26 13
    $Graphics.FillPath($rightBrush, $rightPath)
    $rightPath.Dispose()
    $rightBrush.Dispose()

    $checkPen = New-Object System.Drawing.Pen $script:Black, 2.4
    $checkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $checkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $Graphics.DrawLines($checkPen, @(
      (New-Object System.Drawing.PointF -ArgumentList ($width - 23), 17),
      (New-Object System.Drawing.PointF -ArgumentList ($width - 18), 22),
      (New-Object System.Drawing.PointF -ArgumentList ($width - 10), 12)
    ))
    $checkPen.Dispose()
  }

  $barCount = if ($script:PillShowActions) { 7 } else { 6 }
  $gap = [single]5.5
  $barWidth = [single]2.2
  $totalBars = (($barCount - 1) * $gap) + $barWidth
  $startX = [single](($width - $totalBars) / 2)
  $centerY = [single]($height / 2)
  $barColor = $script:White
  if ($script:PillMode -eq "processing") { $barColor = [System.Drawing.Color]::FromArgb(218, 218, 218) }
  $barBrush = New-Object System.Drawing.SolidBrush $barColor

  for ($i = 0; $i -lt $barCount; $i++) {
    if ($script:PillMode -eq "success" -or $script:PillMode -eq "error") {
      $barHeight = @(10, 16, 22, 22, 16, 12, 8)[$i]
    } else {
      $barHeight = [single](8 + [Math]::Abs([Math]::Sin(($script:MeterTick + ($i * 1.65)) / 2.15)) * 16)
    }
    $barX = [single]($startX + ($i * $gap))
    $barY = [single]($centerY - ($barHeight / 2))
    $barPath = New-RoundedPath $barX $barY $barWidth ([single]$barHeight) 1.1
    $Graphics.FillPath($barBrush, $barPath)
    $barPath.Dispose()
  }
  $barBrush.Dispose()
}

function Position-Pill {
  $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $x = [int]($area.Left + (($area.Width - $script:PillForm.Width) / 2))
  $y = [int]($area.Bottom - 72)
  $script:PillForm.Location = New-Object System.Drawing.Point -ArgumentList $x, $y
}

function Set-PillMode {
  param([string]$Mode)
  $script:PillMode = $Mode
  switch ($Mode) {
    "recording" {
      $script:PillShowActions = $false
      $script:MeterTimer.Start()
    }
    "processing" {
      $script:PillShowActions = $false
      $script:MeterTimer.Start()
    }
    "success" {
      $script:PillShowActions = $true
      $script:MeterTimer.Stop()
    }
    "error" {
      $script:PillShowActions = $true
      $script:MeterTimer.Stop()
    }
  }
  Update-PillSize
  Position-Pill
  Set-PillRegion
  $script:PillForm.Invalidate()
  if (-not $script:PillForm.Visible) { $script:PillForm.Show() }
  $script:PillForm.BringToFront()
}

function Hide-PillLater {
  $script:HideTimer.Stop()
  $script:HideTimer.Start()
}

function Get-FriendlyTranscriptionError {
  param($ErrorRecord)

  $message = [string]$ErrorRecord.Exception.Message
  Write-Log ("Transcription error: " + $message)

  if ($message -match "HTTP 401" -or $message -match "invalid_api_key" -or $message -match "Incorrect API key" -or $message -match "invalid token") {
    return [pscustomobject]@{
      Text = "智谱 API Key 无效或已过期。请重新粘贴有效的智谱 API Key。"
      ShouldOpenSettings = $true
      Status = "智谱 API Key 无效"
    }
  }

  if ($message -match "HTTP 404" -or $message -match "not found" -or $message -match "unsupported" -or $message -match "audio") {
    return [pscustomobject]@{
      Text = "当前智谱语音识别接口不可用。请检查账号是否已开通 GLM-ASR-2512，或稍后重试。"
      ShouldOpenSettings = $false
      Status = "智谱语音识别不可用"
    }
  }

  if ($message -match "1214" -or $message -match "时长限制" -or $message -match "0-30秒") {
    return [pscustomobject]@{
      Text = "单次语音不能超过 30 秒。请短一点再试。"
      ShouldOpenSettings = $false
      Status = "录音太长"
    }
  }

  if ($message -match "HTTP 429" -or $message -match "insufficient_quota" -or $message -match "rate_limit") {
    return [pscustomobject]@{
      Text = "当前账号额度不足或请求过于频繁。请稍后再试，或检查账号额度。"
      ShouldOpenSettings = $false
      Status = "额度或频率限制"
    }
  }

  if ($message -match "NameResolutionFailure" -or $message -match "timed out" -or $message -match "无法连接" -or $message -match "network") {
    return [pscustomobject]@{
      Text = "网络连接失败。请确认当前网络可以访问智谱 API。"
      ShouldOpenSettings = $false
      Status = "网络连接失败"
    }
  }

  return [pscustomobject]@{
    Text = "语音识别失败，请稍后再试。"
    ShouldOpenSettings = $false
    Status = "识别失败"
  }
}

function Start-Capture {
  $apiKey = Get-ApiKey
  if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Set-Status "先输入智谱 API Key"
    Show-Settings
    return
  }

  try {
    if (-not (Test-Path $script:TempDir)) {
      [void](New-Item -ItemType Directory -Path $script:TempDir -Force)
    }
    $script:ActiveWindowHandle = [MoshengNative]::GetForegroundWindow()
    $script:Recorder = New-Object MoshengWaveRecorder
    $script:Recorder.Start()
    $script:IsRecording = $true
    $script:MeterTick = 0
    Set-PillMode "recording"
    $script:MaxCaptureTimer.Stop()
    $script:MaxCaptureTimer.Start()
    Set-Status "正在收音"
  } catch {
    $script:IsRecording = $false
    Set-PillMode "error"
    Set-Status "录音失败"
    [System.Windows.Forms.MessageBox]::Show("无法开始录音：$($_.Exception.Message)", "墨声", "OK", "Warning") | Out-Null
    Hide-PillLater
  }
}

function Stop-Capture {
  if (-not $script:IsRecording -or -not $script:Recorder) { return }
  $script:MaxCaptureTimer.Stop()
  $script:IsRecording = $false
  $script:IsProcessing = $true
  Set-PillMode "processing"
  Set-Status "正在识别"
  [System.Windows.Forms.Application]::DoEvents()

  $wavPath = Join-Path $script:TempDir ("capture-" + [Guid]::NewGuid().ToString("N") + ".wav")
  try {
    $script:Recorder.StopToFile($wavPath) | Out-Null
    $script:Recorder = $null
    $text = Invoke-Transcription $wavPath
    if (-not [string]::IsNullOrWhiteSpace($text)) {
      Insert-Text $text.Trim()
      Set-PillMode "success"
      Set-Status "已输入"
    } else {
      Set-PillMode "error"
      Set-Status "未识别到文字"
    }
  } catch {
    Set-PillMode "error"
    $friendly = Get-FriendlyTranscriptionError $_
    Set-Status $friendly.Status
    if ($friendly.ShouldOpenSettings) { Clear-SavedApiKey }
    [System.Windows.Forms.MessageBox]::Show($friendly.Text, "墨声", "OK", "Warning") | Out-Null
    if ($friendly.ShouldOpenSettings) { Show-Settings }
  } finally {
    $script:IsProcessing = $false
    if (Test-Path $wavPath) {
      Remove-Item -LiteralPath $wavPath -Force -ErrorAction SilentlyContinue
    }
    Hide-PillLater
  }
}

function Invoke-Transcription {
  param([string]$WavPath)
  $apiKey = Get-ApiKey
  if ([string]::IsNullOrWhiteSpace($apiKey)) { throw "Missing Zhipu API Key." }

  $client = New-Object System.Net.Http.HttpClient
  $client.Timeout = [TimeSpan]::FromSeconds(75)
  $client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue -ArgumentList "Bearer", $apiKey
  $form = New-Object System.Net.Http.MultipartFormDataContent
  $stream = [System.IO.File]::OpenRead($WavPath)
  try {
    $fileContent = New-Object System.Net.Http.StreamContent -ArgumentList $stream
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("audio/wav")
    $form.Add($fileContent, "file", [System.IO.Path]::GetFileName($WavPath))
    $form.Add((New-Object System.Net.Http.StringContent -ArgumentList $script:TranscriptionModel), "model")
    $form.Add((New-Object System.Net.Http.StringContent -ArgumentList "false"), "stream")

    $response = $client.PostAsync($script:TranscriptionUrl, $form).Result
    $body = $response.Content.ReadAsStringAsync().Result
    if (-not $response.IsSuccessStatusCode) {
      $rawError = "HTTP $([int]$response.StatusCode): $body"
      Write-Log ("Zhipu raw response: " + $rawError)
      throw $rawError
    }
    $json = $body | ConvertFrom-Json
    return [string]$json.text
  } finally {
    $form.Dispose()
    $client.Dispose()
    $stream.Dispose()
  }
}

function Insert-Text {
  param([string]$Text)
  if ($script:ActiveWindowHandle -ne [IntPtr]::Zero) {
    [void][MoshengNative]::SetForegroundWindow($script:ActiveWindowHandle)
    Start-Sleep -Milliseconds 80
  }

  try {
    if ($script:RestoreClipboard -and [System.Windows.Forms.Clipboard]::ContainsText()) {
      $script:PreviousClipboard = [System.Windows.Forms.Clipboard]::GetText()
    } else {
      $script:PreviousClipboard = $null
    }
    [System.Windows.Forms.Clipboard]::SetText($Text)
    Start-Sleep -Milliseconds 50
    [System.Windows.Forms.SendKeys]::SendWait("^v")
    Start-Sleep -Milliseconds 100
    if ($script:RestoreClipboard -and $null -ne $script:PreviousClipboard) {
      [System.Windows.Forms.Clipboard]::SetText($script:PreviousClipboard)
    }
  } catch {
    [System.Windows.Forms.Clipboard]::SetText($Text)
    throw "文字已复制到剪贴板，但自动粘贴失败。"
  }
}

function Cancel-Capture {
  $script:MaxCaptureTimer.Stop()
  if ($script:IsRecording -and $script:Recorder) {
    try { $script:Recorder.StopToFile($null) | Out-Null } catch { }
  }
  $script:IsRecording = $false
  $script:IsProcessing = $false
  $script:MeterTimer.Stop()
  $script:PillForm.Hide()
  Set-Status "已取消"
}

function Handle-KeyboardEvent {
  param([int]$VkCode, [bool]$IsDown)
  if ($script:IsExiting) { return }

  if ($script:CapturingShortcut -and $IsDown) {
    if ($VkCode -eq 0x1B) {
      $script:CapturingShortcut = $false
      $script:ShortcutButton.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 250)
      $script:ShortcutButton.ForeColor = $script:Ink
      Sync-SettingsUi
      return
    }
    Complete-ShortcutCapture $VkCode
    return
  }

  if ($VkCode -ne $script:ShortcutVk) { return }
  if ($IsDown) {
    if ($script:ShortcutHeld -or $script:IsProcessing) { return }
    $script:ShortcutHeld = $true
    $script:HoldTimer.Stop()
    $script:HoldTimer.Start()
    return
  }

  $script:ShortcutHeld = $false
  $script:HoldTimer.Stop()
  if ($script:IsRecording) { Stop-Capture }
}

function Exit-Mosheng {
  $script:IsExiting = $true
  try { [MoshengNative]::StopHook() } catch { }
  try { $script:TrayIcon.Visible = $false; $script:TrayIcon.Dispose() } catch { }
  try { if ($script:Recorder) { $script:Recorder.Dispose() } } catch { }
  [System.Windows.Forms.Application]::Exit()
}

Load-Settings

$script:SettingsForm = New-Object System.Windows.Forms.Form
$script:SettingsForm.Text = "墨声"
$script:SettingsForm.StartPosition = "CenterScreen"
$script:SettingsForm.Size = New-Object System.Drawing.Size -ArgumentList 520, 430
$script:SettingsForm.MinimumSize = New-Object System.Drawing.Size -ArgumentList 480, 390
$script:SettingsForm.BackColor = $script:White
$script:SettingsForm.Font = New-Font 9

$script:SettingsForm.Controls.Add((New-Label "MOSHENG" 28 24 120 18 8 ([System.Drawing.FontStyle]::Bold) $script:Muted))
$script:SettingsForm.Controls.Add((New-Label "墨声" 28 44 200 48 28 ([System.Drawing.FontStyle]::Bold) $script:Ink))
$script:SettingsForm.Controls.Add((New-Label "输入智谱 API Key" 28 122 260 32 18 ([System.Drawing.FontStyle]::Bold) $script:Ink))
$script:SettingsForm.Controls.Add((New-Label "保存后即可启动语音输入。" 28 156 320 22 9 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$script:SettingsForm.Controls.Add((New-Label "智谱 API Key" 28 194 160 18 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))

$script:ApiKeyBox = New-Object System.Windows.Forms.TextBox
$script:ApiKeyBox.PasswordChar = "*"
$script:ApiKeyBox.Font = New-Font 11
$script:ApiKeyBox.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
Set-Bounds $script:ApiKeyBox 28 216 448 34
$script:SettingsForm.Controls.Add($script:ApiKeyBox)

$saveButton = New-Object System.Windows.Forms.Button
$saveButton.Text = "保存并开始使用"
Set-Bounds $saveButton 28 268 152 38
Set-FlatButton $saveButton $script:Black $script:White
$script:SettingsForm.Controls.Add($saveButton)

$script:KeyState = New-Label "未保存" 196 276 160 22 8 ([System.Drawing.FontStyle]::Bold) $script:Muted
$script:SettingsForm.Controls.Add($script:KeyState)

$script:ShortcutButton = New-Object System.Windows.Forms.Button
$script:ShortcutButton.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
Set-Bounds $script:ShortcutButton 28 324 448 42
Set-FlatButton $script:ShortcutButton ([System.Drawing.Color]::FromArgb(250, 250, 250)) $script:Ink
$script:SettingsForm.Controls.Add($script:ShortcutButton)

$script:StatusLabel = New-Label "设置中" 28 374 300 20 8 ([System.Drawing.FontStyle]::Regular) $script:Muted
$script:SettingsForm.Controls.Add($script:StatusLabel)

$script:PillForm = New-Object System.Windows.Forms.Form
$script:PillForm.Text = "墨声输入"
$script:PillForm.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$script:PillForm.ShowInTaskbar = $false
$script:PillForm.TopMost = $true
$script:PillForm.Size = New-Object System.Drawing.Size -ArgumentList 92, 34
$script:PillForm.BackColor = $script:Black
$script:PillForm.Font = New-Font 9

$script:TrayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$openItem = New-Object System.Windows.Forms.ToolStripMenuItem "打开设置"
$exitItem = New-Object System.Windows.Forms.ToolStripMenuItem "退出墨声"
[void]$script:TrayMenu.Items.Add($openItem)
[void]$script:TrayMenu.Items.Add($exitItem)

$script:TrayIcon = New-Object System.Windows.Forms.NotifyIcon
$script:TrayIcon.Icon = [System.Drawing.SystemIcons]::Application
$script:TrayIcon.Text = "墨声 - 设置中"
$script:TrayIcon.Visible = $true
$script:TrayIcon.ContextMenuStrip = $script:TrayMenu

$script:HoldTimer = New-Object System.Windows.Forms.Timer
$script:HoldTimer.Interval = 260
$script:HoldTimer.Add_Tick({
  $script:HoldTimer.Stop()
  if ($script:ShortcutHeld -and -not $script:IsRecording -and -not $script:IsProcessing) {
    Start-Capture
  }
})

$script:MeterTimer = New-Object System.Windows.Forms.Timer
$script:MeterTimer.Interval = 55
$script:MeterTimer.Add_Tick({
  $script:MeterTick += 1
  if ($script:PillForm.Visible) { $script:PillForm.Invalidate() }
})

$script:MaxCaptureTimer = New-Object System.Windows.Forms.Timer
$script:MaxCaptureTimer.Interval = 28000
$script:MaxCaptureTimer.Add_Tick({
  $script:MaxCaptureTimer.Stop()
  if ($script:IsRecording) {
    Set-Status "已到单次上限，正在识别"
    Stop-Capture
  }
})

$script:HideTimer = New-Object System.Windows.Forms.Timer
$script:HideTimer.Interval = 780
$script:HideTimer.Add_Tick({
  $script:HideTimer.Stop()
  if (-not $script:IsRecording -and -not $script:IsProcessing) {
    $script:PillForm.Hide()
  }
})

$script:KeyboardTimer = New-Object System.Windows.Forms.Timer
$script:KeyboardTimer.Interval = 20
$script:KeyboardTimer.Add_Tick({
  $item = $null
  while ($script:KeyboardQueue.TryDequeue([ref]$item)) {
    try {
      Handle-KeyboardEvent ([int]$item.VkCode) ([bool]$item.IsDown)
    } catch {
      Set-Status "快捷键处理异常"
    }
  }
})

$saveButton.Add_Click({ Enter-InputMode })
$script:ShortcutButton.Add_Click({ Begin-ShortcutCapture })
$script:PillForm.Add_MouseClick({
  param($sender, $eventArgs)
  if ($eventArgs.X -le 38) {
    Cancel-Capture
    return
  }
  if ($script:PillShowActions -and $eventArgs.X -ge ($script:PillForm.Width - 38) -and -not $script:IsRecording -and -not $script:IsProcessing) {
    $script:PillForm.Hide()
  }
})
$openItem.Add_Click({ Show-Settings })
$exitItem.Add_Click({ Exit-Mosheng })
$script:TrayIcon.Add_DoubleClick({ Show-Settings })

$script:SettingsForm.Add_FormClosing({
  param($sender, $eventArgs)
  if ($script:IsExiting) { return }
  $eventArgs.Cancel = $true
  $script:SettingsForm.Hide()
})

$script:PillForm.Add_Shown({ Set-PillRegion })
$script:PillForm.Add_SizeChanged({ Set-PillRegion })
$script:PillForm.Add_Paint({
  param($sender, $eventArgs)
  Paint-Pill $eventArgs.Graphics
})

[MoshengNative]::add_KeyboardAction({
  param([int]$vkCode, [bool]$isDown)
  if ($script:IsExiting) { return }
  try {
    $script:KeyboardQueue.Enqueue([pscustomobject]@{
      VkCode = $vkCode
      IsDown = $isDown
    })
  } catch { }
})

Sync-SettingsUi
Set-Status "设置中"
[MoshengNative]::StartHook()
$script:KeyboardTimer.Start()
[void][System.Windows.Forms.Application]::Run($script:SettingsForm)










