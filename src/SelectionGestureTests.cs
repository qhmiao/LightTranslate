using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class SelectionGestureTests
    {
        private sealed class CaptionForm : Forms.Form
        {
            internal bool CustomCaption;
            protected override void WndProc(ref Forms.Message m)
            {
                if (CustomCaption && m.Msg == 0x84)
                {
                    long packed = m.LParam.ToInt64();
                    var point = PointToClient(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
                    if (point.Y >= 0 && point.Y < 35) { m.Result = new IntPtr(2); return; }
                }
                base.WndProc(ref m);
            }
        }

        internal static int Run()
        {
            uint clipboardBefore = Native.GetClipboardSequenceNumber();
            var passed = new List<string>();
            using (var form = new CaptionForm { Size = new Size(500, 350), StartPosition = Forms.FormStartPosition.Manual, Location = new Point(100, 100) })
            using (var edit = new Forms.TextBox { Multiline = true, Bounds = new Rectangle(10, 50, 350, 170), Text = "prefix reversible suffix" })
            using (var scroll = new Forms.VScrollBar { Bounds = new Rectangle(370, 50, 20, 170) })
            {
                // Own hidden real HWNDs only. No global mouse injection, user text or clipboard payload.
                form.Controls.Add(edit); form.Controls.Add(scroll);
                IntPtr root = form.Handle, editHandle = edit.Handle, scrollHandle = scroll.Handle;
                Func<Forms.Control, int, int, SelectionSurface> at = delegate(Forms.Control control, int x, int y)
                {
                    Point point = control.PointToScreen(new Point(x, y));
                    return SelectionSurface.Capture(control.Handle, new Native.POINT { x = point.X, y = point.Y });
                };
                Action<string, bool> check = delegate(string name, bool value) { ClipboardCaptureTests.Check(value, name); passed.Add(name); };
                var textStart = at(edit, 10, 10); var textEnd = at(edit, 100, 10);
                check("real edit drag accepted", SelectionSurface.AllowsSelection(textStart, textEnd));
                check("real edit double click surface accepted", SelectionSurface.AllowsSelection(textStart, textStart));
                Native.RECT bounds; Native.GetWindowRect(root, out bounds);
                Point origin = form.PointToScreen(Point.Empty);
                var titlePoint = new Native.POINT { x = bounds.left + 100, y = bounds.top + (origin.Y - bounds.top) / 2 };
                var title = SelectionSurface.Capture(root, titlePoint);
                // Reproduce the old classifier: a >5 px same-window drag was sufficient.
                check("old classifier accepts title drag", Math.Abs(titlePoint.x + 20 - titlePoint.x) >= 5 && title.Root == root);
                check("title drag rejected before capture", !SelectionSurface.AllowsSelection(title, textEnd));
                check("title double click rejected", !SelectionSurface.AllowsSelection(title, title));
                var border = SelectionSurface.Capture(root, new Native.POINT { x = bounds.right - 1, y = bounds.top + 150 });
                check("resize border rejected before capture", !SelectionSurface.AllowsSelection(border, textEnd));
                check("scrollbar drag rejected before capture", !SelectionSurface.AllowsSelection(at(scroll, 5, 10), at(scroll, 5, 80)));
                ShowScrollBar(editHandle, 3, true);
                Native.RECT editBounds; Native.GetWindowRect(editHandle, out editBounds);
                var vertical = SelectionSurface.Capture(editHandle, new Native.POINT { x = editBounds.right - 5, y = editBounds.top + 60 });
                var horizontal = SelectionSurface.Capture(editHandle, new Native.POINT { x = editBounds.left + 60, y = editBounds.bottom - 5 });
                check("edit nonclient vertical scrollbar rejected", !SelectionSurface.AllowsSelection(vertical, vertical));
                check("edit nonclient horizontal scrollbar rejected", !SelectionSurface.AllowsSelection(horizontal, horizontal));
                check("drag ending in title rejected", !SelectionSurface.AllowsSelection(textStart, title));
                form.CustomCaption = true;
                var caption = at(form, 100, 15);
                check("custom caption inside client rejected", caption.Client && !SelectionSurface.AllowsSelection(caption, textEnd));
                check("custom title bar leaves edit selection working", SelectionSurface.AllowsSelection(textStart, textEnd));
                form.CustomCaption = false;
                form.Location = new Point(150, 150);
                check("client initiated window move rejected", !SelectionSurface.AllowsSelection(textStart, at(edit, 100, 10)));
                var beforeResize = at(edit, 10, 10);
                form.Size = new Size(550, 400);
                check("client initiated resize rejected", !SelectionSurface.AllowsSelection(beforeResize, at(edit, 100, 10)));
                check("fresh selection after move and resize accepted", SelectionSurface.AllowsSelection(at(edit, 10, 10), at(edit, 100, 10)));
                using (var other = new Forms.Form())
                    check("cross window gesture rejected", !SelectionSurface.AllowsSelection(at(edit, 10, 10), at(other, 20, 20)));
                check("missing geometry rejected", !SelectionSurface.AllowsSelection(null, textEnd));
            }
            File.WriteAllText(Path.Combine(Program.Folder, "gesture-integration-test.json"), new JavaScriptSerializer().Serialize(new {
                passed, clipboardSequenceUnchanged = clipboardBefore == Native.GetClipboardSequenceNumber(),
                scope = "Owned hidden native controls and real window metadata/hit tests; synthetic gesture endpoints; no physical user-app drag automated"
            }));
            return 0;
        }

        [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
    }
}
