using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class SelectionIntegrationTests
    {
        // Real native edit controls and the real external UIA process; no user clipboard payload.
        private sealed class ProtectedClipboard : ISelectionClipboard
        {
            internal int Snapshots, Copies, Reads, Restores;
            public uint Sequence { get { return 1; } }
            public bool TrySnapshot(out ClipboardSnapshot snapshot) { Snapshots++; snapshot = null; return false; }
            public bool TryReadText(IntPtr source, uint expected, out uint sequence, out string text)
            { Reads++; sequence = 0; text = null; throw new Exception("Protected clipboard must not be read"); }
            public RestoreResult Restore(ClipboardSnapshot snapshot, uint expected, Func<bool> valid)
            { Restores++; throw new Exception("Protected clipboard must not be restored"); }
        }
        internal static T Pump<T>(Task<T> task)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < until) { Forms.Application.DoEvents(); Thread.Sleep(5); }
            if (!task.IsCompleted) throw new Exception("Selection integration timed out");
            return task.GetAwaiter().GetResult();
        }
        private static string Capture(ProtectedClipboard clipboard, IntPtr root, IntPtr focus)
        {
            return Pump(SelectionAcquisition.Read(
                delegate { return IsolatedSelectionReader.ReadResult(root, CancellationToken.None, 2001, focus); },
                delegate(SelectionReadResult result) { return ClipboardCapture.Read(clipboard, root, delegate { return true; },
                    delegate { clipboard.Copies++; }, delegate { }, delegate { return Task.Delay(15); },
                    delegate { return Task.FromResult(result.ForCapture()); }); }, delegate { return true; }));
        }

        private static void ExpectFailure(SelectionReadResult result, SelectionReadState state)
        {
            ClipboardCaptureTests.Check(result.State == state, "fixed helper failure classification " + state);
            bool explained = false;
            try { result.ForCapture(); } catch (SelectionUnavailableException) { explained = true; }
            ClipboardCaptureTests.Check(explained, "real failures retain error prompt " + state);
        }
        private static void ExpectUnconfirmed(SelectionReadResult result, SelectionReadState state)
        {
            ClipboardCaptureTests.Check(result.State == state && result.ForCapture() == string.Empty,
                "unconfirmed text context stays quiet with diagnostic state " + state);
        }

        private static void EmptySelectionCases(Forms.TextBoxBase edit, List<string> passed)
        {
            using (var form = new Forms.Form())
            using (edit)
            {
                edit.Text = "prefix reversible suffix";
                form.Controls.Add(edit);
                IntPtr root = form.Handle, focus = edit.Handle;
                var clipboard = new ProtectedClipboard();
                edit.Select(7, 10);
                ClipboardCaptureTests.Check(Capture(clipboard, root, focus) == "reversible", "selected word before collapse");
                foreach (string scenario in new[] { "end-of-document empty drag result", "middle collapsed caret", "empty document" })
                {
                    if (scenario == "empty document") edit.Text = string.Empty;
                    edit.Select(scenario == "middle collapsed caret" ? 5 : edit.TextLength, 0);
                    SelectionReadResult result = Pump(IsolatedSelectionReader.ReadResult(root, CancellationToken.None, 2001, focus));
                    ClipboardCaptureTests.Check(result.State == SelectionReadState.Empty, "successful empty selection classified " + scenario);
                    string selected = Capture(clipboard, root, focus);
                    ClipboardCaptureTests.Check(selected == string.Empty && !SelectionReader.ShouldTranslate(selected, 25), "empty selection silently ends without stale text " + scenario);
                    passed.Add(edit.GetType().Name + ": " + scenario);
                }
                edit.Text = "prefix reversible suffix"; edit.Select(7, 10);
                ClipboardCaptureTests.Check(Capture(clipboard, root, focus) == "reversible", "normal selection still works after empty gestures");
                ClipboardCaptureTests.Check(clipboard.Snapshots == 0 && clipboard.Copies == 0 && clipboard.Reads == 0 && clipboard.Restores == 0, "empty gestures never snapshot, copy or read stale clipboard");
            }
        }
        internal static int Run()
        {
            uint before = Native.GetClipboardSequenceNumber();
            var results = new Dictionary<string, object>();
            var emptyCases = new List<string>();
            EmptySelectionCases(new Forms.TextBox { Multiline = true }, emptyCases);
            EmptySelectionCases(new Forms.RichTextBox(), emptyCases);
            ExpectUnconfirmed(IsolatedSelectionReader.Decode(0, "no-text-pattern", "", 20), SelectionReadState.Unconfirmed);
            ExpectUnconfirmed(IsolatedSelectionReader.Decode(3, "deadline", "", 20), SelectionReadState.ProbeTimeout);
            ExpectUnconfirmed(IsolatedSelectionReader.Decode(2, "provider-error", "", 20), SelectionReadState.ProbeFailed);
            ExpectUnconfirmed(IsolatedSelectionReader.Decode(2, "empty-selection", "", 20), SelectionReadState.ProbeFailed);
            ExpectUnconfirmed(IsolatedSelectionReader.Decode(0, "unknown", "", 20), SelectionReadState.ProbeFailed);
            ExpectFailure(IsolatedSelectionReader.Decode(3, "text-deadline", "", 20), SelectionReadState.Timeout);
            ExpectFailure(IsolatedSelectionReader.Decode(2, "text-provider-error", "", 20), SelectionReadState.Failed);
            ExpectFailure(IsolatedSelectionReader.Decode(0, "text-unsupported", "", 20), SelectionReadState.TextUnsupported);
            using (var unsupported = new Forms.Form())
                ExpectUnconfirmed(Pump(IsolatedSelectionReader.ReadResult(unsupported.Handle, CancellationToken.None, 2001, unsupported.Handle)), SelectionReadState.Unconfirmed);
            using (var form = new Forms.Form())
            using (var text = new Forms.TextBox { Multiline = true, Text = "prefix reversible suffix" })
            {
                form.Controls.Add(text);
                IntPtr root = form.Handle, focus = text.Handle;
                text.Select(7, 10);
                var clipboard = new ProtectedClipboard();
                string selected = Capture(clipboard, root, focus);
                ClipboardCaptureTests.Check(selected == "reversible", "real edit selection through protected clipboard fallback");
                ClipboardCaptureTests.Check(clipboard.Copies == 0 && clipboard.Reads == 0 && clipboard.Restores == 0, "protected clipboard untouched");
                ClipboardCaptureTests.Check(SelectionReader.ShouldTranslate(selected, 25), "selected English passes real language gate");
                using (var lexicon = new Lexicon(Program.Folder))
                    ClipboardCaptureTests.Check((lexicon.Lookup(selected) ?? "").Contains("逆"), "selected text reaches local dictionary without network");
                text.Select(0, 0);
                ClipboardCaptureTests.Check(string.IsNullOrEmpty(Pump(IsolatedSelectionReader.Read(root, CancellationToken.None, 2001, focus))), "empty selection stays empty");
                text.Select(0, text.TextLength);
                ClipboardCaptureTests.Check(Pump(IsolatedSelectionReader.Read(root, CancellationToken.None, 8, focus)).Length == 8, "bounded selection output");
                text.UseSystemPasswordChar = true;
                ClipboardCaptureTests.Check(string.IsNullOrEmpty(Pump(IsolatedSelectionReader.Read(root, CancellationToken.None, 2001, text.Handle))), "password selection rejected");
                using (var other = new Forms.Form())
                    ClipboardCaptureTests.Check(string.IsNullOrEmpty(Pump(IsolatedSelectionReader.Read(other.Handle, CancellationToken.None, 2001, text.Handle))), "wrong root rejected");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool stopped = false;
                    try { Pump(IsolatedSelectionReader.Read(root, canceled.Token, 2001, text.Handle)); }
                    catch (OperationCanceledException) { stopped = true; }
                    ClipboardCaptureTests.Check(stopped, "canceled selection rejected");
                }
            }
            results["selectionIntegration"] = "real Windows edit selection -> external UIA helper -> context gate -> language gate -> local dictionary; empty/password/wrong-root/canceled/length cases passed; no network or clipboard contents accessed";
            results["emptySelectionCases"] = emptyCases;
            results["failureClassification"] = "unconfirmed context and probe failures stay quiet with fixed diagnostics; confirmed text deadline, provider error and unsupported text retain errors; successful empty selections are silent";
            results["clipboardSequenceUnchanged"] = before == Native.GetClipboardSequenceNumber();
            File.WriteAllText(Path.Combine(Program.Folder, "selection-integration-test.json"), new JavaScriptSerializer().Serialize(results));
            return 0;
        }
    }
}
