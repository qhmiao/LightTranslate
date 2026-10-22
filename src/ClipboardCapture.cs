using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal sealed class ClipboardSnapshot
    {
        internal uint Sequence;
        internal readonly List<KeyValuePair<uint, byte[]>> Formats = new List<KeyValuePair<uint, byte[]>>();
    }

    internal enum RestoreResult { Restored, Changed, Busy, Failed }

    internal interface ISelectionClipboard
    {
        uint Sequence { get; }
        bool TrySnapshot(out ClipboardSnapshot snapshot);
        bool TryReadText(IntPtr source, uint expected, out uint sequence, out string text);
        RestoreResult Restore(ClipboardSnapshot snapshot, uint expected, Func<bool> valid);
    }

    internal static class ClipboardCapture
    {
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        internal static async Task<string> Read(ISelectionClipboard clipboard, IntPtr source,
            Func<bool> valid, Action copy, Action<string> trace, Func<Task> wait, Func<Task<string>> withoutClipboard = null,
            ClipboardCapturePolicy policy = null)
        {
            // Do not queue stale selections or snapshot another capture's temporary clipboard.
            if (!gate.Wait(0)) { trace("copy skipped busy"); return null; }
            try
            {
                ClipboardSnapshot saved;
                if (!valid()) return null;
                uint initial = clipboard.Sequence;
                if (policy != null && policy.RequiredSequence.HasValue && policy.RequiredSequence.Value != initial)
                    throw new ClipboardBackupException(ClipboardBackupFailure.Changed, initial);
                if (!clipboard.TrySnapshot(out saved))
                {
                    trace("clipboard snapshot skipped");
                    if (!valid()) return null;
                    if (policy != null)
                    {
                        var diagnostics = clipboard as IClipboardBackupDiagnostics;
                        var reason = diagnostics == null ? ClipboardBackupFailure.Unreadable : diagnostics.BackupFailure;
                        uint current = clipboard.Sequence;
                        if (initial == 0 || current != initial) reason = ClipboardBackupFailure.Changed;
                        trace("clipboard backup reason=" + reason);
                        if (policy.AllowOverwrite && ClipboardBackupException.MayOverwrite(reason))
                            return await ReadWithoutRestore(clipboard, source, current, valid, copy, trace, wait);
                        throw new ClipboardBackupException(reason, current);
                    }
                    if (withoutClipboard == null || !valid()) return null;
                    string selected = await withoutClipboard();
                    return valid() ? selected : null;
                }
                if (!valid() || clipboard.Sequence != saved.Sequence) return null;
                if (policy != null && policy.RequiredSequence.HasValue && saved.Sequence != policy.RequiredSequence.Value)
                    throw new ClipboardBackupException(ClipboardBackupFailure.Changed, clipboard.Sequence);
                copy();
                for (int i = 0; i < 12; i++)
                {
                    await wait();
                    if (!valid()) return null;
                    uint observed = clipboard.Sequence;
                    if (observed == saved.Sequence) continue;
                    uint copied;
                    string text;
                    if (!clipboard.TryReadText(source, observed, out copied, out text)) return null;
                    // Read and restore each check ownership/version while holding the clipboard lock.
                    for (int retry = 0; retry < 3; retry++)
                    {
                        var result = clipboard.Restore(saved, copied, valid);
                        if (result != RestoreResult.Busy)
                        {
                            if (result == RestoreResult.Changed) { trace("clipboard changed before restore; result discarded"); return null; }
                            if (result == RestoreResult.Failed) trace("clipboard restore failed");
                            return valid() ? text : null;
                        }
                        await wait();
                    }
                    trace("clipboard restore busy");
                    return valid() ? text : null;
                }
                return null;
            }
            finally { gate.Release(); }
        }

        private static async Task<string> ReadWithoutRestore(ISelectionClipboard clipboard, IntPtr source, uint before,
            Func<bool> valid, Action copy, Action<string> trace, Func<Task> wait)
        {
            if (!valid()) return null;
            if (before == 0 || clipboard.Sequence != before)
                throw new ClipboardBackupException(ClipboardBackupFailure.Changed, clipboard.Sequence);
            // Do not clear the clipboard first. A failed Ctrl+C must never return the old data.
            // This path has no backup and must never call Restore, even after cancellation.
            trace("clipboard overwrite authorized");
            try { copy(); }
            catch (OperationCanceledException) { throw; }
            catch { if (!valid()) return null; throw new SelectionUnavailableException("未能向源应用发送本次复制，未读取旧剪贴板。原剪贴板可能已改变。"); }
            for (int i = 0; i < 32; i++)
            {
                await wait();
                if (!valid()) return null;
                uint observed = clipboard.Sequence;
                if (observed == before) continue;
                uint copied; string text;
                if (!clipboard.TryReadText(source, observed, out copied, out text) || copied != observed || clipboard.Sequence != copied)
                    throw new SelectionUnavailableException("剪贴板已变化或复制来源无法确认，已取消本次取词；未翻译旧内容。");
                if (!valid()) return null;
                if (string.IsNullOrWhiteSpace(text))
                    throw new SelectionUnavailableException("这次复制没有产生可读取的新文字，未翻译旧内容。原剪贴板可能已改变。");
                return text;
            }
            throw new SelectionUnavailableException("这次复制超时，没有收到可验证的新文字；未翻译旧内容。原剪贴板可能已改变。");
        }
    }

    // Read only documented HGLOBAL text formats. Never materialize IDataObject/OLE objects.
    // Unknown formats reject the entire snapshot before Ctrl+C, rather than losing rich data.
    internal sealed class NativeSelectionClipboard : ISelectionClipboard, IClipboardBackupDiagnostics, IDisposable
    {
        internal const int MaxFormatBytes = 8 * 1024 * 1024;
        private const int MaxSnapshotBytes = 16 * 1024 * 1024;
        private readonly Forms.NativeWindow window = new Forms.NativeWindow();
        internal IntPtr Handle { get { return window.Handle; } }
        public uint Sequence { get { return Native.GetClipboardSequenceNumber(); } }
        public ClipboardBackupFailure BackupFailure { get; private set; }

        internal NativeSelectionClipboard()
        {
            window.CreateHandle(new Forms.CreateParams { Caption = "LightTranslate clipboard", Parent = new IntPtr(-3) });
        }
        public void Dispose() { window.DestroyHandle(); }

        internal static bool Supported(uint format)
        {
            if (format == 1 || format == 7 || format == 13 || format == 16) return true;
            if (format < 0xc000) return false;
            var name = new StringBuilder(256);
            if (ClipboardNative.GetClipboardFormatName(format, name, name.Capacity) == 0) return false;
            return name.ToString() == "HTML Format" || name.ToString() == "Rich Text Format";
        }

        internal static bool TryFormats(out List<uint> formats)
        { ClipboardBackupFailure reason; return TryFormats(out formats, out reason); }
        private static bool TryFormats(out List<uint> formats, out ClipboardBackupFailure reason)
        {
            reason = ClipboardBackupFailure.None;
            formats = new List<uint>();
            uint format = 0;
            while (true)
            {
                ClipboardNative.SetLastError(0);
                format = ClipboardNative.EnumClipboardFormats(format);
                if (format == 0) { if (Marshal.GetLastWin32Error() != 0) { reason = ClipboardBackupFailure.Unreadable; return false; } return true; }
                if (formats.Count >= 64) { reason = ClipboardBackupFailure.TooLarge; return false; }
                if (!Supported(format)) { reason = ClipboardBackupFailure.UnsupportedFormat; return false; }
                if (formats.Contains(format)) { reason = ClipboardBackupFailure.Unreadable; return false; }
                formats.Add(format);
            }
        }

        internal static bool TryBytes(uint format, out byte[] bytes)
        { ClipboardBackupFailure reason; return TryBytes(format, out bytes, out reason); }
        private static bool TryBytes(uint format, out byte[] bytes, out ClipboardBackupFailure reason)
        {
            reason = ClipboardBackupFailure.Unreadable;
            bytes = null;
            IntPtr handle = ClipboardNative.GetClipboardData(format);
            if (handle == IntPtr.Zero) return false;
            ulong size = ClipboardNative.GlobalSize(handle).ToUInt64();
            if (size > MaxFormatBytes) { reason = ClipboardBackupFailure.TooLarge; return false; }
            if (size == 0) return false;
            IntPtr pointer = ClipboardNative.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            try { bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); reason = ClipboardBackupFailure.None; return true; }
            finally { ClipboardNative.GlobalUnlock(handle); }
        }

        public bool TrySnapshot(out ClipboardSnapshot snapshot)
        {
            snapshot = null;
            BackupFailure = ClipboardBackupFailure.None;
            if (!ClipboardNative.OpenClipboard(Handle)) { BackupFailure = ClipboardBackupFailure.Busy; return false; }
            try
            {
                var saved = new ClipboardSnapshot { Sequence = Sequence };
                if (saved.Sequence == 0) { BackupFailure = ClipboardBackupFailure.Changed; return false; }
                List<uint> formats;
                ClipboardBackupFailure reason;
                if (!TryFormats(out formats, out reason)) { BackupFailure = reason; return false; }
                int total = 0;
                foreach (uint format in formats)
                {
                    byte[] bytes;
                    if (!TryBytes(format, out bytes, out reason)) { BackupFailure = reason; return false; }
                    if ((total += bytes.Length) > MaxSnapshotBytes) { BackupFailure = ClipboardBackupFailure.TooLarge; return false; }
                    saved.Formats.Add(new KeyValuePair<uint, byte[]>(format, bytes));
                }
                // Rendering a delayed format may change the sequence or advertise more formats.
                List<uint> after;
                if (saved.Sequence != Sequence || !TryFormats(out after, out reason) || !formats.SequenceEqual(after))
                { BackupFailure = ClipboardBackupFailure.Changed; return false; }
                snapshot = saved;
                return true;
            }
            finally { ClipboardNative.CloseClipboard(); }
        }

        internal static string DecodeUnicode(byte[] bytes)
        {
            if (bytes == null || bytes.Length % 2 != 0) return null;
            for (int i = 0; i < bytes.Length; i += 2)
                if (bytes[i] == 0 && bytes[i + 1] == 0) return Encoding.Unicode.GetString(bytes, 0, i);
            return null;
        }

        public bool TryReadText(IntPtr source, uint expected, out uint sequence, out string text)
        {
            sequence = 0; text = null;
            if (!ClipboardNative.OpenClipboard(Handle)) return false;
            try
            {
                if (Sequence != expected || !Native.SameApp(Native.GetClipboardOwner(), source)) return false;
                byte[] bytes;
                // A non-text copy still needs restoration; never fall back to OLE conversion.
                if (ClipboardNative.IsClipboardFormatAvailable(13) && TryBytes(13, out bytes)) text = DecodeUnicode(bytes);
                sequence = Sequence;
                return sequence != 0 && sequence == expected && Native.SameApp(Native.GetClipboardOwner(), source);
            }
            finally { ClipboardNative.CloseClipboard(); }
        }

        internal static IntPtr Allocate(byte[] bytes)
        {
            IntPtr handle = ClipboardNative.GlobalAlloc(0x42, new UIntPtr((uint)bytes.Length));
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            IntPtr pointer = ClipboardNative.GlobalLock(handle);
            if (pointer == IntPtr.Zero) { ClipboardNative.GlobalFree(handle); return IntPtr.Zero; }
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { ClipboardNative.GlobalUnlock(handle); }
            return handle;
        }

        public RestoreResult Restore(ClipboardSnapshot snapshot, uint expected, Func<bool> valid)
        {
            // Allocate everything before EmptyClipboard: allocation failure must not clear data.
            var handles = new List<IntPtr>();
            try
            {
                foreach (var item in snapshot.Formats)
                {
                    IntPtr handle = Allocate(item.Value);
                    if (handle == IntPtr.Zero) return RestoreResult.Failed;
                    handles.Add(handle);
                }
                if (!ClipboardNative.OpenClipboard(Handle)) return RestoreResult.Busy;
                try
                {
                    if (!valid() || expected == 0 || Sequence != expected) return RestoreResult.Changed;
                    if (!ClipboardNative.EmptyClipboard()) return RestoreResult.Failed;
                    bool complete = true;
                    for (int i = 0; i < handles.Count; i++)
                    {
                        if (ClipboardNative.SetClipboardData(snapshot.Formats[i].Key, handles[i]) == IntPtr.Zero) complete = false;
                        else handles[i] = IntPtr.Zero; // Ownership transferred to Windows.
                    }
                    return complete ? RestoreResult.Restored : RestoreResult.Failed;
                }
                finally { ClipboardNative.CloseClipboard(); }
            }
            finally { foreach (IntPtr handle in handles) if (handle != IntPtr.Zero) ClipboardNative.GlobalFree(handle); }
        }
    }

    internal static class ClipboardNative
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint EnumClipboardFormats(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClipboardFormatName(uint format, StringBuilder name, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
        [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
        [DllImport("user32.dll")] internal static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("kernel32.dll")] internal static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
    }
}
