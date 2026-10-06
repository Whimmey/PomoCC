using System.Drawing;

namespace PomodoroSupervisor
{
    /// <summary>
    /// 统一视觉：配色、字体、圆角。
    /// 约定：这里的所有「尺寸」都是 96 DPI 设计像素，运行时由 Dpi.Walk 统一缩放；
    /// 只有绘制代码里的半径/线宽才用 Dpi.Px() 现算，避免被缩放两次。
    /// </summary>
    public static class Theme
    {
        // 背景与层次
        public static readonly Color Bg = Color.FromArgb(243, 244, 246);
        public static readonly Color Card = Color.White;
        public static readonly Color CardAlt = Color.FromArgb(249, 250, 251);
        public static readonly Color Border = Color.FromArgb(228, 230, 234);
        public static readonly Color BorderStrong = Color.FromArgb(209, 213, 219);
        public static readonly Color Hover = Color.FromArgb(244, 245, 247);
        public static readonly Color Pressed = Color.FromArgb(235, 237, 240);

        // 文字
        public static readonly Color Text = Color.FromArgb(28, 32, 38);
        public static readonly Color SubText = Color.FromArgb(132, 139, 148);
        public static readonly Color FaintText = Color.FromArgb(168, 174, 182);

        // 主色（番茄红）
        public static readonly Color Accent = Color.FromArgb(226, 74, 61);
        public static readonly Color AccentHover = Color.FromArgb(206, 62, 50);
        public static readonly Color AccentPressed = Color.FromArgb(186, 52, 42);
        public static readonly Color AccentSoft = Color.FromArgb(253, 239, 237);

        public static readonly Color Danger = Color.FromArgb(203, 56, 48);
        public static readonly Color DangerSoft = Color.FromArgb(253, 237, 236);
        public static readonly Color Ok = Color.FromArgb(38, 156, 108);
        public static readonly Color Warn = Color.FromArgb(196, 126, 24);
        public static readonly Color WarnSoft = Color.FromArgb(255, 246, 230);

        // 可点链接（用户要求：蓝色 + 下划线）
        public static readonly Color Link = Color.FromArgb(37, 99, 235);
        public static readonly Color LinkActive = Color.FromArgb(29, 78, 216);

        // 字体（point 会随 DPI 正确渲染，不要再乘缩放系数）
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font BodySmall = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Bold = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font SectionTitle = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font PageTitle = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Timer = new Font("Segoe UI", 34f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Button = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font ButtonStrong = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);

        /// <summary>把颜色调亮/调暗，用于 hover / pressed。</summary>
        public static Color Shift(Color c, int delta)
        {
            int r = c.R + delta;
            int g = c.G + delta;
            int b = c.B + delta;
            if (r < 0) r = 0; if (r > 255) r = 255;
            if (g < 0) g = 0; if (g > 255) g = 255;
            if (b < 0) b = 0; if (b > 255) b = 255;
            return Color.FromArgb(c.A, r, g, b);
        }
    }
}
