using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PomoCC
{
    /// <summary>
    /// DPI 缩放。
    ///
    /// 为什么不用 WinForms 自带的 AutoScaleMode.Dpi：
    /// 在 .NET Framework 4.7+ 且启用 PerMonitorV2 时，框架会在加载前把
    /// AutoScaleDimensions 改写成当前 DPI，缩放系数被算成 1.0 —— 实测在 125% 的
    /// 屏幕上窗口仍是 96 DPI 的尺寸，只有字体变大了，布局对不上。
    /// 所以这里自己算系数，按设计基准（96 DPI）显式缩放控件树；
    /// 字号用 point，本身就会随 DPI 正确渲染，不需要也不应该再乘系数。
    /// </summary>
    public static class Dpi
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private static float scale = -1f;

        /// <summary>当前缩放系数（96 DPI = 1.0）。</summary>
        public static float Scale
        {
            get
            {
                if (scale < 0)
                {
                    float s = 1f;
                    try
                    {
                        uint dpi = GetDpiForSystem();
                        if (dpi >= 72) s = dpi / 96f;
                    }
                    catch { }
                    scale = s;
                }
                return scale;
            }
        }

        public static float ForWindow(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 72) return dpi / 96f;
            }
            catch { }
            return Scale;
        }

        public static int Px(float design)
        {
            return (int)Math.Round(design * Scale);
        }

        /// <summary>每个窗体已经按哪个系数缩放过，避免重复缩放。</summary>
        private static readonly System.Collections.Generic.Dictionary<Form, float> applied =
            new System.Collections.Generic.Dictionary<Form, float>();

        /// <summary>在窗体构造完成后调用：按当前 DPI 缩放整棵控件树，并挂上跨显示器缩放。</summary>
        public static void ApplyTo(Form form)
        {
            form.AutoScaleMode = AutoScaleMode.None;   // 由本类显式缩放，避免两套机制打架

            float baseScale = Scale;
            Walk(form, baseScale);
            applied[form] = baseScale;

            form.DpiChanged += delegate(object sender, DpiChangedEventArgs e)
            {
                float already;
                if (!applied.TryGetValue(form, out already)) already = baseScale;

                float now = e.DeviceDpiNew / 96f;
                float ratio = now / already;
                if (Math.Abs(ratio - 1f) < 0.02f)
                {
                    // 窗口刚创建时 WinForms 常补发一次 96 -> 实际 DPI 的事件，此时布局已按实际 DPI 缩放
                    Store.Log(string.Format("忽略重复的 DPI 变化事件 {0} -> {1}（布局已按 {2:0.###} 缩放）",
                        e.DeviceDpiOld, e.DeviceDpiNew, already));
                    return;
                }

                Store.Log(string.Format("显示器 DPI 变化：{0} -> {1}，按 {2:0.###} 重新缩放布局",
                    e.DeviceDpiOld, e.DeviceDpiNew, ratio));
                Walk(form, ratio);
                applied[form] = now;
            };

            form.FormClosed += delegate { applied.Remove(form); };
        }

        private static void Walk(Control c, float f)
        {
            if (Math.Abs(f - 1f) < 0.001f) return;

            // 窗体：只缩放客户区。窗口整体尺寸和屏幕坐标由系统负责，
            // 再乘一次系数就会把窗口撑大（实测 641x539 的窗口被撑到 801x674）。
            Form form = c as Form;
            if (form != null)
            {
                form.ClientSize = ScaleSize(form.ClientSize, f);
            }
            else if (c.Dock == DockStyle.None)
            {
                // 自由摆放的控件（例如面板里的按钮）需要显式缩放位置与尺寸
                c.Size = ScaleSize(c.Size, f);
                c.Location = new Point((int)Math.Round(c.Left * f), (int)Math.Round(c.Top * f));
            }
            else if (c.Dock == DockStyle.Top || c.Dock == DockStyle.Bottom)
            {
                // Top/Bottom 的「厚度」取自身 Height，必须缩放，否则里面的按钮会被裁掉
                c.Height = (int)Math.Round(c.Height * f);
            }
            else if (c.Dock == DockStyle.Left || c.Dock == DockStyle.Right)
            {
                c.Width = (int)Math.Round(c.Width * f);
            }

            // 内边距与外边距对所有控件都要缩放
            c.Padding = ScalePadding(c.Padding, f);
            c.Margin = ScalePadding(c.Margin, f);

            TableLayoutPanel tlp = c as TableLayoutPanel;
            if (tlp != null)
            {
                for (int i = 0; i < tlp.ColumnStyles.Count; i++)
                {
                    ColumnStyle cs = tlp.ColumnStyles[i];
                    if (cs.SizeType == SizeType.Absolute) cs.Width = cs.Width * f;
                }
                for (int i = 0; i < tlp.RowStyles.Count; i++)
                {
                    RowStyle rs = tlp.RowStyles[i];
                    if (rs.SizeType == SizeType.Absolute) rs.Height = rs.Height * f;
                }
            }

            ListView lv = c as ListView;
            if (lv != null)
            {
                for (int i = 0; i < lv.Columns.Count; i++)
                    lv.Columns[i].Width = (int)Math.Round(lv.Columns[i].Width * f);
                if (lv.SmallImageList != null)
                    lv.SmallImageList.ImageSize = ScaleSize(lv.SmallImageList.ImageSize, f);
            }

            DataGridView dgv = c as DataGridView;
            if (dgv != null)
            {
                for (int i = 0; i < dgv.Columns.Count; i++)
                {
                    DataGridViewColumn col = dgv.Columns[i];
                    if (col.AutoSizeMode == DataGridViewAutoSizeColumnMode.None)
                        col.Width = (int)Math.Round(col.Width * f);
                }
            }

            foreach (Control child in c.Controls) Walk(child, f);
        }

        private static Size ScaleSize(Size s, float f)
        {
            return new Size((int)Math.Round(s.Width * f), (int)Math.Round(s.Height * f));
        }

        private static Padding ScalePadding(Padding p, float f)
        {
            return new Padding(
                (int)Math.Round(p.Left * f),
                (int)Math.Round(p.Top * f),
                (int)Math.Round(p.Right * f),
                (int)Math.Round(p.Bottom * f));
        }
    }
}
