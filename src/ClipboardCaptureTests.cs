using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LightTranslate
{
    internal static class ClipboardCaptureTests
    {
        internal static void Check(bool value, string message)
        { if (!value) throw new Exception("Clipboard: " + message); }

        private sealed class Fake : ISelectionClipboard
        {
            internal uint Version = 10;
            internal bool SnapshotWorks = true, ReadWorks = true, Valid = true;
            internal int Copies, Restores, RestoreCalls, Snapshots, Busy;
            internal Action OnSnapshot, OnRead, OnRestore;
            public uint Sequence { get { return Version; } }
            public bool TrySnapshot(out ClipboardSnapshot snapshot)
            {
                Snapshots++;
                snapshot = new ClipboardSnapshot { Sequence = Version };
                if (OnSnapshot != null) OnSnapshot();
                return SnapshotWorks;
            }
            public bool TryReadText(IntPtr source, uint expected, out uint sequence, out string text)
            {
                sequence = Version; text = "selected";
                if (OnRead != null) OnRead();
                return ReadWorks && Version == expected;
            }
            public RestoreResult Restore(ClipboardSnapshot snapshot, uint expected, Func<bool> valid)
            {
                RestoreCalls++;
                if (OnRestore != null) OnRestore();
                if (!valid() || expected != Version) return RestoreResult.Changed;
                if (Busy-- > 0) return RestoreResult.Busy;
                Restores++; Version++;
                return RestoreResult.Restored;
            }
            internal Task<string> Run(Func<Task> wait)
            {
                return ClipboardCapture.Read(this, IntPtr.Zero, delegate { return Valid; },
                    delegate { Copies++; Version++; }, delegate { }, wait);
            }
        }

        internal static void Run(Dictionary<string, object> results)
        {
            Func<Task> tick = delegate { return Task.FromResult(0); };
            var normal = new Fake();
            Check(normal.Run(tick).GetAwaiter().GetResult() == "selected" && normal.Copies == 1 && normal.Restores == 1, "copy and restore");
            var rejected = new Fake { SnapshotWorks = false };
            Check(rejected.Run(tick).Result == null && rejected.Copies == 0, "failed/unsupported snapshot never copies");
            var stale = new Fake(); stale.OnSnapshot = delegate { stale.Version++; };
            Check(stale.Run(tick).Result == null && stale.Copies == 0, "changed snapshot never copies");
            var canceled = new Fake(); canceled.OnSnapshot = delegate { canceled.Valid = false; };
            Check(canceled.Run(tick).Result == null && canceled.Copies == 0, "canceled snapshot never copies");
            var userCopy = new Fake(); userCopy.OnRestore = delegate { userCopy.Version++; };
            Check(userCopy.Run(tick).GetAwaiter().GetResult() == null && userCopy.Restores == 0, "newer clipboard is not overwritten and stale capture is discarded");
            var cancelAfter = new Fake(); cancelAfter.OnRead = delegate { cancelAfter.Valid = false; };
            Check(cancelAfter.Run(tick).Result == null && cancelAfter.Restores == 0, "cancellation before restore preserves clipboard");
            var wrongOwner = new Fake { ReadWorks = false };
            Check(wrongOwner.Run(tick).Result == null && wrongOwner.Restores == 0, "unverified copy never restores");
            var race = new Fake(); race.OnRead = delegate { race.Version++; };
            Check(race.Run(tick).Result == null && race.Restores == 0, "read race never restores");
            var busy = new Fake { Busy = 2 };
            Check(busy.Run(tick).Result == "selected" && busy.Restores == 1 && busy.RestoreCalls == 3, "bounded busy retries");
            var blocked = new Fake { Busy = 10 };
            blocked.Run(tick).GetAwaiter().GetResult();
            Check(blocked.RestoreCalls == 3 && blocked.Restores == 0, "busy retries stop");
            var signal = new TaskCompletionSource<int>();
            var first = new Fake(); var pending = first.Run(delegate { return signal.Task; });
            var second = new Fake();
            Check(second.Run(tick).Result == null && second.Snapshots == 0 && second.Copies == 0, "overlapping capture is skipped");
            signal.SetResult(0); pending.GetAwaiter().GetResult();
            Check(second.Run(tick).Result == "selected", "gate released for next selection");
            Check(NativeSelectionClipboard.DecodeUnicode(Encoding.Unicode.GetBytes("text\0")) == "text", "bounded Unicode decode");
            Check(NativeSelectionClipboard.DecodeUnicode(new byte[] { 65 }) == null, "odd byte count rejected");
            Check(NativeSelectionClipboard.DecodeUnicode(new byte[] { 65, 0 }) == null, "unterminated text rejected");
            results["clipboardCapture"] = "workflow/race/decoding checks passed; no system clipboard access";
        }

        internal static int RunStandalone()
        {
            var results = new Dictionary<string, object>(); Run(results);
            File.WriteAllText(Path.Combine(Program.Folder, "clipboard-test.json"), new JavaScriptSerializer().Serialize(results));
            return 0;
        }
    }
}
