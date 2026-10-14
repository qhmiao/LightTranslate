using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LightTranslate
{
    // Captured on the input thread, before Windows processes the button event.
    // Only local window metadata APIs are used here: never send messages from a hook.
    internal sealed class SelectionSurface
    {
        internal IntPtr Window, Root;
        internal Native.POINT Point;
        internal Native.RECT Bounds;
        internal bool Client;

        internal static SelectionSurface Capture(Native.POINT point)
        { return Capture(Native.WindowFromPoint(point), point); }

        internal static SelectionSurface Capture(IntPtr window, Native.POINT point)
        {
            var value = new SelectionSurface { Window = window, Point = point, Root = Native.GetAncestor(window, 2) };
            Native.RECT client;
            var local = point;
            var name = new StringBuilder(64);
            Native.GetClassName(window, name, name.Capacity);
            value.Client = window != IntPtr.Zero && value.Root != IntPtr.Zero
                && Native.GetWindowRect(value.Root, out value.Bounds)
                && GetClientRect(window, out client) && ScreenToClient(window, ref local)
                && local.x >= client.left && local.x < client.right && local.y >= client.top && local.y < client.bottom
                && !string.Equals(name.ToString(), "ScrollBar", StringComparison.OrdinalIgnoreCase)
                && !name.ToString().StartsWith("WindowsForms10.SCROLLBAR.", StringComparison.OrdinalIgnoreCase);
            return value;
        }

        // Run on the dispatcher only after a potential selection gesture. A hung source
        // must not hold up the input hook or cause us to copy arbitrary focused text.
        internal static bool AllowsSelection(SelectionSurface start, SelectionSurface end)
        {
            if (start == null || end == null || !start.Client || !end.Client
                || start.Root != end.Root || !Equal(start.Bounds, end.Bounds)) return false;
            Native.RECT current;
            if (!Native.GetWindowRect(start.Root, out current) || !Equal(start.Bounds, current)) return false;
            return ClientHit(start) && ClientHit(end);
        }

        private static bool ClientHit(SelectionSurface surface)
        {
            // Custom title bars can lie inside the client rectangle but report HTCAPTION.
            if (!Hit(surface.Root, surface.Point, false)) return false;
            return surface.Window == surface.Root || Hit(surface.Window, surface.Point, true);
        }

        private static bool Hit(IntPtr window, Native.POINT point, bool child)
        {
            IntPtr result;
            var position = new IntPtr(unchecked((int)(((uint)point.y & 0xffff) << 16 | ((uint)point.x & 0xffff))));
            if (SendMessageTimeout(window, 0x84, IntPtr.Zero, position, 0x22, 25, out result) == IntPtr.Zero) return false;
            return result.ToInt64() == 1 || (child && result.ToInt64() == -1); // HTCLIENT / transparent child
        }

        internal static bool Equal(Native.RECT a, Native.RECT b)
        { return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom; }

        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Native.RECT rect);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Native.POINT point);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
            IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
