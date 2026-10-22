using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightTranslate
{
    internal class SelectionUnavailableException : Exception
    {
        internal SelectionUnavailableException(string message = "当前应用暂时无法读取选区，已保留剪贴板原内容。") : base(message) { }
    }
    internal enum SelectionReadState { Selected, Empty, NonText, Unconfirmed, TextUnsupported, Blocked, ProbeTimeout, ProbeFailed, Timeout, Failed }
    internal sealed class SelectionReadResult
    {
        internal SelectionReadState State;
        internal string Text;
        internal SelectionReadResult(SelectionReadState state, string text = null) { State = state; Text = text; }
        internal string ForCapture()
        {
            if (State == SelectionReadState.Empty || State == SelectionReadState.NonText || State == SelectionReadState.Blocked
                || State == SelectionReadState.Unconfirmed || State == SelectionReadState.ProbeTimeout || State == SelectionReadState.ProbeFailed) return string.Empty;
            if (State != SelectionReadState.Selected) throw new SelectionUnavailableException();
            if (Text.Length >= 65537) throw new SelectionUnavailableException("选中文字较长，请分段选择");
            return Text;
        }
    }
    internal static class SelectionAcquisition
    {
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        internal static async Task<string> Read(Func<Task<SelectionReadResult>> inspect,
            Func<SelectionReadResult, Task<string>> textFallback, Func<bool> valid)
        {
            if (!gate.Wait(0)) return null;
            try
            {
                if (!valid()) return null;
                SelectionReadResult result = await inspect();
                if (!valid()) return null;
                // Clipboard access requires positive evidence of a text context. UIA failures
                // and item selection must never send Ctrl+C to a list or stale focused control.
                if (result.State == SelectionReadState.TextUnsupported) return await textFallback(result);
                return result.ForCapture();
            }
            finally { gate.Release(); }
        }
    }
    internal static class IsolatedSelectionReader
    {
        internal static async Task<string> Read(IntPtr window, CancellationToken ct, int maximum = 65537, IntPtr focus = default(IntPtr), Native.POINT? point = null)
        { return (await ReadResult(window, ct, maximum, focus, point)).Text; }

        internal static SelectionReadResult Decode(int exitCode, string status, string text, int maximum)
        {
            if (status == "text-deadline") return new SelectionReadResult(SelectionReadState.Timeout);
            if (status == "text-provider-error" || text.Length > maximum) return new SelectionReadResult(SelectionReadState.Failed);
            if (status == "deadline") return new SelectionReadResult(SelectionReadState.ProbeTimeout);
            if (exitCode != 0) return new SelectionReadResult(SelectionReadState.ProbeFailed);
            if (status == "password") return new SelectionReadResult(SelectionReadState.Blocked, string.Empty);
            if (text.Length != 0 && status == "selected") return new SelectionReadResult(SelectionReadState.Selected, text);
            if (text.Length == 0 && status == "empty-selection") return new SelectionReadResult(SelectionReadState.Empty, string.Empty);
            if (text.Length == 0 && status == "non-text") return new SelectionReadResult(SelectionReadState.NonText, string.Empty);
            if (status == "text-unsupported") return new SelectionReadResult(SelectionReadState.TextUnsupported);
            if (status == "no-text-pattern" || status == "depth-limit") return new SelectionReadResult(SelectionReadState.Unconfirmed);
            return new SelectionReadResult(SelectionReadState.ProbeFailed);
        }

        internal static async Task<SelectionReadResult> ReadResult(IntPtr window, CancellationToken ct, int maximum = 65537, IntPtr focus = default(IntPtr), Native.POINT? point = null, SelectionSurface origin = null)
        {
            ct.ThrowIfCancellationRequested();
            if (focus == IntPtr.Zero)
            {
                uint pid;
                uint thread = Native.GetWindowThreadProcessId(window, out pid);
                var info = new Native.GUITHREADINFO { size = (uint)Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                // Missing native focus does not mean the foreground document has no UIA selection.
                focus = Native.GetGUIThreadInfo(thread, ref info) && info.focus != IntPtr.Zero ? info.focus : window;
            }
            string path = Path.Combine(Program.Folder, "LightTranslate.SelectionHost.exe");
            if (focus == IntPtr.Zero || !File.Exists(path)) { Program.Log("selection-helper", "missing-helper-or-window"); return new SelectionReadResult(SelectionReadState.Failed); }
            string arguments = window.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + focus.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + maximum.ToString(CultureInfo.InvariantCulture);
            if (point.HasValue) arguments += " " + point.Value.x.ToString(CultureInfo.InvariantCulture) + " " + point.Value.y.ToString(CultureInfo.InvariantCulture);
            if (point.HasValue && origin != null) arguments += " " + origin.Window.ToInt64().ToString(CultureInfo.InvariantCulture)
                + " " + origin.Point.x.ToString(CultureInfo.InvariantCulture) + " " + origin.Point.y.ToString(CultureInfo.InvariantCulture);
            var start = new ProcessStartInfo(path, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            try
            {
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var diagnostic = process.StandardError.ReadToEndAsync();
                    // The helper self-exits on timeout; never terminate the user's application.
                    bool exited = await Task.Run(delegate { return process.WaitForExit(3800); });
                    ct.ThrowIfCancellationRequested();
                    if (!exited) { Program.Log("selection-helper", "wait-timeout"); return new SelectionReadResult(SelectionReadState.ProbeTimeout); }
                    string status = (await diagnostic).Trim();
                    // Only fixed status codes enter logs, never provider text or selected content.
                    if (status != "selected" && status != "no-text-pattern" && status != "text-unsupported" && status != "non-text" && status != "empty-selection" && status != "password"
                        && status != "wrong-root" && status != "deadline" && status != "text-deadline" && status != "provider-error" && status != "text-provider-error" && status != "depth-limit") status = "unknown";
                    if (process.ExitCode != 0) Program.Log("selection-helper", "exit=" + process.ExitCode + " status=" + status);
                    string text = await output;
                    if (string.IsNullOrEmpty(text)) Program.Log("selection-helper", "status=" + status);
                    return Decode(process.ExitCode, status, text, maximum);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { Program.Log("selection-helper", "launch-or-pipe-error"); return new SelectionReadResult(SelectionReadState.Failed); }
        }
    }
}
