using System;
using System.Threading;

namespace LightTranslate
{
    internal enum ClipboardBackupFailure { None, UnsupportedFormat, TooLarge, Unreadable, Busy, Changed }
    internal interface IClipboardBackupDiagnostics
    { ClipboardBackupFailure BackupFailure { get; } }

    internal sealed class ClipboardCapturePolicy
    {
        internal bool AllowOverwrite;
        internal uint? RequiredSequence;
    }

    internal sealed class ClipboardBackupException : SelectionUnavailableException
    {
        internal readonly ClipboardBackupFailure Reason;
        internal readonly uint Sequence;
        internal bool CanOffer { get { return Sequence != 0 && MayOverwrite(Reason); } }
        internal const string Risk = "覆盖后，原剪贴板中的图片、文字或应用专用数据可能丢失。Windows 剪贴板历史不保证能够恢复。能完整备份时，仍按原方式取词后恢复。";
        internal ClipboardBackupException(ClipboardBackupFailure reason, uint sequence) : base(MessageFor(reason))
        { Reason = reason; Sequence = sequence; }
        internal static bool MayOverwrite(ClipboardBackupFailure reason)
        { return reason == ClipboardBackupFailure.UnsupportedFormat || reason == ClipboardBackupFailure.TooLarge || reason == ClipboardBackupFailure.Unreadable; }
        internal static string MessageFor(ClipboardBackupFailure reason)
        {
            switch (reason)
            {
                case ClipboardBackupFailure.UnsupportedFormat: return "当前剪贴板含有无法完整保留的数据格式，尚未执行复制。";
                case ClipboardBackupFailure.TooLarge: return "当前剪贴板数据超过安全备份上限，尚未执行复制。";
                case ClipboardBackupFailure.Busy: return "当前剪贴板暂时无法打开，尚未执行复制。请稍后重新选词。";
                case ClipboardBackupFailure.Changed: return "剪贴板已被其他操作更新，已取消本次取词。请重新选择文字。";
                default: return "当前剪贴板无法完整读取和备份，尚未执行复制。";
            }
        }
    }

    // No selected text is retained. A button authorizes one stable source/clipboard/generation only.
    internal sealed class ClipboardOverwritePermit
    {
        private int consumed;
        internal readonly int Generation;
        internal readonly IntPtr Source;
        internal readonly uint Sequence;
        internal readonly DateTime Expires;
        internal ClipboardOverwritePermit(int generation, IntPtr source, uint sequence, DateTime now)
        { Generation = generation; Source = source; Sequence = sequence; Expires = now.AddSeconds(15); }
        internal bool TryConsume(int generation, IntPtr foreground, uint sequence, bool enabled, DateTime now)
        {
            if (Interlocked.Exchange(ref consumed, 1) != 0) return false;
            return enabled && generation == Generation && Source != IntPtr.Zero && foreground == Source
                && Sequence != 0 && sequence == Sequence && now < Expires;
        }
    }
}
