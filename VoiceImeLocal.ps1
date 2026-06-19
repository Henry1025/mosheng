Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

Add-Type @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    if (_hookId == IntPtr.Zero) {
      _hookId = SetHook(_proc);
    }
  }

  public static void StopHook() {
    if (_hookId != IntPtr.Zero) {
      UnhookWindowsHookEx(_hookId);
      _hookId = IntPtr.Zero;
    }
  }

  private static IntPtr SetHook(LowLevelKeyboardProc proc) {
    using (Process process = Process.GetCurrentProcess())
    using (ProcessModule module = process.MainModule) {
      return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
    }
  }

  private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

  private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
    if (nCode >= 0) {
      int message = wParam.ToInt32();
      if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN || message == WM_KEYUP || message == WM_SYSKEYUP) {
        int vkCode = Marshal.ReadInt32(lParam);
        if (vkCode == 0xA0) {
          bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
          KeyboardAction?.Invoke(vkCode, isDown);
        }
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
}
"@

$script:Ink = [System.Drawing.Color]::FromArgb(16, 16, 16)
$script:Muted = [System.Drawing.Color]::FromArgb(104, 104, 104)
$script:Line = [System.Drawing.Color]::FromArgb(214, 214, 214)
$script:Paper = [System.Drawing.Color]::FromArgb(245, 245, 243)
$script:Panel = [System.Drawing.Color]::White
$script:Soft = [System.Drawing.Color]::FromArgb(238, 238, 236)

$script:Ready = $false
$script:IsRecording = $false
$script:ShiftHeld = $false
$script:SavedKeyTail = ""
$script:KeyVisible = $false
$script:ClipboardRestore = $true
$script:PreviousClipboard = $null
$script:ActiveWindowHandle = [IntPtr]::Zero
$script:IsExiting = $false
$script:RecordingTicks = 0
$script:TranscriptText = ""

$script:Samples = @(
  "我们先把墨声做成一个轻量的 Windows 语音输入工具。",
  "长按左 Shift 后出现悬浮输入栏，松开后确认文字再插入。",
  "这个版本先用模拟识别，下一步再接真实的云端语音 API。",
  "识别完成后，文字会进入当前光标所在的位置。"
)

function New-Font {
  param(
    [float]$Size,
    [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular,
    [string]$Family = "Microsoft YaHei UI"
  )
  return New-Object System.Drawing.Font -ArgumentList $Family, $Size, $Style
}

function Set-Bounds {
  param($Control, [int]$X, [int]$Y, [int]$Width, [int]$Height)
  $Control.Location = New-Object System.Drawing.Point -ArgumentList $X, $Y
  $Control.Size = New-Object System.Drawing.Size -ArgumentList $Width, $Height
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
  Set-Bounds $label $X $Y $Width $Height
  return $label
}

function New-Card {
  param([int]$X, [int]$Y, [int]$Width, [int]$Height)
  $panel = New-Object System.Windows.Forms.Panel
  Set-Bounds $panel $X $Y $Width $Height
  $panel.BackColor = $script:Panel
  $panel.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
  return $panel
}

function Add-Eyebrow {
  param($Parent, [string]$Text, [int]$X, [int]$Y)
  $label = New-Label $Text $X $Y 180 18 7.5 ([System.Drawing.FontStyle]::Bold) $script:Muted
  $Parent.Controls.Add($label)
  return $label
}

function Set-AppStatus {
  param([string]$Text)
  $script:SetupStatus.Text = $Text
  $script:TrayIcon.Text = "墨声 - $Text"
}

function Format-Transcript {
  param([string]$Text)
  $trimmed = $Text.Trim()
  if (-not $script:PunctuationCheck.Checked) { return $trimmed }
  if ($trimmed.EndsWith("。") -or $trimmed.EndsWith(".") -or $trimmed.EndsWith("？") -or $trimmed.EndsWith("?") -or $trimmed.EndsWith("！") -or $trimmed.EndsWith("!")) {
    return $trimmed
  }
  return "$trimmed。"
}

function Save-ApiKey {
  $value = $script:ApiKeyBox.Text.Trim()
  if ([string]::IsNullOrWhiteSpace($value)) {
    $script:KeyState.Text = "请输入 API Key"
    Set-AppStatus "等待 API Key"
    return
  }

  $tailLength = [Math]::Min(4, $value.Length)
  $script:SavedKeyTail = $value.Substring($value.Length - $tailLength)
  $script:ApiKeyBox.Clear()
  $script:ApiKeyBox.PasswordChar = "*"
  $script:KeyVisible = $false
  $script:RevealButton.Text = "显示"
  $script:KeyState.Text = "已保存 · 末尾 $script:SavedKeyTail"
  Set-AppStatus "API Key 已保存"
}

function Test-ApiKey {
  if ([string]::IsNullOrWhiteSpace($script:SavedKeyTail) -and [string]::IsNullOrWhiteSpace($script:ApiKeyBox.Text.Trim())) {
    $script:KeyState.Text = "先保存密钥"
    Set-AppStatus "先保存密钥"
    return
  }
  $script:KeyState.Text = "连接正常（demo）"
  Set-AppStatus "连接正常（demo）"
}

function Clear-ApiKey {
  $script:SavedKeyTail = ""
  $script:ApiKeyBox.Clear()
  $script:KeyState.Text = "未保存"
  Set-AppStatus "API Key 已清除"
}

function Toggle-KeyVisibility {
  $script:KeyVisible = -not $script:KeyVisible
  if ($script:KeyVisible) {
    $script:ApiKeyBox.PasswordChar = [char]0
    $script:RevealButton.Text = "隐藏"
  } else {
    $script:ApiKeyBox.PasswordChar = "*"
    $script:RevealButton.Text = "显示"
  }
}

function Enter-InputMode {
  $script:Ready = $true
  $script:SettingsForm.Hide()
  Set-AppStatus "运行中 · 长按左 Shift"
  $script:TrayIcon.ShowBalloonTip(2500, "墨声正在运行", "长按左 Shift 唤醒语音输入。", [System.Windows.Forms.ToolTipIcon]::Info)
}

function Show-Settings {
  $script:SettingsForm.Show()
  $script:SettingsForm.WindowState = [System.Windows.Forms.FormWindowState]::Normal
  $script:SettingsForm.Activate()
}

function Set-FloatingStatus {
  param([string]$Text, [bool]$Recording)
  $script:FloatStatus.Text = $Text
  if ($Recording) {
    $script:FloatDot.BackColor = $script:Ink
    $script:RecordBand.BackColor = $script:Ink
    $script:RecordBand.ForeColor = [System.Drawing.Color]::White
  } else {
    $script:FloatDot.BackColor = [System.Drawing.Color]::FromArgb(150, 150, 150)
    $script:RecordBand.BackColor = $script:Ink
    $script:RecordBand.ForeColor = [System.Drawing.Color]::White
  }
}

function Position-FloatingWindow {
  $screen = [System.Windows.Forms.Screen]::FromHandle($script:ActiveWindowHandle)
  if ($null -eq $screen) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen
  }
  $area = $screen.WorkingArea
  $x = $area.Left + [int](($area.Width - $script:FloatForm.Width) / 2)
  $y = $area.Bottom - $script:FloatForm.Height - 54
  $script:FloatForm.Location = New-Object System.Drawing.Point -ArgumentList $x, $y
}

function Show-FloatingWindow {
  Position-FloatingWindow
  if (-not $script:FloatForm.Visible) {
    $script:FloatForm.Show()
  }
  $script:FloatForm.TopMost = $true
  $script:FloatForm.BringToFront()
}

function Start-VoiceCapture {
  if ($script:IsRecording) { return }
  $script:IsRecording = $true
  $script:RecordingTicks = 0
  $script:TranscriptText = ""
  $script:TranscriptBox.Text = ""
  $script:ConfirmButton.Enabled = $false
  $script:CopyButton.Enabled = $false
  $script:CancelButton.Enabled = $true
  Set-FloatingStatus "正在收音" $true
  $script:RecordBand.Text = "按住左 Shift 说话"
  Show-FloatingWindow
  $script:MeterTimer.Start()
}

function Stop-VoiceCapture {
  if (-not $script:IsRecording) { return }
  $script:IsRecording = $false
  $script:MeterTimer.Stop()
  $index = Get-Random -Minimum 0 -Maximum $script:Samples.Count
  $script:TranscriptText = Format-Transcript $script:Samples[$index]
  $script:TranscriptBox.Text = $script:TranscriptText
  $script:ConfirmButton.Enabled = $true
  $script:CopyButton.Enabled = $true
  $script:RecordBand.Text = "识别完成"
  Set-FloatingStatus "等待确认" $false

  switch ($script:InputMode.SelectedIndex) {
    1 { Insert-Transcript }
    2 { Copy-Transcript; Hide-FloatingSoon }
    default { }
  }
}

function Copy-Transcript {
  $text = $script:TranscriptBox.Text.Trim()
  if ([string]::IsNullOrWhiteSpace($text)) {
    Set-FloatingStatus "没有可复制的文字" $false
    return
  }
  [System.Windows.Forms.Clipboard]::SetText($text)
  Set-FloatingStatus "已复制" $false
}

function Insert-Transcript {
  $text = $script:TranscriptBox.Text.Trim()
  if ([string]::IsNullOrWhiteSpace($text)) {
    Set-FloatingStatus "没有可输入的文字" $false
    return
  }

  try {
    if ($script:ClipboardRestore -and [System.Windows.Forms.Clipboard]::ContainsText()) {
      $script:PreviousClipboard = [System.Windows.Forms.Clipboard]::GetText()
    } else {
      $script:PreviousClipboard = $null
    }

    [System.Windows.Forms.Clipboard]::SetText($text)
    if ($script:ActiveWindowHandle -ne [IntPtr]::Zero) {
      [void][MoshengNative]::SetForegroundWindow($script:ActiveWindowHandle)
      Start-Sleep -Milliseconds 120
    }
    [System.Windows.Forms.SendKeys]::SendWait("^v")
    Start-Sleep -Milliseconds 160

    if ($script:ClipboardRestore -and $null -ne $script:PreviousClipboard) {
      [System.Windows.Forms.Clipboard]::SetText($script:PreviousClipboard)
    }

    Set-FloatingStatus "已输入到当前光标" $false
    Hide-FloatingSoon
  } catch {
    Set-FloatingStatus "输入失败，已保留预览" $false
  }
}

function Cancel-Transcript {
  $script:TranscriptBox.Text = ""
  Set-FloatingStatus "已取消" $false
  Hide-FloatingSoon
}

function Hide-FloatingSoon {
  $script:HideTimer.Stop()
  $script:HideTimer.Start()
}

function Handle-LeftShift {
  param([bool]$IsDown)
  if (-not $script:Ready) { return }

  if ($IsDown) {
    if ($script:ShiftHeld) { return }
    $script:ShiftHeld = $true
    $script:ActiveWindowHandle = [MoshengNative]::GetForegroundWindow()
    $script:HoldTimer.Stop()
    $script:HoldTimer.Start()
    return
  }

  $script:ShiftHeld = $false
  $script:HoldTimer.Stop()
  if ($script:IsRecording) {
    Stop-VoiceCapture
  }
}

function Exit-Demo {
  $script:IsExiting = $true
  try { [MoshengNative]::StopHook() } catch {}
  if ($script:TrayIcon) {
    $script:TrayIcon.Visible = $false
    $script:TrayIcon.Dispose()
  }
  if ($script:FloatForm -and -not $script:FloatForm.IsDisposed) {
    $script:FloatForm.Close()
  }
  if ($script:SettingsForm -and -not $script:SettingsForm.IsDisposed) {
    $script:SettingsForm.Close()
  }
  [System.Windows.Forms.Application]::Exit()
}

$script:SettingsForm = New-Object System.Windows.Forms.Form
$script:SettingsForm.Text = "墨声"
$script:SettingsForm.StartPosition = "CenterScreen"
$script:SettingsForm.Size = New-Object System.Drawing.Size -ArgumentList 940, 560
$script:SettingsForm.MinimumSize = New-Object System.Drawing.Size -ArgumentList 860, 520
$script:SettingsForm.BackColor = $script:Paper
$script:SettingsForm.Font = New-Font 9

$rail = New-Object System.Windows.Forms.Panel
Set-Bounds $rail 0 0 210 560
$rail.Anchor = "Top,Bottom,Left"
$rail.BackColor = $script:Soft
$script:SettingsForm.Controls.Add($rail)

$rail.Controls.Add((New-Label "MOSHENG" 22 22 120 18 7.5 ([System.Drawing.FontStyle]::Bold) $script:Muted))
$brand = New-Label "墨声" 20 48 150 54 26 ([System.Drawing.FontStyle]::Bold)
$rail.Controls.Add($brand)

$phase1 = New-Object System.Windows.Forms.Button
$phase1.Text = "PHASE 1`r`n安装后设置"
$phase1.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
Set-Bounds $phase1 22 124 166 48
Set-FlatButton $phase1 ([System.Drawing.Color]::Black) ([System.Drawing.Color]::White)
$rail.Controls.Add($phase1)

$phase2 = New-Object System.Windows.Forms.Button
$phase2.Text = "PHASE 2`r`n实际输入中"
$phase2.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
Set-Bounds $phase2 22 182 166 48
Set-FlatButton $phase2 ([System.Drawing.Color]::White) $script:Ink
$rail.Controls.Add($phase2)

$shortcutCard = New-Card 22 252 166 62
$rail.Controls.Add($shortcutCard)
$shortcutCard.Controls.Add((New-Label "当前快捷键" 12 10 120 18 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$shortcutCard.Controls.Add((New-Label "长按左 Shift" 12 32 130 20 10 ([System.Drawing.FontStyle]::Bold) $script:Ink))

$engineCard = New-Card 22 430 166 64
$rail.Controls.Add($engineCard)
$engineCard.Controls.Add((New-Label "当前引擎" 12 10 120 18 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$engineCard.Controls.Add((New-Label "云端 AI 识别" 12 32 130 20 10 ([System.Drawing.FontStyle]::Bold) $script:Ink))

$setupPanel = New-Object System.Windows.Forms.Panel
Set-Bounds $setupPanel 210 0 714 520
$setupPanel.Anchor = "Top,Bottom,Left,Right"
$setupPanel.BackColor = [System.Drawing.Color]::White
$script:SettingsForm.Controls.Add($setupPanel)

$setupPanel.Controls.Add((New-Label "SETUP" 44 36 100 18 7.5 ([System.Drawing.FontStyle]::Bold) $script:Muted))
$setupPanel.Controls.Add((New-Label "墨声" 42 62 220 56 26 ([System.Drawing.FontStyle]::Bold) $script:Ink))

$speechCard = New-Card 44 134 334 248
$setupPanel.Controls.Add($speechCard)
Add-Eyebrow $speechCard "SPEECH TO TEXT" 16 14 | Out-Null
$speechCard.Controls.Add((New-Label "说话转文字" 210 14 106 28 16 ([System.Drawing.FontStyle]::Bold) $script:Ink))
$note = New-Label "接入云端 AI 识别 API，自动识别常用语言。" 16 50 294 34 9 ([System.Drawing.FontStyle]::Regular) $script:Ink
$speechCard.Controls.Add($note)

$apiSummary = New-Card 16 88 300 42
$apiSummary.BackColor = [System.Drawing.Color]::FromArgb(247, 247, 245)
$speechCard.Controls.Add($apiSummary)
$apiSummary.Controls.Add((New-Label "识别 API" 12 11 90 20 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$apiSummary.Controls.Add((New-Label "云端 AI 识别" 190 11 100 20 10 ([System.Drawing.FontStyle]::Bold) $script:Ink))

$speechCard.Controls.Add((New-Label "自动识别中文、英文、粤语混说" 16 140 260 18 8.5 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$speechCard.Controls.Add((New-Label "准确率和响应体验最好，需要网络和 API Key" 16 162 300 18 8.5 ([System.Drawing.FontStyle]::Regular) $script:Muted))

$speechCard.Controls.Add((New-Label "云端 API Key" 16 190 120 18 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$script:KeyState = New-Label "未保存" 244 190 72 18 8 ([System.Drawing.FontStyle]::Bold) $script:Muted
$script:KeyState.TextAlign = [System.Drawing.ContentAlignment]::TopRight
$speechCard.Controls.Add($script:KeyState)

$script:ApiKeyBox = New-Object System.Windows.Forms.TextBox
Set-Bounds $script:ApiKeyBox 16 212 198 28
$script:ApiKeyBox.PasswordChar = "*"
$speechCard.Controls.Add($script:ApiKeyBox)

$script:RevealButton = New-Object System.Windows.Forms.Button
$script:RevealButton.Text = "显示"
Set-Bounds $script:RevealButton 220 212 46 28
Set-FlatButton $script:RevealButton ([System.Drawing.Color]::White) $script:Ink
$speechCard.Controls.Add($script:RevealButton)

$saveKeyButton = New-Object System.Windows.Forms.Button
$saveKeyButton.Text = "保存密钥"
Set-Bounds $saveKeyButton 16 248 86 34
Set-FlatButton $saveKeyButton ([System.Drawing.Color]::Black) ([System.Drawing.Color]::White)
$speechCard.Controls.Add($saveKeyButton)

$testKeyButton = New-Object System.Windows.Forms.Button
$testKeyButton.Text = "测试连接"
Set-Bounds $testKeyButton 112 248 86 34
Set-FlatButton $testKeyButton ([System.Drawing.Color]::White) $script:Ink
$speechCard.Controls.Add($testKeyButton)

$clearKeyButton = New-Object System.Windows.Forms.Button
$clearKeyButton.Text = "清除"
Set-Bounds $clearKeyButton 208 248 64 34
Set-FlatButton $clearKeyButton ([System.Drawing.Color]::White) $script:Ink
$speechCard.Controls.Add($clearKeyButton)

$inputCard = New-Card 392 134 276 248
$setupPanel.Controls.Add($inputCard)
Add-Eyebrow $inputCard "INPUT SETUP" 16 14 | Out-Null
$inputCard.Controls.Add((New-Label "输入设置" 170 14 90 28 16 ([System.Drawing.FontStyle]::Bold) $script:Ink))

$inputCard.Controls.Add((New-Label "快捷键" 16 54 80 18 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$hotkeyStatic = New-Card 16 76 244 42
$inputCard.Controls.Add($hotkeyStatic)
$hotkeyStatic.Controls.Add((New-Label "当前" 12 11 70 20 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$hotkeyStatic.Controls.Add((New-Label "长按左 Shift" 132 11 100 20 10 ([System.Drawing.FontStyle]::Bold) $script:Ink))

$inputCard.Controls.Add((New-Label "输入模式" 16 132 100 18 8 ([System.Drawing.FontStyle]::Regular) $script:Muted))
$script:InputMode = New-Object System.Windows.Forms.ComboBox
$script:InputMode.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$script:InputMode.Items.AddRange(@("先预览，再确认输入", "松开后自动输入", "只复制到剪贴板"))
$script:InputMode.SelectedIndex = 0
Set-Bounds $script:InputMode 16 154 244 28
$inputCard.Controls.Add($script:InputMode)

$script:PunctuationCheck = New-Object System.Windows.Forms.CheckBox
$script:PunctuationCheck.Text = "自动补标点"
$script:PunctuationCheck.Checked = $true
Set-Bounds $script:PunctuationCheck 16 196 116 22
$inputCard.Controls.Add($script:PunctuationCheck)

$script:KeepPreviewCheck = New-Object System.Windows.Forms.CheckBox
$script:KeepPreviewCheck.Text = "失败保留预览"
$script:KeepPreviewCheck.Checked = $true
Set-Bounds $script:KeepPreviewCheck 144 196 116 22
$inputCard.Controls.Add($script:KeepPreviewCheck)

$script:ClipboardCheck = New-Object System.Windows.Forms.CheckBox
$script:ClipboardCheck.Text = "插入后恢复剪贴板"
$script:ClipboardCheck.Checked = $true
Set-Bounds $script:ClipboardCheck 16 222 150 22
$inputCard.Controls.Add($script:ClipboardCheck)

$script:SetupStatus = New-Label "本地安全：不保存录音 / 不保存转写历史 / API Key 存系统安全区" 44 410 420 22 8 ([System.Drawing.FontStyle]::Regular) $script:Muted
$setupPanel.Controls.Add($script:SetupStatus)

$saveAndRunButton = New-Object System.Windows.Forms.Button
$saveAndRunButton.Text = "保存并进入输入状态"
Set-Bounds $saveAndRunButton 500 402 168 38
Set-FlatButton $saveAndRunButton ([System.Drawing.Color]::Black) ([System.Drawing.Color]::White)
$setupPanel.Controls.Add($saveAndRunButton)

$script:FloatForm = New-Object System.Windows.Forms.Form
$script:FloatForm.Text = "墨声语音输入"
$script:FloatForm.Size = New-Object System.Drawing.Size -ArgumentList 560, 246
$script:FloatForm.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$script:FloatForm.ShowInTaskbar = $false
$script:FloatForm.TopMost = $true
$script:FloatForm.BackColor = [System.Drawing.Color]::White
$script:FloatForm.Font = New-Font 9
$script:FloatForm.Add_Paint({
  param($sender, $eventArgs)
  $pen = New-Object System.Drawing.Pen $script:Ink, 1
  $eventArgs.Graphics.DrawRectangle($pen, 0, 0, $sender.Width - 1, $sender.Height - 1)
  $pen.Dispose()
})

$script:FloatDot = New-Object System.Windows.Forms.Panel
Set-Bounds $script:FloatDot 18 22 10 10
$script:FloatDot.BackColor = [System.Drawing.Color]::FromArgb(150, 150, 150)
$script:FloatForm.Controls.Add($script:FloatDot)
$script:FloatForm.Controls.Add((New-Label "VOICE INPUT" 40 12 110 18 7.5 ([System.Drawing.FontStyle]::Bold) $script:Muted))
$script:FloatStatus = New-Label "待机" 40 32 260 24 13 ([System.Drawing.FontStyle]::Bold) $script:Ink
$script:FloatForm.Controls.Add($script:FloatStatus)

$script:RecordBand = New-Object System.Windows.Forms.Label
$script:RecordBand.Text = "长按左 Shift 说话"
$script:RecordBand.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$script:RecordBand.Font = New-Font 12 ([System.Drawing.FontStyle]::Bold)
$script:RecordBand.BackColor = $script:Ink
$script:RecordBand.ForeColor = [System.Drawing.Color]::White
Set-Bounds $script:RecordBand 18 64 524 46
$script:FloatForm.Controls.Add($script:RecordBand)

$script:TranscriptBox = New-Object System.Windows.Forms.TextBox
$script:TranscriptBox.Multiline = $true
$script:TranscriptBox.ScrollBars = "Vertical"
$script:TranscriptBox.Font = New-Font 10
Set-Bounds $script:TranscriptBox 18 124 524 58
$script:FloatForm.Controls.Add($script:TranscriptBox)

$script:ConfirmButton = New-Object System.Windows.Forms.Button
$script:ConfirmButton.Text = "确认输入"
$script:ConfirmButton.Enabled = $false
Set-Bounds $script:ConfirmButton 18 196 160 34
Set-FlatButton $script:ConfirmButton ([System.Drawing.Color]::Black) ([System.Drawing.Color]::White)
$script:FloatForm.Controls.Add($script:ConfirmButton)

$script:CopyButton = New-Object System.Windows.Forms.Button
$script:CopyButton.Text = "复制"
$script:CopyButton.Enabled = $false
Set-Bounds $script:CopyButton 190 196 160 34
Set-FlatButton $script:CopyButton ([System.Drawing.Color]::White) $script:Ink
$script:FloatForm.Controls.Add($script:CopyButton)

$script:CancelButton = New-Object System.Windows.Forms.Button
$script:CancelButton.Text = "取消"
$script:CancelButton.Enabled = $false
Set-Bounds $script:CancelButton 362 196 180 34
Set-FlatButton $script:CancelButton ([System.Drawing.Color]::White) $script:Ink
$script:FloatForm.Controls.Add($script:CancelButton)

$script:TrayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$openItem = New-Object System.Windows.Forms.ToolStripMenuItem "打开设置"
$exitItem = New-Object System.Windows.Forms.ToolStripMenuItem "退出墨声"
$script:TrayMenu.Items.Add($openItem) | Out-Null
$script:TrayMenu.Items.Add($exitItem) | Out-Null

$script:TrayIcon = New-Object System.Windows.Forms.NotifyIcon
$script:TrayIcon.Icon = [System.Drawing.SystemIcons]::Application
$script:TrayIcon.Text = "墨声 - 设置中"
$script:TrayIcon.Visible = $true
$script:TrayIcon.ContextMenuStrip = $script:TrayMenu

$script:HoldTimer = New-Object System.Windows.Forms.Timer
$script:HoldTimer.Interval = 300
$script:HoldTimer.Add_Tick({
  $script:HoldTimer.Stop()
  if ($script:ShiftHeld -and -not $script:IsRecording) {
    Start-VoiceCapture
  }
})

$script:MeterTimer = New-Object System.Windows.Forms.Timer
$script:MeterTimer.Interval = 260
$script:MeterTimer.Add_Tick({
  $script:RecordingTicks += 1
  $dots = "." * (($script:RecordingTicks % 3) + 1)
  $script:RecordBand.Text = "正在收音$dots"
})

$script:HideTimer = New-Object System.Windows.Forms.Timer
$script:HideTimer.Interval = 950
$script:HideTimer.Add_Tick({
  $script:HideTimer.Stop()
  $script:FloatForm.Hide()
  Set-FloatingStatus "待机" $false
  $script:RecordBand.Text = "长按左 Shift 说话"
})

[MoshengNative]::add_KeyboardAction({
  param([int]$vkCode, [bool]$isDown)
  Handle-LeftShift $isDown
})

$saveKeyButton.Add_Click({ Save-ApiKey })
$testKeyButton.Add_Click({ Test-ApiKey })
$clearKeyButton.Add_Click({ Clear-ApiKey })
$script:RevealButton.Add_Click({ Toggle-KeyVisibility })
$saveAndRunButton.Add_Click({ Enter-InputMode })
$script:ClipboardCheck.Add_CheckedChanged({ $script:ClipboardRestore = $script:ClipboardCheck.Checked })
$script:ConfirmButton.Add_Click({ Insert-Transcript })
$script:CopyButton.Add_Click({ Copy-Transcript })
$script:CancelButton.Add_Click({ Cancel-Transcript })
$openItem.Add_Click({ Show-Settings })
$exitItem.Add_Click({ Exit-Demo })
$script:TrayIcon.Add_DoubleClick({ Show-Settings })

$script:SettingsForm.Add_FormClosing({
  param($sender, $eventArgs)
  if ($script:IsExiting) { return }
  if ($script:Ready) {
    $eventArgs.Cancel = $true
    $script:SettingsForm.Hide()
    return
  }
  Exit-Demo
})

$script:FloatForm.Add_FormClosing({
  param($sender, $eventArgs)
  if (-not $script:IsExiting) {
    $eventArgs.Cancel = $true
    $script:FloatForm.Hide()
  }
})

$phase2.Add_Click({ Enter-InputMode })
$phase1.Add_Click({ Show-Settings })

Set-AppStatus "设置中"
[MoshengNative]::StartHook()
[void][System.Windows.Forms.Application]::Run($script:SettingsForm)

