// LightTranslate: an automatic selection translation bubble for Windows.
// Compile with the Windows .NET Framework compiler; no extra runtime is required.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class Program
    {
        internal static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = 8;
                if (args.Length > 0 && args[0] == "--build-dictionary")
                {
                    Lexicon.Build(args[1], Folder);
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--self-test-clipboard") return ClipboardCaptureTests.RunStandalone();
                if (args.Length > 0 && args[0] == "--self-test-clipboard-native") return ClipboardNativeTests.Run();
                if (args.Length > 0 && args[0] == "--self-test-providers") return TranslationProviderTests.RunStandalone();
                if (args.Length > 0 && args[0] == "--self-test-tray") return TrayIconRenderer.RunStandalone();
                if (args.Length > 0 && args[0] == "--self-test-gestures") return SelectionGestureTests.Run();
                if (args.Length > 0 && args[0] == "--self-test-selection-context") return SelectionContextTests.Run();
                if (args.Length > 0 && args[0] == "--self-test-selection") return SelectionIntegrationTests.Run();
                if (args.Length > 0 && args[0] == "--self-test") return SelfTest.Run(false);
                if (args.Length > 0 && args[0] == "--self-test-offline") return SelfTest.Run(true);
                bool created;
                using (var single = new Mutex(true, "Local\\LightTranslate.Instance", out created))
                {
                    if (!created) return 0;
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log("ui", e.Exception.GetType().Name);
                        e.Handled = true;
                    };
                    using (var context = new Controller(app)) app.Run();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log("startup", ex.ToString());
                return 1;
            }
        }
        internal static void Log(string area, string message)
        {
            // Never log selected text or clipboard contents.
            try
            {
                var path = Path.Combine(Folder, "status.log");
                if (File.Exists(path) && new FileInfo(path).Length > 128000) File.WriteAllText(path, "");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + area + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    internal sealed class Settings
    {
        public bool Enabled = true;
        public bool ClipboardFallback = true;
        public string Target = "zh-Hans";
        public string TranslationProvider = "auto";
        public int MaxSelectionLength = 2000;
        public string[] ExcludedApps = new string[0];
        public bool Diagnostics = false;
        public string BubbleFontFamily = "Microsoft YaHei UI";
        public double BubbleFontSize = 14;
        public string BubbleBackground = "";
        public int AutoHideSeconds = 0;
        public int MinEnglishPercent = 0;
        internal event Action Changed;
        public static Settings Load()
        { return Load(Path.Combine(Program.Folder, "settings.json")); }
        internal static Settings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path));
                    if (loaded != null) { loaded.NormalizeReading(); return loaded; }
                }
            }
            catch { }
            return new Settings();
        }
        internal void NormalizeReading()
        {
            TranslationProvider = Translator.NormalizeProvider(TranslationProvider);
            BubbleFontFamily = BubbleAppearance.ResolveFont(BubbleFontFamily).Source;
            if (double.IsNaN(BubbleFontSize) || double.IsInfinity(BubbleFontSize) || BubbleFontSize < 10 || BubbleFontSize > 48) BubbleFontSize = 14;
            Color color;
            BubbleBackground = BubbleAppearance.TryParseBackground(BubbleBackground, out color) ? BubbleBackground.Trim().ToUpperInvariant() : "";
            if (AutoHideSeconds < 0 || AutoHideSeconds > 120) AutoHideSeconds = 0;
            if (MinEnglishPercent < 0 || MinEnglishPercent > 100) MinEnglishPercent = 0;
        }
        internal Settings Clone()
        { return new JavaScriptSerializer().Deserialize<Settings>(new JavaScriptSerializer().Serialize(this)); }
        internal void CopyReadingFrom(Settings other)
        {
            BubbleFontFamily = other.BubbleFontFamily; BubbleFontSize = other.BubbleFontSize;
            BubbleBackground = other.BubbleBackground; AutoHideSeconds = other.AutoHideSeconds;
            MinEnglishPercent = other.MinEnglishPercent;
        }
        internal void CopyUserFrom(Settings other)
        {
            Enabled = other.Enabled; ClipboardFallback = other.ClipboardFallback;
            Target = other.Target; TranslationProvider = other.TranslationProvider; MaxSelectionLength = other.MaxSelectionLength;
            ExcludedApps = other.ExcludedApps == null ? null : (string[])other.ExcludedApps.Clone();
            CopyReadingFrom(other);
            if (Changed != null) Changed();
        }
        internal bool TrySetEnabled(bool enabled, string path)
        {
            var next = Clone(); next.Enabled = enabled;
            if (!next.TrySave(path)) return false;
            CopyUserFrom(next); return true;
        }
        public void Save()
        { TrySave(Path.Combine(Program.Folder, "settings.json")); }
        internal bool TrySave(string path)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return true;
            }
            catch { return false; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }

    internal sealed class Controller : IDisposable
    {
        private readonly Application app;
        private readonly Settings settings;
        private readonly Bubble bubble;
        private readonly Translator translator;
        private readonly Lexicon lexicon;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripMenuItem enabledItem;
        private readonly DispatcherTimer timer;
        private readonly InputHooks inputHooks;
        private CancellationTokenSource pending;
        private int generation;
        private bool down, dragged, disposed;
        private Native.POINT downPoint, previousUp, anchor;
        private IntPtr downWindow, sourceWindow;
        private uint lastUpTime;
        private readonly BubbleLifetime lifetime = new BubbleLifetime();
        private SettingsWindow settingsWindow;
        private readonly Queue<string> diagnosticQueue = new Queue<string>();
        private long mouseEvents, mouseDowns, mouseUps, selections;
        private DateTime nextHeartbeat, nextHealthLog, nextSettingsCheck;

        public Controller(Application app)
        {
            this.app = app;
            settings = Settings.Load();
            lexicon = new Lexicon(Program.Folder);
            translator = new Translator();
            if (settings.TranslationProvider == "auto" || settings.TranslationProvider == "microsoft") { var warming = translator.WarmConnection(); }
            bubble = new Bubble();
            bubble.ApplySettings(settings);
            tray = new Forms.NotifyIcon { Text = "轻译 · 选中即译", Icon = MakeIcon(settings), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            enabledItem = new Forms.ToolStripMenuItem("自动划词翻译") { Checked = settings.Enabled, CheckOnClick = true };
            enabledItem.Click += delegate
            {
                if (!settings.TrySetEnabled(enabledItem.Checked, Path.Combine(Program.Folder, "settings.json")))
                {
                    enabledItem.Checked = settings.Enabled;
                    tray.ShowBalloonTip(4000, "轻译", "无法保存自动翻译开关，请检查应用目录是否可写。", Forms.ToolTipIcon.Error);
                    return;
                }
                Dismiss(true);
            };
            menu.Items.Add(enabledItem);
            menu.Items.Add("设置…", null, delegate { OpenSettings(); });
            menu.Items.Add("打开设置文件", null, delegate { Process.Start("notepad.exe", Path.Combine(Program.Folder, "settings.json")); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出轻译", null, delegate { app.Shutdown(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { enabledItem.PerformClick(); };
            settings.Save();
            inputHooks = new InputHooks(app.Dispatcher, HandleMouse, HandleKeyboard);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += delegate { CheckDismissal(); FlushDiagnostics(); };
            timer.Start();
            Program.Log("started", "hooks=dedicated-thread recovery=enabled capture=isolated-context clipboard=text-fallback dictionary=" + lexicon.Count);
        }

        private void OpenSettings()
        {
            Dismiss(true);
            if (settingsWindow != null)
            {
                if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
                settingsWindow.Activate(); return;
            }
            settingsWindow = new SettingsWindow(settings, delegate { enabledItem.Checked = settings.Enabled; Dismiss(true); bubble.ApplySettings(settings); TrayIconRenderer.Update(tray, settings); });
            settingsWindow.Closed += delegate { settingsWindow = null; };
            settingsWindow.Show();
        }

        internal static System.Drawing.Icon MakeIcon(Settings value = null)
        { return TrayIconRenderer.Create(value ?? new Settings()); }

        private SelectionSurface downSurface;

        private void HandleMouse(int message, Native.MOUSE data, IntPtr capturedWindow, SelectionSurface surface)
        {
            if (!disposed)
            {
                mouseEvents++;
                // Remote input, accessibility tools, and mouse utilities may inject human actions.
                // Only ignore input generated by this application, rather than all injected input.
                if (data.extra != Native.OwnInputMarker)
                {
                    if (message == 0x201)
                    {
                        mouseDowns++;
                        bool inBubble = bubble.Contains(data.pt);
                        if (!inBubble)
                        {
                            Dismiss(false);
                            down = settings.Enabled;
                            dragged = false;
                            downPoint = data.pt;
                            downSurface = surface;
                            downWindow = surface == null ? IntPtr.Zero : surface.Root;
                        }
                    }
                    else if (message == 0x200 && down)
                    {
                        if (Math.Abs(data.pt.x - downPoint.x) >= 5 || Math.Abs(data.pt.y - downPoint.y) >= 5) dragged = true;
                    }
                    else if (message == 0x202 && down)
                    {
                        mouseUps++;
                        down = false;
                        bool eligible = SelectionSurface.AllowsSelection(downSurface, surface);
                        bool twice = eligible && lastUpTime != 0 && unchecked(data.time - lastUpTime) <= Native.GetDoubleClickTime()
                            && Math.Abs(data.pt.x - previousUp.x) <= Native.GetSystemMetrics(36)
                            && Math.Abs(data.pt.y - previousUp.y) <= Native.GetSystemMetrics(37);
                        previousUp = data.pt;
                        lastUpTime = eligible ? data.time : 0;
                        IntPtr window = capturedWindow;
                        Trace("mouse-up eligible=" + eligible + " drag=" + dragged + " double=" + twice + " same-window=" + (window == downWindow) + " same-app=" + Native.SameApp(window, downWindow) + " injected=" + ((data.flags & 1) != 0));
                        if (settings.Enabled && eligible && (dragged || twice) && (window == downWindow || Native.SameApp(window, downWindow)) && !bubble.Contains(data.pt))
                        {
                            var point = data.pt;
                            var origin = downSurface;
                            int queuedVersion = generation;
                            app.Dispatcher.BeginInvoke(new Action(delegate { if (queuedVersion == generation) BeginSelection(window, point, origin); }));
                        }
                    }
                    else if (message == 0x20A || message == 0x20E || message == 0x204 || message == 0x207)
                    {
                        if (!bubble.Contains(data.pt)) Dismiss(false);
                    }
                }
            }
        }

        private void HandleKeyboard(int message, Native.KEYBOARD data, IntPtr capturedWindow)
        {
            if (!disposed)
            {
                if (data.extra != Native.OwnInputMarker)
                {
                    if (message == 0x100 || message == 0x104) Dismiss(false);
                    if ((message == 0x100 || message == 0x104) && data.vkCode == 0x1B)
                    { down = false; lastUpTime = 0; } // Escape cancels an in-progress drag.
                    if ((message == 0x101 || message == 0x105) && (data.vkCode == 0xA0 || data.vkCode == 0xA1) && settings.Enabled)
                    {
                        Native.POINT point;
                        Native.GetCursorPos(out point);
                        var window = capturedWindow;
                        int queuedVersion = generation;
                        app.Dispatcher.BeginInvoke(new Action(delegate { if (queuedVersion == generation) BeginSelection(window, point); }));
                    }
                }
            }
        }

        private void Dismiss(bool immediate)
        {
            generation++;
            if (pending != null) { pending.Cancel(); pending = null; }
            if (bubble.IsVisible) bubble.Dismiss(immediate);
            lifetime.Reset();
        }

        private async void BeginSelection(IntPtr window, Native.POINT point, SelectionSurface origin = null)
        {
            selections++;
            Dismiss(true);
            if (!settings.Enabled || window == IntPtr.Zero) return;
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            if (pid == Process.GetCurrentProcess().Id) return;
            string processName;
            try { using (var process = Process.GetProcessById((int)pid)) processName = process.ProcessName; }
            catch { return; }
            Trace("selection app=" + processName + " pid=" + pid);
            if (settings.ExcludedApps != null && settings.ExcludedApps.Any(x => string.Equals(Path.GetFileNameWithoutExtension(x), processName, StringComparison.OrdinalIgnoreCase))) return;
            int version = generation;
            var cancellation = new CancellationTokenSource();
            pending = cancellation;
            CancellationToken ct = cancellation.Token;
            try
            {
                await Task.Delay(25, ct);
                if (!Valid(version, window)) return;
                // Inspect text context in the isolated helper before any clipboard access.
                // The main process never owns UIAutomationClient objects.
                if (!settings.ClipboardFallback || SelectionReader.IsTerminal(processName)) return;
                string text = await SelectionReader.CopySelection(window, ct, Trace, point, origin);
                Trace("selection chars=" + (text == null ? 0 : text.Length));
                if (!Valid(version, window) || string.IsNullOrWhiteSpace(text)) { Trace("selection discarded valid=" + Valid(version, window)); return; }
                text = text.Trim();
                if (!SelectionReader.ShouldTranslate(text, settings.MinEnglishPercent)) { Trace("selection language threshold skipped"); return; }
                anchor = point;
                sourceWindow = window;
                if (text.Length > settings.MaxSelectionLength)
                {
                    ShowText("选中文字较长，请分段选择", point, 5);
                    return;
                }
                string provider = Translator.NormalizeProvider(settings.TranslationProvider);
                string target = settings.Target;
                string translated = provider == "auto" ? lexicon.Lookup(text) : null;
                if (translated != null || translator.TryCached(text, target, out translated, provider))
                {
                    ShowText(translated, point, ReadingSeconds(translated), true);
                    return;
                }
                var request = translator.Translate(text, target, ct, provider);
                if (await Task.WhenAny(request, Task.Delay(220, ct)) != request && Valid(version, window))
                    ShowText("…", point, 8);
                translated = await request;
                if (!Valid(version, window)) return;
                ShowText(translated, point, ReadingSeconds(translated), true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Program.Log("translate", ex.GetType().Name);
                bool explained = ex is DeepLException || ex is SelectionUnavailableException;
                if (Valid(version, window))
                {
                    anchor = point; sourceWindow = window;
                    ShowText(explained ? ex.Message : "暂时无法连接翻译服务", point, explained ? 6 : 3);
                }
            }
            finally
            {
                if (ReferenceEquals(pending, cancellation)) pending = null;
                cancellation.Dispose();
            }
        }
        private bool Valid(int version, IntPtr window)
        {
            return !disposed && version == generation && settings.Enabled && Native.GetForegroundWindow() == window;
        }
        private static int ReadingSeconds(string text) { return Math.Max(6, Math.Min(22, 4 + text.Length / 12)); }
        private void ShowText(string text, Native.POINT point, int seconds, bool translated = false)
        {
            lifetime.Start(DateTime.UtcNow, BubbleLifetime.DisplaySeconds(settings.AutoHideSeconds, seconds, translated));
            bubble.Present(text, point);
            Trace("bubble visible=" + bubble.IsVisible + " chars=" + text.Length);
        }
        private void Trace(string value)
        {
            if (settings.Diagnostics && diagnosticQueue.Count < 64) diagnosticQueue.Enqueue(value);
        }
        private void FlushDiagnostics()
        {
            var now = DateTime.UtcNow;
            if (now >= nextSettingsCheck)
            {
                nextSettingsCheck = now.AddSeconds(2);
                var currentSettings = Settings.Load();
                if (currentSettings != null) settings.Diagnostics = currentSettings.Diagnostics;
            }
            if (now >= nextHealthLog)
            {
                nextHealthLog = now.AddMinutes(1);
                Program.Log("health", "hook-install=" + inputHooks.InstallationVersion + " mouse=" + inputHooks.MouseEvents + " keyboard=" + inputHooks.KeyboardEvents + " selection=" + selections);
            }
            if (!settings.Diagnostics) return;
            if (DateTime.UtcNow >= nextHeartbeat)
            {
                nextHeartbeat = DateTime.UtcNow.AddSeconds(5);
                Trace("activity mouse=" + mouseEvents + " down=" + mouseDowns + " up=" + mouseUps + " selection=" + selections + " rawMouse=" + inputHooks.MouseEvents + " rawKeyboard=" + inputHooks.KeyboardEvents);
            }
            while (diagnosticQueue.Count > 0) Program.Log("debug", diagnosticQueue.Dequeue());
        }
        private void CheckDismissal()
        {
            if (!bubble.IsVisible) return;
            if (Native.GetForegroundWindow() != sourceWindow) { Dismiss(false); return; }
            Native.POINT point;
            Native.GetCursorPos(out point);
            bool near = Math.Abs(point.x - anchor.x) < 120 && Math.Abs(point.y - anchor.y) < 90;
            bool over = bubble.Contains(point);
            if (lifetime.ShouldDismiss(DateTime.UtcNow, near, over)) Dismiss(false);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Dismiss(true);
            timer.Stop();
            inputHooks.Dispose();
            tray.Visible = false;
            tray.Icon.Dispose();
            tray.Dispose();
            if (settingsWindow != null) settingsWindow.Close();
            bubble.Close();
            translator.Dispose();
            lexicon.Dispose();
        }
    }

    // Low-level callbacks must return promptly even if the WPF/clipboard STA stalls.
    // Their own message loop only captures input and queues work onto the UI thread.
    internal sealed class InputHooks : IDisposable
    {
        private const uint ProbeMessage = 0x84c0, DropTestMessage = 0x84c1;
        private readonly Dispatcher dispatcher;
        private readonly Action<int, Native.MOUSE, IntPtr, SelectionSurface> onMouse;
        private readonly Action<int, Native.KEYBOARD, IntPtr> onKeyboard;
        private readonly Native.Hook mouseCallback, keyboardCallback;
        private readonly Thread thread;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private readonly bool testMode;
        private volatile bool stopping;
        private Exception startupError;
        private uint threadId;
        private IntPtr mouseHook, keyboardHook;
        private long mouseEvents, keyboardEvents;
        private int installationVersion, probes, renewalRequested;
        private bool trackingDrag, dragForwarded;
        private Native.POINT dragStart, lastCursor;
        private uint lastInputTime;
        private long observedMouse, observedKeyboard;
        private DateTime nextRenewal;

        public long MouseEvents { get { return Interlocked.Read(ref mouseEvents); } }
        public long KeyboardEvents { get { return Interlocked.Read(ref keyboardEvents); } }
        public int InstallationVersion { get { return Interlocked.CompareExchange(ref installationVersion, 0, 0); } }
        internal int Probes { get { return Interlocked.CompareExchange(ref probes, 0, 0); } }

        public InputHooks(Dispatcher dispatcher, Action<int, Native.MOUSE, IntPtr, SelectionSurface> onMouse,
            Action<int, Native.KEYBOARD, IntPtr> onKeyboard, bool testMode = false)
        {
            this.dispatcher = dispatcher;
            this.onMouse = onMouse;
            this.onKeyboard = onKeyboard;
            this.testMode = testMode;
            mouseCallback = MouseCallback;
            keyboardCallback = KeyboardCallback;
            thread = new Thread(Run) { IsBackground = true, Name = "LightTranslate input" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(5000)) { Dispose(); throw new TimeoutException("Input thread startup timed out"); }
            if (startupError != null) { Dispose(); throw new InvalidOperationException("Input hooks could not start", startupError); }
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void Run()
        {
            UIntPtr timerId = UIntPtr.Zero;
            try
            {
                threadId = Native.GetCurrentThreadId();
                Native.MSG message;
                Native.PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                Renew("startup");
                Native.GetCursorPos(out lastCursor);
                var input = new Native.LASTINPUTINFO { size = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO)) };
                if (Native.GetLastInputInfo(ref input)) lastInputTime = input.time;
                timerId = Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, 1000, IntPtr.Zero);
                if (timerId == UIntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                ready.Set();
                while (!stopping)
                {
                    int result = Native.GetMessage(out message, IntPtr.Zero, 0, 0);
                    if (result == 0) break;
                    if (result < 0) throw new System.ComponentModel.Win32Exception();
                    if (message.message == 0x113) Maintain();
                    else if (message.message == ProbeMessage && testMode)
                    {
                        Interlocked.Increment(ref probes);
                        Queue(delegate { onMouse(0, new Native.MOUSE(), IntPtr.Zero, null); });
                    }
                    else if (message.message == DropTestMessage && testMode) RemoveHooks();
                }
            }
            catch (Exception ex)
            {
                startupError = ex;
                Log("thread-error=" + ex.GetType().Name);
                ready.Set();
            }
            finally
            {
                if (timerId != UIntPtr.Zero) Native.KillTimer(IntPtr.Zero, timerId);
                RemoveHooks();
            }
        }

        private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !stopping)
            {
                try
                {
                    var data = (Native.MOUSE)Marshal.PtrToStructure(lParam, typeof(Native.MOUSE));
                    Interlocked.Increment(ref mouseEvents);
                    if (data.extra != Native.OwnInputMarker)
                    {
                        int message = wParam.ToInt32();
                        bool forward = message != 0x200;
                        if (message == 0x201) { trackingDrag = true; dragForwarded = false; dragStart = data.pt; }
                        else if (message == 0x200 && trackingDrag && !dragForwarded
                            && (Math.Abs(data.pt.x - dragStart.x) >= 5 || Math.Abs(data.pt.y - dragStart.y) >= 5))
                        { dragForwarded = true; forward = true; }
                        else if (message == 0x202) trackingDrag = false;
                        if (forward)
                        {
                            IntPtr window = Native.GetForegroundWindow();
                            SelectionSurface surface = (message == 0x201 || message == 0x202) ? SelectionSurface.Capture(data.pt) : null;
                            Queue(delegate { onMouse(message, data, window, surface); });
                        }
                    }
                }
                catch { }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !stopping)
            {
                try
                {
                    var data = (Native.KEYBOARD)Marshal.PtrToStructure(lParam, typeof(Native.KEYBOARD));
                    Interlocked.Increment(ref keyboardEvents);
                    if (data.extra != Native.OwnInputMarker)
                    {
                        int message = wParam.ToInt32();
                        IntPtr window = Native.GetForegroundWindow();
                        Queue(delegate { onKeyboard(message, data, window); });
                    }
                }
                catch { }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void Queue(Action action)
        {
            if (stopping || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate { if (!stopping) action(); }));
        }

        private void Maintain()
        {
            var info = new Native.LASTINPUTINFO { size = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO)) };
            bool haveInput = Native.GetLastInputInfo(ref info);
            Native.POINT cursor;
            bool haveCursor = Native.GetCursorPos(out cursor);
            long mouse = MouseEvents, keyboard = KeyboardEvents;
            bool missedMouse = haveCursor && (cursor.x != lastCursor.x || cursor.y != lastCursor.y) && mouse == observedMouse;
            bool missedInput = haveInput && info.time != lastInputTime && mouse == observedMouse && keyboard == observedKeyboard;
            bool requested = Interlocked.Exchange(ref renewalRequested, 0) != 0;
            bool missing = mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero;
            bool scheduled = DateTime.UtcNow >= nextRenewal && (Native.GetAsyncKeyState(1) & 0x8000) == 0;
            if (missing || requested || missedMouse || missedInput || scheduled)
            {
                try { Renew(missing ? "missing" : requested ? "session-resume" : missedMouse || missedInput ? "missed-input" : "refresh"); }
                catch (Exception ex) { Log("renew-error=" + ex.GetType().Name); }
            }
            if (haveCursor) lastCursor = cursor;
            if (haveInput) lastInputTime = info.time;
            observedMouse = mouse;
            observedKeyboard = keyboard;
        }

        private void Renew(string reason)
        {
            var module = Native.GetModuleHandle(null);
            IntPtr newMouse = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
            IntPtr newKeyboard = Native.SetWindowsHookEx(13, keyboardCallback, module, 0);
            if (newMouse == IntPtr.Zero || newKeyboard == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (newMouse != IntPtr.Zero) Native.UnhookWindowsHookEx(newMouse);
                if (newKeyboard != IntPtr.Zero) Native.UnhookWindowsHookEx(newKeyboard);
                throw new System.ComponentModel.Win32Exception(error);
            }
            RemoveHooks();
            mouseHook = newMouse;
            keyboardHook = newKeyboard;
            nextRenewal = DateTime.UtcNow.AddSeconds(30);
            int version = Interlocked.Increment(ref installationVersion);
            if (reason != "startup" && reason != "refresh") Log("renewed reason=" + reason + " install=" + version);
        }

        private void RemoveHooks()
        {
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
            mouseHook = keyboardHook = IntPtr.Zero;
            trackingDrag = dragForwarded = false;
        }

        private void OnPowerMode(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        { if (e.Mode == Microsoft.Win32.PowerModes.Resume) Interlocked.Exchange(ref renewalRequested, 1); }
        private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        { Interlocked.Exchange(ref renewalRequested, 1); }
        private static void Log(string message)
        { ThreadPool.QueueUserWorkItem(delegate { Program.Log("hooks", message); }); }
        internal void ProbeForTest() { Native.PostThreadMessage(threadId, ProbeMessage, UIntPtr.Zero, IntPtr.Zero); }
        internal void DropForTest() { Native.PostThreadMessage(threadId, DropTestMessage, UIntPtr.Zero, IntPtr.Zero); }
        public void Dispose()
        {
            stopping = true;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (threadId != 0) Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != thread && thread.IsAlive) thread.Join(2000);
            if (!thread.IsAlive) ready.Dispose();
        }
    }

    internal static class SelectionReader
    {
        public static bool IsTerminal(string name)
        {
            return new[] { "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "mintty", "wezterm", "alacritty", "putty" }
                .Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        }
        public static bool HasForeignText(string text)
        {
            return text.Any(c => char.IsLetter(c) && !(c >= '\u3400' && c <= '\u9fff'));
        }
        internal static bool ShouldTranslate(string text, int minimumPercent)
        {
            if (string.IsNullOrWhiteSpace(text) || !HasForeignText(text)) return false;
            // Zero disables the new ratio filter, preserving the existing language guard.
            if (minimumPercent <= 0) return true;
            int english = 0, letters = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLetter(text, i))
                {
                    letters++;
                    if ((text[i] >= 'A' && text[i] <= 'Z') || (text[i] >= 'a' && text[i] <= 'z')) english++;
                }
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            }
            return english > 0 && english * 100L >= letters * (long)minimumPercent;
        }
        public static async Task<string> CopySelection(IntPtr window, CancellationToken ct, Action<string> trace, Native.POINT? point = null, SelectionSurface origin = null)
        {
            if (Native.GetForegroundWindow() != window || Native.ModifierDown()) { trace("copy skipped focus/modifier"); return null; }
            if (Native.IsPasswordFocus(window)) { trace("password field skipped"); return null; }
            Func<bool> valid = delegate { return !ct.IsCancellationRequested && Native.GetForegroundWindow() == window && !Native.ModifierDown(); };
            return await SelectionAcquisition.Read(
                delegate { return IsolatedSelectionReader.ReadResult(window, ct, point: point, origin: origin); },
                async delegate(SelectionReadResult result)
                {
                    using (var clipboard = new NativeSelectionClipboard())
                        return await ClipboardCapture.Read(clipboard, window, valid, Native.Copy, trace,
                            delegate { return Task.Delay(15); }, delegate { return Task.FromResult(result.ForCapture()); });
                }, valid);
        }
    }

    internal sealed class Bubble : Window
    {
        private readonly Border card;
        private readonly TextBlock content;
        private int hideVersion;
        public Bubble()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            content = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 14,
                LineHeight = 23,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
                Foreground = new SolidColorBrush(Color.FromRgb(36, 40, 48))
            };
            var scroller = new ScrollViewer
            {
                Content = content, MaxHeight = 322,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false
            };
            card = new Border
            {
                Child = scroller, Padding = new Thickness(15, 10, 15, 11),
                Background = new SolidColorBrush(Color.FromRgb(252, 252, 253)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(35, 40, 48, 64)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, Opacity = 0.15, ShadowDepth = 3 }
            };
            Content = new Border { Padding = new Thickness(12), Child = card };
            ApplySettings(new Settings());
            SourceInitialized += delegate
            {
                var handle = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x08000000 | 0x80);
                HwndSource.FromHwnd(handle).AddHook(delegate(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
                {
                    if (message == 0x21) { handled = true; return new IntPtr(3); }
                    return IntPtr.Zero;
                });
            };
        }
        internal void ApplySettings(Settings settings)
        {
            var background = BubbleAppearance.Background(settings.BubbleBackground);
            content.FontFamily = BubbleAppearance.ResolveFont(settings.BubbleFontFamily);
            content.FontSize = settings.BubbleFontSize;
            content.LineHeight = settings.BubbleFontSize * 23 / 14;
            content.Foreground = new SolidColorBrush(BubbleAppearance.Foreground(background));
            card.Background = new SolidColorBrush(background);
            card.BorderBrush = new SolidColorBrush(BubbleAppearance.Border(background));
        }
        public bool Contains(Native.POINT p)
        {
            if (!IsVisible) return false;
            Native.RECT bounds;
            if (!Native.GetWindowRect(new WindowInteropHelper(this).Handle, out bounds)) return false;
            return p.x >= bounds.left && p.x < bounds.right && p.y >= bounds.top && p.y < bounds.bottom;
        }
        public void Present(string text, Native.POINT point)
        {
            hideVersion++;
            bool wasVisible = IsVisible;
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            // Move the hidden HWND first so WPF adopts the destination monitor's DPI.
            if (!wasVisible) Native.SetWindowPos(handle, IntPtr.Zero, point.x, point.y, 0, 0, 0x15);
            content.Text = text;
            ((FrameworkElement)Content).Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = ((FrameworkElement)Content).DesiredSize;
            double scaleX = 1, scaleY = 1;
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                scaleX = source.CompositionTarget.TransformToDevice.M11;
                scaleY = source.CompositionTarget.TransformToDevice.M22;
            }
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(point.x, point.y)).WorkingArea;
            double width = desired.Width * scaleX, height = desired.Height * scaleY;
            double x = point.x + 12, y = point.y + 18;
            if (x + width > screen.Right) x = point.x - width - 8;
            if (y + height > screen.Bottom) y = point.y - height - 8;
            x = Math.Max(screen.Left + 2, Math.Min(x, screen.Right - width - 2));
            y = Math.Max(screen.Top + 2, Math.Min(y, screen.Bottom - height - 2));
            Left = x / scaleX; Top = y / scaleY;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!wasVisible)
            {
                Show();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(85)));
            }
            Native.SetWindowPos(handle, IntPtr.Zero, (int)x, (int)y, 0, 0, 0x15);
        }
        public void Dismiss(bool immediate)
        {
            if (!IsVisible) return;
            int version = ++hideVersion;
            if (immediate) { BeginAnimation(OpacityProperty, null); Hide(); return; }
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(90));
            fade.Completed += delegate { if (hideVersion == version) { Hide(); BeginAnimation(OpacityProperty, null); Opacity = 1; } };
            BeginAnimation(OpacityProperty, fade);
        }
        internal void RenderPreview(string file)
        {
            content.Text = "选中后直接显示译文。\n继续操作，浮层自动消失。";
            var view = (FrameworkElement)Content;
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) png.Save(stream);
        }
    }

    internal sealed class Lexicon : IDisposable
    {
        private BinaryReader data;
        private long[] offsets;
        public int Count { get { return offsets == null ? 0 : offsets.Length; } }
        private readonly Dictionary<string, string> technical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cache", "缓存；藏匿处" }, { "token", "词元；令牌；标记" }, { "prompt", "提示；提示词；促使" },
            { "context", "上下文；语境" }, { "repository", "代码仓库；存储库" }, { "commit", "提交；承诺" },
            { "branch", "分支；树枝" }, { "staging", "暂存；预发布" }, { "deployment", "部署" },
            { "rollback", "回滚；撤回" }, { "dependency", "依赖；依赖项" }, { "dependencies", "依赖项" },
            { "worktree", "工作树；独立的代码工作目录" }, { "refactor", "重构" }, { "refactoring", "重构" },
            { "clipboard", "剪贴板" }, { "hover", "悬停；盘旋" }, { "latency", "延迟" },
            { "streaming", "流式传输" }, { "fallback", "备用方式；回退" }
        };
        public Lexicon(string folder)
        {
            try
            {
                var index = Path.Combine(folder, "lexicon.idx");
                if (!File.Exists(index)) return;
                using (var reader = new BinaryReader(File.OpenRead(index)))
                {
                    int count = reader.ReadInt32();
                    offsets = new long[count];
                    for (int i = 0; i < count; i++) offsets[i] = reader.ReadInt64();
                }
                data = new BinaryReader(File.OpenRead(Path.Combine(folder, "lexicon.dat")), Encoding.UTF8);
            }
            catch { offsets = null; if (data != null) data.Dispose(); data = null; }
        }
        public string Lookup(string text)
        {
            string key = text.Trim().Trim('.', ',', '!', '?', ':', ';', '"', '“', '”', '‘', '’').ToLowerInvariant();
            if (key.Length > 80 || !Regex.IsMatch(key, "^[a-z][a-z '\\-]*$")) return null;
            string known;
            if (technical.TryGetValue(key, out known)) return known;
            if (data == null) return null;
            int low = 0, high = offsets.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                data.BaseStream.Position = offsets[middle];
                var word = data.ReadString();
                int order = string.CompareOrdinal(word, key);
                if (order == 0) return data.ReadString();
                if (order < 0) low = middle + 1;
                else high = middle - 1;
            }
            return null;
        }
        public void Dispose() { if (data != null) data.Dispose(); }
        public static void Build(string csv, string folder)
        {
            var words = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(csv, Encoding.UTF8))
            {
                parser.TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited;
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;
                var headers = parser.ReadFields();
                int wordIndex = Array.IndexOf(headers, "word"), translationIndex = Array.IndexOf(headers, "translation");
                while (!parser.EndOfData)
                {
                    var row = parser.ReadFields();
                    if (row.Length <= translationIndex) continue;
                    string word = row[wordIndex].ToLowerInvariant().Trim(), translated = row[translationIndex].Replace("\\n", "\n").Trim();
                    if (translated.Length > 0 && word.Length <= 80 && Regex.IsMatch(word, "^[a-z][a-z '\\-]*$")) words[word] = translated;
                }
            }
            using (var values = new BinaryWriter(File.Create(Path.Combine(folder, "lexicon.dat")), Encoding.UTF8))
            using (var index = new BinaryWriter(File.Create(Path.Combine(folder, "lexicon.idx"))))
            {
                index.Write(words.Count);
                foreach (var entry in words)
                {
                    index.Write(values.BaseStream.Position);
                    values.Write(entry.Key);
                    values.Write(entry.Value);
                }
            }
            File.WriteAllText(Path.Combine(folder, "dictionary-build.txt"), "ECDICT English–Chinese entries: " + words.Count);
        }
    }

    internal sealed class Translator : IDisposable
    {
        private readonly HttpClient http;
        private readonly DeepLTranslator deepl;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        private readonly Queue<string> eviction = new Queue<string>();
        public Translator() : this(null) { }
        internal Translator(HttpMessageHandler handler, IDeepLCredentials credentials = null, HttpMessageHandler deepLHandler = null)
        {
            deepl = new DeepLTranslator(credentials ?? DeepLCredentials.Current(), deepLHandler);
            http = handler == null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(7);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            http.DefaultRequestHeaders.ExpectContinue = false;
        }
        internal async Task WarmConnection()
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Head, "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hans"))
                using (var response = await http.SendAsync(request)) { }
            }
            catch { }
        }
        internal static string NormalizeProvider(string provider)
        { return provider == "microsoft" || provider == "tencent" || provider == "deepl" ? provider : "auto"; }
        private static string CacheKey(string text, string target, string provider)
        { return NormalizeProvider(provider) + "\n" + (provider == "tencent" ? "zh" : target) + "\n" + text; }
        public bool TryCached(string text, string target, out string result, string provider = "auto")
        {
            // DeepL validates the current credential before consulting its separate cache.
            if (provider == "deepl") { result = null; return false; }
            return cache.TryGetValue(CacheKey(text, target, provider), out result);
        }
        public async Task<string> Translate(string text, string target, CancellationToken ct, string provider = "auto")
        {
            ct.ThrowIfCancellationRequested();
            provider = NormalizeProvider(provider);
            if (provider == "deepl") return await deepl.Translate(text, target, ct);
            string result;
            if (TryCached(text, target, out result, provider)) return result;
            if (provider == "tencent") result = await Tencent(text, ct);
            else if (provider == "microsoft") result = await Microsoft(text, target, ct);
            else
            {
                bool fallback = false;
                result = null;
                try { result = await Microsoft(text, target, ct); }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; fallback = true; }
                catch { ct.ThrowIfCancellationRequested(); fallback = true; }
                if (fallback) result = await Tencent(text, ct);
            }
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Empty translation");
            ct.ThrowIfCancellationRequested();
            string key = CacheKey(text, target, provider);
            if (!cache.ContainsKey(key))
            {
                if (cache.Count >= 256) cache.Remove(eviction.Dequeue());
                cache[key] = result;
                eviction.Enqueue(key);
            }
            return result;
        }
        private async Task<string> Microsoft(string text, string target, CancellationToken ct)
        {
            string path = "api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + Uri.EscapeDataString(target);
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://" + path))
            {
                request.Headers.TryAddWithoutValidation("X-MT-Signature", Signature(path));
                request.Content = new StringContent(json.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var array = json.DeserializeObject(await response.Content.ReadAsStringAsync()) as object[];
                    var item = (Dictionary<string, object>)array[0];
                    var translations = (object[])item["translations"];
                    return (string)((Dictionary<string, object>)translations[0])["text"];
                }
            }
        }
        private async Task<string> Tencent(string text, CancellationToken ct)
        {
            var payload = new
            {
                header = new { fn = "auto_translation_block", client_key = "browser-chrome-110.0.0-Windows-" + Guid.NewGuid().ToString() },
                type = "plain", model_category = "normal", source = new { lang = "auto", text_block = text }, target = new { lang = "zh" }
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://transmart.qq.com/api/imt"))
            {
                request.Headers.Referrer = new Uri("https://yi.qq.com/zh-CN/index");
                request.Content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var item = json.DeserializeObject(await response.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                    return (string)item["auto_translation"];
                }
            }
        }
        private static string Signature(string path)
        {
            // Adapted from STranslate's MIT-licensed built-in Microsoft translator.
            // See licenses/STranslate-LICENSE.txt and THIRD_PARTY_NOTICES.md.
            byte[] key = { 0xa2,0x29,0x3a,0x3d,0xd0,0xdd,0x32,0x73,0x97,0x7a,0x64,0xdb,0xc2,0xf3,0x27,0xf5,0xd7,0xbf,0x87,0xd9,0x45,0x9d,0xf0,0x5a,0x09,0x66,0xc6,0x30,0xc6,0x6a,0xaa,0x84,0x9a,0x41,0xaa,0x94,0x3a,0xa8,0xd5,0x1a,0x6e,0x4d,0xaa,0xc9,0xa3,0x70,0x12,0x35,0xc7,0xeb,0x12,0xf6,0xe8,0x23,0x07,0x9e,0x47,0x10,0x95,0x91,0x88,0x55,0xd8,0x17 };
            string guid = Guid.NewGuid().ToString("N");
            string date = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ssG\\MT", System.Globalization.CultureInfo.InvariantCulture);
            string value = ("MSTranslatorAndroidApp" + Uri.EscapeDataString(path) + date + guid).ToLowerInvariant();
            using (var hmac = new HMACSHA256(key))
                return "MSTranslatorAndroidApp::" + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))) + "::" + date + "::" + guid;
        }
        public void Dispose() { http.Dispose(); deepl.Dispose(); }
    }

    internal static class Native
    {
        public delegate IntPtr Hook(int code, IntPtr wParam, IntPtr lParam);
        public static readonly UIntPtr OwnInputMarker = new UIntPtr(0x4C54524Eu);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct MSG
        {
            public IntPtr window;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT point;
            public uint privateData;
        }
        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint size, time; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSE { public POINT pt; public uint mouseData, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBOARD { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO
        {
            public uint size, flags;
            public IntPtr active, focus, capture, menuOwner, moveSize, caret;
            public RECT caretRect;
        }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION union; }
        [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] public static extern int GetMessage(out MSG message, IntPtr window, uint minimum, uint maximum);
        [DllImport("user32.dll", EntryPoint = "PeekMessageW")] public static extern bool PeekMessage(out MSG message, IntPtr window, uint minimum, uint maximum, uint remove);
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] public static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr window, UIntPtr id);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        public static bool SameProcess(IntPtr first, IntPtr second)
        {
            if (first == IntPtr.Zero || second == IntPtr.Zero) return false;
            uint p1, p2;
            GetWindowThreadProcessId(first, out p1);
            GetWindowThreadProcessId(second, out p2);
            return p1 != 0 && p1 == p2;
        }
        public static bool SameApp(IntPtr first, IntPtr second)
        {
            if (first == IntPtr.Zero || second == IntPtr.Zero) return false;
            uint p1, p2;
            GetWindowThreadProcessId(first, out p1);
            GetWindowThreadProcessId(second, out p2);
            if (p1 == p2 && p1 != 0) return true;
            try
            {
                using (var a = Process.GetProcessById((int)p1))
                using (var b = Process.GetProcessById((int)p2))
                    return string.Equals(a.MainModule.FileName, b.MainModule.FileName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        public static bool IsPasswordFocus(IntPtr window)
        {
            uint pid;
            uint thread = GetWindowThreadProcessId(window, out pid);
            var info = new GUITHREADINFO { size = (uint)Marshal.SizeOf(typeof(GUITHREADINFO)) };
            return GetGUIThreadInfo(thread, ref info) && IsPasswordEdit(info.focus);
        }
        internal static bool IsPasswordEdit(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            return name.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0
                && (GetWindowLong(window, -16) & 0x20) != 0;
        }
        public static bool ModifierDown() { return (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0; }
        public static void Copy()
        {
            INPUT[] keys = new INPUT[4];
            for (int i = 0; i < 4; i++)
            {
                keys[i].type = 1;
                keys[i].union.keyboard.vk = (ushort)((i == 0 || i == 3) ? 0x11 : 0x43);
                keys[i].union.keyboard.flags = (uint)(i >= 2 ? 2 : 0);
                keys[i].union.keyboard.extra = OwnInputMarker;
            }
            if (SendInput(4, keys, Marshal.SizeOf(typeof(INPUT))) != 4) throw new System.ComponentModel.Win32Exception();
        }
    }

    internal static class SelfTest
    {
        public static int Run(bool offline)
        {
            Program.Log("self-test", "started");
            var results = new Dictionary<string, object>();
            using (var lexicon = new Lexicon(Program.Folder))
            {
                var sw = Stopwatch.StartNew();
                string word = lexicon.Lookup("reversible");
                sw.Stop();
                if (word == null || !word.Contains("逆")) throw new Exception("Lexicon lookup failed");
                if (lexicon.Lookup("a completely unknown sentence goes here") != null) throw new Exception("Lexicon false positive");
                results["dictionaryEntries"] = lexicon.Count;
                results["dictionaryLookupMs"] = sw.Elapsed.TotalMilliseconds;
                results["dictionaryText"] = word;
            }
            ClipboardCaptureTests.Run(results);
            if (!SelectionReader.IsTerminal("pwsh") || SelectionReader.IsTerminal("Codex")) throw new Exception("Terminal guard failed");
            if (SelectionReader.HasForeignText("这是中文") || !SelectionReader.HasForeignText("Read this text")) throw new Exception("Language guard failed");
            results["guards"] = "terminal + language passed";
            TrayIconRenderer.Test(results);
            ReadingSettingsTests.Run(results);
            UnifiedSettingsTests.Run(results);
            TranslationProviderTests.Run(results);
            Program.Log("self-test", "dictionary and clipboard snapshot passed");
            if (System.Reflection.Assembly.GetExecutingAssembly().GetReferencedAssemblies().Any(a => a.Name == "UIAutomationClient"))
                throw new Exception("Unsafe UIA client dependency remains");
            using (var password = new Forms.TextBox { UseSystemPasswordChar = true })
            using (var plain = new Forms.TextBox())
            {
                if (!Native.IsPasswordEdit(password.Handle) || Native.IsPasswordEdit(plain.Handle)) throw new Exception("Password guard failed");
            }
            results["capture"] = "clipboard only; no UIAutomationClient dependency";
            results["passwordGuard"] = "native password/plain controls passed";
            Program.Log("self-test", "password guard passed; collecting objects");
            // OLE finalizers can marshal back to the STA: pump while awaiting them.
            var finalizers = Task.Run(delegate { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!finalizers.IsCompleted && DateTime.UtcNow < deadline)
            {
                Forms.Application.DoEvents();
                Thread.Sleep(5);
            }
            if (!finalizers.IsCompleted) throw new TimeoutException("Finalizer test timed out");
            finalizers.GetAwaiter().GetResult();
            results["finalizers"] = "passed";
            int forwarded = 0;
            var dispatcher = Dispatcher.CurrentDispatcher;
            using (var hooks = new InputHooks(dispatcher,
                delegate(int message, Native.MOUSE data, IntPtr window, SelectionSurface surface) { if (message == 0) forwarded++; },
                delegate(int message, Native.KEYBOARD data, IntPtr window) { }, true))
            {
                // Keep the WPF/clipboard STA blocked while probing the real input thread.
                hooks.ProbeForTest();
                var probeDeadline = DateTime.UtcNow.AddSeconds(2);
                while (hooks.Probes == 0 && DateTime.UtcNow < probeDeadline) Thread.Sleep(10);
                if (hooks.Probes == 0) throw new Exception("Input thread stalled with the UI thread");
                if (forwarded != 0) throw new Exception("Input handling ran outside the UI dispatcher");
                int installed = hooks.InstallationVersion;
                hooks.DropForTest();
                var recoveryDeadline = DateTime.UtcNow.AddSeconds(4);
                while (hooks.InstallationVersion <= installed && DateTime.UtcNow < recoveryDeadline) Thread.Sleep(10);
                if (hooks.InstallationVersion <= installed) throw new Exception("Removed input hooks were not recovered");
                Thread.Sleep(1200);
                hooks.ProbeForTest();
                probeDeadline = DateTime.UtcNow.AddSeconds(2);
                while (hooks.Probes < 2 && DateTime.UtcNow < probeDeadline) Thread.Sleep(10);
                if (hooks.Probes < 2) throw new Exception("Input thread stopped during a UI stall");
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
                if (forwarded < 2) throw new Exception("Captured input was not forwarded to the UI dispatcher");
            }
            results["inputThread"] = "responsive while UI dispatcher blocked; deferred dispatch passed";
            results["hookRecovery"] = "native hooks removed and automatically reinstalled";
            Program.Log("self-test", "finalizers passed; testing translation");
            using (var engine = new Translator())
            {
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    bool stopped = false;
                    try { engine.Translate("Canceled request", "zh-Hans", canceled.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { stopped = true; }
                    if (!stopped) throw new Exception("Request cancellation failed");
                    results["cancellation"] = "passed";
                }
                if (!offline)
                {
                engine.WarmConnection().GetAwaiter().GetResult();
                var timings = new List<object>();
                foreach (string text in new[] { "This change improves performance.", "Select the text to see the translation.", "The window closes automatically." })
                {
                    var sw = Stopwatch.StartNew();
                    string translated = engine.Translate(text, "zh-Hans", CancellationToken.None).GetAwaiter().GetResult();
                    sw.Stop();
                    if (!translated.Any(c => c >= '\u4e00' && c <= '\u9fff')) throw new Exception("Network translation failed");
                    timings.Add(new { milliseconds = sw.ElapsedMilliseconds, text = translated });
                }
                string cached;
                if (!engine.TryCached("This change improves performance.", "zh-Hans", out cached)) throw new Exception("Cache missed");
                results["network"] = timings;
                results["cache"] = "passed";
                }
                else results["network"] = "skipped (offline self-test)";
            }
            var bubble = new Bubble();
            var handle = new WindowInteropHelper(bubble).EnsureHandle();
            if ((Native.GetWindowLong(handle, -20) & 0x08000000) == 0) throw new Exception("No-activation style missing");
            results["noActivation"] = "window style verified";
            bubble.RenderPreview(Path.Combine(Program.Folder, "preview.png"));
            File.WriteAllBytes(Path.Combine(Program.Folder, "LightTranslate.ico"), TrayIconRenderer.Encode(new Settings()));
            results["bubblePreview"] = "rendered";
            File.WriteAllText(Path.Combine(Program.Folder, "self-test.json"), new JavaScriptSerializer().Serialize(results), Encoding.UTF8);
            Program.Log("self-test", "passed");
            return 0;
        }
    }
}
