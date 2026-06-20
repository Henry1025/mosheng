using System;
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
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Mosheng
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
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
            using (AppController controller = new AppController())
            {
                controller.Start();
                Application.Run();
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

    internal sealed class AppController : IDisposable
    {
        private const int LeftShift = 0xA0;
        private readonly SettingsStore settingsStore = new SettingsStore();
        private readonly ZhipuSpeechClient speechClient = new ZhipuSpeechClient();
        private readonly Timer holdTimer = new Timer();
        private readonly Timer maxCaptureTimer = new Timer();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private Settings settings;
        private SettingsForm settingsForm;
        private OverlayForm overlay;
        private MoshengWaveRecorder recorder;
        private bool shortcutHeld;
        private bool isRecording;
        private bool isProcessing;
        private bool isExiting;
        private IntPtr activeWindow;

        public void Start()
        {
            settings = settingsStore.Load();
            settingsForm = new SettingsForm(settings);
            overlay = new OverlayForm();

            settingsForm.SaveRequested += OnSaveRequested;
            settingsForm.ShortcutCaptureRequested += OnShortcutCaptureRequested;
            settingsForm.ShortcutResetRequested += OnShortcutResetRequested;
            settingsForm.OverlayPreviewRequested += OnOverlayPreviewRequested;

            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "墨声";
            trayIcon.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("打开设置");
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出墨声");
            settingsItem.Click += delegate { ShowSettings(); };
            exitItem.Click += delegate { Exit(); };
            menu.Items.Add(settingsItem);
            menu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { ShowSettings(); };

            holdTimer.Interval = 260;
            holdTimer.Tick += delegate
            {
                holdTimer.Stop();
                if (shortcutHeld && !isRecording && !isProcessing)
                {
                    StartCapture();
                }
            };

            maxCaptureTimer.Interval = 120000;
            maxCaptureTimer.Tick += delegate
            {
                maxCaptureTimer.Stop();
                if (isRecording)
                {
                    StopCapture();
                }
            };

            MoshengNative.KeyboardAction += OnKeyboardAction;
            MoshengNative.StartHook();
            ShowSettings();
        }

        private void OnSaveRequested(object sender, SaveSettingsEventArgs args)
        {
            if (!String.IsNullOrWhiteSpace(args.ApiKey))
            {
                settings.ProtectedApiKey = SecretBox.Protect(args.ApiKey.Trim());
                settings.KeyTail = Tail(args.ApiKey.Trim());
            }
            settings.ShortcutVk = args.ShortcutVk;
            settings.ShortcutLabel = KeyNames.Label(args.ShortcutVk);
            settingsStore.Save(settings);
            settingsForm.RefreshFrom(settings);
            trayIcon.ShowBalloonTip(1300, "墨声正在运行", settings.ShortcutLabel + " 录音，松开后输入。", ToolTipIcon.Info);
        }

        private void OnShortcutCaptureRequested(object sender, EventArgs args)
        {
            settingsForm.BeginShortcutCapture();
        }

        private void OnShortcutResetRequested(object sender, EventArgs args)
        {
            settings.ShortcutVk = 0xA0;
            settings.ShortcutLabel = KeyNames.Label(settings.ShortcutVk);
            settingsStore.Save(settings);
            settingsForm.RefreshFrom(settings);
        }

        private void OnOverlayPreviewRequested(object sender, EventArgs args)
        {
            overlay.ShowMode(OverlayMode.Recording);
            overlay.HideLater();
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
            if (settingsForm.IsCapturingShortcut && isDown)
            {
                settings.ShortcutVk = vkCode;
                settings.ShortcutLabel = KeyNames.Label(vkCode);
                settingsStore.Save(settings);
                settingsForm.EndShortcutCapture(settings);
                return;
            }

            if (vkCode != settings.ShortcutVk) return;

            if (isDown)
            {
                if (shortcutHeld) return;
                shortcutHeld = true;
                activeWindow = MoshengNative.GetForegroundWindow();
                holdTimer.Stop();
                holdTimer.Start();
            }
            else
            {
                shortcutHeld = false;
                holdTimer.Stop();
                if (isRecording)
                {
                    StopCapture();
                }
            }
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

                string apiKey = SecretBox.Unprotect(settings.ProtectedApiKey);
                string text = TranscribeLongAudio(apiKey, pcm);
                if (!String.IsNullOrWhiteSpace(text))
                {
                    InputInjector.InsertText(activeWindow, text.Trim(), true);
                    overlay.ShowMode(OverlayMode.Success);
                }
                else
                {
                    overlay.ShowMode(OverlayMode.Error);
                    MessageBox.Show("没有识别到文字。", "墨声", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Transcription failed: " + ex);
                overlay.ShowMode(OverlayMode.Error);
                MessageBox.Show(FriendlyError.From(ex), "墨声", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                isProcessing = false;
                overlay.HideLater();
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
        }

        private static string Tail(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return value.Substring(Math.Max(0, value.Length - Math.Min(4, value.Length)));
        }

        private void Exit()
        {
            isExiting = true;
            try { MoshengNative.StopHook(); } catch { }
            try { trayIcon.Visible = false; trayIcon.Dispose(); } catch { }
            try { if (recorder != null) recorder.Dispose(); } catch { }
            Application.Exit();
        }

        public void Dispose()
        {
            Exit();
        }
    }

    internal sealed class SettingsForm : Form
    {
        public event EventHandler<SaveSettingsEventArgs> SaveRequested;
        public event EventHandler ShortcutCaptureRequested;
        public event EventHandler ShortcutResetRequested;
        public event EventHandler OverlayPreviewRequested;
        private readonly Label closeButton = new Label();
        private readonly TextBox apiKeyBox = new TextBox();
        private readonly Panel apiKeyFrame = new Panel();
        private readonly Button saveButton = new Button();
        private readonly Button shortcutButton = new Button();
        private readonly Button resetShortcutButton = new Button();
        private readonly Button previewButton = new Button();
        private readonly Label keyState = new Label();
        private bool dragging;
        private Point dragStart;
        private int shortcutVk;

        public bool IsCapturingShortcut { get; private set; }

        public SettingsForm(Settings settings)
        {
            Text = "墨声";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            ShowIcon = false;
            Size = new Size(460, 384);
            MinimumSize = new Size(460, 384);
            MaximumSize = new Size(460, 384);
            BackColor = Color.White;
            Font = Fonts.Ui(9f, FontStyle.Regular);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            shortcutVk = settings.ShortcutVk;

            closeButton.Text = "×";
            closeButton.TextAlign = ContentAlignment.MiddleCenter;
            closeButton.Font = Fonts.Ui(13f, FontStyle.Regular);
            closeButton.ForeColor = Color.FromArgb(120, 120, 120);
            closeButton.SetBounds(418, 18, 24, 24);
            closeButton.Cursor = Cursors.Hand;
            closeButton.Click += delegate { Hide(); };
            Controls.Add(closeButton);

            Controls.Add(NewLabel("MOSHENG", 32, 30, 160, 18, 8f, FontStyle.Bold, Palette.Muted));
            Controls.Add(NewLabel("墨声", 32, 50, 120, 34, 22f, FontStyle.Bold, Palette.Ink));
            Controls.Add(NewLabel("语音输入设置", 32, 94, 180, 24, 15f, FontStyle.Bold, Palette.Ink));
            Controls.Add(NewLabel("输入 API Key 后，长按快捷键说话。", 32, 122, 330, 22, 9f, FontStyle.Regular, Palette.Muted));
            Controls.Add(NewLabel("API Key", 32, 156, 140, 18, 8f, FontStyle.Bold, Palette.Muted));

            apiKeyBox.PasswordChar = '*';
            apiKeyBox.Font = Fonts.Ui(10.5f, FontStyle.Regular);
            apiKeyBox.BorderStyle = BorderStyle.None;
            apiKeyBox.BackColor = Color.White;
            apiKeyBox.SetBounds(12, 9, 372, 20);
            apiKeyFrame.BackColor = Color.White;
            apiKeyFrame.SetBounds(32, 178, 396, 38);
            apiKeyFrame.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(185, 185, 185), 1f))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, apiKeyFrame.Width - 1, apiKeyFrame.Height - 1);
                }
            };
            apiKeyFrame.Controls.Add(apiKeyBox);
            Controls.Add(apiKeyFrame);

            saveButton.Text = "保存";
            saveButton.SetBounds(32, 234, 96, 34);
            StyleButton(saveButton, Color.Black, Color.White, false);
            saveButton.Click += delegate
            {
                if (SaveRequested != null)
                {
                    SaveRequested(this, new SaveSettingsEventArgs(apiKeyBox.Text, shortcutVk));
                    apiKeyBox.Clear();
                }
            };
            Controls.Add(saveButton);

            keyState.SetBounds(144, 242, 180, 20);
            keyState.Font = Fonts.Ui(8f, FontStyle.Bold);
            keyState.ForeColor = Palette.Muted;
            Controls.Add(keyState);

            Controls.Add(NewLabel("唤醒方式", 32, 296, 90, 18, 8f, FontStyle.Bold, Palette.Muted));
            shortcutButton.TextAlign = ContentAlignment.MiddleLeft;
            shortcutButton.SetBounds(116, 286, 188, 34);
            StyleButton(shortcutButton, Color.FromArgb(248, 248, 248), Palette.Ink, true);
            shortcutButton.Click += delegate
            {
                if (ShortcutCaptureRequested != null) ShortcutCaptureRequested(this, EventArgs.Empty);
            };
            Controls.Add(shortcutButton);

            resetShortcutButton.Text = "恢复默认";
            resetShortcutButton.SetBounds(316, 286, 92, 34);
            StyleButton(resetShortcutButton, Color.White, Palette.Ink, true);
            resetShortcutButton.Click += delegate
            {
                if (ShortcutResetRequested != null) ShortcutResetRequested(this, EventArgs.Empty);
            };
            Controls.Add(resetShortcutButton);

            previewButton.Text = "预览输入胶囊";
            previewButton.SetBounds(32, 334, 128, 30);
            StyleButton(previewButton, Color.White, Palette.Muted, true);
            previewButton.Click += delegate
            {
                if (OverlayPreviewRequested != null) OverlayPreviewRequested(this, EventArgs.Empty);
            };
            Controls.Add(previewButton);

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
            shortcutButton.Text = settings.ShortcutLabel;
            keyState.Text = String.IsNullOrEmpty(settings.KeyTail) ? "未保存" : "已保存 · " + settings.KeyTail;
        }

        public void BeginShortcutCapture()
        {
            IsCapturingShortcut = true;
            shortcutButton.Text = "按下想使用的键";
            shortcutButton.BackColor = Color.Black;
            shortcutButton.ForeColor = Color.White;
        }

        public void EndShortcutCapture(Settings settings)
        {
            IsCapturingShortcut = false;
            RefreshFrom(settings);
            shortcutButton.BackColor = Color.FromArgb(250, 250, 250);
            shortcutButton.ForeColor = Palette.Ink;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(Color.FromArgb(216, 216, 216), 1f))
            {
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
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
            button.Cursor = Cursors.Hand;
        }
    }

    internal sealed class SaveSettingsEventArgs : EventArgs
    {
        public readonly string ApiKey;
        public readonly int ShortcutVk;

        public SaveSettingsEventArgs(string apiKey, int shortcutVk)
        {
            ApiKey = apiKey;
            ShortcutVk = shortcutVk;
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
        private bool showActions;
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

            waveTimer.Interval = 110;
            waveTimer.Tick += delegate
            {
                phase += mode == OverlayMode.Processing ? 0.18 : 0.26;
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
            showActions = nextMode == OverlayMode.Success || nextMode == OverlayMode.Error;
            Size = showActions ? new Size(132, 34) : new Size(92, 34);
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

        private void FadeOut()
        {
            fadingOut = true;
            fadeTimer.Start();
        }

        private void PositionNearBottom()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int x = area.Left + ((area.Width - Width) / 2);
            int y = area.Bottom - 72;
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

            if (showActions)
            {
                DrawActionCaps(g);
            }
            DrawWave(g);
        }

        private void DrawActionCaps(Graphics g)
        {
            using (GraphicsPath left = RoundedPath(new RectangleF(4, 4, 28, 26), 13))
            using (SolidBrush leftBrush = new SolidBrush(Color.FromArgb(34, 34, 34)))
            {
                g.FillPath(leftBrush, left);
            }
            using (Pen xPen = new Pen(Color.White, 2.05f))
            {
                xPen.StartCap = LineCap.Round;
                xPen.EndCap = LineCap.Round;
                g.DrawLine(xPen, 14, 13, 22, 21);
                g.DrawLine(xPen, 22, 13, 14, 21);
            }

            Color rightFill = mode == OverlayMode.Error ? Color.FromArgb(222, 222, 222) : Color.White;
            using (GraphicsPath right = RoundedPath(new RectangleF(Width - 34, 4, 30, 26), 13))
            using (SolidBrush rightBrush = new SolidBrush(rightFill))
            {
                g.FillPath(rightBrush, right);
            }
            using (Pen checkPen = new Pen(Color.Black, 2.35f))
            {
                checkPen.StartCap = LineCap.Round;
                checkPen.EndCap = LineCap.Round;
                g.DrawLines(checkPen, new PointF[] {
                    new PointF(Width - 23, 17),
                    new PointF(Width - 18, 22),
                    new PointF(Width - 10, 12)
                });
            }
        }

        private void DrawWave(Graphics g)
        {
            int barCount = showActions ? 7 : 6;
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
            byte[] data;
            lock (sync)
            {
                recording = false;
                data = stream == null ? new byte[0] : stream.ToArray();
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

    internal static class InputInjector
    {
        public static void InsertText(IntPtr target, string text, bool restoreClipboard)
        {
            if (target != IntPtr.Zero)
            {
                MoshengNative.SetForegroundWindow(target);
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
                Clipboard.SetText(text);
                System.Threading.Thread.Sleep(60);
                SendKeys.SendWait("^v");
                System.Threading.Thread.Sleep(100);
                if (restoreClipboard && hadText) Clipboard.SetText(previous);
            }
            catch
            {
                Clipboard.SetText(text);
                throw new InvalidOperationException("文字已复制到剪贴板，但自动粘贴失败。");
            }
        }
    }

    internal sealed class Settings
    {
        public string ProtectedApiKey = "";
        public string KeyTail = "";
        public int ShortcutVk = 0xA0;
        public string ShortcutLabel = "长按左 Shift";
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
                settings.ShortcutLabel = KeyNames.Label(settings.ShortcutVk);
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
            data["shortcutLabel"] = settings.ShortcutLabel ?? "";
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

    internal static class KeyNames
    {
        public static string Label(int vk)
        {
            switch (vk)
            {
                case 0xA0: return "长按左 Shift";
                case 0xA1: return "长按右 Shift";
                case 0xA2: return "长按左 Ctrl";
                case 0xA3: return "长按右 Ctrl";
                case 0xA4: return "长按左 Alt";
                case 0xA5: return "长按右 Alt";
                case 0x20: return "长按 Space";
                default: return "长按 VK " + vk;
            }
        }
    }

    internal static class FriendlyError
    {
        public static string From(Exception ex)
        {
            string message = ex.Message ?? "";
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
