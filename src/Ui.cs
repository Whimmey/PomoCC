using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PomoCC
{
    /// <summary>圆角工具。</summary>
    public static class Round
    {
        public static GraphicsPath Path(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>
    /// 圆角卡片/容器：白色底、1px 浅边框、圆角，内容用 Dock/AutoSize 排布。
    /// 用 Region 裁剪，子控件不会画出圆角外侧。
    /// </summary>
    public class Card : Panel
    {
        public int Radius = 10;
        public bool DrawBorder = true;
        private Color fill;

        public Card()
        {
            fill = Theme.Card;
            BackColor = fill;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(18);
        }

        public Card(Color background)
            : this()
        {
            fill = background;
            BackColor = fill;
        }

        public Color FillColor
        {
            get { return fill; }
            set { fill = value; BackColor = value; Invalidate(); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRegion();
        }

        private void ApplyRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath p = Round.Path(new Rectangle(0, 0, Width, Height), Dpi.Px(Radius)))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 自绘容器也要自己擦背景（双缓冲 + 自绘会有残留）
            using (GraphicsPath fillPath = Round.Path(new Rectangle(0, 0, Width, Height), Dpi.Px(Radius)))
            using (SolidBrush b = new SolidBrush(fill))
            {
                g.FillPath(b, fillPath);
            }

            if (!DrawBorder) return;

            // 描边必须内缩 1px：用整块矩形描边的话，右/下边框正好落在控件边界外，
            // 会被裁掉 —— 看起来就是"输入框下边缘不见了"。
            using (GraphicsPath borderPath = Round.Path(new Rectangle(0, 0, Width - 1, Height - 1), Dpi.Px(Radius)))
            using (Pen pen = new Pen(Theme.Border))
            {
                g.DrawPath(pen, borderPath);
            }
        }
    }

    /// <summary>
    /// 扁平圆角按钮。四种角色：主按钮 / 次按钮 / 幽灵按钮 / 危险按钮。
    ///
    /// 刻意**不继承 Button**：Win32 的 Button 窗口类会自己画一圈三维边框
    /// （上/左深、下/右浅）和它自己的文字。之前继承 Button 时，基类的绘制会和
    /// 这里的自绘叠加，表现就是「按钮边缘有黑框」「文字像叠了两层」。
    /// 改成继承 Control 并自行实现点击/键盘/对话框语义后，绘制完全可控。
    /// </summary>
    public class FlatButton : Control, IButtonControl
    {
        public enum Kind { Primary, Secondary, Ghost, Danger }

        private Kind kind;
        private bool hovering;
        private bool pressing;
        private bool selected;
        private int radius = 8;
        private ContentAlignment textAlign = ContentAlignment.MiddleCenter;
        private DialogResult dialogResult = DialogResult.None;

        public FlatButton(string text, Kind kind)
        {
            this.kind = kind;
            Text = text;
            Font = kind == Kind.Primary ? Theme.ButtonStrong : Theme.Button;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable | ControlStyles.StandardClick |
                     ControlStyles.StandardDoubleClick | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = kind == Kind.Primary ? 38 : 34;
        }

        public int Radius
        {
            get { return radius; }
            set { radius = value; Invalidate(); }
        }

        /// <summary>导航项选中态：浅主色底 + 主色加粗文字。</summary>
        public bool Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        public ContentAlignment ContentAlign
        {
            get { return textAlign; }
            set { textAlign = value; Invalidate(); }
        }

        // ---------------------------------------------------------- 交互

        public DialogResult DialogResult
        {
            get { return dialogResult; }
            set { dialogResult = value; }
        }

        public void NotifyDefault(bool value)
        {
            // 默认按钮不做特殊描边（避免又出现多余的框）
        }

        public void PerformClick()
        {
            if (Enabled && Visible) OnClick(EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            // 复刻 Button 的行为：设了 DialogResult 就关掉所属对话框
            if (dialogResult != DialogResult.None)
            {
                Form f = FindForm();
                if (f != null) f.DialogResult = dialogResult;
            }
            base.OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressing = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                pressing = true;
                Focus();
                Invalidate();
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pressing)
            {
                pressing = false;
                Invalidate();
                if (ClientRectangle.Contains(e.Location)) PerformClick();
            }
            base.OnMouseUp(e);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                pressing = true;
                Invalidate();
            }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                pressing = false;
                Invalidate();
                PerformClick();
            }
            base.OnKeyUp(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        private void Colors(out Color fill, out Color border, out Color fg)
        {
            switch (kind)
            {
                case Kind.Primary:
                    fill = Theme.Accent; border = Color.Empty; fg = Color.White;
                    if (hovering) fill = Theme.AccentHover;
                    if (pressing) fill = Theme.AccentPressed;
                    break;
                case Kind.Danger:
                    fill = Theme.DangerSoft; border = Color.Empty; fg = Theme.Danger;
                    if (hovering) fill = Theme.Shift(Theme.DangerSoft, -8);
                    if (pressing) fill = Theme.Shift(Theme.DangerSoft, -16);
                    break;
                case Kind.Ghost:
                    fill = Color.Transparent; border = Color.Transparent; fg = Theme.SubText;
                    if (hovering) { fill = Theme.Hover; fg = Theme.Text; }
                    if (pressing) fill = Theme.Pressed;
                    break;
                default:
                    fill = Theme.Card; border = Theme.BorderStrong; fg = Theme.Text;
                    if (hovering) fill = Theme.Hover;
                    if (pressing) fill = Theme.Pressed;
                    break;
            }
            if (!Enabled)
            {
                fill = Theme.Shift(Theme.Bg, 0);
                border = Theme.Border;
                fg = Theme.FaintText;
            }
            else if (selected && kind == Kind.Ghost)
            {
                fill = Theme.AccentSoft;
                fg = Theme.Accent;
            }
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 第一步永远是擦背景：FlatStyle.Flat 会把按钮标成 Opaque，
            // 框架不再替我们填背景，幽灵按钮更是除了文字什么都不画。
            using (SolidBrush back = new SolidBrush(Ui.EffectiveBackColor(this)))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            Color fill, border, fg;
            Colors(out fill, out border, out fg);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Round.Path(r, Dpi.Px(radius)))
            {
                if (Ui.IsVisibleColor(fill))
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                }
                // 注意：GDI+ 的 Pen 会忽略 alpha，Color.Empty 的笔等于「不透明黑笔」，
                // 所以必须先判断颜色是否真的可见 —— 否则每个按钮都会被描一圈黑边。
                if (Ui.IsVisibleColor(border))
                {
                    using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
                }
            }

            TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            Rectangle textRect = r;
            if (textAlign == ContentAlignment.MiddleLeft)
            {
                flags |= TextFormatFlags.Left;
                textRect = new Rectangle(r.X + Dpi.Px(12), r.Y, r.Width - Dpi.Px(12), r.Height);
            }
            else
            {
                flags |= TextFormatFlags.HorizontalCenter;
            }

            Font f = (selected && kind == Kind.Ghost) ? Theme.Bold : Font;
            TextRenderer.DrawText(g, Text, f, textRect, fg, flags);
        }
    }

    /// <summary>圆角输入框外壳：里面放无边框 TextBox，边框由外壳画。</summary>
    public class InputBox : Card
    {
        public TextBox Box;

        public InputBox(bool password, bool multiline)
        {
            Radius = 8;
            Padding = new Padding(10, 6, 10, 6);
            BackColor = Theme.Card;
            DrawBorder = true;
            Height = 34;      // 必须有明确高度：Panel 默认 100，AutoSize=false 时会被当成期望高度

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Theme.Body;
            Box.BackColor = Theme.Card;
            Box.ForeColor = Theme.Text;
            Box.HideSelection = true;      // 失焦后不要留着选中高亮
            if (password) Box.UseSystemPasswordChar = true;
            if (multiline)
            {
                Box.Multiline = true;
                Box.ScrollBars = ScrollBars.Vertical;
                Box.AcceptsReturn = true;
            }
            Box.Dock = DockStyle.Fill;
            Controls.Add(Box);
        }
    }

    /// <summary>
    /// 数字输入：普通文本框 + 单位后缀（刻意不用 NumericUpDown —— 它会抢鼠标滚轮改数值）。
    /// 失焦时校验并夹到范围内。
    /// </summary>
    public class NumberBox : Card
    {
        private TextBox box;
        private int min;
        private int max;
        private int current;

        public NumberBox(int minValue, int maxValue, int value, string suffix)
        {
            min = minValue;
            max = maxValue;
            Radius = 8;
            Padding = new Padding(10, 6, 10, 6);
            BackColor = Theme.Card;
            Height = 34;      // 同上，不能留 Panel 的默认 100

            Label unit = new Label();
            unit.Text = suffix;
            unit.Font = Theme.BodySmall;
            unit.ForeColor = Theme.SubText;
            unit.AutoSize = false;
            unit.Width = 42;
            unit.Dock = DockStyle.Right;
            unit.TextAlign = ContentAlignment.MiddleLeft;

            box = new TextBox();
            box.BorderStyle = BorderStyle.None;
            box.Font = Theme.Body;
            box.BackColor = Theme.Card;
            box.ForeColor = Theme.Text;
            box.TextAlign = HorizontalAlignment.Right;
            box.HideSelection = true;
            box.Dock = DockStyle.Fill;
            box.TextChanged += delegate { Normalize(false); };
            box.LostFocus += delegate { Normalize(true); };

            // Dock 顺序：先加 Fill 的，再加 Right 的，Right 优先占位
            Controls.Add(box);
            Controls.Add(unit);

            Value = value;
        }

        public int Value
        {
            get { return current; }
            set
            {
                current = Clamp(value);
                string text = current.ToString();
                if (box.Text != text) box.Text = text;
            }
        }

        private int Clamp(int v)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void Normalize(bool fixEmpty)
        {
            int v;
            if (int.TryParse(box.Text.Trim(), out v))
            {
                current = Clamp(v);
                return;
            }
            if (fixEmpty)
            {
                box.Text = current.ToString();
            }
        }
    }

    /// <summary>环形倒计时：底色环 + 进度弧 + 中间大号时间。</summary>
    public class TimerDial : Control
    {
        private int progress;      // 0..100
        private string timeText = "25:00";
        private string stateText = "待机";
        private bool active;

        public TimerDial()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.StandardClick | ControlStyles.StandardDoubleClick |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Card;      // 画在白色卡片上，避免透明背景不被支持的问题
            Font = Theme.Timer;
        }

        public int Progress
        {
            get { return progress; }
            set { int v = value < 0 ? 0 : (value > 100 ? 100 : value); if (v != progress) { progress = v; Invalidate(); } }
        }

        public bool Active
        {
            get { return active; }
            set { if (active != value) { active = value; Invalidate(); } }
        }

        public void SetState(string time, string state)
        {
            if (timeText == time && stateText == state) return;
            timeText = time;
            stateText = state;
            Invalidate();
        }

        /// <summary>时间下方的小字提示（例如「双击可改时长」）。</summary>
        public string HintText = "";

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 同样先擦背景，避免残留
            using (SolidBrush back = new SolidBrush(Ui.EffectiveBackColor(this)))
            {
                g.FillRectangle(back, ClientRectangle);
            }

            int thickness = Dpi.Px(12);
            int pad = thickness / 2 + Dpi.Px(4);
            int size = Math.Min(Width, Height) - pad * 2;
            if (size <= 10) return;

            Rectangle ring = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);

            using (Pen bgPen = new Pen(Theme.Border, thickness))
            {
                g.DrawEllipse(bgPen, ring);
            }

            if (progress > 0)
            {
                using (Pen fgPen = new Pen(active ? Theme.Accent : Theme.BorderStrong, thickness))
                {
                    fgPen.StartCap = LineCap.Round;
                    fgPen.EndCap = LineCap.Round;
                    g.DrawArc(fgPen, ring, -90f, 360f * progress / 100f);
                }
            }

            // 中间时间
            Rectangle inner = new Rectangle(ring.X, ring.Y - Dpi.Px(6), ring.Width, ring.Height);
            TextRenderer.DrawText(g, timeText, Theme.Timer, inner, active ? Theme.Text : Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // 时间下方状态
            Rectangle under = new Rectangle(ring.X, ring.Y + ring.Height / 2 + Dpi.Px(20), ring.Width, Dpi.Px(24));
            TextRenderer.DrawText(g, stateText, Theme.BodySmall, under,
                active ? Theme.Accent : Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine);

            // 更下面一行灰色提示（双击可改时长）
            if (!string.IsNullOrEmpty(HintText))
            {
                Rectangle hint = new Rectangle(ring.X, under.Bottom + Dpi.Px(2), ring.Width, Dpi.Px(20));
                TextRenderer.DrawText(g, HintText, Theme.BodySmall, hint, Theme.FaintText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>控件工厂：统一间距、字号、配色，避免每个窗口各写一套。</summary>
    public static class Ui
    {
        /// <summary>
        /// 颜色是否真的画得出来。
        /// 完全透明和 Color.Empty 都不该拿去画 —— 尤其不能拿去建 Pen：
        /// GDI+ 的 Pen 会忽略 alpha，Color.Empty 的笔等于「不透明黑笔」。
        /// </summary>
        internal static bool IsVisibleColor(Color c)
        {
            return !c.IsEmpty && c.A > 0 && c != Color.Transparent;
        }
        /// <summary>
        /// 取这个控件实际显示在什么底色上（向上找到第一个不透明的父容器）。
        /// 自绘控件必须自己擦背景：FlatStyle.Flat 会把按钮标成 Opaque，框架不再填背景，
        /// 双缓冲位图会把上一帧的文字留下来 —— 表现就是「按钮上有两层字」。
        /// </summary>
        internal static Color EffectiveBackColor(Control c)
        {
            Control p = c == null ? null : c.Parent;
            while (p != null)
            {
                if (p.BackColor != Color.Transparent) return p.BackColor;
                p = p.Parent;
            }
            return Theme.Bg;
        }

        /// <summary>
        /// 点击空白处/标签/卡片时把焦点从输入框上摘掉。
        /// 否则点别处时输入框仍然有焦点，光标一直闪 —— 看起来像没失去焦点。
        /// </summary>
        public static void DismissFocusOnBlankClick(Form form)
        {
            if (form == null) return;
            AttachDismiss(form, form);
        }

        private static void AttachDismiss(Control c, Form form)
        {
            bool keepsFocus = c is TextBox || c is TextBoxBase || c is ComboBox || c is ListBox ||
                              c is DataGridView || c is FlatButton || c is CheckBox || c is Button;
            if (!keepsFocus)
            {
                c.MouseDown += delegate
                {
                    if (form != null && !form.IsDisposed && form.ActiveControl != null)
                        form.ActiveControl = null;   // 摘掉焦点，光标随之消失
                };
            }
            foreach (Control child in c.Controls) AttachDismiss(child, form);
        }

        public static Label Title(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.PageTitle;
            l.ForeColor = Theme.Text;
            l.AutoSize = true;
            return l;
        }

        public static Label SectionTitle(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.SectionTitle;
            l.ForeColor = Theme.Text;
            l.AutoSize = true;
            return l;
        }

        public static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.BodySmall;
            l.ForeColor = Theme.SubText;
            l.AutoSize = true;
            // 宽度由 Ui.Add 按容器实际宽度设置，这里不写死，否则会把卡片撑宽
            l.Margin = new Padding(0, 0, 0, 12);
            return l;
        }

        public static Label FieldLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.Body;
            l.ForeColor = Theme.SubText;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            return l;
        }

        /// <summary>一行：左标签 + 右控件。行高自适应，绝不会裁掉控件。</summary>
        public static TableLayoutPanel Row(string label, Control field)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = 2;
            t.RowCount = 1;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Dock = DockStyle.Top;
            t.Margin = new Padding(0, 0, 0, 10);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132f));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            if (!string.IsNullOrEmpty(label)) t.Controls.Add(FieldLabel(label), 0, 0);
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(0);
            t.Controls.Add(field, 1, 0);
            return t;
        }

        /// <summary>横向按钮条，高度自适应。</summary>
        public static FlowLayoutPanel ButtonRow(params Control[] buttons)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.FlowDirection = FlowDirection.LeftToRight;
            f.WrapContents = false;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Dock = DockStyle.Top;
            f.Margin = new Padding(0);
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Margin = new Padding(0, 0, 10, 0);
                f.Controls.Add(buttons[i]);
            }
            return f;
        }

        /// <summary>右对齐按钮条。</summary>
        public static FlowLayoutPanel RightButtonRow(params Control[] buttons)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.FlowDirection = FlowDirection.RightToLeft;
            f.WrapContents = false;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Dock = DockStyle.Top;
            f.Margin = new Padding(0);
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Margin = new Padding(10, 0, 0, 0);
                f.Controls.Add(buttons[i]);
            }
            return f;
        }

        /// <summary>
        /// 卡片内的纵向堆叠容器：1 列、行高自适应、子控件横向撑满。
        /// 行高由内容决定，所以永远不会出现「最后一行按钮被裁掉」。
        /// </summary>
        public static TableLayoutPanel Stack()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 1;
            t.RowCount = 0;
            t.BackColor = Color.Transparent;
            t.Margin = new Padding(0);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            return t;
        }

        /// <summary>追加一行；标签的换行宽度会跟随容器，避免先按固定宽度撑宽后溢出。</summary>
        public static void Add(TableLayoutPanel stack, Control c)
        {
            int row = stack.RowCount;
            stack.RowCount = row + 1;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Dock = DockStyle.Top;      // 横向撑满、高度取自身，配合 AutoSize 行不会裁切
            stack.Controls.Add(c, 0, row);

            Label l = c as Label;
            if (l != null && l.AutoSize)
            {
                EventHandler fit = delegate
                {
                    int w = stack.ClientSize.Width - l.Margin.Horizontal - Dpi.Px(4);
                    if (w > Dpi.Px(60)) l.MaximumSize = new Size(w, 0);
                };
                stack.SizeChanged += fit;
                fit(null, EventArgs.Empty);
            }
        }

        /// <summary>把输入控件包进圆角白底外壳，视觉统一。</summary>
        public static Card Field(Control inner)
        {
            Card card = new Card();
            card.Radius = 8;
            card.Padding = new Padding(10, 5, 10, 5);
            card.Margin = new Padding(0);
            card.Height = 36;     // 明确高度，避免 Panel 默认 100 把整行撑高
            inner.Dock = DockStyle.Fill;
            inner.Margin = new Padding(0);
            card.Controls.Add(inner);
            return card;
        }

        /// <summary>
        /// 设置页容器：1 列 + 行高自适应，卡片用 Dock=Fill 由框架算出正确高度。
        /// 行高跟随内容，所以永远不会出现「最后一行按钮被裁掉」或卡片高度不够。
        /// </summary>
        public static TableLayoutPanel Page(out TableLayoutPanel content)
        {
            TableLayoutPanel page = new TableLayoutPanel();
            page.Dock = DockStyle.Fill;
            page.BackColor = Theme.Bg;
            page.ColumnCount = 1;
            page.RowCount = 0;
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // 内容比窗口高时必须能滚动：否则下面的设置项用户根本看不到
            page.AutoScroll = true;
            content = page;
            return page;
        }

        /// <summary>往设置页里追加一张卡片：卡片自己按内容长高，行不再依赖框架猜高度。</summary>
        public static void AddCard(TableLayoutPanel page, Card card)
        {
            int row = page.RowCount;
            page.RowCount = row + 1;
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Dock = DockStyle.Top;
            page.Controls.Add(card, 0, row);
        }

        /// <summary>扁平化表格：无网格线、浅色表头、行高舒展。</summary>
        public static void StyleGrid(DataGridView grid)        {
            grid.BorderStyle = BorderStyle.None;
            grid.BackgroundColor = Theme.Card;
            grid.GridColor = Theme.Border;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Card;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.SubText;
            grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodySmall;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Card;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.SubText;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(Dpi.Px(6), 0, 0, 0);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = Dpi.Px(34);
            grid.RowTemplate.Height = Dpi.Px(36);
            grid.DefaultCellStyle.BackColor = Theme.Card;
            grid.DefaultCellStyle.ForeColor = Theme.Text;
            grid.DefaultCellStyle.Font = Theme.Body;
            grid.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
            grid.DefaultCellStyle.Padding = new Padding(Dpi.Px(6), 0, 0, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Card;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.ShowCellToolTips = true;
        }
    }
}
