using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

// A short-lived process owns every UIA/COM object. No clipboard API, file output or network.
internal static class SelectionHost
{
    private static Timer deadline;
    private static string status = "no-text-pattern";
    private static volatile bool textContext;
    [MTAThread]
    private static int Main(string[] args)
    {
        // Keep the deadline alive through COM finalization; it may only end this helper.
        deadline = new Timer(delegate { try { Console.Error.WriteLine(textContext ? "text-deadline" : "deadline"); } catch { } TerminateProcess(new IntPtr(-1), 3); }, null, 3000, Timeout.Infinite);
        try
        {
            long rootValue, focusValue; int maximum;
            if ((args.Length != 3 && args.Length != 5 && args.Length != 8) || !long.TryParse(args[0], out rootValue) || !long.TryParse(args[1], out focusValue)
                || !int.TryParse(args[2], out maximum) || maximum < 1 || maximum > 65537) return 2;
            IntPtr root = new IntPtr(rootValue), focus = new IntPtr(focusValue);
            if (root == IntPtr.Zero || focus == IntPtr.Zero || GetAncestor(focus, 2) != root) { Console.Error.WriteLine("wrong-root"); return 2; }
            // A file drag may end over an unrelated Edit or Document. Validate the
            // captured origin before inspecting the release point or keyboard focus.
            if (args.Length == 8)
            {
                long originHandle; int originX, originY;
                if (!long.TryParse(args[5], out originHandle) || !int.TryParse(args[6], out originX) || !int.TryParse(args[7], out originY)) return 2;
                IntPtr origin = new IntPtr(originHandle);
                if (GetForegroundWindow() != root || origin == IntPtr.Zero || GetAncestor(origin, 2) != root)
                { Console.Error.WriteLine("no-text-pattern"); return 0; }
                var hit = AutomationElement.FromPoint(new System.Windows.Point(originX, originY));
                // If layout/focus changed after down, the endpoint cannot authorize text
                // capture. Keep the native origin as a boundary, not an application list.
                IntPtr currentOrigin = WindowFromPoint(new POINT { x = originX, y = originY });
                // Browser UIA trees may omit a native child HWND. Use the native hit
                // boundary rather than requiring that HWND to appear in the UIA tree.
                if (hit == null || (currentOrigin != origin && !IsChild(origin, currentOrigin)))
                    hit = AutomationElement.FromHandle(origin);
                ReadAncestors(hit, root, maximum, false);
                if (!textContext)
                { Console.Error.WriteLine(status); return 0; }
                status = "no-text-pattern";
            }
            if (GetForegroundWindow() == root)
            {
                var current = AutomationElement.FocusedElement;
                if (current != null && current.Current.IsPassword) { Console.Error.WriteLine("password"); return 0; }
            }
            var element = AutomationElement.FromHandle(focus);
            if (element == null || element.Current.IsPassword) { Console.Error.WriteLine("password"); return 0; }
            string text = null;
            // Selecting web text need not move keyboard focus into that document/iframe.
            // The release point identifies the selected surface without scanning other documents.
            if (args.Length >= 5 && GetForegroundWindow() == root)
            {
                int x, y;
                if (!int.TryParse(args[3], out x) || !int.TryParse(args[4], out y)) return 2;
                var hit = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                bool inSource = hit != null && BelongsTo(hit, root);
                text = ReadAncestors(hit, root, maximum);
                if (status == "password") { Console.Error.WriteLine("password"); return 0; }
                // A successfully inspected pointer surface with no text evidence is not
                // permission to read a different control's stale keyboard selection.
                if (inSource && text == null && status != "text-unsupported") text = string.Empty;
            }
            // null means unsupported/unresolved; empty means a successful, authoritative
            // empty selection. Do not replace an empty pointer selection with stale focus text.
            if (text == null && status != "text-unsupported") text = ReadAncestors(element, root, maximum);
            // Browsers can expose their actual focused document below the native HWND.
            if (text == null && status != "text-unsupported" && GetForegroundWindow() == root)
            {
                var focused = AutomationElement.FocusedElement;
                if (focused != null && !focused.Current.IsPassword && BelongsTo(focused, root))
                    text = ReadAncestors(focused, root, maximum);
            }
            if (!string.IsNullOrEmpty(text))
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.Write(text);
                Console.Error.WriteLine("selected");
            }
            else Console.Error.WriteLine(status);
            return 0;
        }
        catch { Console.Error.WriteLine(textContext ? "text-provider-error" : "provider-error"); return 2; }
    }
    private static string ReadAncestors(AutomationElement element, IntPtr root, int maximum, bool selection = true)
    {
        // Chromium can put many layout groups between focus and its Document.
        // Stay within the source window, with a depth bound and process deadline.
        bool itemSurface = false;
        for (int i = 0; i < 32 && element != null; i++)
        {
            if (element.Current.IsPassword) { status = "password"; return null; }
            if (!BelongsTo(element, root))
            {
                // Shell UIA trees may skip the native WorkerW root. Once traversal
                // leaves our source window, an already identified item surface is final.
                if (itemSurface) { status = "non-text"; return string.Empty; }
                return null;
            }
            ControlType type = element.Current.ControlType;
            // An inline rename Edit remains text even when its parent is an icon list.
            // A web list can belong to a selectable Document: do not blacklist its app.
            if (type == ControlType.Edit || type == ControlType.Document)
            {
                bool inactiveProperty = type == ControlType.Edit && IsInactiveItemProperty(element, root);
                if (!inactiveProperty) textContext = true;
                string text = Read(element, maximum, selection);
                if (text == null)
                {
                    // Shell/property grids can expose display cells as Edit + ValuePattern.
                    // That role alone does not mean a text selection or an active editor.
                    if (inactiveProperty)
                    { status = "non-text"; return string.Empty; }
                    textContext = true;
                    status = "text-unsupported";
                }
                return text;
            }
            itemSurface |= IsItemSurface(element, type);
            if (!itemSurface)
            {
                string text = Read(element, maximum, selection);
                if (text != null) return text;
            }
            if (element.Current.NativeWindowHandle == root.ToInt64())
            {
                if (itemSurface) { status = "non-text"; return string.Empty; }
                return null;
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        if (element != null) status = "depth-limit";
        else if (itemSurface) { status = "non-text"; return string.Empty; }
        return null;
    }
    private static bool IsInactiveItemProperty(AutomationElement element, IntPtr root)
    {
        // A real active editor without TextPattern may still need the guarded clipboard
        // fallback. Read-only cells and inactive item properties must never trigger it.
        object value;
        var parent = TreeWalker.ControlViewWalker.GetParent(element);
        for (int i = 0; i < 32 && parent != null && BelongsTo(parent, root); i++)
        {
            ControlType type = parent.Current.ControlType;
            if (type == ControlType.Document || type == ControlType.Edit) return false;
            if (type == ControlType.List || type == ControlType.ListItem || type == ControlType.Tree || type == ControlType.TreeItem
                || type == ControlType.DataGrid || type == ControlType.DataItem
                || parent.TryGetCurrentPattern(SelectionPattern.Pattern, out value)
                || parent.TryGetCurrentPattern(SelectionItemPattern.Pattern, out value))
            {
                bool readOnly = element.TryGetCurrentPattern(ValuePattern.Pattern, out value) && ((ValuePattern)value).Current.IsReadOnly;
                return readOnly || !element.Current.HasKeyboardFocus || !element.Current.IsKeyboardFocusable;
            }
            if (parent.Current.NativeWindowHandle == root.ToInt64()) break;
            parent = TreeWalker.ControlViewWalker.GetParent(parent);
        }
        return false;
    }
    private static bool IsItemSurface(AutomationElement element, ControlType type)
    {
        if (type == ControlType.List || type == ControlType.ListItem || type == ControlType.Tree || type == ControlType.TreeItem
            || type == ControlType.DataGrid || type == ControlType.DataItem || type == ControlType.Header || type == ControlType.HeaderItem
            || type == ControlType.Button || type == ControlType.CheckBox || type == ControlType.RadioButton || type == ControlType.ComboBox
            || type == ControlType.ScrollBar || type == ControlType.Slider || type == ControlType.Menu || type == ControlType.MenuItem
            || type == ControlType.Tab || type == ControlType.TabItem || type == ControlType.ToolBar || type == ControlType.Image
            || type == ControlType.Separator || type == ControlType.ProgressBar) return true;
        object pattern;
        return element.TryGetCurrentPattern(SelectionPattern.Pattern, out pattern)
            || element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern);
    }
    private static bool BelongsTo(AutomationElement element, IntPtr root)
    {
        for (int i = 0; i < 32 && element != null; i++)
        {
            int handle = element.Current.NativeWindowHandle;
            if (handle != 0) return GetAncestor(new IntPtr(handle), 2) == root;
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return false;
    }
    private static string Read(AutomationElement element, int maximum, bool selection)
    {
        if (element.Current.IsPassword) { status = "password"; return string.Empty; }
        object value;
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out value)) return null;
        textContext = true;
        if (!selection) return string.Empty; // Origin inspection never fetches selected text.
        var ranges = ((TextPattern)value).GetSelection();
        if (ranges == null || ranges.Length > 8) throw new InvalidOperationException();
        status = "empty-selection";
        if (ranges.Length == 0) return string.Empty;
        var text = new StringBuilder();
        foreach (var range in ranges)
        {
            // A caret range is not a selection, even if a provider's GetText returns
            // an adjacent character. Multiple collapsed ranges must not create newlines.
            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0) continue;
            string part = range.GetText(maximum - text.Length);
            if (string.IsNullOrEmpty(part)) continue;
            if (text.Length != 0) text.Append('\n');
            if (text.Length >= maximum) break;
            text.Append(part.Substring(0, Math.Min(part.Length, maximum - text.Length)));
        }
        return text.ToString();
    }
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct POINT { internal int x, y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
}
