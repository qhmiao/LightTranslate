using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class SelectionContextTests
    {
        // Reproduce the metadata contract observed on real Explorer property cells:
        // MSAA text/Edit role, no TextPattern, nested under an item-selection surface.
        private sealed class PropertyCell : Forms.Control
        {
            internal bool ReadOnlyCell;
            protected override Forms.AccessibleObject CreateAccessibilityInstance() { return new PropertyAccessibility(this); }
            private sealed class PropertyAccessibility : Forms.Control.ControlAccessibleObject
            {
                private readonly PropertyCell cell;
                internal PropertyAccessibility(PropertyCell cell) : base(cell) { this.cell = cell; }
                public override Forms.AccessibleRole Role { get { return Forms.AccessibleRole.Text; } }
                public override Forms.AccessibleStates State { get { return cell.ReadOnlyCell ? Forms.AccessibleStates.ReadOnly | Forms.AccessibleStates.Focusable : Forms.AccessibleStates.None; } }
                public override string Value { get { return "synthetic property"; } set { } }
            }
        }
        private sealed class Clipboard : ISelectionClipboard
        {
            internal int Snapshots, Copies, Reads, Restores;
            internal bool CanSnapshot = true;
            private uint sequence = 10;
            public uint Sequence { get { return sequence; } }
            public bool TrySnapshot(out ClipboardSnapshot saved)
            { Snapshots++; saved = new ClipboardSnapshot { Sequence = sequence }; return CanSnapshot; }
            internal void Copy() { Copies++; sequence++; }
            public bool TryReadText(IntPtr source, uint expected, out uint version, out string text)
            { Reads++; version = sequence; text = "reversible"; return true; }
            public RestoreResult Restore(ClipboardSnapshot saved, uint expected, Func<bool> valid)
            { Restores++; return RestoreResult.Restored; }
        }

        private static string Acquire(SelectionReadResult result, Clipboard clipboard)
        {
            return SelectionIntegrationTests.Pump(SelectionAcquisition.Read(delegate { return Task.FromResult(result); },
                delegate(SelectionReadResult inspected) { return ClipboardCapture.Read(clipboard, IntPtr.Zero, delegate { return true; },
                    clipboard.Copy, delegate { }, delegate { return Task.FromResult(0); }, delegate { return Task.FromResult(inspected.ForCapture()); }); },
                delegate { return true; }));
        }

        internal static int Run()
        {
            var passed = new List<string>();
            Action<string, bool> check = delegate(string name, bool value) { ClipboardCaptureTests.Check(value, name); passed.Add(name); };
            uint before = Native.GetClipboardSequenceNumber();
            using (var form = new Forms.Form())
            using (var list = new Forms.ListView { View = Forms.View.LargeIcon, MultiSelect = true, LabelEdit = true })
            using (var tree = new Forms.TreeView())
            using (var stale = new Forms.TextBox { Text = "stale fixture" })
            {
                form.Controls.Add(list); form.Controls.Add(tree); form.Controls.Add(stale);
                IntPtr root = form.Handle, listHandle = list.Handle, treeHandle = tree.Handle, staleHandle = stale.Handle;
                stale.SelectAll();
                Func<Forms.Control, SelectionReadResult> probe = delegate(Forms.Control control)
                { return SelectionIntegrationTests.Pump(IsolatedSelectionReader.ReadResult(root, CancellationToken.None, 2001, control.Handle)); };
                Action<string, Forms.Control> nonText = delegate(string label, Forms.Control control)
                {
                    SelectionReadResult result = probe(control);
                    check(label + " classified as non-text", result.State == SelectionReadState.NonText);
                    foreach (bool snapshotAllowed in new[] { true, false })
                    {
                        var clipboard = new Clipboard { CanSnapshot = snapshotAllowed };
                        check(label + " returns no stale text", Acquire(result, clipboard) == string.Empty);
                        check(label + " never accesses clipboard", clipboard.Snapshots == 0 && clipboard.Copies == 0 && clipboard.Reads == 0 && clipboard.Restores == 0);
                    }
                };
                nonText("desktop-style empty icon canvas", list);
                list.Items.Add("synthetic icon").Selected = true;
                nonText("selected icon drag context", list);
                list.View = Forms.View.Details; list.Columns.Add("synthetic column", 120);
                nonText("explorer-style item list", list);
                list.Items.Add("second synthetic item").Selected = true;
                nonText("multiple selected items", list);
                using (var property = new PropertyCell())
                {
                    list.Controls.Add(property); IntPtr propertyHandle = property.Handle;
                    nonText("inactive editable property inside item list", property);
                    property.ReadOnlyCell = true;
                    nonText("read-only property inside item list", property);
                }
                using (var unsupportedEdit = new PropertyCell())
                {
                    form.Controls.Add(unsupportedEdit); IntPtr unsupportedHandle = unsupportedEdit.Handle;
                    check("unsupported Edit outside item surface is not globally suppressed", probe(unsupportedEdit).State == SelectionReadState.TextUnsupported);
                }
                tree.Nodes.Add("synthetic tree item"); tree.SelectedNode = tree.Nodes[0];
                nonText("tree selection context", tree);
                using (var rename = new Forms.TextBox { Text = "reversible.txt" })
                {
                    list.Controls.Add(rename); IntPtr renameHandle = rename.Handle; rename.Select(0, 10);
                    var clipboard = new Clipboard();
                    check("inline edit inside item list remains selectable text", Acquire(probe(rename), clipboard) == "reversible");
                    check("inline edit selection avoids clipboard", clipboard.Snapshots == 0 && clipboard.Copies == 0);
                    rename.ReadOnly = true;
                    check("read-only text with TextPattern remains selectable inside list", Acquire(probe(rename), clipboard) == "reversible");
                }
                foreach (Forms.Control surface in new Forms.Control[] {
                    new Forms.Panel(), new Forms.GroupBox(), new Forms.Button { Text = "synthetic action" },
                    new Forms.Label { Text = "synthetic notification" }, new Forms.MonthCalendar(),
                    new Forms.TrackBar(), new Forms.CheckBox { Text = "synthetic setting" } })
                using (surface)
                {
                    form.Controls.Add(surface); IntPtr handle = surface.Handle;
                    SelectionReadResult result = probe(surface);
                    check(surface.GetType().Name + " has no confirmed selectable text", result.State == SelectionReadState.NonText || result.State == SelectionReadState.Unconfirmed);
                    var clipboard = new Clipboard();
                    check(surface.GetType().Name + " gesture quietly ends without stale text", Acquire(result, clipboard) == string.Empty);
                    check(surface.GetType().Name + " never touches clipboard", clipboard.Snapshots == 0 && clipboard.Copies == 0 && clipboard.Reads == 0 && clipboard.Restores == 0);
                }
                using (var panel = new Forms.Panel())
                using (var edit = new Forms.TextBox { Text = "reversible" })
                {
                    form.Controls.Add(panel); panel.Controls.Add(edit); IntPtr panelHandle = panel.Handle, editHandle = edit.Handle;
                    edit.SelectAll(); var clipboard = new Clipboard();
                    check("text edit inside generic flyout panel remains usable", Acquire(probe(edit), clipboard) == "reversible" && clipboard.Snapshots == 0);
                }
            }
            foreach (SelectionReadState state in new[] { SelectionReadState.Unconfirmed, SelectionReadState.ProbeTimeout, SelectionReadState.ProbeFailed })
            {
                var clipboard = new Clipboard();
                check(state + " quietly ends before clipboard access", Acquire(new SelectionReadResult(state), clipboard) == string.Empty && clipboard.Snapshots == 0 && clipboard.Copies == 0);
            }
            foreach (SelectionReadState state in new[] { SelectionReadState.Timeout, SelectionReadState.Failed })
            {
                var clipboard = new Clipboard(); bool error = false;
                try { Acquire(new SelectionReadResult(state), clipboard); } catch (SelectionUnavailableException) { error = true; }
                check(state + " remains an error before clipboard access", error && clipboard.Snapshots == 0 && clipboard.Copies == 0);
            }
            var supportedFallback = new Clipboard();
            check("confirmed text context can use safe clipboard fallback", Acquire(new SelectionReadResult(SelectionReadState.TextUnsupported), supportedFallback) == "reversible"
                && supportedFallback.Snapshots == 1 && supportedFallback.Copies == 1 && supportedFallback.Restores == 1);
            var protectedFallback = new Clipboard { CanSnapshot = false }; bool protectedError = false;
            try { Acquire(new SelectionReadResult(SelectionReadState.TextUnsupported), protectedFallback); } catch (SelectionUnavailableException) { protectedError = true; }
            check("unsupported text preserves unsafe clipboard and reports error", protectedError && protectedFallback.Copies == 0 && protectedFallback.Reads == 0);
            bool valid = true; int fallbackCalls = 0;
            string canceled = SelectionIntegrationTests.Pump(SelectionAcquisition.Read(
                delegate { valid = false; return Task.FromResult(new SelectionReadResult(SelectionReadState.Selected, "stale fixture")); },
                delegate { fallbackCalls++; return Task.FromResult("stale fallback"); }, delegate { return valid; }));
            check("changed source discards selection before fallback or translation", canceled == null && fallbackCalls == 0);
            var waiting = new TaskCompletionSource<SelectionReadResult>(); int probes = 0;
            Func<SelectionReadResult, Task<string>> noFallback = delegate { throw new Exception("Unexpected clipboard fallback"); };
            Task<string> first = SelectionAcquisition.Read(delegate { probes++; return waiting.Task; }, noFallback, delegate { return true; });
            string second = SelectionIntegrationTests.Pump(SelectionAcquisition.Read(delegate { probes++; return waiting.Task; }, noFallback, delegate { return true; }));
            waiting.SetResult(new SelectionReadResult(SelectionReadState.NonText, string.Empty));
            check("overlapping gestures do not launch another helper", second == null && SelectionIntegrationTests.Pump(first) == string.Empty && probes == 1);
            File.WriteAllText(Path.Combine(Program.Folder, "selection-context-test.json"), new JavaScriptSerializer().Serialize(new {
                passed, clipboardSequenceUnchanged = before == Native.GetClipboardSequenceNumber(),
                scope = "Owned hidden lists, tree, edit, panel, group, button, static label, calendar, slider and checkbox; real external UIA; no actual shell actions, notifications or settings touched"
            }));
            return 0;
        }
    }
}
