using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Mosheng
{
    internal enum InputMode
    {
        Structured,
        Email
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            DpiBootstrap.Enable();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                Log.Write("Unhandled exception: " + args.ExceptionObject);
            };
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs args)
            {
                Log.Write("UI exception: " + args.Exception);
                MessageBox.Show("墨声遇到一个本地错误，请重启后再试。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            ServicePointManagerShim.EnableTls12();
            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "Mosheng.VoiceIme.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("墨声已经在运行。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using (AppController controller = new AppController())
                {
                    controller.Start();
                    Application.Run();
                }
            }
        }
    }

    internal static class ServicePointManagerShim
    {
        public static void EnableTls12()
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
        }
    }

    internal static class DpiBootstrap
    {
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
        private static readonly IntPtr PerMonitorAware = new IntPtr(-3);

        public static void Enable()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(PerMonitorAwareV2)) return;
                if (SetProcessDpiAwarenessContext(PerMonitorAware)) return;
                SetProcessDPIAware();
            }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }

    internal sealed class AppController : IDisposable
    {
        private const int LeftShift = 0xA0;
        private readonly SettingsStore settingsStore = new SettingsStore();
        private readonly ZhipuSpeechClient speechClient = new ZhipuSpeechClient();
        private readonly ZhipuTextFormatClient textFormatClient = new ZhipuTextFormatClient();
        private readonly Timer holdTimer = new Timer();
        private readonly Timer maxCaptureTimer = new Timer();
        private readonly Timer hookWatchdogTimer = new Timer();
        private readonly Timer shortcutStateWatchdogTimer = new Timer();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly HashSet<int> pressedKeys = new HashSet<int>();
        private Settings settings;
        private SettingsForm settingsForm;
        private OverlayForm overlay;
        private ResultFallbackForm resultFallbackForm;
        private StructuredChoiceForm structuredChoiceForm;
        private MoshengWaveRecorder recorder;
        private bool shortcutWasMatched;
        private bool isRecording;
        private bool isProcessing;
        private bool isExiting;
        private IntPtr activeWindow;
        private InputMode activeInputMode = InputMode.Structured;
        private InputMode capturingShortcutMode = InputMode.Structured;
        private string lastRecognizedText = "";
        private StructuredDraftSession pendingStructuredDraft;

        public void Start()
        {
            settings = settingsStore.Load();
            settingsForm = new SettingsForm(settings);
            overlay = new OverlayForm();
            resultFallbackForm = new ResultFallbackForm();
            structuredChoiceForm = new StructuredChoiceForm();

            settingsForm.SaveRequested += OnSaveRequested;
            settingsForm.ShortcutCaptureRequested += OnShortcutCaptureRequested;
            structuredChoiceForm.CommitRequested += OnStructuredCommitRequested;
            structuredChoiceForm.EnhanceRequested += OnStructuredEnhanceRequested;
            structuredChoiceForm.BankingRewriteRequested += OnStructuredBankingRewriteRequested;
            structuredChoiceForm.CancelRequested += OnStructuredCancelRequested;
            structuredChoiceForm.FormClosed += delegate { pendingStructuredDraft = null; };

            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "墨声";
            trayIcon.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("打开设置");
            ToolStripMenuItem latestResultItem = new ToolStripMenuItem("最近结果");
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出墨声");
            settingsItem.Click += delegate { ShowSettings(); };
            latestResultItem.Click += delegate { ShowLatestResult(); };
            exitItem.Click += delegate { Exit(); };
            menu.Items.Add(settingsItem);
            menu.Items.Add(latestResultItem);
            menu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { ShowSettings(); };

            holdTimer.Interval = 260;
            holdTimer.Tick += delegate
            {
                holdTimer.Stop();
                if (shortcutWasMatched && !isRecording && !isProcessing)
                {
                    StartCapture();
                }
            };

            maxCaptureTimer.Interval = 90000;
            maxCaptureTimer.Tick += delegate
            {
                maxCaptureTimer.Stop();
                if (isRecording)
                {
                    StopCapture();
                }
            };

            hookWatchdogTimer.Interval = 45000;
            hookWatchdogTimer.Tick += delegate
            {
                if (!isExiting && !isRecording && !isProcessing && (settingsForm == null || !settingsForm.IsCapturingShortcut))
                {
                    RestartKeyboardHook("watchdog");
                }
            };

            shortcutStateWatchdogTimer.Interval = 100;
            shortcutStateWatchdogTimer.Tick += delegate
            {
                if (shortcutWasMatched && !AreAnyShortcutKeysPhysicallyDown())
                {
                    Log.Write("Shortcut state reset: tracked keys no longer physically held.");
                    pressedKeys.Clear();
                    shortcutWasMatched = false;
                }
            };

            MoshengNative.KeyboardAction += OnKeyboardAction;
            MoshengNative.StartHook();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            hookWatchdogTimer.Start();
            shortcutStateWatchdogTimer.Start();
            ShowSettings();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Resume)
            {
                RestartKeyboardHook("power-resume");
            }
            else if (args.Mode == PowerModes.Suspend)
            {
                ResetInputState("power-suspend");
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionUnlock || args.Reason == SessionSwitchReason.ConsoleConnect || args.Reason == SessionSwitchReason.RemoteConnect)
            {
                RestartKeyboardHook("session-" + args.Reason);
            }
        }

        private void RestartKeyboardHook(string reason)
        {
            try
            {
                ResetInputState(reason);
                MoshengNative.RestartHook();
                Log.Write("Keyboard hook restarted: " + reason);
            }
            catch (Exception ex)
            {
                Log.Write("Keyboard hook restart failed: " + reason + " " + ex);
            }
        }

        private void ResetInputState(string reason)
        {
            shortcutWasMatched = false;
            pressedKeys.Clear();
            holdTimer.Stop();
            if (isRecording)
            {
                try { maxCaptureTimer.Stop(); } catch { }
                try { if (recorder != null) recorder.Dispose(); } catch { }
                recorder = null;
                isRecording = false;
                try { if (overlay != null) overlay.HideNow(); } catch { }
                Log.Write("Recording reset: " + reason);
            }
        }

        private void OnSaveRequested(object sender, SaveSettingsEventArgs args)
        {
            if (!String.IsNullOrWhiteSpace(args.ApiKey))
            {
                settings.ProtectedApiKey = SecretBox.Protect(args.ApiKey.Trim());
                settings.KeyTail = Tail(args.ApiKey.Trim());
            }
            settings.ShortcutVk = args.ShortcutVk;
            settings.ShortcutCtrl = args.ShortcutCtrl;
            settings.ShortcutAlt = args.ShortcutAlt;
            settings.ShortcutShift = args.ShortcutShift;
            settings.ShortcutLabel = ShortcutKeys.Label(settings);
            settings.EmailShortcutVk = args.EmailShortcutVk;
            settings.EmailShortcutCtrl = args.EmailShortcutCtrl;
            settings.EmailShortcutAlt = args.EmailShortcutAlt;
            settings.EmailShortcutShift = args.EmailShortcutShift;
            settings.EmailShortcutLabel = ShortcutKeys.Label(settings.EmailShortcutVk, settings.EmailShortcutCtrl, settings.EmailShortcutAlt, settings.EmailShortcutShift);
            settingsStore.Save(settings);
            settingsForm.RefreshFrom(settings);
            trayIcon.ShowBalloonTip(1300, "墨声正在运行", settings.ShortcutLabel + " 开始录音，再按一次结束。", ToolTipIcon.Info);
        }

        private void OnShortcutCaptureRequested(object sender, ShortcutCaptureEventArgs args)
        {
            capturingShortcutMode = args.Mode;
            settingsForm.BeginShortcutCapture(args.Mode);
        }

        private void OnKeyboardAction(int vkCode, bool isDown)
        {
            if (isExiting || settingsForm == null || settingsForm.IsDisposed) return;
            try
            {
                settingsForm.BeginInvoke(new Action(delegate
                {
                    HandleKeyboardEvent(vkCode, isDown);
                }));
            }
            catch
            {
            }
        }

        private void HandleKeyboardEvent(int vkCode, bool isDown)
        {
            if (isDown) ShortcutKeys.TrackDown(pressedKeys, vkCode);
            else ShortcutKeys.TrackUp(pressedKeys, vkCode);

            if (settingsForm.IsCapturingShortcut)
            {
                if (isDown && vkCode == 0x1B)
                {
                    settingsForm.EndShortcutCapture(settings);
                }
                else if (isDown && !ShortcutKeys.IsModifier(vkCode))
                {
                    ApplyCapturedShortcut(vkCode);
                }
                else if (isDown)
                {
                    settingsForm.ShowShortcutCaptureHint();
                }
                else if (!isDown && ShortcutKeys.IsModifier(vkCode))
                {
                    ApplyShortcutToSettings(capturingShortcutMode, vkCode);
                    settingsStore.Save(settings);
                    settingsForm.EndShortcutCapture(settings);
                }
                return;
            }

            bool structuredShortcutMatches = ShortcutKeys.Matches(settings, pressedKeys);
            bool shortcutMatches = structuredShortcutMatches;

            bool justMatched = shortcutMatches && !shortcutWasMatched;
            shortcutWasMatched = shortcutMatches;

            if (pendingStructuredDraft != null)
            {
                if (justMatched && isDown && structuredShortcutMatches)
                {
                    OnStructuredEnhanceRequested(this, EventArgs.Empty);
                }
                return;
            }

            if (justMatched && isDown)
            {
                if (isRecording)
                {
                    StopCapture();
                }
                else if (!isProcessing)
                {
                    activeInputMode = InputMode.Structured;
                    activeWindow = MoshengNative.GetForegroundWindow();
                    StartCapture();
                }
            }
        }

        private bool AreAnyShortcutKeysPhysicallyDown()
        {
            return IsShortcutPhysicallyDown(settings.ShortcutVk, settings.ShortcutCtrl, settings.ShortcutAlt, settings.ShortcutShift);
        }

        private bool IsShortcutPhysicallyDown(int shortcutVk, bool ctrl, bool alt, bool shift)
        {
            if (ctrl && !IsKeyPhysicallyDown(0x11) && !IsKeyPhysicallyDown(0xA2) && !IsKeyPhysicallyDown(0xA3)) return false;
            if (alt && !IsKeyPhysicallyDown(0x12) && !IsKeyPhysicallyDown(0xA4) && !IsKeyPhysicallyDown(0xA5)) return false;
            if (shift && !IsKeyPhysicallyDown(0x10) && !IsKeyPhysicallyDown(0xA0) && !IsKeyPhysicallyDown(0xA1)) return false;
            if (!ShortcutKeys.IsModifier(shortcutVk) && !IsKeyPhysicallyDown(shortcutVk)) return false;
            return true;
        }

        private bool IsKeyPhysicallyDown(int vkCode)
        {
            return (GetAsyncKeyState(vkCode) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private void ApplyCapturedShortcut(int vkCode)
        {
            ApplyShortcutToSettings(capturingShortcutMode, vkCode);
            settingsStore.Save(settings);
            settingsForm.EndShortcutCapture(settings);
        }

        private void ApplyShortcutToSettings(InputMode mode, int vkCode)
        {
            bool ctrl = ShortcutKeys.CtrlDown(pressedKeys) && !ShortcutKeys.IsCtrl(vkCode);
            bool alt = ShortcutKeys.AltDown(pressedKeys) && !ShortcutKeys.IsAlt(vkCode);
            bool shift = ShortcutKeys.ShiftDown(pressedKeys) && !ShortcutKeys.IsShift(vkCode);
            if (mode == InputMode.Email)
            {
                settings.EmailShortcutVk = vkCode;
                settings.EmailShortcutCtrl = ctrl;
                settings.EmailShortcutAlt = alt;
                settings.EmailShortcutShift = shift;
                settings.EmailShortcutLabel = ShortcutKeys.Label(settings.EmailShortcutVk, ctrl, alt, shift);
            }
            else
            {
                settings.ShortcutVk = vkCode;
                settings.ShortcutCtrl = ctrl;
                settings.ShortcutAlt = alt;
                settings.ShortcutShift = shift;
                settings.ShortcutLabel = ShortcutKeys.Label(settings);
            }
        }

        private bool IsReleaseForActiveMode(int vkCode)
        {
            if (activeInputMode == InputMode.Email)
            {
                return ShortcutKeys.IsEquivalent(settings.EmailShortcutVk, vkCode);
            }
            return ShortcutKeys.IsEquivalent(settings.ShortcutVk, vkCode);
        }

        private void StartCapture()
        {
            string apiKey = SecretBox.Unprotect(settings.ProtectedApiKey);
            if (String.IsNullOrWhiteSpace(apiKey))
            {
                ShowSettings();
                MessageBox.Show("先输入智谱 API Key。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Directory.CreateDirectory(AppPaths.TempDir);
                recorder = new MoshengWaveRecorder();
                recorder.Start();
                isRecording = true;
                overlay.ShowMode(OverlayMode.Recording);
                maxCaptureTimer.Stop();
                maxCaptureTimer.Start();
            }
            catch (Exception ex)
            {
                Log.Write("Start capture failed: " + ex);
                isRecording = false;
                overlay.ShowMode(OverlayMode.Error);
                overlay.HideLater();
                MessageBox.Show("无法开始录音。请确认麦克风可用。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StopCapture()
        {
            if (!isRecording || recorder == null) return;
            maxCaptureTimer.Stop();
            isRecording = false;
            isProcessing = true;
            overlay.ShowMode(OverlayMode.Processing);
            Application.DoEvents();

            byte[] pcm = new byte[0];
            try
            {
                pcm = recorder.StopToBytes();
                recorder.Dispose();
                recorder = null;
            }
            catch (Exception ex)
            {
                Log.Write("Stop capture failed: " + ex);
                isProcessing = false;
                overlay.ShowMode(OverlayMode.Error);
                overlay.HideLater();
                MessageBox.Show("录音结束失败。请重试。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string apiKey = SecretBox.Unprotect(settings.ProtectedApiKey);
            IntPtr targetWindow = activeWindow;
            InputMode mode = activeInputMode;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                ProcessCapture(apiKey, pcm, targetWindow, mode);
            });
        }

        private void ProcessCapture(string apiKey, byte[] pcm, IntPtr targetWindow, InputMode mode)
        {
            try
            {
                AudioStats stats = AudioInspector.Inspect(pcm, 16000, 1, 16);
                Log.Write("Capture audio: bytes=" + pcm.Length + ", seconds=" + stats.Seconds.ToString("0.00") + ", peak=" + stats.Peak + ", rms=" + stats.Rms.ToString("0.0"));
                if (pcm.Length < 16000)
                {
                    throw new InvalidOperationException("NO_AUDIO_BYTES");
                }
                if (!stats.HasSignal)
                {
                    throw new InvalidOperationException("NO_VOICE_SIGNAL");
                }

                string text = TranscribeLongAudio(apiKey, pcm);
                if (!String.IsNullOrWhiteSpace(text))
                {
                    RunOnUi(delegate
                    {
                        string sourceText = text.Trim();
                        if (mode == InputMode.Structured)
                        {
                            string preview = LocalTextFormatter.FinalClean(LocalTextFormatter.LightClean(sourceText));
                            ShowStructuredChoice(apiKey, sourceText, preview, stats.Seconds, targetWindow);
                        }
                        else
                        {
                            string output = SmartTextFormatter.Format(apiKey, sourceText, stats.Seconds, textFormatClient, mode);
                            DeliverRecognizedText(targetWindow, output.Trim(), false);
                        }
                    });
                }
                else
                {
                    RunOnUi(delegate
                    {
                        overlay.ShowMode(OverlayMode.Error);
                        MessageBox.Show("没有识别到文字。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Write("Transcription failed: " + ex);
                RunOnUi(delegate
                {
                    overlay.ShowMode(OverlayMode.Error);
                    MessageBox.Show(FriendlyError.From(ex), "墨声", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
            finally
            {
                RunOnUi(delegate
                {
                    isProcessing = false;
                    if (overlay.Visible) overlay.HideLater();
                });
            }
        }

        private void RunOnUi(MethodInvoker action)
        {
            try
            {
                Control marshal = overlay != null && overlay.IsHandleCreated ? (Control)overlay : settingsForm;
                if (marshal != null && marshal.IsHandleCreated && marshal.InvokeRequired)
                {
                    marshal.Invoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException) { }
        }

        private void ShowFallbackResult(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return;
            if (resultFallbackForm == null || resultFallbackForm.IsDisposed)
            {
                resultFallbackForm = new ResultFallbackForm();
            }
            resultFallbackForm.ShowResult(text, "未能自动粘贴，结果已为你保留。");
        }

        private void ShowLatestResult()
        {
            if (String.IsNullOrWhiteSpace(lastRecognizedText))
            {
                MessageBox.Show("还没有最近一次识别结果。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ShowFallbackResult(lastRecognizedText);
        }

        private void ShowStructuredChoice(string apiKey, string sourceText, string previewText, double audioSeconds, IntPtr targetWindow)
        {
            string preview = String.IsNullOrWhiteSpace(previewText) ? sourceText : previewText;
            pendingStructuredDraft = new StructuredDraftSession(apiKey, sourceText, preview, audioSeconds, targetWindow);
            lastRecognizedText = preview;
            if (structuredChoiceForm == null || structuredChoiceForm.IsDisposed)
            {
                structuredChoiceForm = new StructuredChoiceForm();
                structuredChoiceForm.CommitRequested += OnStructuredCommitRequested;
                structuredChoiceForm.EnhanceRequested += OnStructuredEnhanceRequested;
                structuredChoiceForm.BankingRewriteRequested += OnStructuredBankingRewriteRequested;
                structuredChoiceForm.CancelRequested += OnStructuredCancelRequested;
                structuredChoiceForm.FormClosed += delegate { pendingStructuredDraft = null; };
            }
            structuredChoiceForm.ShowDraft(preview, settings == null ? "" : settings.ShortcutLabel);
        }

        private void OnStructuredCommitRequested(object sender, EventArgs args)
        {
            StructuredDraftSession draft = pendingStructuredDraft;
            if (draft == null || draft.IsBusy) return;
            pendingStructuredDraft = null;
            if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.Hide();
            DeliverRecognizedText(draft.TargetWindow, draft.PreviewText, false);
        }

        private void OnStructuredCancelRequested(object sender, EventArgs args)
        {
            StructuredDraftSession draft = pendingStructuredDraft;
            if (draft == null || draft.IsBusy) return;
            pendingStructuredDraft = null;
            if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.Hide();
            lastRecognizedText = draft.PreviewText;
            ShowFallbackResult(draft.PreviewText);
        }

        private void OnStructuredEnhanceRequested(object sender, EventArgs args)
        {
            StructuredDraftSession draft = pendingStructuredDraft;
            if (draft == null || draft.IsBusy) return;

            draft.IsBusy = true;
            isProcessing = true;
            if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.ShowBusy("Applying light polish...", "Removing fillers and organizing bullets.");

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string output = SmartTextFormatter.Format(draft.ApiKey, draft.SourceText, draft.AudioSeconds, textFormatClient, InputMode.Structured);
                    RunOnUi(delegate
                    {
                        if (pendingStructuredDraft != draft) return;
                        pendingStructuredDraft = null;
                        if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.Hide();
                        DeliverRecognizedText(draft.TargetWindow, output.Trim(), false);
                    });
                }
                catch
                {
                    RunOnUi(delegate
                    {
                        if (pendingStructuredDraft != draft) return;
                        pendingStructuredDraft = null;
                        if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.Hide();
                        DeliverRecognizedText(draft.TargetWindow, draft.PreviewText, false);
                    });
                }
                finally
                {
                    RunOnUi(delegate { isProcessing = false; });
                }
            });
        }

        private void OnStructuredBankingRewriteRequested(object sender, EventArgs args)
        {
            StructuredDraftSession draft = pendingStructuredDraft;
            if (draft == null || draft.IsBusy) return;

            draft.IsBusy = true;
            isProcessing = true;
            if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.ShowBusy("Generating IB email version...", "Using the stronger investment-banking rewrite path.");

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string output = SmartTextFormatter.Format(draft.ApiKey, draft.SourceText, draft.AudioSeconds, textFormatClient, InputMode.Email);
                    RunOnUi(delegate
                    {
                        if (pendingStructuredDraft != draft) return;
                        pendingStructuredDraft = null;
                        if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.Hide();
                        DeliverRecognizedText(draft.TargetWindow, output.Trim(), false);
                    });
                }
                catch
                {
                    RunOnUi(delegate
                    {
                        if (pendingStructuredDraft != draft) return;
                        pendingStructuredDraft = null;
                        if (structuredChoiceForm != null && !structuredChoiceForm.IsDisposed) structuredChoiceForm.Hide();
                        DeliverRecognizedText(draft.TargetWindow, draft.PreviewText, false);
                    });
                }
                finally
                {
                    RunOnUi(delegate { isProcessing = false; });
                }
            });
        }

        private void DeliverRecognizedText(IntPtr targetWindow, string text, bool forceFallbackWindow)
        {
            string finalOutput = String.IsNullOrWhiteSpace(text) ? "" : text.Trim();
            if (finalOutput.Length == 0) return;

            lastRecognizedText = finalOutput;
            InsertResult insertResult = InputInjector.InsertText(targetWindow, finalOutput, false);
            if (insertResult == InsertResult.ClipboardOnly || forceFallbackWindow)
            {
                overlay.ShowMode(OverlayMode.Error);
                ShowFallbackResult(finalOutput);
            }
        }

        private string TranscribeLongAudio(string apiKey, byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return "";
            List<byte[]> chunks = AudioChunker.SplitPcm(pcm, 16000, 1, 16, 28);
            StringBuilder result = new StringBuilder();
            string prompt = "";
            for (int i = 0; i < chunks.Count; i++)
            {
                string wavPath = Path.Combine(AppPaths.TempDir, "capture-" + Guid.NewGuid().ToString("N") + "-" + i + ".wav");
                try
                {
                    WavWriter.Write(wavPath, chunks[i], 16000, 1, 16);
                    string part = speechClient.Transcribe(apiKey, wavPath, prompt);
                    AppendText(result, part);
                    prompt = TrimPrompt(result.ToString());
                }
                finally
                {
                    try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
                }
            }
            return result.ToString();
        }

        private static void AppendText(StringBuilder builder, string part)
        {
            if (String.IsNullOrWhiteSpace(part)) return;
            string text = part.Trim();
            if (builder.Length > 0 && NeedsSpace(builder[builder.Length - 1], text[0]))
            {
                builder.Append(' ');
            }
            builder.Append(text);
        }

        private static bool NeedsSpace(char left, char right)
        {
            return (Char.IsLetterOrDigit(left) && Char.IsLetterOrDigit(right) && left < 128 && right < 128);
        }

        private static string TrimPrompt(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            if (text.Length <= 1200) return text;
            return text.Substring(text.Length - 1200);
        }

        private void ShowSettings()
        {
            settingsForm.RefreshFrom(settings);
            settingsForm.Show();
            settingsForm.Activate();
            settingsForm.Focus();
        }

        private static string Tail(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return value.Substring(Math.Max(0, value.Length - Math.Min(4, value.Length)));
        }

        private void Exit()
        {
            if (isExiting) return;
            isExiting = true;
            try { holdTimer.Stop(); } catch { }
            try { maxCaptureTimer.Stop(); } catch { }
            try { hookWatchdogTimer.Stop(); } catch { }
            try { shortcutStateWatchdogTimer.Stop(); } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            try { MoshengNative.StopHook(); } catch { }
            try { if (recorder != null) recorder.Dispose(); } catch { }
            recorder = null;
            try
            {
                if (overlay != null)
                {
                    overlay.HideNow();
                    overlay.Close();
                    overlay.Dispose();
                }
            }
            catch { }
            overlay = null;
            try
            {
                if (resultFallbackForm != null)
                {
                    resultFallbackForm.Close();
                    resultFallbackForm.Dispose();
                }
            }
            catch { }
            resultFallbackForm = null;
            try
            {
                if (settingsForm != null)
                {
                    settingsForm.Hide();
                    settingsForm.Dispose();
                }
            }
            catch { }
            settingsForm = null;
            try { trayIcon.Visible = false; trayIcon.Dispose(); } catch { }
            Application.ExitThread();
        }

        public void Dispose()
        {
            Exit();
        }
    }

    internal sealed class SettingsForm : Form
    {
        public event EventHandler<SaveSettingsEventArgs> SaveRequested;
        public event EventHandler<ShortcutCaptureEventArgs> ShortcutCaptureRequested;
        private readonly Label closeButton = new Label();
        private readonly TextBox apiKeyBox = new TextBox();
        private readonly Panel apiKeyFrame = new Panel();
        private readonly Button saveButton = new Button();
        private readonly Button shortcutButton = new Button();
        private readonly Button emailShortcutButton = new Button();
        private readonly Label shortcutValue = new Label();
        private readonly Label emailShortcutValue = new Label();
        private bool dragging;
        private Point dragStart;
        private int shortcutVk;
        private bool shortcutCtrl;
        private bool shortcutAlt;
        private bool shortcutShift;
        private int emailShortcutVk;
        private bool emailShortcutCtrl;
        private bool emailShortcutAlt;
        private bool emailShortcutShift;
        private InputMode capturingMode = InputMode.Structured;
        private bool hasSavedApiKey;
        private bool editingApiKey;

        public bool IsCapturingShortcut { get; private set; }

        public SettingsForm(Settings settings)
        {
            Text = "墨声";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            ShowIcon = false;
            Size = new Size(500, 410);
            MinimumSize = new Size(500, 410);
            MaximumSize = new Size(500, 410);
            BackColor = Color.White;
            Font = Fonts.Ui(9f, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            shortcutVk = settings.ShortcutVk;
            shortcutCtrl = settings.ShortcutCtrl;
            shortcutAlt = settings.ShortcutAlt;
            shortcutShift = settings.ShortcutShift;
            emailShortcutVk = settings.EmailShortcutVk;
            emailShortcutCtrl = settings.EmailShortcutCtrl;
            emailShortcutAlt = settings.EmailShortcutAlt;
            emailShortcutShift = settings.EmailShortcutShift;

            closeButton.Text = "×";
            closeButton.TextAlign = ContentAlignment.MiddleCenter;
            closeButton.Font = Fonts.Ui(13f, FontStyle.Regular);
            closeButton.ForeColor = Color.FromArgb(120, 120, 120);
            closeButton.SetBounds(456, 18, 28, 28);
            closeButton.Cursor = Cursors.Hand;
            closeButton.Click += delegate { Hide(); };
            Controls.Add(closeButton);

            apiKeyBox.PasswordChar = '*';
            apiKeyBox.Font = Fonts.Ui(10.5f, FontStyle.Regular);
            apiKeyBox.BorderStyle = BorderStyle.None;
            apiKeyBox.BackColor = Color.White;
            apiKeyBox.SetBounds(14, 10, 400, 24);
            apiKeyFrame.BackColor = Color.White;
            apiKeyFrame.SetBounds(36, 232, 424, 44);
            apiKeyFrame.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(185, 185, 185), 1f))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, apiKeyFrame.Width - 1, apiKeyFrame.Height - 1);
                }
                if (hasSavedApiKey && !editingApiKey && apiKeyBox.Text.Trim().Length == 0)
                {
                    TextRenderer.DrawText(
                        e.Graphics,
                        "已保存密钥  •••• •••• ••••",
                        Fonts.Ui(8.5f, FontStyle.Bold),
                        new Rectangle(14, 0, apiKeyFrame.Width - 28, apiKeyFrame.Height),
                        Palette.Ink,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                }
            };
            apiKeyFrame.Controls.Add(apiKeyBox);
            apiKeyFrame.Click += delegate
            {
                if (hasSavedApiKey && !editingApiKey && apiKeyBox.Text.Trim().Length == 0)
                {
                    BeginApiKeyEdit();
                }
            };
            Controls.Add(apiKeyFrame);

            saveButton.Text = "保存";
            saveButton.SetBounds(36, 294, 96, 38);
            StyleButton(saveButton, Color.Black, Color.White, false);
            saveButton.Click += delegate
            {
                if (SaveRequested != null)
                {
                    SaveRequested(this, new SaveSettingsEventArgs(apiKeyBox.Text, shortcutVk, shortcutCtrl, shortcutAlt, shortcutShift, emailShortcutVk, emailShortcutCtrl, emailShortcutAlt, emailShortcutShift));
                    apiKeyBox.Clear();
                    editingApiKey = false;
                    RenderApiKeyState();
                }
            };
            Controls.Add(saveButton);
            apiKeyBox.TextChanged += delegate
            {
                RenderApiKeyState();
            };
            apiKeyBox.Leave += delegate
            {
                if (apiKeyBox.Text.Trim().Length == 0) editingApiKey = false;
                RenderApiKeyState();
            };

            shortcutValue.SetBounds(116, 372, 250, 30);
            shortcutValue.Font = Fonts.Ui(9.5f, FontStyle.Bold);
            shortcutValue.ForeColor = Palette.Ink;
            Controls.Add(shortcutValue);

            shortcutButton.Text = "更改";
            shortcutButton.TextAlign = ContentAlignment.MiddleCenter;
            shortcutButton.SetBounds(404, 368, 56, 32);
            StyleButton(shortcutButton, Color.White, Palette.Ink, true);
            shortcutButton.Click += delegate
            {
                if (ShortcutCaptureRequested != null) ShortcutCaptureRequested(this, new ShortcutCaptureEventArgs(InputMode.Structured));
            };
            Controls.Add(shortcutButton);

            emailShortcutValue.SetBounds(116, 412, 250, 30);
            emailShortcutValue.Font = Fonts.Ui(9.5f, FontStyle.Bold);
            emailShortcutValue.ForeColor = Palette.Ink;
            Controls.Add(emailShortcutValue);

            emailShortcutButton.Text = "更改";
            emailShortcutButton.TextAlign = ContentAlignment.MiddleCenter;
            emailShortcutButton.SetBounds(404, 408, 56, 32);
            StyleButton(emailShortcutButton, Color.White, Palette.Ink, true);
            emailShortcutButton.Click += delegate
            {
                if (ShortcutCaptureRequested != null) ShortcutCaptureRequested(this, new ShortcutCaptureEventArgs(InputMode.Email));
            };
            Controls.Add(emailShortcutButton);
            emailShortcutButton.Visible = false;
            emailShortcutValue.Visible = false;

            MouseDown += OnDragMouseDown;
            MouseMove += OnDragMouseMove;
            MouseUp += OnDragMouseUp;
            foreach (Control control in Controls)
            {
                if (control is TextBox || control is Button) continue;
                control.MouseDown += OnDragMouseDown;
                control.MouseMove += OnDragMouseMove;
                control.MouseUp += OnDragMouseUp;
            }

            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                e.Cancel = true;
                Hide();
            };
            RefreshFrom(settings);
        }

        public void RefreshFrom(Settings settings)
        {
            shortcutVk = settings.ShortcutVk;
            shortcutCtrl = settings.ShortcutCtrl;
            shortcutAlt = settings.ShortcutAlt;
            shortcutShift = settings.ShortcutShift;
            emailShortcutVk = settings.EmailShortcutVk;
            emailShortcutCtrl = settings.EmailShortcutCtrl;
            emailShortcutAlt = settings.EmailShortcutAlt;
            emailShortcutShift = settings.EmailShortcutShift;
            shortcutButton.Text = "更改";
            emailShortcutButton.Text = "更改";
            shortcutValue.Text = settings.ShortcutLabel;
            emailShortcutValue.Text = settings.EmailShortcutLabel;
            hasSavedApiKey = !String.IsNullOrEmpty(settings.KeyTail);
            editingApiKey = apiKeyBox.Text.Trim().Length > 0;
            RenderApiKeyState();
        }

        private void RenderApiKeyState()
        {
            bool hasDraft = apiKeyBox.Text.Trim().Length > 0;
            saveButton.Text = hasDraft || !hasSavedApiKey ? "保存" : "已保存";
            apiKeyBox.Visible = editingApiKey || hasDraft || !hasSavedApiKey;
            apiKeyFrame.Invalidate();
        }

        private void BeginApiKeyEdit()
        {
            editingApiKey = true;
            RenderApiKeyState();
            apiKeyBox.Focus();
        }

        public void BeginShortcutCapture(InputMode mode)
        {
            capturingMode = mode;
            IsCapturingShortcut = true;
            Button button = mode == InputMode.Email ? emailShortcutButton : shortcutButton;
            Label value = mode == InputMode.Email ? emailShortcutValue : shortcutValue;
            button.Text = "按键";
            value.Text = "等待输入";
            button.BackColor = Color.Black;
            button.ForeColor = Color.White;
        }

        public void ShowShortcutCaptureHint()
        {
            if (!IsCapturingShortcut) return;
            Label value = capturingMode == InputMode.Email ? emailShortcutValue : shortcutValue;
            value.Text = "继续按主键，或松开确认";
        }

        public void EndShortcutCapture(Settings settings)
        {
            IsCapturingShortcut = false;
            RefreshFrom(settings);
            shortcutButton.BackColor = Color.FromArgb(250, 250, 250);
            shortcutButton.ForeColor = Palette.Ink;
            emailShortcutButton.BackColor = Color.FromArgb(250, 250, 250);
            emailShortcutButton.ForeColor = Palette.Ink;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(Color.FromArgb(216, 216, 216), 1f))
            {
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
            DrawStaticText(e.Graphics);
        }

        private void DrawStaticText(Graphics g)
        {
            DrawText(g, "MOSHENG", 36, 30, 180, 28, 8f, FontStyle.Bold, Palette.Muted);
            DrawText(g, "墨声", 36, 58, 180, 58, 19f, FontStyle.Bold, Palette.Ink);
            DrawText(g, "语音输入设置", 36, 120, 300, 46, 14f, FontStyle.Bold, Palette.Ink);
            DrawText(g, "输入智谱 API Key 后，按快捷键开始，再按一次结束。", 36, 160, 390, 24, 9f, FontStyle.Regular, Palette.Muted);
            DrawText(g, "用于语音识别与智能排版。", 36, 184, 390, 24, 8.5f, FontStyle.Regular, Palette.Muted);
            DrawText(g, "智谱 API Key", 36, 206, 160, 26, 8f, FontStyle.Bold, Palette.Muted);
            DrawText(g, "快捷键", 36, 344, 120, 24, 8f, FontStyle.Bold, Palette.Muted);
            DrawText(g, "整理输入", 36, 374, 90, 24, 8.5f, FontStyle.Bold, Palette.Ink);
            DrawText(g, "邮件输入", 36, 414, 90, 24, 8.5f, FontStyle.Bold, Palette.Ink);
        }

        private static void DrawText(Graphics g, string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            TextRenderer.DrawText(
                g,
                text,
                Fonts.Ui(size, style),
                new Rectangle(x, y, width, height),
                color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private void OnDragMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            Control source = sender as Control;
            dragStart = source == null ? PointToScreen(e.Location) : source.PointToScreen(e.Location);
        }

        private void OnDragMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Control source = sender as Control;
            Point current = source == null ? PointToScreen(e.Location) : source.PointToScreen(e.Location);
            Location = new Point(Location.X + current.X - dragStart.X, Location.Y + current.Y - dragStart.Y);
            dragStart = current;
        }

        private void OnDragMouseUp(object sender, MouseEventArgs e)
        {
            dragging = false;
        }

        private static Label NewLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = Fonts.Ui(size, style);
            label.ForeColor = color;
            label.AutoSize = false;
            label.UseCompatibleTextRendering = false;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.SetBounds(x, y, width, height);
            return label;
        }

        private static void StyleButton(Button button, Color back, Color fore, bool bordered)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Palette.Ink;
            button.FlatAppearance.BorderSize = bordered ? 1 : 0;
            button.BackColor = back;
            button.ForeColor = fore;
            button.Font = Fonts.Ui(9f, bordered ? FontStyle.Regular : FontStyle.Bold);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Cursor = Cursors.Hand;
        }
    }

    internal sealed class SaveSettingsEventArgs : EventArgs
    {
        public readonly string ApiKey;
        public readonly int ShortcutVk;
        public readonly bool ShortcutCtrl;
        public readonly bool ShortcutAlt;
        public readonly bool ShortcutShift;
        public readonly int EmailShortcutVk;
        public readonly bool EmailShortcutCtrl;
        public readonly bool EmailShortcutAlt;
        public readonly bool EmailShortcutShift;

        public SaveSettingsEventArgs(string apiKey, int shortcutVk, bool shortcutCtrl, bool shortcutAlt, bool shortcutShift, int emailShortcutVk, bool emailShortcutCtrl, bool emailShortcutAlt, bool emailShortcutShift)
        {
            ApiKey = apiKey;
            ShortcutVk = shortcutVk;
            ShortcutCtrl = shortcutCtrl;
            ShortcutAlt = shortcutAlt;
            ShortcutShift = shortcutShift;
            EmailShortcutVk = emailShortcutVk;
            EmailShortcutCtrl = emailShortcutCtrl;
            EmailShortcutAlt = emailShortcutAlt;
            EmailShortcutShift = emailShortcutShift;
        }
    }

    internal sealed class ShortcutCaptureEventArgs : EventArgs
    {
        public readonly InputMode Mode;

        public ShortcutCaptureEventArgs(InputMode mode)
        {
            Mode = mode;
        }
    }

    internal enum OverlayMode
    {
        Recording,
        Processing,
        Success,
        Error
    }

    internal sealed class OverlayForm : Form
    {
        private readonly Timer waveTimer = new Timer();
        private readonly Timer fadeTimer = new Timer();
        private readonly Timer hideTimer = new Timer();
        private OverlayMode mode = OverlayMode.Recording;
        private double phase;
        private bool fadingOut;

        public OverlayForm()
        {
            Text = "墨声输入";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Size = new Size(92, 34);
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            waveTimer.Interval = 55;
            waveTimer.Tick += delegate
            {
                phase += mode == OverlayMode.Processing ? 0.28 : 0.22;
                Invalidate();
            };

            fadeTimer.Interval = 16;
            fadeTimer.Tick += delegate
            {
                if (fadingOut)
                {
                    Opacity -= 0.075;
                    if (Opacity <= 0.02)
                    {
                        fadeTimer.Stop();
                        Hide();
                    }
                }
                else
                {
                    double next = Opacity + ((0.96 - Opacity) * 0.22);
                    Opacity = Math.Min(0.96, next);
                    if (Opacity >= 0.94) fadeTimer.Stop();
                }
            };

            hideTimer.Interval = 900;
            hideTimer.Tick += delegate
            {
                hideTimer.Stop();
                FadeOut();
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;
                return cp;
            }
        }

        public void ShowMode(OverlayMode nextMode)
        {
            mode = nextMode;
            bool isTerminal = nextMode == OverlayMode.Success || nextMode == OverlayMode.Error;
            Size = isTerminal ? new Size(44, 34) : (nextMode == OverlayMode.Processing ? new Size(118, 34) : new Size(92, 34));
            UpdateRegion();
            PositionNearBottom();
            Invalidate();
            if (!Visible)
            {
                Opacity = 0;
                Show();
            }
            fadingOut = false;
            fadeTimer.Start();
            if (nextMode == OverlayMode.Success || nextMode == OverlayMode.Error)
            {
                waveTimer.Stop();
            }
            else
            {
                waveTimer.Start();
            }
            BringToFront();
        }

        public void HideLater()
        {
            hideTimer.Stop();
            hideTimer.Start();
        }

        public void HideNow()
        {
            hideTimer.Stop();
            fadeTimer.Stop();
            waveTimer.Stop();
            fadingOut = false;
            Opacity = 0;
            Hide();
        }

        private void FadeOut()
        {
            fadingOut = true;
            fadeTimer.Start();
        }

        private void PositionNearBottom()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int x = area.Left + ((area.Width - Width) / 2);
            int y = area.Bottom - Height - 56;
            Location = new Point(x, y);
        }

        private void UpdateRegion()
        {
            Region old = Region;
            using (GraphicsPath path = RoundedPath(new RectangleF(0, 0, Width, Height), Height / 2f))
            {
                Region = new Region(path);
            }
            if (old != null) old.Dispose();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using (GraphicsPath body = RoundedPath(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            using (SolidBrush brush = new SolidBrush(Color.Black))
            {
                g.FillPath(brush, body);
            }

            if (mode == OverlayMode.Success)
            {
                DrawCheck(g);
            }
            else if (mode == OverlayMode.Error)
            {
                DrawError(g);
            }
            else if (mode == OverlayMode.Processing)
            {
                DrawProcessing(g);
            }
            else
            {
                DrawWave(g);
            }
        }

        private void DrawCheck(Graphics g)
        {
            using (Pen checkPen = new Pen(Color.White, 2.35f))
            {
                checkPen.StartCap = LineCap.Round;
                checkPen.EndCap = LineCap.Round;
                g.DrawLines(checkPen, new PointF[] {
                    new PointF(Width / 2f - 7f, Height / 2f),
                    new PointF(Width / 2f - 2f, Height / 2f + 5f),
                    new PointF(Width / 2f + 8f, Height / 2f - 6f)
                });
            }
        }

        private void DrawError(Graphics g)
        {
            using (Pen xPen = new Pen(Color.White, 2.2f))
            {
                xPen.StartCap = LineCap.Round;
                xPen.EndCap = LineCap.Round;
                g.DrawLine(xPen, Width / 2f - 6f, Height / 2f - 6f, Width / 2f + 6f, Height / 2f + 6f);
                g.DrawLine(xPen, Width / 2f + 6f, Height / 2f - 6f, Width / 2f - 6f, Height / 2f + 6f);
            }
        }

        private void DrawProcessing(Graphics g)
        {
            TextRenderer.DrawText(
                g,
                "思考中",
                Fonts.Ui(8.2f, FontStyle.Bold),
                new Rectangle(14, 0, 58, Height),
                Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int barCount = 4;
            float gap = 4.8f;
            float barWidth = 2.05f;
            float total = ((barCount - 1) * gap) + barWidth;
            float startX = Width - 18f - total;
            float centerY = Height / 2f;
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(238, 255, 255, 255)))
            {
                for (int i = 0; i < barCount; i++)
                {
                    double value = (Math.Sin(phase + (i * 0.82)) + 1.0) / 2.0;
                    float h = (float)(5.5 + value * 12.5);
                    RectangleF rect = new RectangleF(startX + (i * gap), centerY - (h / 2f), barWidth, h);
                    using (GraphicsPath path = RoundedPath(rect, 1.1f))
                    {
                        g.FillPath(brush, path);
                    }
                }
            }
        }

        private void DrawWave(Graphics g)
        {
            int barCount = 6;
            float gap = 5.6f;
            float barWidth = 2.15f;
            float total = ((barCount - 1) * gap) + barWidth;
            float startX = (Width - total) / 2f;
            float centerY = Height / 2f;
            Color barColor = mode == OverlayMode.Processing ? Color.FromArgb(218, 218, 218) : Color.White;
            using (SolidBrush brush = new SolidBrush(barColor))
            {
                for (int i = 0; i < barCount; i++)
                {
                    float h;
                    if (mode == OverlayMode.Success || mode == OverlayMode.Error)
                    {
                        int[] heights = new int[] { 10, 15, 20, 20, 15, 12, 9 };
                        h = heights[i];
                    }
                    else
                    {
                        double value = Math.Abs(Math.Sin(phase + (i * 0.72)));
                        h = (float)(8.5 + value * 9.5);
                    }
                    RectangleF rect = new RectangleF(startX + (i * gap), centerY - (h / 2f), barWidth, h);
                    using (GraphicsPath path = RoundedPath(rect, 1.1f))
                    {
                        g.FillPath(brush, path);
                    }
                }
            }
        }

        private static GraphicsPath RoundedPath(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class ResultFallbackForm : Form
    {
        private readonly Label titleLabel = new Label();
        private readonly TextBox contentBox = new TextBox();
        private readonly Button copyButton = new Button();
        private readonly Button closeButton = new Button();

        public ResultFallbackForm()
        {
            Text = "墨声 - 最近结果";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            BackColor = Color.White;
            ClientSize = new Size(560, 320);
            Font = Fonts.Ui(9f, FontStyle.Regular);

            titleLabel.SetBounds(18, 14, 520, 22);
            titleLabel.Font = Fonts.Ui(9.5f, FontStyle.Bold);
            titleLabel.ForeColor = Palette.Ink;
            titleLabel.Text = "未能自动粘贴，结果已为你保留。";
            Controls.Add(titleLabel);

            contentBox.SetBounds(18, 46, 524, 220);
            contentBox.Multiline = true;
            contentBox.ReadOnly = true;
            contentBox.ScrollBars = ScrollBars.Vertical;
            contentBox.Font = Fonts.Ui(9.5f, FontStyle.Regular);
            contentBox.BackColor = Color.White;
            Controls.Add(contentBox);

            copyButton.Text = "复制";
            copyButton.SetBounds(382, 278, 76, 30);
            copyButton.Click += delegate
            {
                try { Clipboard.SetText(contentBox.Text ?? "", TextDataFormat.UnicodeText); } catch { }
            };
            Controls.Add(copyButton);

            closeButton.Text = "关闭";
            closeButton.SetBounds(466, 278, 76, 30);
            closeButton.Click += delegate { Hide(); };
            Controls.Add(closeButton);
        }

        public void ShowResult(string text, string title)
        {
            titleLabel.Text = String.IsNullOrWhiteSpace(title) ? "最近一次识别结果" : title;
            contentBox.Text = text ?? "";
            if (!Visible) Show();
            BringToFront();
            Activate();
        }
    }

    internal sealed class StructuredDraftSession
    {
        public readonly string ApiKey;
        public readonly string SourceText;
        public readonly string PreviewText;
        public readonly double AudioSeconds;
        public readonly IntPtr TargetWindow;
        public bool IsBusy;

        public StructuredDraftSession(string apiKey, string sourceText, string previewText, double audioSeconds, IntPtr targetWindow)
        {
            ApiKey = apiKey;
            SourceText = sourceText;
            PreviewText = previewText;
            AudioSeconds = audioSeconds;
            TargetWindow = targetWindow;
        }
    }

    internal sealed class StructuredChoiceForm : Form
    {
        private readonly Label titleLabel = new Label();
        private readonly Label hintLabel = new Label();
        private readonly TextBox contentBox = new TextBox();
        private readonly Button cancelButton = new Button();
        private readonly Button directButton = new Button();
        private readonly Button enhanceButton = new Button();
        private readonly Button bankingButton = new Button();

        public event EventHandler CommitRequested;
        public event EventHandler CancelRequested;
        public event EventHandler EnhanceRequested;
        public event EventHandler BankingRewriteRequested;

        public StructuredChoiceForm()
        {
            Text = "Mosheng Preview";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            KeyPreview = true;
            BackColor = Color.White;
            ClientSize = new Size(860, 300);
            Font = Fonts.Ui(9f, FontStyle.Regular);

            titleLabel.SetBounds(18, 14, 824, 24);
            titleLabel.Font = Fonts.Ui(9.5f, FontStyle.Bold);
            titleLabel.ForeColor = Palette.Ink;
            Controls.Add(titleLabel);

            hintLabel.SetBounds(18, 42, 824, 54);
            hintLabel.Font = Fonts.Ui(8.5f, FontStyle.Regular);
            hintLabel.ForeColor = Color.FromArgb(110, 110, 110);
            Controls.Add(hintLabel);

            contentBox.SetBounds(18, 102, 824, 140);
            contentBox.Multiline = true;
            contentBox.ReadOnly = true;
            contentBox.ScrollBars = ScrollBars.Vertical;
            contentBox.Font = Fonts.Ui(9.5f, FontStyle.Regular);
            contentBox.BackColor = Color.White;
            Controls.Add(contentBox);

            cancelButton.Text = "Close";
            cancelButton.SetBounds(398, 254, 76, 30);
            cancelButton.Click += delegate
            {
                if (CancelRequested != null) CancelRequested(this, EventArgs.Empty);
            };
            Controls.Add(cancelButton);

            enhanceButton.Text = "Bullet polish";
            enhanceButton.SetBounds(484, 254, 110, 30);
            enhanceButton.Click += delegate
            {
                if (EnhanceRequested != null) EnhanceRequested(this, EventArgs.Empty);
            };
            Controls.Add(enhanceButton);

            bankingButton.Text = "IB email rewrite";
            bankingButton.SetBounds(604, 254, 130, 30);
            bankingButton.Click += delegate
            {
                if (BankingRewriteRequested != null) BankingRewriteRequested(this, EventArgs.Empty);
            };
            Controls.Add(bankingButton);

            directButton.Text = "Paste as-is";
            directButton.SetBounds(744, 254, 98, 30);
            directButton.Click += delegate
            {
                if (CommitRequested != null) CommitRequested(this, EventArgs.Empty);
            };
            Controls.Add(directButton);
        }

        public void ShowDraft(string text, string shortcutLabel)
        {
            contentBox.Text = text ?? "";
            titleLabel.Text = String.IsNullOrWhiteSpace(shortcutLabel)
                ? "Voice input complete. Choose how to paste this text."
                : "Voice input complete. Shortcut: " + shortcutLabel;
            hintLabel.Text = String.IsNullOrWhiteSpace(shortcutLabel)
                ? "Paste as-is keeps the original wording. Bullet polish removes fillers and organizes the text into cleaner bullets. IB email rewrite expands the content into a stronger investment-banking style message."
                : "Paste as-is keeps the original wording. Bullet polish removes fillers and organizes the text into cleaner bullets. IB email rewrite expands the content into a stronger investment-banking style message. Press " + shortcutLabel + " again to trigger Bullet polish quickly.";
            cancelButton.Enabled = true;
            directButton.Enabled = true;
            enhanceButton.Enabled = true;
            bankingButton.Enabled = true;
            if (!Visible) Show();
            BringToFront();
            Activate();
            Focus();
        }

        public void ShowBusy(string title, string hint)
        {
            titleLabel.Text = title;
            hintLabel.Text = hint;
            cancelButton.Enabled = false;
            directButton.Enabled = false;
            enhanceButton.Enabled = false;
            bankingButton.Enabled = false;
        }
    }

    internal sealed class ZhipuSpeechClient
    {
        private const string Url = "https://open.bigmodel.cn/api/paas/v4/audio/transcriptions";
        private const string Model = "glm-asr-2512";

        public string Transcribe(string apiKey, string wavPath, string prompt)
        {
            using (HttpClient client = new HttpClient())
            using (MultipartFormDataContent form = new MultipartFormDataContent())
            using (FileStream stream = File.OpenRead(wavPath))
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                StreamContent file = new StreamContent(stream);
                file.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
                form.Add(file, "file", Path.GetFileName(wavPath));
                form.Add(new StringContent(Model), "model");
                form.Add(new StringContent("false"), "stream");
                if (!String.IsNullOrWhiteSpace(prompt))
                {
                    form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
                }

                HttpResponseMessage response = client.PostAsync(Url, form).Result;
                string body = response.Content.ReadAsStringAsync().Result;
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("HTTP " + (int)response.StatusCode + ": " + Redactor.Scrub(body, apiKey));
                }
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> json = serializer.Deserialize<Dictionary<string, object>>(body);
                if (json != null && json.ContainsKey("text") && json["text"] != null)
                {
                    return Convert.ToString(json["text"]);
                }
                return "";
            }
        }
    }

    internal static class SmartTextFormatter
    {
        public static string Format(string apiKey, string text, double audioSeconds, ZhipuTextFormatClient client, InputMode mode)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";

            SmartFormatDecision decision = SmartFormatDetector.Evaluate(text, audioSeconds);
            Log.Write("Smart format decision: mode=" + mode + ", score=" + decision.Score + ", action=" + decision.Action + ", reason=" + decision.Reason);

            string llmInput = LocalTextFormatter.PrepareForLlm(text);
            string cleaned = LocalTextFormatter.LightClean(text);
            string localStructured = LocalTextFormatter.TryStructure(cleaned);
            bool structuredFastPath = mode == InputMode.Structured
                && String.IsNullOrWhiteSpace(localStructured)
                && decision.Action != SmartFormatAction.Llm
                && cleaned.Length < 40;
            string fallback;
            if (mode == InputMode.Structured)
            {
                if (!String.IsNullOrWhiteSpace(localStructured))
                {
                    fallback = LocalTextFormatter.FinalClean(localStructured);
                }
                else if (decision.Action == SmartFormatAction.Llm || cleaned.Length >= 50)
                {
                    fallback = LocalTextFormatter.FinalClean(cleaned);
                }
                else
                {
                    fallback = LocalTextFormatter.LightFallbackClean(text);
                }
            }
            else
            {
                fallback = LocalTextFormatter.FinalClean(localStructured ?? cleaned);
            }
            if (structuredFastPath)
            {
                Log.Write("Smart format fast path: mode=Structured, cleanedLength=" + cleaned.Length);
            }
            bool shouldUseLlm = mode == InputMode.Email || (mode == InputMode.Structured && !structuredFastPath) || decision.Action == SmartFormatAction.Llm;
            if (!shouldUseLlm)
            {
                return fallback;
            }

            try
            {
                string formatted = client.Format(apiKey, llmInput, mode);
                string repaired = SmartFormatValidator.Repair(formatted);
                string normalized = LocalTextFormatter.PolishLlmOutput(repaired);
                Log.Write(
                    "Smart format pipeline: mode=" + mode +
                    ", rawFormattedLength=" + SafeLength(formatted) +
                    ", repairedLength=" + SafeLength(repaired) +
                    ", normalizedLength=" + SafeLength(normalized));
                string rejectReason;
                if (SmartFormatValidator.Accept(llmInput, normalized, mode, out rejectReason))
                {
                    Log.Write("Smart format applied: rawLength=" + llmInput.Length + ", formattedLength=" + normalized.Length);
                    return normalized.Trim();
                }

                Log.Write("Smart format rejected by validator: " + rejectReason);
                return fallback;
            }
            catch (Exception ex)
            {
                Exception inner = ex is AggregateException && ((AggregateException)ex).InnerException != null ? ((AggregateException)ex).InnerException : ex;
                Log.Write("Smart format failed, fallback local/raw text: " + inner.Message);
                return fallback;
            }
        }

        private static int SafeLength(string text)
        {
            return String.IsNullOrEmpty(text) ? 0 : text.Length;
        }
    }

    internal enum SmartFormatAction
    {
        None,
        LightClean,
        Llm
    }

    internal sealed class SmartFormatDecision
    {
        public readonly int Score;
        public readonly SmartFormatAction Action;
        public readonly string Reason;

        public SmartFormatDecision(int score, SmartFormatAction action, string reason)
        {
            Score = score;
            Action = action;
            Reason = reason;
        }
    }

    internal static class SmartFormatDetector
    {
        private static readonly string[] StrongSignals = new string[]
        {
            "第一点", "第二点", "第三点", "第一个", "第二个", "第三个",
            "首先", "其次", "最后", "第一", "第二", "第三",
            "一是", "二是", "三是", "其一", "其二", "其三",
            "分点", "分几点", "列出来", "排版", "换行", "分段",
            "另外", "接着", "然后第二", "第二呢"
        };

        private static readonly string[] CountSignals = new string[]
        {
            "有两个", "有三个", "有四个", "两个点", "三个点", "四个点",
            "几个点", "几个方面", "几点", "几个事情", "几个内容", "这么几个点"
        };

        public static SmartFormatDecision Evaluate(string text, double audioSeconds)
        {
            if (String.IsNullOrWhiteSpace(text)) return new SmartFormatDecision(0, SmartFormatAction.None, "empty");

            int score = 0;
            List<string> reasons = new List<string>();
            string compact = Regex.Replace(text, "\\s+", "");

            if (audioSeconds >= 8)
            {
                score += 1;
                reasons.Add("audio>=8s");
            }
            if (compact.Length >= 50)
            {
                score += 1;
                reasons.Add("text>=50");
            }
            if (ContainsAny(compact, StrongSignals))
            {
                score += 3;
                reasons.Add("structure-word");
            }
            if (ContainsAny(compact, CountSignals))
            {
                score += 2;
                reasons.Add("count-word");
            }
            if (Regex.IsMatch(compact, "(首先|第一点|第一个|一是).{2,80}(其次|第二点|第二个|二是).{2,80}(最后|第三点|第三个|三是)"))
            {
                score += 2;
                reasons.Add("ordered-chain");
            }
            if (Regex.IsMatch(text, "[，,、][^，,、。.!！？?]{1,8}[，,、][^，,、。.!！？?]{1,8}"))
            {
                score += 1;
                reasons.Add("short-enum");
            }

            if (score >= 2) return new SmartFormatDecision(score, SmartFormatAction.Llm, String.Join(",", reasons.ToArray()));
            return new SmartFormatDecision(score, SmartFormatAction.None, String.Join(",", reasons.ToArray()));
        }

        private static bool ContainsAny(string text, string[] terms)
        {
            for (int i = 0; i < terms.Length; i++)
            {
                if (text.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }

    internal static class FinancialGlossary
    {
        private static readonly object Sync = new object();
        private static bool loaded;
        private static string promptText = "";
        private static Dictionary<string, string> localCorrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<string, string> LocalCorrections
        {
            get
            {
                EnsureLoaded();
                return localCorrections;
            }
        }

        public static string BuildPromptText()
        {
            EnsureLoaded();
            return promptText;
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (Sync)
            {
                if (loaded) return;
                try
                {
                    LoadFromFile();
                }
                catch (Exception ex)
                {
                    Log.Write("Financial glossary load failed: " + ex.Message);
                    promptText = "";
                    localCorrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                loaded = true;
            }
        }

        private static void LoadFromFile()
        {
            Dictionary<string, string> corrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            StringBuilder prompt = new StringBuilder();
            if (!File.Exists(AppPaths.GlossaryPath))
            {
                Log.Write("Financial glossary not found: " + AppPaths.GlossaryPath);
                localCorrections = corrections;
                promptText = "";
                return;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object parsed = serializer.DeserializeObject(File.ReadAllText(AppPaths.GlossaryPath, Encoding.UTF8));
            object[] categories = parsed as object[];
            if (categories == null)
            {
                localCorrections = corrections;
                promptText = "";
                return;
            }

            prompt.AppendLine("扩展金融词库：");
            for (int i = 0; i < categories.Length; i++)
            {
                Dictionary<string, object> category = categories[i] as Dictionary<string, object>;
                if (category == null || !category.ContainsKey("terms")) continue;
                string categoryName = category.ContainsKey("category") ? Convert.ToString(category["category"]) : "finance";
                object[] terms = category["terms"] as object[];
                if (terms == null) continue;

                List<string> canonicalTerms = new List<string>();
                for (int j = 0; j < terms.Length; j++)
                {
                    Dictionary<string, object> term = terms[j] as Dictionary<string, object>;
                    if (term == null || !term.ContainsKey("canonical")) continue;
                    string canonical = Convert.ToString(term["canonical"]);
                    if (String.IsNullOrWhiteSpace(canonical)) continue;
                    canonicalTerms.Add(canonical);

                    bool localReplace = term.ContainsKey("local_replace") && Convert.ToBoolean(term["local_replace"]);
                    if (!localReplace) continue;

                    AddArrayCorrections(corrections, term, "wrong_asr", canonical);
                }

                if (canonicalTerms.Count > 0)
                {
                    prompt.Append(categoryName);
                    prompt.Append("：");
                    prompt.AppendLine(String.Join("、", canonicalTerms.ToArray()));
                }
            }

            localCorrections = corrections;
            promptText = prompt.ToString().Trim();
        }

        private static void AddArrayCorrections(Dictionary<string, string> corrections, Dictionary<string, object> term, string key, string canonical)
        {
            if (!term.ContainsKey(key) || term[key] == null) return;
            object[] values = term[key] as object[];
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                AddCorrection(corrections, Convert.ToString(values[i]), canonical);
            }
        }

        private static void AddCorrection(Dictionary<string, string> corrections, string value, string canonical)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            string key = value.Trim();
            if (key.Length == 0 || key == canonical) return;
            if (!corrections.ContainsKey(key)) corrections.Add(key, canonical);
        }
    }

    internal static class LocalTextFormatter
    {
        private static readonly Dictionary<string, string> DomainCorrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "麦青锡", "麦肯锡" },
            { "麦肯西", "麦肯锡" },
            { "麦肯希", "麦肯锡" },
            { "麦肯其", "麦肯锡" },
            { "卖肯锡", "麦肯锡" },
            { "麦肯西咨询", "麦肯锡" },
            { "贝恩咨询", "贝恩" },
            { "贝因", "贝恩" },
            { "贝音", "贝恩" },
            { "贝恩公司", "贝恩" },
            { "必CG", "BCG" },
            { "毕西基", "BCG" },
            { "比西基", "BCG" },
            { "B C G", "BCG" },
            { "高盛之列", "高盛之类" },
            { "高胜", "高盛" },
            { "摩根斯坦利", "摩根士丹利" },
            { "摩根史丹利", "摩根士丹利" },
            { "摩根斯丹利", "摩根士丹利" },
            { "大小摩", "大摩小摩" },
            { "小摩根", "小摩" },
            { "JP摩根", "J.P. Morgan" },
            { "摩根大通", "J.P. Morgan" },
            { "瑞银集团", "瑞银" },
            { "中经公司", "中金公司" },
            { "中信建头", "中信建投" },
            { "普华永道", "PwC" },
            { "安永华明", "安永" },
            { "并够", "并购" },
            { "并勾", "并购" },
            { "病购", "并购" },
            { "境调", "尽调" },
            { "竞调", "尽调" },
            { "敬调", "尽调" },
            { "金调", "尽调" },
            { "路言", "路演" },
            { "路艳", "路演" },
            { "估直", "估值" },
            { "古直", "估值" },
            { "古值", "估值" },
            { "估纸", "估值" },
            { "财务模形", "财务模型" },
            { "财务磨型", "财务模型" },
            { "现金流折现", "DCF" },
            { "企业价值", "EV" },
            { "股权价值", "Equity Value" },
            { "息税折旧摊销前利润", "EBITDA" },
            { "息税前利润", "EBIT" },
            { "市盈率", "P/E" },
            { "市销率", "P/S" },
            { "市净率", "P/B" },
            { "内部收益率", "IRR" },
            { "投资回报率", "ROI" },
            { "净现值", "NPV" },
            { "加权平均资本成本", "WACC" },
            { "终端价值", "终值" },
            { "杠杆收购", "LBO" },
            { "杆杆收购", "LBO" },
            { "首次公开发行", "IPO" },
            { "信息备忘录", "IM" },
            { "资本开支", "CapEx" },
            { "运营支出", "OpEx" },
            { "营运资本", "Working Capital" },
            { "净营运资本", "NWC" },
            { "议题数", "议题树" },
            { "问题树", "议题树" },
            { "市场测算", "市场规模测算" },
            { "市场规模推算", "市场规模测算" },
            { "价值连", "价值链" },
            { "对飙", "对标" },
            { "基准分析", "benchmark" },
            { "莫伊克", "MOIC" },
            { "毛伊克", "MOIC" },
            { "D P I", "DPI" },
            { "T V P I", "TVPI" },
            { "G P", "GP" },
            { "L P", "LP" },
            { "资本召缴", "capital call" },
            { "凯瑞分成", "carry 分成" },
            { "干火药", "dry powder" },
            { "质朴", "智谱" },
            { "typus", "typos" }
        };

        public static string LightClean(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string result = text.Trim();
            result = Regex.Replace(result, "\\s+", " ");
            result = Regex.Replace(result, "(?:\\bN\\s*I\\s*){2,}", "", RegexOptions.IgnoreCase);
            result = RemoveSpeechFillers(result);
            result = ApplyDomainCorrections(result);
            result = result.Replace("也也", "也");
            return result.Trim();
        }

        public static string PrepareForLlm(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string result = text.Trim();
            result = Regex.Replace(result, "(?:\\bN\\s*I\\s*){2,}", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "[ \\t]+", " ");
            result = ApplyDomainCorrections(result);
            return result.Trim();
        }

        public static string PolishLlmOutput(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string result = NormalizeImText(text);
            string[] lines = result.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = Regex.Replace(lines[i], "[ \\t]+", " ").Trim();
                line = ApplyDomainCorrections(line);
                lines[i] = line;
            }
            result = String.Join("\r\n", lines);
            result = DropLikelyHallucinatedEnglish(result);
            result = Regex.Replace(result, "(\\r\\n){3,}", "\r\n\r\n");
            return result.Trim();
        }

        public static string LightFallbackClean(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string result = NormalizeImText(text);
            string[] lines = result.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = Regex.Replace(lines[i], "[ \\t]+", " ").Trim();
                line = Regex.Replace(line, "^(嗯|呃|额|啊|那个|这个|就是|然后|然后呢|接着|接着呢)[，,、\\s]+", "");
                line = Regex.Replace(line, "([，,、。！？!?；;]){2,}", "$1");
                line = ApplyDomainCorrections(line);
                lines[i] = line;
            }
            result = String.Join("\r\n", lines);
            result = DropLikelyHallucinatedEnglish(result);
            result = Regex.Replace(result, "(\\r\\n){3,}", "\r\n\r\n");
            return result.Trim();
        }

        public static string FinalClean(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string result = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = result.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = RemoveSpeechFillers(lines[i]).Trim();
                lines[i] = ApplyDomainCorrections(lines[i]);
            }
            result = String.Join("\n", lines);
            result = DropLikelyHallucinatedEnglish(result);
            result = Regex.Replace(result, "\\n{3,}", "\n\n");
            return result.Replace("\n", "\r\n").Trim();
        }

        private static string ApplyDomainCorrections(string text)
        {
            string result = text;
            foreach (KeyValuePair<string, string> pair in DomainCorrections)
            {
                result = result.Replace(pair.Key, pair.Value);
            }
            foreach (KeyValuePair<string, string> pair in FinancialGlossary.LocalCorrections)
            {
                result = result.Replace(pair.Key, pair.Value);
            }
            result = Regex.Replace(result, "\\btypus\\b", "typos", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "\\bDCF\\s*法\\b", "DCF", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "\\bLBO\\s*模型\\b", "LBO 模型", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "\\bIPO\\s*项目\\b", "IPO 项目", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "\\bM\\s*&\\s*A\\b", "M&A", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "\\bEV\\s*/\\s*EBITDA\\b", "EV/EBITDA", RegexOptions.IgnoreCase);
            return result;
        }

        private static string DropLikelyHallucinatedEnglish(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string[] lines = text.Split('\n');
            List<string> kept = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    kept.Add(line);
                    continue;
                }
                int latin = 0;
                int cjk = 0;
                for (int j = 0; j < line.Length; j++)
                {
                    char c = line[j];
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
                    if (c >= 0x4e00 && c <= 0x9fff) cjk++;
                }
                bool looksLikeEnglishHallucination = latin > 80 && cjk == 0;
                bool containsAudioScene = Regex.IsMatch(line, "(sound of|footsteps|gravel|rain|snow|wildlife|forest|meadow)", RegexOptions.IgnoreCase);
                if (!looksLikeEnglishHallucination && !containsAudioScene) kept.Add(line);
            }
            return String.Join("\n", kept).Trim();
        }

        private static string RemoveSpeechFillers(string text)
        {
            string result = text;
            result = Regex.Replace(result, "(嗯|呃|额|啊){2,}", "");
            result = Regex.Replace(result, "^(哎|诶|欸|嗯|呃|额|啊|呃呃|那个|这个|就是|就是吧|然后呢|对吧|你知道吗|我想说)[，,、\\s]+", "");
            result = Regex.Replace(result, "(?<=[\\u4e00-\\u9fff])(嗯|呃|额|啊)(?=(可能|大概|应该|有|是|吃|喝|买|看|做|去|要|可以|能|会|把|给|先|再|然后))", "");
            result = Regex.Replace(result, "(?<=[了的过着嘛吗呢呀哈])(吧)(?=(大概|可能|应该|差不多|也许|有|是))", "");
            result = Regex.Replace(result, "(?<=[\\u4e00-\\u9fff])(那个|这个|就是|就是说)(?=(可能|大概|应该|有|是|要|可以|能|会|先|再|然后|第一|第二|第三))", "");
            result = Regex.Replace(result, "(?<=[。！？!?；;，,、\\s]|^)(然后呢?|接着呢?)[，,、\\s]*(?=(我|你|他|她|它|我们|你们|他们|先|再|把|给|去|说|讲|做|看|处理|安排|确认|补充|同步|另外|还有|最后|第一|第二|第三))", "");
            result = Regex.Replace(result, "(?<=[\\u4e00-\\u9fff])(然后呢?|接着呢?)(?=(先|再|把|给|去|说|讲|做|看|处理|安排|确认|补充|同步|另外|还有|最后|第一|第二|第三))", "");
            result = Regex.Replace(result, "(?<=[。！？!?；;，,、\\s]|^)(就这些|大概就这些|差不多就这些)[。！？!?；;，,、\\s]*(?=(还有|另外|然后|接着|再|最后))", "");
            result = Regex.Replace(result, "[，,、\\s]+(嗯|呃|额|啊|呢|那个|这个|就是|就是说|就是吧|然后呢|对吧|你知道吗)[，,、\\s]+", "，");
            result = Regex.Replace(result, "[，,、\\s]+(嗯|呃|额|啊|呢|那个|这个|就是|就是说|就是吧|然后呢|对吧|你知道吗)(?=[。！？!?；;，,、\\s]|$)", "");
            result = Regex.Replace(result, "(?<=[\\u4e00-\\u9fff])(对吧|你知道吗|我觉得吧|就是吧)(?=[。！？!?；;，,、\\s]|$)", "");
            result = Regex.Replace(result, "([，,、]){2,}", "，");
            result = Regex.Replace(result, "^[，,、\\s]+", "");
            result = Regex.Replace(result, "[，,、\\s]+$", "");
            return result.Trim();
        }

        public static string TryStructure(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;

            Regex markerRegex = new Regex("(首先|第一点|第一个|第一|一是|其一|其次|第二点|第二个|第二呢|然后第二|第二|二是|其二|另外|接着|最后|第三点|第三个|第三|三是|其三)");
            MatchCollection matches = markerRegex.Matches(text);
            if (matches.Count < 2) return null;

            string intro = text.Substring(0, matches[0].Index).Trim();
            intro = TrimTrailingPunctuation(intro);
            List<string> items = new List<string>();

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index + matches[i].Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                if (end <= start) continue;
                string item = text.Substring(start, end - start);
                item = CleanItem(item);
                if (item.Length > 0) items.Add(item);
            }

            if (items.Count < 2) return null;

            StringBuilder builder = new StringBuilder();
            if (intro.Length > 0)
            {
                builder.Append(intro);
                builder.Append("：");
                builder.Append("\r\n");
            }
            for (int i = 0; i < items.Count; i++)
            {
                builder.Append("• ");
                builder.Append(items[i]);
                if (i + 1 < items.Count) builder.Append("\r\n");
            }
            return builder.ToString();
        }

        public static string NormalizeImText(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string result = text.Trim();
            result = Regex.Replace(result, "^```[a-zA-Z]*\\s*", "");
            result = Regex.Replace(result, "\\s*```$", "");
            result = result.Replace("\r\n", "\n").Replace("\r", "\n");
            result = Regex.Replace(result, "\\n{3,}", "\n\n");
            result = Regex.Replace(result, "[ \\t]+\\n", "\n");
            result = result.Replace("\n", "\r\n");
            return result.Trim();
        }

        private static string CleanItem(string item)
        {
            string result = item.Trim();
            result = Regex.Replace(result, "^[，,、。；;：:\\s]+", "");
            result = Regex.Replace(result, "^(呢|是|就是|我把|我要|我们把|这个)", "");
            result = TrimTrailingPunctuation(result);
            return result.Trim();
        }

        private static string TrimTrailingPunctuation(string text)
        {
            return Regex.Replace(text.Trim(), "[，,。；;：:\\s]+$", "");
        }
    }

    internal sealed class ZhipuTextFormatClient
    {
        private const string Url = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
        private const string Model = "glm-5.2";

        public string Format(string apiKey, string text, InputMode mode)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(14);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["model"] = Model;
                payload["do_sample"] = false;
                payload["temperature"] = 0.1;
                payload["top_p"] = 0.7;
                payload["max_tokens"] = Math.Min(1800, Math.Max(320, text.Length * 3 + 220));
                payload["thinking"] = new Dictionary<string, object>
                {
                    { "type", "disabled" }
                };
                payload["messages"] = new object[]
                {
                    Message("system", BuildSystemPrompt(mode)),
                    Message("user", "原始转写：\n" + text)
                };

                string jsonBody = serializer.Serialize(payload);
                using (StringContent content = new StringContent(jsonBody, Encoding.UTF8, "application/json"))
                {
                    HttpResponseMessage response = client.PostAsync(Url, content).Result;
                    string body = response.Content.ReadAsStringAsync().Result;
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("HTTP " + (int)response.StatusCode + ": " + Redactor.Scrub(body, apiKey));
                    }
                    string parsed = ParseContent(serializer, body);
                    if (String.IsNullOrWhiteSpace(parsed))
                    {
                        Log.Write("Text format parse empty: mode=" + mode + ", body=" + TruncateForLog(Redactor.Scrub(body, apiKey), 1200));
                    }
                    else
                    {
                        Log.Write("Text format parse ok: mode=" + mode + ", parsedLength=" + parsed.Length);
                    }
                    return parsed;
                }
            }
        }

        private static Dictionary<string, object> Message(string role, string content)
        {
            Dictionary<string, object> message = new Dictionary<string, object>();
            message["role"] = role;
            message["content"] = content;
            return message;
        }

        private static string BuildSystemPrompt(InputMode mode)
        {
            if (mode == InputMode.Email) return BuildEmailPrompt();
            return BuildStructuredPrompt();
        }

        private static string BuildDomainGlossary()
        {
            return
                "\n" +
                "专业词识别规则：目标用户多为咨询、投行、PE/VC、战略、财务和高净值商务人群。看到相关上下文时，优先按以下专业词理解；只在语义明确时修正，不要为了凑术语而强行替换。\n" +
                "1. 咨询方法：MECE、议题树、假设驱动、金字塔原理、市场规模测算、商业模式、价值链、客户旅程、增长飞轮、GTM、go-to-market、benchmark、best practice、桌面研究、专家访谈、管理层访谈、竞品分析、用户分层、转化漏斗、留存、复购、单位经济模型。\n" +
                "2. 投行交易：IPO、M&A、并购、卖方顾问、买方顾问、尽职调查、财务尽调、法律尽调、商业尽调、路演、管理层路演、投资人材料、信息备忘录/IM、teaser、NDA、term sheet、SPA、交割、对赌、earn-out、协同效应、控股权、少数股权。\n" +
                "3. 估值建模：DCF、LBO、可比公司法、可比交易法、先例交易、交易倍数、EV/Revenue、EV/EBITDA、P/E、P/B、WACC、终值、永续增长率、退出倍数、敏感性分析、情景分析、收入预测、毛利率、EBITDA、EBIT、净利润、自由现金流、NWC、CapEx、OpEx。\n" +
                "4. PE/VC 和基金：GP、LP、IRR、MOIC、DPI、TVPI、RVPI、capital call、distribution、carry、hurdle rate、dry powder、fundraising、portfolio company、exit、secondary、co-invest、follow-on、bridge round、down round、liquidation preference。\n" +
                "5. 机构和品牌：麦肯锡、贝恩、BCG、埃森哲、德勤、PwC、安永、KPMG、高盛、摩根士丹利、J.P. Morgan、花旗、瑞银、美银、巴克莱、Jefferies、Lazard、Evercore、中金公司、中信证券、中信建投、华泰证券、国泰君安、招商证券、招银国际。\n" +
                "6. 常见误写：智谱不要写成质朴；typos 不要写成 typus；consulting、banking、whisky、whiskey、prompt、API、ASR、LLM、AUM、KPI、OKR、SOP 保持准确。\n" +
                FinancialGlossary.BuildPromptText() + "\n";
        }

        private static string BuildStructuredPrompt()
        {
            return
                "你是墨声语音输入法里的“整理输入”模块。输入来自 ASR，可能包含错字、漏字、重复、口头禅、语气词、错误空格和顺序词。\n" +
                "你的职责不是润色写作，而是做受限编辑：把用户刚刚说的话整理成可以直接粘贴到微信、飞书、企业微信、文档或网页输入框里的文本。\n" +
                "总原则：最小必要编辑。忠实、克制、清楚。保留用户的真实意思、判断和语气强度，不总结、不扩写、不新增事实、不替用户做决定。\n" +
                BuildDomainGlossary() +
                "\n" +
                "只允许做这些编辑：\n" +
                "1. 删除不影响含义的口头禅、语气词、重复词和自我纠正过程，例如：嗯、呃、额、啊、呢、那个、这个、就是、然后呢、对吧、你知道吗、我觉得吧。\n" +
                "2. “然后”“接着”只有在表达真实时间顺序或逻辑推进时才保留；作为口头填充时删除。\n" +
                "3. 修正高置信 ASR 错误：同音错字、错误空格、重复词、无意义片段、连续的 N I N I 等。\n" +
                "4. 补最基本的标点和分句，让文本可直接发送，但不要重写表达风格。\n" +
                "5. 只有在用户明显在列事项、讲几个点、做对比、讲步骤、安排计划时，才分行或分点输出。\n" +
                "6. 咨询/投行术语优先保持准确：麦肯锡、贝恩、BCG、高盛、摩根士丹利、J.P. Morgan、中金、中信、华泰、并购、尽调、路演、估值、财务模型、DCF、LBO、IPO、M&A、EV/EBITDA、EBITDA、IRR、ROI、NPV、CapEx、OpEx、consulting、banking 等不要写错。\n" +
                "7. 常见产品和英文词优先修正：智谱不要写成质朴；typos 不要写成 typus；API、ASR、LLM、prompt、whisky、whiskey 保持准确。\n" +
                "\n" +
                "明确禁止：\n" +
                "1. 不要扩写、总结、润色、升格、解释、分析、补背景、补因果、补结论。\n" +
                "2. 不要把普通短句改成更正式、更礼貌或更像邮件的话术。\n" +
                "3. 不要补主语、补对象、补时间、补动作，除非原文已经明确表达。\n" +
                "4. 不要因为想让句子更顺，就改写用户的判断、立场、语气强弱或事实边界。\n" +
                "5. 不确定的词不要猜，不要为了变顺而改掉用户可能有意使用的术语、名称、数字或英文。\n" +
                "6. 英文专业词不要强行翻译；用户明显在说英文术语时保留英文拼写。\n" +
                "\n" +
                "格式规则：\n" +
                "1. 使用真实换行，不要输出字面量 \\n。\n" +
                "2. 不要 Markdown 代码块，不要标题，不要“整理如下”“优化后”等解释语。\n" +
                "3. 不要输出引号包裹全文。\n" +
                "4. 只输出最终可粘贴文本。";
        }

        private static string BuildEmailPrompt()
        {
            return
                "你是墨声语音输入法里的“邮件输入”模块。输入来自 ASR，用户是在口述一封邮件、正式 IM、客户沟通、内部通知或跟进消息的要点。\n" +
                "你的任务是把任何口语要点都整理成可以直接粘贴发送的完整正式邮件或完整正式消息。只要进入这个模式，输出就必须具有邮件/正式消息格式：合适的开头称呼、清楚的正文、自然的结尾敬语。\n" +
                "总原则：忠实用户意图，在不编造事实的前提下做适度扩写，把零散口述整理成完整可发送的邮件文本。\n" +
                "投行/咨询邮件风格：像外资投行 banker 给客户、团队或律师会计师写 short professional note。要求结论/请求前置、背景极短、行动项清楚、语气克制直接。不要写成中文互联网客服话术，不要过度寒暄，不要为了显得正式而扩写。\n" +
                BuildDomainGlossary() +
                "\n" +
                "绝对禁止：\n" +
                "1. 不要编造收件人、公司、日期、金额、承诺、结论、附件、会议时间、截止时间或任何用户没有说的信息。\n" +
                "2. 不要把口述要点总结成过短摘要；必须保留用户给出的关键事项、限制、请求和原因。\n" +
                "3. 不要输出“以下是邮件”“优化后如下”“当然可以”等解释语。\n" +
                "4. 不要默认生成主题，除非用户明确说“主题是...”或“帮我写个标题”。\n" +
                "5. 不要添加用户没说过的例子、环境描述、英文段落、背景解释或原因推测。\n" +
                "6. 如果原文只是产品反馈、bug 描述或一句问题，不要把事实范围扩写大，但仍然要把它整理成一封可直接发送的正式邮件/正式消息。\n" +
                "7. 如果原文没有英文，不要输出英文；如果原文只有少量英文术语，只保留这些术语，不要生成整段英文。\n" +
                "8. 不要编造具体署名、职位、公司或联系人姓名；缺少这些信息时，使用通用称呼和通用结尾即可。\n" +
                "\n" +
                "语言和语气：\n" +
                "1. 默认使用中文投行/咨询商务语气：短、准、稳、可执行。\n" +
                "2. 优先使用“请确认/请补充/建议/我们先/下一步/麻烦看一下”这类清楚动作，不堆砌客套话。\n" +
                "3. 如果用户语气明显很随意，但场景是邮件或正式消息，要转换为更稳妥的表达，但不要改变事实强度。\n" +
                "4. 如果用户明确要求强硬、委婉、简短、正式、轻松，要按用户要求调整语气。\n" +
                "5. 如果用户说的是英文术语、公司名、模型名、交易名，不要强行翻译；保留原英文或通用写法。\n" +
                "6. 对上级/客户：语气更克制，先讲结论和请求；对团队：可更直接，强调 owner、deadline 和 next step。\n" +
                "\n" +
                "内容处理：\n" +
                "1. 删除明显语音词和口头禅，例如：嗯、呃、额、啊、呢、那个、这个、就是、对吧、你知道吗。\n" +
                "2. 修正高置信 ASR 错误：同音错字、错误空格、重复词、无意义片段；不确定时保留原词，不要猜。\n" +
                "3. 咨询/投行术语优先保持准确：麦肯锡、贝恩、BCG、高盛、摩根士丹利、J.P. Morgan、中金、中信、华泰、并购、尽调、路演、估值、财务模型、DCF、LBO、IPO、M&A、EV/EBITDA、EBITDA、IRR、ROI、NPV、CapEx、OpEx、consulting、banking 等不要写错。\n" +
                "4. 常见产品和英文词优先修正：智谱不要写成质朴；typos 不要写成 typus；API、ASR、LLM、prompt、whisky、whiskey 保持准确。\n" +
                "5. 如果用户是在请求对方做事，要把请求写清楚：希望对方确认什么、提供什么、反馈什么、完成什么。\n" +
                "6. 如果用户是在同步进展，要把状态、问题、下一步分开写清楚。\n" +
                "7. 如果用户是在道歉或解释，要语气诚恳，但不要主动承担用户没说过的责任。\n" +
                "8. 如果用户是在催办，要礼貌但明确，不要过度软化核心诉求。\n" +
                "9. 如果用户是在回复别人，要保留回复意图，不要改成一封全新的邮件。\n" +
                "10. 如果用户只是口述几个要点、几个句子或一段半成品，不要原样堆砌；要在不新增事实的前提下，补齐必要的连接句和礼貌表达，让它变成可直接发送的文本。\n" +
                "11. 即使输入本身是在讨论邮件、讲邮件要求、解释这个能力怎么用，也仍然要把这段内容改写成一封可发送的邮件/正式消息，只是不要编造新的事实。\n" +
                "\n" +
                "结构规则：\n" +
                "1. 默认添加通用称呼和通用结尾，让输出像完整邮件或正式消息。若未提供具体收件人，可使用“您好：”或“各位好：”；若未提供署名，可只用“谢谢。”或“敬请知悉。”这类通用结尾。\n" +
                "2. 如果是完整邮件，正文结构优先为：目的/请求 -> 必要背景 -> 具体行动项/时间点 -> 简短结尾。\n" +
                "3. 内容只有一句简单请求或反馈时，也要整理成一封可直接发送的正式邮件/正式消息，但不要补长篇背景。\n" +
                "4. 内容包含多个事项、条件、步骤或问题时，用短段落或 bullet 分开，避免堆成一整段。\n" +
                "5. 如果用户说“第一/第二/第三”或明显是事项列表，可以使用 bullet；如果是流程步骤，可以使用编号。\n" +
                "6. 如涉及请求对方动作，尽量写清楚 action、owner、deadline 或需要确认的事项；没有明确 deadline 时不要编造。\n" +
                "7. 邮件正文优先输出正文；只有用户明确要求主题时，才输出“主题：...”。\n" +
                "8. 不允许退回普通整理文本；只要处于 Email 模式，最终输出必须是完整邮件或完整正式消息格式。\n" +
                "\n" +
                "格式规则：\n" +
                "1. 使用真实换行，不要输出字面量 \\n。\n" +
                "2. 不要 Markdown 代码块，不要标题，不要解释。\n" +
                "3. 不要输出引号包裹全文。\n" +
                "4. 只输出最终可粘贴文本。";
        }

        private static string ParseContent(JavaScriptSerializer serializer, string body)
        {
            Dictionary<string, object> json = serializer.Deserialize<Dictionary<string, object>>(body);
            if (json == null || !json.ContainsKey("choices") || json["choices"] == null) return "";

            IEnumerable choices = json["choices"] as IEnumerable;
            if (choices == null) return "";
            foreach (object choiceObj in choices)
            {
                Dictionary<string, object> choice = choiceObj as Dictionary<string, object>;
                if (choice == null || !choice.ContainsKey("message")) continue;
                Dictionary<string, object> message = choice["message"] as Dictionary<string, object>;
                if (message == null || !message.ContainsKey("content") || message["content"] == null) continue;
                return Convert.ToString(message["content"]);
            }
            return "";
        }

        private static string TruncateForLog(string text, int maxLength)
        {
            if (String.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? "";
            return text.Substring(0, maxLength) + "...[truncated]";
        }
    }

    internal static class SmartFormatValidator
    {
        private static readonly string[] BadPrefixes = new string[]
        {
            "以下是", "优化后", "整理后", "总结如下", "原始转写", "最终文本"
        };

        private static readonly string[] HallucinationTerms = new string[]
        {
            "The sound of", "footsteps", "gravel", "rain or snow", "wildlife", "forest", "meadow",
            "环境音", "脚步声", "雨雪", "草地", "森林", "野生动物"
        };

        public static bool Accept(string original, string formatted, InputMode mode, out string reason)
        {
            reason = "";
            if (String.IsNullOrWhiteSpace(formatted))
            {
                reason = "empty-output";
                return false;
            }
            string output = formatted.Trim();
            bool structuredLayout = LooksStructured(output);
            int multiplier = mode == InputMode.Email ? 85 : (structuredLayout ? 42 : 32);
            int minimum = mode == InputMode.Email ? 520 : (structuredLayout ? 180 : 120);
            if (output.Length > Math.Max(minimum, original.Length * multiplier / 10))
            {
                reason = "too-long";
                return false;
            }

            for (int i = 0; i < BadPrefixes.Length; i++)
            {
                if (output.StartsWith(BadPrefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    reason = "bad-prefix";
                    return false;
                }
            }
            for (int i = 0; i < HallucinationTerms.Length; i++)
            {
                if (output.IndexOf(HallucinationTerms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reason = "hallucination-term";
                    return false;
                }
            }

            int originalLatin = CountLatinLetters(original);
            int outputLatin = CountLatinLetters(output);
            if (originalLatin < 20 && outputLatin > 80)
            {
                reason = "too-much-latin";
                return false;
            }
            if (mode == InputMode.Email && original.Length < 80 && output.Length > Math.Max(360, original.Length * 7))
            {
                reason = "email-over-expanded";
                return false;
            }

            string originalCore = CoreText(original);
            string outputCore = CoreText(output);
            if (outputCore.Length == 0)
            {
                reason = "empty-core";
                return false;
            }
            if (originalCore.Length >= 20 && outputCore.Length <= 3)
            {
                reason = "core-collapsed";
                return false;
            }
            reason = "accepted";
            return true;
        }

        public static string Repair(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            string result = LocalTextFormatter.NormalizeImText(text);
            result = Regex.Replace(result, "^(以下是|整理如下[：:]?|优化后[：:]?|最终文本[：:]?|原始转写[：:]?)\\s*", "", RegexOptions.IgnoreCase);
            string[] lines = result.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length > 1)
            {
                string firstLine = lines[0].Trim();
                for (int i = 0; i < BadPrefixes.Length; i++)
                {
                    if (firstLine.StartsWith(BadPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return String.Join("\r\n", lines, 1, lines.Length - 1).Trim();
                    }
                }
            }
            return result.Trim();
        }

        private static int CountLatinLetters(string text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) count++;
            }
            return count;
        }

        private static string CoreText(string text)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (Char.IsLetterOrDigit(c) || (c >= 0x4e00 && c <= 0x9fff))
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }

        private static bool LooksStructured(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return false;
            if (text.IndexOf("\r\n", StringComparison.Ordinal) >= 0) return true;
            return Regex.IsMatch(text, "(^|\\r\\n)(?:[•·-]|\\d+\\.)\\s*", RegexOptions.Multiline);
        }
    }

    internal static class AudioChunker
    {
        public static List<byte[]> SplitPcm(byte[] pcm, int sampleRate, int channels, int bitsPerSample, int seconds)
        {
            List<byte[]> chunks = new List<byte[]>();
            int blockAlign = channels * (bitsPerSample / 8);
            int maxBytes = sampleRate * blockAlign * seconds;
            maxBytes = Math.Max(blockAlign, (maxBytes / blockAlign) * blockAlign);
            for (int offset = 0; offset < pcm.Length; offset += maxBytes)
            {
                int length = Math.Min(maxBytes, pcm.Length - offset);
                length = (length / blockAlign) * blockAlign;
                if (length <= 0) break;
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(pcm, offset, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }
    }

    internal static class WavWriter
    {
        public static void Write(string path, byte[] data, int sampleRate, short channels, short bitsPerSample)
        {
            using (FileStream fs = File.Create(path))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                short blockAlign = (short)(channels * (bitsPerSample / 8));
                int byteRate = sampleRate * blockAlign;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + data.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write(bitsPerSample);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(data.Length);
                writer.Write(data);
            }
        }
    }

    internal struct AudioStats
    {
        public double Seconds;
        public int Peak;
        public double Rms;
        public bool HasSignal;
    }

    internal static class AudioInspector
    {
        public static AudioStats Inspect(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
        {
            AudioStats stats = new AudioStats();
            if (pcm == null || pcm.Length < 2 || bitsPerSample != 16)
            {
                return stats;
            }

            int sampleCount = pcm.Length / 2;
            long sumSquares = 0;
            int peak = 0;
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(pcm, i);
                int abs = Math.Abs((int)sample);
                if (abs > peak) peak = abs;
                sumSquares += (long)sample * sample;
            }

            double rms = Math.Sqrt(sumSquares / Math.Max(1.0, sampleCount));
            int bytesPerSecond = sampleRate * channels * (bitsPerSample / 8);
            stats.Seconds = bytesPerSecond <= 0 ? 0 : pcm.Length / (double)bytesPerSecond;
            stats.Peak = peak;
            stats.Rms = rms;
            bool hasBasicVoice = peak > 500 && rms > 45;
            bool longLowSignal = stats.Seconds >= 25 && rms < 85;
            bool veryLongLowSignal = stats.Seconds >= 90 && rms < 120;
            stats.HasSignal = hasBasicVoice && !longLowSignal && !veryLongLowSignal;
            return stats;
        }
    }

    internal sealed class MoshengWaveRecorder : IDisposable
    {
        private const int WAVE_MAPPER = -1;
        private const uint CALLBACK_FUNCTION = 0x00030000;
        private const uint WIM_DATA = 0x3C0;
        private const ushort WAVE_FORMAT_PCM = 1;
        private readonly object sync = new object();
        private readonly List<BufferState> buffers = new List<BufferState>();
        private readonly int sampleRate = 16000;
        private readonly short channels = 1;
        private readonly short bitsPerSample = 16;
        private IntPtr handle = IntPtr.Zero;
        private WaveInProc callback;
        private MemoryStream stream;
        private bool recording;
        private int headerSize;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private sealed class BufferState
        {
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

        public void Start()
        {
            if (handle != IntPtr.Zero) throw new InvalidOperationException("Recorder is already running.");
            if (waveInGetNumDevs() == 0) throw new InvalidOperationException("No recording device was found.");

            stream = new MemoryStream();
            callback = Callback;
            headerSize = Marshal.SizeOf(typeof(WaveHdr));

            WaveFormatEx format = new WaveFormatEx();
            format.wFormatTag = WAVE_FORMAT_PCM;
            format.nChannels = (ushort)channels;
            format.nSamplesPerSec = (uint)sampleRate;
            format.wBitsPerSample = (ushort)bitsPerSample;
            format.nBlockAlign = (ushort)(channels * (bitsPerSample / 8));
            format.nAvgBytesPerSec = (uint)(sampleRate * format.nBlockAlign);
            format.cbSize = 0;

            Check(waveInOpen(out handle, WAVE_MAPPER, ref format, callback, IntPtr.Zero, CALLBACK_FUNCTION), "waveInOpen");
            recording = true;
            int bufferSize = sampleRate * format.nBlockAlign / 5;
            for (int i = 0; i < 4; i++) AddBuffer(bufferSize);
            Check(waveInStart(handle), "waveInStart");
        }

        public byte[] StopToBytes()
        {
            lock (sync)
            {
                recording = false;
            }

            if (handle != IntPtr.Zero)
            {
                waveInStop(handle);
                waveInReset(handle);
                foreach (BufferState buffer in buffers)
                {
                    waveInUnprepareHeader(handle, buffer.HeaderPtr, headerSize);
                }
                waveInClose(handle);
                handle = IntPtr.Zero;
            }

            byte[] data;
            lock (sync)
            {
                data = stream == null ? new byte[0] : stream.ToArray();
            }

            foreach (BufferState buffer in buffers)
            {
                if (buffer.HeaderPtr != IntPtr.Zero) Marshal.FreeHGlobal(buffer.HeaderPtr);
                if (buffer.DataHandle.IsAllocated) buffer.DataHandle.Free();
            }
            buffers.Clear();
            if (stream != null)
            {
                stream.Dispose();
                stream = null;
            }
            return data;
        }

        public void Dispose()
        {
            try { StopToBytes(); } catch { }
        }

        private void AddBuffer(int bufferSize)
        {
            BufferState state = new BufferState();
            state.Data = new byte[bufferSize];
            state.DataHandle = GCHandle.Alloc(state.Data, GCHandleType.Pinned);
            state.HeaderPtr = Marshal.AllocHGlobal(headerSize);
            WaveHdr header = new WaveHdr();
            header.lpData = state.DataHandle.AddrOfPinnedObject();
            header.dwBufferLength = (uint)bufferSize;
            Marshal.StructureToPtr(header, state.HeaderPtr, false);
            Check(waveInPrepareHeader(handle, state.HeaderPtr, headerSize), "waveInPrepareHeader");
            Check(waveInAddBuffer(handle, state.HeaderPtr, headerSize), "waveInAddBuffer");
            buffers.Add(state);
        }

        private void Callback(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg != WIM_DATA || dwParam1 == IntPtr.Zero) return;
            WaveHdr header = (WaveHdr)Marshal.PtrToStructure(dwParam1, typeof(WaveHdr));
            lock (sync)
            {
                if (stream != null && header.dwBytesRecorded > 0)
                {
                    byte[] chunk = new byte[header.dwBytesRecorded];
                    Marshal.Copy(header.lpData, chunk, 0, (int)header.dwBytesRecorded);
                    stream.Write(chunk, 0, chunk.Length);
                }
                if (recording && handle != IntPtr.Zero)
                {
                    header.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(header, dwParam1, false);
                    waveInAddBuffer(handle, dwParam1, headerSize);
                }
            }
        }

        private static void Check(int result, string action)
        {
            if (result != 0) throw new InvalidOperationException(action + " failed with code " + result + ".");
        }
    }

    internal enum InsertResult
    {
        Pasted,
        ClipboardOnly
    }

    internal static class InputInjector
    {
        public static InsertResult InsertText(IntPtr target, string text, bool restoreClipboard)
        {
            string pasteText = LocalTextFormatter.NormalizeImText(text);
            if (target != IntPtr.Zero)
            {
                if (!MoshengNative.SetForegroundWindow(target))
                {
                    SetClipboardText(pasteText);
                    return InsertResult.ClipboardOnly;
                }
                System.Threading.Thread.Sleep(80);
            }
            string previous = null;
            bool hadText = false;
            try
            {
                if (restoreClipboard && Clipboard.ContainsText())
                {
                    previous = Clipboard.GetText();
                    hadText = true;
                }
                SetClipboardText(pasteText);
                System.Threading.Thread.Sleep(60);
                SendKeys.SendWait("^v");
                System.Threading.Thread.Sleep(100);
                if (restoreClipboard && hadText) SetClipboardText(previous);
                return InsertResult.Pasted;
            }
            catch
            {
                SetClipboardText(pasteText);
                return InsertResult.ClipboardOnly;
            }
        }

        private static void SetClipboardText(string text)
        {
            Exception last = null;
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    return;
                }
                catch (ExternalException ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(80 + (i * 40));
                }
            }
            if (last != null) throw last;
        }
    }

    internal sealed class Settings
    {
        public string ProtectedApiKey = "";
        public string KeyTail = "";
        public int ShortcutVk = 0x20;
        public bool ShortcutCtrl = true;
        public bool ShortcutAlt = false;
        public bool ShortcutShift = false;
        public string ShortcutLabel = "Ctrl + Space";
        public int EmailShortcutVk = 0x20;
        public bool EmailShortcutCtrl = true;
        public bool EmailShortcutAlt = true;
        public bool EmailShortcutShift = false;
        public string EmailShortcutLabel = "Ctrl + Alt + Space";
    }

    internal sealed class SettingsStore
    {
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public Settings Load()
        {
            Settings settings = new Settings();
            try
            {
                if (!File.Exists(AppPaths.ConfigPath)) return settings;
                Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(AppPaths.ConfigPath, Encoding.UTF8));
                if (data == null) return settings;
                if (data.ContainsKey("protectedApiKey") && data["protectedApiKey"] != null) settings.ProtectedApiKey = Convert.ToString(data["protectedApiKey"]);
                if (data.ContainsKey("keyTail") && data["keyTail"] != null) settings.KeyTail = Convert.ToString(data["keyTail"]);
                if (data.ContainsKey("shortcutVk") && data["shortcutVk"] != null) settings.ShortcutVk = Convert.ToInt32(data["shortcutVk"]);
                if (data.ContainsKey("shortcutCtrl") && data["shortcutCtrl"] != null) settings.ShortcutCtrl = Convert.ToBoolean(data["shortcutCtrl"]);
                if (data.ContainsKey("shortcutAlt") && data["shortcutAlt"] != null) settings.ShortcutAlt = Convert.ToBoolean(data["shortcutAlt"]);
                if (data.ContainsKey("shortcutShift") && data["shortcutShift"] != null) settings.ShortcutShift = Convert.ToBoolean(data["shortcutShift"]);
                if (data.ContainsKey("emailShortcutVk") && data["emailShortcutVk"] != null) settings.EmailShortcutVk = Convert.ToInt32(data["emailShortcutVk"]);
                if (data.ContainsKey("emailShortcutCtrl") && data["emailShortcutCtrl"] != null) settings.EmailShortcutCtrl = Convert.ToBoolean(data["emailShortcutCtrl"]);
                if (data.ContainsKey("emailShortcutAlt") && data["emailShortcutAlt"] != null) settings.EmailShortcutAlt = Convert.ToBoolean(data["emailShortcutAlt"]);
                if (data.ContainsKey("emailShortcutShift") && data["emailShortcutShift"] != null) settings.EmailShortcutShift = Convert.ToBoolean(data["emailShortcutShift"]);
                if (settings.ShortcutVk == 0xA0 && !settings.ShortcutCtrl && !settings.ShortcutAlt && !settings.ShortcutShift)
                {
                    settings.ShortcutVk = 0x20;
                    settings.ShortcutCtrl = true;
                    settings.ShortcutAlt = false;
                    settings.ShortcutShift = false;
                }
                if (settings.EmailShortcutVk == 0xA0 && settings.EmailShortcutCtrl && settings.EmailShortcutAlt && !settings.EmailShortcutShift)
                {
                    settings.EmailShortcutVk = 0x20;
                    settings.EmailShortcutCtrl = true;
                    settings.EmailShortcutAlt = true;
                    settings.EmailShortcutShift = false;
                }
                settings.ShortcutLabel = ShortcutKeys.Label(settings);
                settings.EmailShortcutLabel = ShortcutKeys.Label(settings.EmailShortcutVk, settings.EmailShortcutCtrl, settings.EmailShortcutAlt, settings.EmailShortcutShift);
            }
            catch (Exception ex)
            {
                Log.Write("Load settings failed: " + ex);
            }
            return settings;
        }

        public void Save(Settings settings)
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["protectedApiKey"] = settings.ProtectedApiKey ?? "";
            data["keyTail"] = settings.KeyTail ?? "";
            data["shortcutVk"] = settings.ShortcutVk;
            data["shortcutCtrl"] = settings.ShortcutCtrl;
            data["shortcutAlt"] = settings.ShortcutAlt;
            data["shortcutShift"] = settings.ShortcutShift;
            data["shortcutLabel"] = settings.ShortcutLabel ?? "";
            data["emailShortcutVk"] = settings.EmailShortcutVk;
            data["emailShortcutCtrl"] = settings.EmailShortcutCtrl;
            data["emailShortcutAlt"] = settings.EmailShortcutAlt;
            data["emailShortcutShift"] = settings.EmailShortcutShift;
            data["emailShortcutLabel"] = settings.EmailShortcutLabel ?? "";
            File.WriteAllText(AppPaths.ConfigPath, serializer.Serialize(data), Encoding.UTF8);
        }
    }

    internal static class SecretBox
    {
        public static string Protect(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                byte[] plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return "";
            }
        }
    }

    internal static class ShortcutKeys
    {
        public static bool Matches(Settings settings, HashSet<int> pressedKeys)
        {
            return Matches(settings.ShortcutVk, settings.ShortcutCtrl, settings.ShortcutAlt, settings.ShortcutShift, pressedKeys);
        }

        public static bool MatchesEmail(Settings settings, HashSet<int> pressedKeys)
        {
            return Matches(settings.EmailShortcutVk, settings.EmailShortcutCtrl, settings.EmailShortcutAlt, settings.EmailShortcutShift, pressedKeys);
        }

        public static bool Matches(int shortcutVk, bool ctrl, bool alt, bool shift, HashSet<int> pressedKeys)
        {
            if (!ContainsShortcutKey(shortcutVk, pressedKeys)) return false;
            if (!IsCtrl(shortcutVk) && ctrl != CtrlDown(pressedKeys)) return false;
            if (!IsAlt(shortcutVk) && alt != AltDown(pressedKeys)) return false;
            if (!IsShift(shortcutVk) && shift != ShiftDown(pressedKeys)) return false;
            return true;
        }

        public static void TrackDown(HashSet<int> pressedKeys, int vk)
        {
            pressedKeys.Add(vk);
            if (IsCtrl(vk)) pressedKeys.Add(0x11);
            if (IsAlt(vk)) pressedKeys.Add(0x12);
            if (IsShift(vk)) pressedKeys.Add(0x10);
        }

        public static void TrackUp(HashSet<int> pressedKeys, int vk)
        {
            if (IsCtrl(vk))
            {
                pressedKeys.Remove(0x11);
                pressedKeys.Remove(0xA2);
                pressedKeys.Remove(0xA3);
                return;
            }
            if (IsAlt(vk))
            {
                pressedKeys.Remove(0x12);
                pressedKeys.Remove(0xA4);
                pressedKeys.Remove(0xA5);
                return;
            }
            if (IsShift(vk))
            {
                pressedKeys.Remove(0x10);
                pressedKeys.Remove(0xA0);
                pressedKeys.Remove(0xA1);
                return;
            }
            pressedKeys.Remove(vk);
        }

        public static bool IsEquivalent(int configuredVk, int eventVk)
        {
            if (IsCtrl(configuredVk) && IsCtrl(eventVk)) return true;
            if (IsAlt(configuredVk) && IsAlt(eventVk)) return true;
            if (IsShift(configuredVk) && IsShift(eventVk)) return true;
            return configuredVk == eventVk;
        }

        private static bool ContainsShortcutKey(int configuredVk, HashSet<int> pressedKeys)
        {
            if (IsCtrl(configuredVk)) return CtrlDown(pressedKeys);
            if (IsAlt(configuredVk)) return AltDown(pressedKeys);
            if (IsShift(configuredVk)) return ShiftDown(pressedKeys);
            return pressedKeys.Contains(configuredVk);
        }

        public static string Label(Settings settings)
        {
            return Label(settings.ShortcutVk, settings.ShortcutCtrl, settings.ShortcutAlt, settings.ShortcutShift);
        }

        public static string Label(int shortcutVk, bool ctrl, bool alt, bool shift)
        {
            List<string> parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(KeyLabel(shortcutVk));
            return String.Join(" + ", parts.ToArray());
        }

        public static bool IsModifier(int vk)
        {
            return IsCtrl(vk) || IsAlt(vk) || IsShift(vk);
        }

        public static bool IsCtrl(int vk)
        {
            return vk == 0x11 || vk == 0xA2 || vk == 0xA3;
        }

        public static bool IsAlt(int vk)
        {
            return vk == 0x12 || vk == 0xA4 || vk == 0xA5;
        }

        public static bool IsShift(int vk)
        {
            return vk == 0x10 || vk == 0xA0 || vk == 0xA1;
        }

        public static bool CtrlDown(HashSet<int> pressedKeys)
        {
            return pressedKeys.Contains(0x11) || pressedKeys.Contains(0xA2) || pressedKeys.Contains(0xA3);
        }

        public static bool AltDown(HashSet<int> pressedKeys)
        {
            return pressedKeys.Contains(0x12) || pressedKeys.Contains(0xA4) || pressedKeys.Contains(0xA5);
        }

        public static bool ShiftDown(HashSet<int> pressedKeys)
        {
            return pressedKeys.Contains(0x10) || pressedKeys.Contains(0xA0) || pressedKeys.Contains(0xA1);
        }

        private static string KeyLabel(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F).ToString();
            switch (vk)
            {
                case 0xA0: return "左 Shift";
                case 0xA1: return "右 Shift";
                case 0xA2: return "左 Ctrl";
                case 0xA3: return "右 Ctrl";
                case 0xA4: return "左 Alt";
                case 0xA5: return "右 Alt";
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
                case 0x1B: return "Esc";
                default: return "VK " + vk;
            }
        }
    }

    internal static class FriendlyError
    {
        public static string From(Exception ex)
        {
            string message = ex.Message ?? "";
            if (message.IndexOf("NO_AUDIO_BYTES", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "没有录到音频。请检查 Windows 麦克风权限，或确认当前默认麦克风可用。";
            }
            if (message.IndexOf("NO_VOICE_SIGNAL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "录到了音频，但声音接近静音。请确认麦克风没有被静音、输入设备选对了，并靠近一点再试。";
            }
            if (message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "API Key 无效或已过期，请重新粘贴智谱 API Key。";
            }
            if (message.IndexOf("1214", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("时长限制", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "单段语音超过限制。墨声会自动分段，请短一点再试或重新录制。";
            }
            if (message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "当前账号额度不足或请求过于频繁，请稍后再试。";
            }
            if (message.IndexOf("network", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "网络连接失败，请确认可以访问智谱 API。";
            }
            return "语音识别失败，请稍后再试。";
        }
    }

    internal static class Redactor
    {
        public static string Scrub(string text, string apiKey)
        {
            if (text == null) return "";
            string result = text;
            if (!String.IsNullOrEmpty(apiKey)) result = result.Replace(apiKey, "[REDACTED_API_KEY]");
            return result;
        }
    }

    internal static class Log
    {
        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ConfigDir);
                File.AppendAllText(AppPaths.LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    internal static class AppPaths
    {
        public static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mosheng");
        public static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");
        public static readonly string LogPath = Path.Combine(ConfigDir, "mosheng-demo.log");
        public static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Mosheng");
        public static readonly string ResourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources");
        public static readonly string GlossaryPath = Path.Combine(ResourceDir, "financial_glossary_seed.json");
    }

    internal static class Fonts
    {
        public static Font Ui(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style);
        }
    }

    internal static class Palette
    {
        public static readonly Color Ink = Color.FromArgb(12, 12, 12);
        public static readonly Color Muted = Color.FromArgb(112, 112, 112);
    }

    public static class MoshengNative
    {
        public delegate void KeyboardEvent(int vkCode, bool isDown);
        public static event KeyboardEvent KeyboardAction;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private static LowLevelKeyboardProc proc = HookCallback;
        private static IntPtr hookId = IntPtr.Zero;

        public static void StartHook()
        {
            if (hookId == IntPtr.Zero) hookId = SetHook(proc);
            if (hookId == IntPtr.Zero)
            {
                Log.Write("Keyboard hook install returned zero. Win32Error=" + Marshal.GetLastWin32Error());
            }
        }

        public static void RestartHook()
        {
            StopHook();
            StartHook();
        }

        public static void StopHook()
        {
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process process = Process.GetCurrentProcess())
            using (ProcessModule module = process.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN || message == WM_KEYUP || message == WM_SYSKEYUP)
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                    KeyboardEvent handler = KeyboardAction;
                    if (handler != null) handler(vkCode, isDown);
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
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
}
