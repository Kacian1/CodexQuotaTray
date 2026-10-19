using System;
using System.Drawing;

namespace CodexQuotaTray
{
    internal static class WidgetPlacement
    {
        public static double ClampRatio(double ratio)
        {
            return double.IsNaN(ratio) || double.IsInfinity(ratio) ? 0.5 : Math.Max(0, Math.Min(1, ratio));
        }

        public static int Coordinate(int start, int end, int extent, double ratio)
        {
            var minimum = start + 4;
            var maximum = Math.Max(minimum, end - extent - 4);
            return minimum + (int)Math.Round(ClampRatio(ratio) * (maximum - minimum));
        }

        public static double Ratio(int coordinate, int start, int end, int extent)
        {
            var minimum = start + 4;
            var span = end - extent - 4 - minimum;
            return span <= 0 ? 0 : ClampRatio((coordinate - minimum) / (double)span);
        }

        /// <summary>
        /// 把数字条限制在任务栏托盘左边界之前，避免覆盖“显示隐藏的图标”箭头。
        /// trayEdge 是托盘区（TrayNotifyWnd）在拖动方向上的起点。
        /// </summary>
        public static int ClampBeforeTray(int coordinate, int taskbarStart, int trayEdge, int extent, int gap)
        {
            var maximum = Math.Max(taskbarStart + 4, trayEdge - extent - Math.Max(1, gap));
            return Math.Min(coordinate, maximum);
        }

        public static bool StartsDrag(Point origin, Point current, Size threshold)
        {
            return Math.Abs((long)current.X - origin.X) >= Math.Max(1, threshold.Width)
                || Math.Abs((long)current.Y - origin.Y) >= Math.Max(1, threshold.Height);
        }
    }
}
