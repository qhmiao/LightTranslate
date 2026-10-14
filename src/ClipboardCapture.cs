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
            Func<bool> valid, Action copy, Action<string> trace, Func<Task> wait, Func<Task<string>> withoutClipboard = null)
        {
            // Do not queue stale selections or snapshot another capture's temporary clipboard.
            if (!gate.Wait(0)) { trace("copy skipped busy"); return null; }
            try
            {
                ClipboardSnapshot saved;
                if (!valid()) return null;
                if (!clipboard.TrySnapshot(out saved))
                {
                    trace("clipboard snapshot skipped");
                    if (withoutClipboard == null || !valid()) return null;
                    string selected = await withoutClipboard();
                    return valid() ? selected : null;
                }
                if (!valid() || clipboard.Sequence != saved.Sequence) return null;
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
    }

    // Read only documented HGLOBAL text formats. Never materialize IDataObject/OLE objects.
    // Unknown formats reject the entire snapshot before Ctrl+C, rather than losing rich data.
    internal sealed class NativeSelectionClipboard : ISelectionClipboard, IDisposable
    {
        internal const int MaxFormatBytes = 8 * 1024 * 1024;
        private const int MaxSnapshotBytes = 16 * 1024 * 1024;
        private readonly Forms.NativeWindow window = new Forms.NativeWindow();
        internal IntPtr Handle { get { return window.Handle; } }
        public uint Sequence { get { return Native.GetClipboardSequenceNumber(); } }

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
        {
            formats = new List<uint>();
            uint format = 0;
            while (true)
            {
                ClipboardNative.SetLastError(0);
                format = ClipboardNative.EnumClipboardFormats(format);
                if (format == 0) return Marshal.GetLastWin32Error() == 0;
                if (formats.Count >= 64 || !Supported(format) || formats.Contains(format)) return false;
                formats.Add(format);
            }
        }

        internal static bool TryBytes(uint format, out byte[] bytes)
        {
            bytes = null;
            IntPtr handle = ClipboardNative.GetClipboardData(format);
            if (handle == IntPtr.Zero) return false;
            ulong size = ClipboardNative.GlobalSize(handle).ToUInt64();
            if (size == 0 || size > MaxFormatBytes) return false;
            IntPtr pointer = ClipboardNative.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            try { bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return true; }
            finally { ClipboardNative.GlobalUnlock(handle); }
        }

        public bool TrySnapshot(out ClipboardSnapshot snapshot)
        {
            snapshot = null;
            if (!ClipboardNative.OpenClipboard(Handle)) return false;
            try
            {
                var saved = new ClipboardSnapshot { Sequence = Sequence };
                if (saved.Sequence == 0) return false;
                List<uint> formats;
                if (!TryFormats(out formats)) return false;
                int total = 0;
                foreach (uint format in formats)
                {
                    byte[] bytes;
                    if (!TryBytes(format, out bytes) || (total += bytes.Length) > MaxSnapshotBytes) return false;
                    saved.Formats.Add(new KeyValuePair<uint, byte[]>(format, bytes));
                }
                // Rendering a delayed format may change the sequence or advertise more formats.
                List<uint> after;
                if (saved.Sequence != Sequence || !TryFormats(out after) || !formats.SequenceEqual(after)) return false;
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
