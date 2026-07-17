using System;
using System.Windows;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Keeps a restored window on-screen. Window positions are persisted to config, so a
    /// window dragged (mostly) off-screen — or one left on a monitor that has since been
    /// unplugged — would otherwise come back unreachable, recoverable only by hand-editing
    /// the config. This clamps a proposed position against the current virtual desktop so a
    /// grabbable strip of the window always stays visible.
    /// </summary>
    internal static class WindowPlacement
    {
        // How many DIPs of the window must remain on-screen so the title bar stays grabbable.
        private const double MinVisible = 80;

        /// <summary>
        /// Returns a Left/Top adjusted so at least <see cref="MinVisible"/> of the window
        /// stays within the virtual desktop and the title bar never sits above the top edge.
        /// SystemParameters.VirtualScreen* and Window.Left/Top are both in DIPs, so no DPI
        /// conversion is needed. Non-finite inputs are returned unchanged.
        /// </summary>
        public static (double Left, double Top) Clamp(double left, double top, double width, double height)
        {
            if (!IsFinite(left) || !IsFinite(top))
                return (left, top);
            if (!IsFinite(width) || width <= 0) width = MinVisible;
            if (!IsFinite(height) || height <= 0) height = MinVisible;

            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vRight = vLeft + SystemParameters.VirtualScreenWidth;
            double vBottom = vTop + SystemParameters.VirtualScreenHeight;

            // Horizontal: never let more than (width - MinVisible) slide past either edge.
            double minLeft = vLeft - (width - MinVisible);
            double maxLeft = vRight - MinVisible;
            left = Clamp(left, minLeft, maxLeft);

            // Vertical: the title bar must stay reachable, so never above the topmost edge,
            // and never so low that only a sliver remains at the bottom.
            double minTop = vTop;
            double maxTop = vBottom - MinVisible;
            top = Clamp(top, minTop, maxTop);

            return (left, top);
        }

        /// <summary>Clamps a restored window's Left/Top in place. Call after applying saved bounds.</summary>
        public static void ClampToVisibleArea(Window window)
        {
            double width = IsFinite(window.Width) ? window.Width : window.ActualWidth;
            double height = IsFinite(window.Height) ? window.Height : window.ActualHeight;
            var (left, top) = Clamp(window.Left, window.Top, width, height);
            window.Left = left;
            window.Top = top;
        }

        private static double Clamp(double value, double min, double max)
            => min > max ? min : Math.Max(min, Math.Min(max, value));

        private static bool IsFinite(double v) => double.IsFinite(v);
    }
}
