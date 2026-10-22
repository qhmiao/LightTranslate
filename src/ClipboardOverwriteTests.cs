using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LightTranslate
{
    internal static class ClipboardOverwriteTests
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception("Clipboard overwrite: " + message); }
        private sealed class Fake : ISelectionClipboard, IClipboardBackupDiagnostics
        {
            internal uint Version = 10;
            internal bool SnapshotWorks, Valid = true, CopyWorks = true, SourceOwned = true;
            internal string Selected = "fresh selected text";
            internal int Copies, Restores, Reads, Snapshots, Waits;
            internal Action OnSnapshot, OnWait, OnRead, OnCopy;
            internal ClipboardBackupFailure Failure = ClipboardBackupFailure.UnsupportedFormat;
            public ClipboardBackupFailure BackupFailure { get { return Failure; } }
            public uint Sequence { get { return Version; } }
            public bool TrySnapshot(out ClipboardSnapshot snapshot)
            {
                Snapshots++; snapshot = new ClipboardSnapshot { Sequence = Version };
                if (OnSnapshot != null) OnSnapshot(); return SnapshotWorks;
            }
            public bool TryReadText(IntPtr source, uint expected, out uint sequence, out string text)
            {
                Reads++; sequence = Version; text = Selected;
                if (OnRead != null) OnRead();
                return SourceOwned && expected == Version && sequence == Version;
            }
            public RestoreResult Restore(ClipboardSnapshot snapshot, uint expected, Func<bool> valid)
            {
                if (!valid() || Version != expected) return RestoreResult.Changed;
                Restores++; Version++; return RestoreResult.Restored;
            }
            internal Task<string> Run(bool allow, uint? required = null, Func<Task> wait = null)
            {
                return ClipboardCapture.Read(this, new IntPtr(1), () => Valid,
                    delegate { Copies++; if (CopyWorks) Version++; if (OnCopy != null) OnCopy(); }, delegate { },
                    wait ?? delegate { Waits++; if (OnWait != null) OnWait(); return Task.FromResult(0); }, null,
                    new ClipboardCapturePolicy { AllowOverwrite = allow, RequiredSequence = required });
            }
        }
        private static ClipboardBackupException BackupFails(Fake fake, bool allow, uint? required = null)
        {
            try { fake.Run(allow, required).GetAwaiter().GetResult(); throw new Exception("Expected backup failure"); }
            catch (ClipboardBackupException ex) { return ex; }
        }
        private static void CaptureFails(Fake fake, string expected)
        {
            try { fake.Run(true).GetAwaiter().GetResult(); throw new Exception("Expected capture failure"); }
            catch (SelectionUnavailableException ex) { Check(ex.Message.Contains(expected), "accurate failure " + expected); }
            Check(fake.Restores == 0, "overwrite failure never restores old data");
        }
        internal static int RunStandalone()
        {
            var results = new Dictionary<string, object>(); Run(results);
            File.WriteAllText(Path.Combine(Program.Folder, "clipboard-overwrite-test.json"), new JavaScriptSerializer().Serialize(results));
            return 0;
        }
        internal static void Run(Dictionary<string, object> results)
        {
            Workflow(); Permits(); SettingsAndUi();
            results["clipboardOverwrite"] = "fake clipboard only: default refusal, reason classification, complete backup preferred, explicit overwrite without restore, fresh sequence/source checks, failed/empty copy, bounded timeout, cancellation/focus loss, user-copy races, overlapping operations and next-request recovery passed";
            results["overwriteConsent"] = "single-use source/generation/sequence/expiry permit, double-click guard, legacy default-off, explicit opt-in confirmation, reject/cancel/save/reopen/reset/failure rollback passed; risk dialog and non-activating bubble rendered without touching real clipboard";
        }
        private static void Workflow()
        {
            var safe = new Fake { SnapshotWorks = true };
            Check(safe.Run(true).Result == "fresh selected text" && safe.Restores == 1, "complete backup preferred even when overwrite enabled");
            var denied = new Fake(); var warning = BackupFails(denied, false);
            Check(warning.Reason == ClipboardBackupFailure.UnsupportedFormat && warning.CanOffer && warning.Sequence == 10 && denied.Copies == 0 && denied.Reads == 0, "default does not copy/read and can offer one-time choice");
            foreach (var reason in new[] { ClipboardBackupFailure.UnsupportedFormat, ClipboardBackupFailure.TooLarge, ClipboardBackupFailure.Unreadable })
            {
                var allowed = new Fake { Failure = reason };
                Check(allowed.Run(true).Result == "fresh selected text" && allowed.Copies == 1 && allowed.Restores == 0, "allowed stable failure " + reason);
            }
            foreach (var reason in new[] { ClipboardBackupFailure.Busy, ClipboardBackupFailure.Changed })
            {
                var blocked = new Fake { Failure = reason }; var ex = BackupFails(blocked, true);
                Check(!ex.CanOffer && blocked.Copies == 0, "busy/changed data never auto-overwritten");
            }
            var before = new Fake(); Check(BackupFails(before, true, 9).Reason == ClipboardBackupFailure.Changed && before.Snapshots == 0 && before.Copies == 0, "one-time sequence checked before snapshot");
            var during = new Fake(); during.OnSnapshot = () => during.Version++;
            Check(BackupFails(during, true).Reason == ClipboardBackupFailure.Changed && during.Copies == 0, "snapshot race cancels overwrite");
            var canceled = new Fake { Valid = false }; Check(canceled.Run(true).Result == null && canceled.Copies == 0, "canceled before capture");
            var cancelSnapshot = new Fake(); cancelSnapshot.OnSnapshot = () => cancelSnapshot.Valid = false;
            Check(cancelSnapshot.Run(true).Result == null && cancelSnapshot.Copies == 0, "canceled during snapshot");
            var lostFocus = new Fake(); lostFocus.OnCopy = () => lostFocus.Valid = false;
            Check(lostFocus.Run(true).Result == null && lostFocus.Reads == 0 && lostFocus.Restores == 0, "focus lost after Ctrl+C discards result");
            var failedCopy = new Fake { CopyWorks = false, Selected = "stale preexisting text" };
            CaptureFails(failedCopy, "超时"); Check(failedCopy.Waits == 32 && failedCopy.Reads == 0 && failedCopy.Version == 10, "copy failure never reads old clipboard and has bounded polling");
            var sendFailure = new Fake(); sendFailure.OnCopy = delegate { throw new InvalidOperationException("synthetic partial SendInput failure"); };
            CaptureFails(sendFailure, "未能向源应用发送"); Check(sendFailure.Reads == 0, "copy dispatch exception never reads stale data");
            var empty = new Fake { Selected = " " }; CaptureFails(empty, "没有产生");
            var other = new Fake(); other.OnWait = delegate { other.Version++; other.SourceOwned = false; };
            CaptureFails(other, "来源无法确认");
            var userCopy = new Fake(); userCopy.OnWait = delegate { userCopy.Version++; userCopy.Valid = false; };
            Check(userCopy.Run(true).Result == null && userCopy.Reads == 0 && userCopy.Restores == 0, "concurrent user key/copy cancels before reading");
            var readRace = new Fake(); readRace.OnRead = () => readRace.Version++;
            CaptureFails(readRace, "来源无法确认");
            var signal = new TaskCompletionSource<int>(); var first = new Fake();
            var pending = first.Run(true, null, () => signal.Task); var second = new Fake();
            Check(second.Run(true).Result == null && second.Snapshots == 0 && second.Copies == 0, "overlapping operations do not copy");
            signal.SetResult(0); Check(pending.Result == "fresh selected text", "first pending capture completes");
            Check(second.Run(true).Result == "fresh selected text", "gate reusable after completion");
            Check(ClipboardBackupException.MessageFor(ClipboardBackupFailure.TooLarge).Contains("上限") && ClipboardBackupException.MessageFor(ClipboardBackupFailure.Busy).Contains("暂时无法打开"), "failure descriptions reflect reason");
        }
        private static void Permits()
        {
            var now = DateTime.UtcNow; var source = new IntPtr(42);
            var permit = new ClipboardOverwritePermit(7, source, 100, now);
            Check(permit.TryConsume(7, source, 100, true, now) && !permit.TryConsume(7, source, 100, true, now), "one-time permission cannot be replayed");
            Check(!new ClipboardOverwritePermit(7, source, 100, now).TryConsume(8, source, 100, true, now), "new selection/settings generation invalidates offer");
            Check(!new ClipboardOverwritePermit(7, source, 100, now).TryConsume(7, new IntPtr(9), 100, true, now), "wrong foreground invalidates offer");
            Check(!new ClipboardOverwritePermit(7, source, 100, now).TryConsume(7, source, 101, true, now), "user clipboard update invalidates offer");
            Check(!new ClipboardOverwritePermit(7, source, 100, now).TryConsume(7, source, 100, true, now.AddSeconds(15)), "fixed expiry despite hover");
            Check(!new ClipboardOverwritePermit(7, source, 100, now).TryConsume(7, source, 100, false, now), "disabled capture invalidates offer");
            var invalidated = new ClipboardOverwritePermit(7, source, 100, now);
            Check(!invalidated.TryConsume(7, source, 101, true, now) && !invalidated.TryConsume(7, source, 100, true, now), "invalidated offer cannot become valid again");
        }
        private static void SettingsAndUi()
        {
            string root = Path.Combine(Program.Folder, "overwrite-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "settings.json");
                File.WriteAllText(path, "{\"TranslationProvider\":\"index\",\"IndexModel\":\"Index-Translate-9B\",\"BubbleFontSize\":18}");
                var settings = Settings.Load(path); Check(!settings.AllowClipboardOverwrite, "legacy default off");
                int confirmations = 0; bool approve = false;
                Func<bool> confirm = delegate { confirmations++; return approve; };
                var window = new SettingsWindow(settings, null, new FakeStartupRegistration(), null, confirm);
                window.SetOverwriteForTest(true); Check(!window.OverwriteForTest && confirmations == 1 && !settings.AllowClipboardOverwrite, "declined risk prompt does not enable or save");
                approve = true; window.SetOverwriteForTest(true);
                Check(window.OverwriteForTest && !settings.AllowClipboardOverwrite, "accepted confirmation remains staged until save");
                window.Close(); Check(!Settings.Load(path).AllowClipboardOverwrite, "cancel keeps disabled configuration");
                window = new SettingsWindow(settings, null, new FakeStartupRegistration(), null, confirm); window.SetOverwriteForTest(true);
                Check(window.Apply(path) && settings.AllowClipboardOverwrite && settings.Clone().AllowClipboardOverwrite, "save and runtime copy roundtrip");
                var loaded = Settings.Load(path); int count = confirmations;
                window = new SettingsWindow(loaded, null, new FakeStartupRegistration(), null, confirm);
                Check(window.OverwriteForTest && confirmations == count && loaded.IndexModel == "Index-Translate-9B" && loaded.BubbleFontSize == 18, "reopen preserves prior consent and other settings");
                window.RenderPreview(Path.Combine(Program.Folder, "settings-overwrite-enabled-preview.png"), 0);
                window.Fill(new Settings()); Check(!window.OverwriteForTest && loaded.AllowClipboardOverwrite, "reset input only"); window.Close();
                var original = new Settings(); window = new SettingsWindow(original, null, new FakeStartupRegistration(), null, confirm); window.SetOverwriteForTest(true);
                Check(!window.Apply(Path.Combine(root, "missing", "settings.json")) && !original.AllowClipboardOverwrite, "failed save cannot enable overwrite"); window.Close();
                window = new SettingsWindow(new Settings(), null, new FakeStartupRegistration(), null, delegate { throw new InvalidOperationException("synthetic confirmation failure"); });
                window.SetOverwriteForTest(true); Check(!window.OverwriteForTest && window.ErrorForTest.Contains("保持关闭"), "confirmation failure defaults to refusal"); window.Close();
                window = new SettingsWindow(new Settings(), null, new FakeStartupRegistration(), null, confirm);
                window.RenderPreview(Path.Combine(Program.Folder, "settings-overwrite-default-preview.png"), 0); window.Close();
                var dialog = new ClipboardOverwriteConfirmation(); dialog.RenderPreview(Path.Combine(Program.Folder, "overwrite-risk-confirmation-preview.png")); dialog.Close();
                var bubble = new Bubble(); int clicks = 0;
                IntPtr bubbleHandle = new System.Windows.Interop.WindowInteropHelper(bubble).EnsureHandle();
                bubble.RenderOverwritePreview(Path.Combine(Program.Folder, "overwrite-bubble-preview.png"), delegate { clicks++; });
                Check(bubble.OverwriteVisibleForTest, "one-time action visible in warning");
                bubble.InvokeOverwriteForTest(); bubble.InvokeOverwriteForTest(); Check(clicks == 1, "double click cannot authorize twice");
                bubble.Dismiss(true); bubble.InvokeOverwriteForTest(); Check(clicks == 1 && !bubble.OverwriteVisibleForTest, "dismiss revokes pending action");
                Check(bubble.OwnsSurface(new SelectionSurface { Root = bubbleHandle }) && !bubble.OwnsSurface(new SelectionSurface { Root = new IntPtr(98765) }), "queued bubble click stays classified as own UI after hiding");
                int cancels = 0;
                bubble.RenderOverwritePreview(Path.Combine(Program.Folder, "overwrite-bubble-preview.png"), delegate { clicks++; }, delegate { cancels++; bubble.Dismiss(true); });
                bubble.InvokeOverwriteCancelForTest(); bubble.InvokeOverwriteForTest();
                Check(cancels == 1 && clicks == 1, "cancel button revokes pending authorization"); bubble.Close();
                Check(ClipboardBackupException.Risk.Contains("可能丢失") && ClipboardBackupException.Risk.Contains("不保证"), "risk wording never promises history recovery");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
