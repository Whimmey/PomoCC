using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PomodoroSupervisor
{
    /// <summary>
    /// 主面板点「程序」弹出的只读窗口：和监督名单一样的列（名称 / 软件 / 规则时长），
    /// 但**没有最后的「操作」列** —— 这里只是看，不是改。
    /// </summary>
    public class WatchListForm : ModernDialog
    {
        /// <summary>供自检读取列名。</summary>
        internal DataGridView List;

        public WatchListForm(Settings s)
            : base("正在监视的程序", 620, 460)
        {
            // 用弹窗自带的内容栈，最后 FinishLayout() 按内容自适应高矮（不留大片空白）
            TableLayoutPanel stack = Stack;

            List<WatchRule> show = new List<WatchRule>();
            int disabled = 0;
            if (s != null && s.Rules != null)
            {
                for (int i = 0; i < s.Rules.Count; i++)
                {
                    WatchRule r = s.Rules[i];
                    if (r == null) continue;
                    if (ProcessMonitor.NormalizeName(r.Exe).Length == 0) continue;
                    if (r.Enabled) show.Add(r);
                    else disabled++;
                }
            }

            Label head = new Label();
            head.AutoSize = true;
            head.MaximumSize = new Size(Dpi.Px(520), 0);
            head.Font = Theme.Body;
            head.ForeColor = Theme.Text;
            head.Margin = new Padding(0, 2, 0, 6);
            head.Text = show.Count == 0
                ? "监督名单里还没有启用的程序 —— 去「设置 → 监督名单」加几个。"
                : string.Format("专注期间这些程序只要在跑，就按各自的规则时长计时{0}。",
                    disabled > 0 ? string.Format("（另有 {0} 个已停用，不算在内）", disabled) : "");
            Ui.Add(stack, head);

            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Top;
            grid.Height = 240;
            grid.Margin = new Padding(0, 0, 0, 6);
            Ui.StyleGrid(grid);
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

            DataGridViewTextBoxColumn colName = new DataGridViewTextBoxColumn();
            colName.HeaderText = "名称";
            colName.Name = "colName";
            colName.Width = 170;
            colName.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(colName);

            DataGridViewTextBoxColumn colSoft = new DataGridViewTextBoxColumn();
            colSoft.HeaderText = "软件";
            colSoft.Name = "colSoft";
            colSoft.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colSoft.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(colSoft);

            DataGridViewTextBoxColumn colLimit = new DataGridViewTextBoxColumn();
            colLimit.HeaderText = "规则时长（分钟）";
            colLimit.Name = "colLimit";
            colLimit.Width = 170;
            colLimit.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(colLimit);

            for (int i = 0; i < show.Count; i++)
            {
                WatchRule r = show[i];
                string name = string.IsNullOrEmpty(r.Name) ? r.DisplayLabel : r.Name;
                grid.Rows.Add(name, r.SoftwareText, r.LimitMinutes);
            }
            grid.ClearSelection();
            grid.CurrentCell = null;
            List = grid;
            Ui.Add(stack, grid);

            Label foot = new Label();
            foot.AutoSize = true;
            foot.MaximumSize = new Size(Dpi.Px(520), 0);
            foot.Font = Theme.BodySmall;
            foot.ForeColor = Theme.SubText;
            foot.Margin = new Padding(0, 0, 0, 6);
            foot.Text = "「规则时长」＝ 专注期间该程序累计运行超过这个时间，就会给监督人发告状邮件。\r\n" +
                        "要增删或改时长，请到「设置 → 监督名单」。";
            Ui.Add(stack, foot);

            FlatButton close = new FlatButton("关闭", FlatButton.Kind.Primary);
            close.Width = 96;
            close.DialogResult = DialogResult.OK;
            Ui.Add(stack, Ui.RightButtonRow(close));

            AcceptButton = close;
            CancelButton = close;

            FinishLayout();
        }
    }

    /// <summary>无系统边框的现代弹窗：圆角、可拖动、圆角卡片里放内容。</summary>
    public class ModernDialog : Form
    {
        internal Card Body;
        internal TableLayoutPanel Stack;
        /// <summary>内容区宿主：需要撑满的弹窗（如邮件预览）把控件加到这里。</summary>
        internal Panel ContentHost;
        private TableLayoutPanel Root;

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        public ModernDialog(string title, int width, int height)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            Font = Theme.Body;
            ForeColor = Theme.Text;
            ClientSize = new Size(width, height);
            Padding = new Padding(1);

            Body = new Card();
            Body.Dock = DockStyle.Fill;
            Body.Padding = new Padding(20, 16, 20, 18);
            Controls.Add(Body);

            // 两行：标题行（自适应高度）+ 内容区（撑满）
            Root = new TableLayoutPanel();
            Root.Dock = DockStyle.Fill;
            Root.BackColor = Theme.Card;
            Root.ColumnCount = 1;
            Root.RowCount = 2;
            Root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            Root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Body.Controls.Add(Root);

            // 标题行：标题 + 关闭
            TableLayoutPanel head = new TableLayoutPanel();
            head.AutoSize = true;
            head.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            head.Dock = DockStyle.Top;
            head.ColumnCount = 2;
            head.RowCount = 1;
            head.BackColor = Theme.Card;
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34F));
            head.Margin = new Padding(0, 0, 0, 10);

            Label lbl = new Label();
            lbl.Text = title;
            lbl.Font = Theme.PageTitle;
            lbl.ForeColor = Theme.Text;
            lbl.Dock = DockStyle.Fill;
            lbl.AutoSize = false;
            lbl.Height = 30;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            head.Controls.Add(lbl, 0, 0);

            FlatButton close = new FlatButton("✕", FlatButton.Kind.Ghost);
            close.Dock = DockStyle.Fill;
            close.Margin = new Padding(0);
            close.Radius = 6;
            close.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            head.Controls.Add(close, 1, 0);
            Root.Controls.Add(head, 0, 0);

            ContentHost = new Panel();
            ContentHost.Dock = DockStyle.Fill;
            ContentHost.BackColor = Theme.Card;
            Root.Controls.Add(ContentHost, 0, 1);

            Stack = Ui.Stack();
            ContentHost.Controls.Add(Stack);
        }

        /// <summary>改用「撑满内容区」的布局（大文本预览等），不再按内容高度收缩窗口。</summary>
        internal void UseFillContent()
        {
            Stack.Visible = false;
        }

        /// <summary>
        /// 内容加完之后必须调用：按 DPI 缩放整棵控件树，并把窗口调整到刚好装下内容。
        /// 之前忘了这一步 —— 于是弹窗没被缩放，而里面的字按 125% 渲染，
        /// 结果右边缘和下边缘都被切掉了。
        /// </summary>
        internal void FinishLayout()
        {
            Dpi.ApplyTo(this);
            Ui.DismissFocusOnBlankClick(this);
            FitToContent();
        }

        /// <summary>把窗口尺寸调整到内容刚好放得下，多退少补，绝不裁切也不留大片空白。</summary>
        internal void FitToContent()
        {
            Stack.PerformLayout();
            Root.PerformLayout();
            // 期望高度 = 标题行实际高度 + 内容栈期望高度。
            // （Root.PreferredSize 对 Percent 行算不出内容高度，不能直接用）
            int headH = 0;
            Control head = Root.GetControlFromPosition(0, 0);
            if (head != null)
            {
                head.PerformLayout();
                headH = head.PreferredSize.Height + head.Margin.Vertical;
            }
            int needH = headH + Stack.PreferredSize.Height + Body.Padding.Vertical + Padding.Vertical + Dpi.Px(8);
            int needW = Stack.PreferredSize.Width + Body.Padding.Horizontal + Padding.Horizontal + Dpi.Px(2);
            int w = Math.Max(ClientSize.Width, needW);
            int h = Math.Max(Dpi.Px(90), needH);
            if (w != ClientSize.Width || h != ClientSize.Height) ClientSize = new Size(w, h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            using (System.Drawing.Drawing2D.GraphicsPath p = Round.Path(new Rectangle(0, 0, Width, Height), Dpi.Px(12)))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < Dpi.Px(56))
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
            base.OnMouseDown(e);
        }
    }

    public static class PasswordDialog
    {
        /// <summary>构造「输入密码」对话框（Ask 使用；自检截图也用它）。</summary>
        internal static ModernDialog BuildAsk(string title, string prompt, out InputBox box)
        {
            ModernDialog dlg = new ModernDialog(title, 380, 200);
            Ui.Add(dlg.Stack, Ui.Hint(prompt));

            box = new InputBox(true, false);
            Ui.Add(dlg.Stack, box);

            FlatButton ok = new FlatButton("确定", FlatButton.Kind.Primary);
            ok.Width = 96;
            FlatButton cancel = new FlatButton("取消", FlatButton.Kind.Secondary);
            cancel.Width = 96;
            cancel.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            ok.Click += delegate { dlg.DialogResult = DialogResult.OK; dlg.Close(); };

            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            Ui.Add(dlg.Stack, Ui.RightButtonRow(ok, cancel));
            dlg.FinishLayout();
            box.Box.Focus();
            return dlg;
        }

        /// <summary>构造「设置新密码」对话框。</summary>
        internal static ModernDialog BuildAskNew(string title, out InputBox t1, out InputBox t2)
        {
            ModernDialog dlg = new ModernDialog(title, 400, 290);
            Ui.Add(dlg.Stack, Ui.Hint("新密码至少 4 位。忘记密码只能删掉配置文件重来，请记牢。"));

            t1 = new InputBox(true, false);
            Ui.Add(dlg.Stack, Ui.Row("新密码", t1));

            t2 = new InputBox(true, false);
            Ui.Add(dlg.Stack, Ui.Row("再输一遍", t2));

            FlatButton ok = new FlatButton("确定", FlatButton.Kind.Primary);
            ok.Width = 96;
            FlatButton cancel = new FlatButton("取消", FlatButton.Kind.Secondary);
            cancel.Width = 96;
            cancel.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            InputBox f1 = t1;
            InputBox f2 = t2;
            ok.Click += delegate
            {
                if (f1.Box.Text.Length < 4)
                {
                    MessageBox.Show(dlg, "密码至少 4 位。", "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (f1.Box.Text != f2.Box.Text)
                {
                    MessageBox.Show(dlg, "两次输入不一致。", "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            Ui.Add(dlg.Stack, Ui.RightButtonRow(ok, cancel));
            dlg.FinishLayout();
            t1.Box.Focus();
            return dlg;
        }

        public static string Ask(IWin32Window owner, string title, string prompt)
        {
            InputBox box;
            using (ModernDialog dlg = BuildAsk(title, prompt, out box))
            {
                if (dlg.ShowDialog(owner) == DialogResult.OK) return box.Box.Text;
                return null;
            }
        }

        public static string AskNew(IWin32Window owner, string title)
        {
            InputBox t1, t2;
            using (ModernDialog dlg = BuildAskNew(title, out t1, out t2))
            {
                if (dlg.ShowDialog(owner) == DialogResult.OK) return t1.Box.Text;
                return null;
            }
        }
    }

    /// <summary>只读文本查看器（邮件预览 / 记录全文）。</summary>
    public static class TextForm
    {
        /// <summary>构造预览窗（Show 用它；截图自检也用它）。</summary>
        internal static ModernDialog Build(string title, string text)
        {
            ModernDialog dlg = new ModernDialog(title, 660, 600);
            dlg.UseFillContent();

            Card box = new Card();
            box.Dock = DockStyle.Fill;
            box.Padding = new Padding(12);
            box.Margin = new Padding(0);
            dlg.ContentHost.Controls.Add(box);

            TextBox view = new TextBox();
            view.Multiline = true;
            view.ReadOnly = true;
            view.ScrollBars = ScrollBars.Both;
            view.WordWrap = true;
            view.BorderStyle = BorderStyle.None;
            view.BackColor = Theme.Card;
            view.ForeColor = Theme.Text;
            view.Font = Theme.Body;
            view.HideSelection = true;
            view.ShortcutsEnabled = true;
            view.Dock = DockStyle.Fill;
            view.Text = text;
            view.Select(0, 0);
            box.Controls.Add(view);

            Panel foot = new Panel();
            foot.Dock = DockStyle.Bottom;
            foot.Height = 46;
            foot.BackColor = Theme.Card;
            box.Controls.Add(foot);

            FlatButton close = new FlatButton("关闭", FlatButton.Kind.Primary);
            close.Width = 96;
            close.DialogResult = DialogResult.OK;
            FlowLayoutPanel right = Ui.RightButtonRow(close);
            right.Dock = DockStyle.Right;
            foot.Controls.Add(right);

            dlg.AcceptButton = close;
            dlg.CancelButton = close;
            Dpi.ApplyTo(dlg);
            // 用 Dock=Fill 的内容，不再按内容高度收缩窗口
            return dlg;
        }

        public static void Show(IWin32Window owner, string title, string text)
        {
            using (ModernDialog dlg = Build(title, text))
            {
                dlg.ShowDialog(owner);
            }
        }
    }

    public class HistoryForm : Form
    {
        private TextBox detail;
        private List<HistoryEntry> entries;

        public HistoryForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "告状记录";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 600);
            MinimumSize = new Size(620, 460);
            BackColor = Theme.Bg;
            Font = Theme.Body;
            ForeColor = Theme.Text;
            MinimizeBox = false;

            entries = Store.ReadHistory(100);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(16, 16, 16, 14);
            root.BackColor = Theme.Bg;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            Label head = new Label();
            head.Dock = DockStyle.Fill;
            head.TextAlign = ContentAlignment.MiddleLeft;
            head.Font = Theme.SectionTitle;
            head.ForeColor = Theme.Text;
            head.AutoSize = false;
            head.Text = entries.Count == 0
                ? "还没有告状记录（这是好事）"
                : string.Format("最近 {0} 条告状记录，点一条看邮件全文", entries.Count);
            root.Controls.Add(head, 0, 0);

            Card listCard = new Card();
            listCard.Dock = DockStyle.Fill;
            listCard.Margin = new Padding(0, 0, 0, 12);
            listCard.Padding = new Padding(8);
            root.Controls.Add(listCard, 0, 1);

            DataGridView list = new DataGridView();
            list.Dock = DockStyle.Fill;
            Ui.StyleGrid(list);
            list.ReadOnly = true;
            list.MultiSelect = false;
            list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

            DataGridViewTextBoxColumn colTime = new DataGridViewTextBoxColumn();
            colTime.HeaderText = "时间";
            colTime.Name = "colTime";
            colTime.Width = 185;
            colTime.SortMode = DataGridViewColumnSortMode.NotSortable;
            list.Columns.Add(colTime);

            DataGridViewTextBoxColumn colReason = new DataGridViewTextBoxColumn();
            colReason.HeaderText = "原因";
            colReason.Name = "colReason";
            colReason.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colReason.FillWeight = 60;
            colReason.SortMode = DataGridViewColumnSortMode.NotSortable;
            list.Columns.Add(colReason);

            DataGridViewTextBoxColumn colStatus = new DataGridViewTextBoxColumn();
            colStatus.HeaderText = "状态";
            colStatus.Name = "colStatus";
            colStatus.Width = 215;
            colStatus.SortMode = DataGridViewColumnSortMode.NotSortable;
            list.Columns.Add(colStatus);

            for (int i = 0; i < entries.Count; i++)
            {
                HistoryEntry e = entries[i];
                int idx = list.Rows.Add(e.Time, e.Reason, StatusText(e));
                list.Rows[idx].Tag = e;
            }
            list.SelectionChanged += delegate
            {
                if (list.CurrentRow == null) return;
                HistoryEntry e = list.CurrentRow.Tag as HistoryEntry;
                if (e == null) return;
                ShowDetail(e);
            };
            listCard.Controls.Add(list);

            Card detailCard = new Card();
            detailCard.Dock = DockStyle.Fill;
            detailCard.Margin = new Padding(0);
            detailCard.Padding = new Padding(12);
            root.Controls.Add(detailCard, 0, 2);

            detail = new TextBox();
            detail.Multiline = true;
            detail.ReadOnly = true;
            detail.ScrollBars = ScrollBars.Both;
            detail.WordWrap = true;
            detail.BorderStyle = BorderStyle.None;
            detail.BackColor = Theme.Card;
            detail.ForeColor = Theme.Text;
            detail.Dock = DockStyle.Fill;
            detail.Margin = new Padding(0);
            detailCard.Controls.Add(detail);

            Panel foot = new Panel();
            foot.Dock = DockStyle.Bottom;
            foot.Height = 48;
            foot.BackColor = Theme.Card;
            detailCard.Controls.Add(foot);

            FlatButton copy = new FlatButton("复制全文", FlatButton.Kind.Secondary);
            copy.Width = 100;
            copy.Click += delegate
            {
                if (detail.Text.Length > 0)
                {
                    try { Clipboard.SetText(detail.Text); }
                    catch { }
                }
            };

            FlatButton resend = new FlatButton("重发选中", FlatButton.Kind.Secondary);
            resend.Width = 100;
            resend.Click += delegate
            {
                if (list.CurrentRow == null) return;
                HistoryEntry e = list.CurrentRow.Tag as HistoryEntry;
                if (e == null) return;
                if (MessageBox.Show(this,
                        "把这封告状邮件重新发一次给 " + Store.LoadSettings().SupervisorEmail + "？\r\n\r\n（原记录会保留，重发结果会另记一条）",
                        "重发告状邮件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

                Cursor = Cursors.WaitCursor;
                string status;
                string err = "";
                try
                {
                    Mailer.Send(Store.LoadSettings(), e.Subject, e.Body);
                    status = "重发成功";
                }
                catch (Exception ex)
                {
                    status = "重发失败";
                    err = ex.Message;
                }
                finally
                {
                    Cursor = Cursors.Default;
                }

                HistoryEntry again = new HistoryEntry();
                again.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                again.Reason = e.Reason + "（重发 " + e.Time + "）";
                again.Subject = e.Subject;
                again.Body = e.Body;
                again.Status = status;
                again.Error = err;
                Store.AppendHistory(again);
                Store.Log("重发告状邮件：" + status + (err.Length > 0 ? "：" + err : ""));

                int idx = list.Rows.Add(again.Time, again.Reason, StatusText(again));
                list.Rows[idx].Tag = again;
                list.ClearSelection();
                list.CurrentCell = list.Rows[idx].Cells[0];
                list.Rows[idx].Selected = true;
                ShowDetail(again);
            };

            FlatButton close = new FlatButton("关闭", FlatButton.Kind.Primary);
            close.Width = 96;
            close.DialogResult = DialogResult.OK;
            FlowLayoutPanel right = Ui.RightButtonRow(close, resend, copy);
            right.Dock = DockStyle.Right;
            foot.Controls.Add(right);

            AcceptButton = close;
            CancelButton = close;

            Dpi.ApplyTo(this);
            Ui.DismissFocusOnBlankClick(this);

            if (list.Rows.Count > 0)
            {
                list.Rows[0].Selected = true;
                list.CurrentCell = list.Rows[0].Cells[0];
                // 直接填充，不依赖 SelectionChanged（构造期设置选中不一定触发事件）
                HistoryEntry first = list.Rows[0].Tag as HistoryEntry;
                if (first != null) ShowDetail(first);
            }
        }

        /// <summary>
        /// 状态列显示文字。
        /// 旧版本先写「待发送」占位、发完再补一条，若程序提前退出就会留下悬空的「待发送」，
        /// 让人以为邮件还在排队。这里如实说清：结果没等到，可能没发出去。
        /// </summary>
        internal static string StatusText(HistoryEntry e)
        {
            if (e == null) return "";
            string s = e.Status == null ? "" : e.Status.Trim();
            if (s == "待发送" || s.Length == 0) return "未确认（可能未发出）";
            return s;
        }

        private void ShowDetail(HistoryEntry e)
        {
            if (e == null) return;
            string text = e.Body;
            if (!string.IsNullOrEmpty(e.Error)) text = text + "\r\n\r\n【发送错误】\r\n" + e.Error;
            if (e.Status == "待发送")
            {
                text = "【注意】这条是旧版本留下的记录：程序在邮件发完之前就退出了，\r\n" +
                       "所以当时那封邮件很可能没有发出去。点「重发选中」可以补发一次。\r\n" +
                       "（新版本不会再出现这种记录：只有拿到发送结果后才写记录。）\r\n\r\n" + text;
            }
            detail.Text = text;
            detail.Select(0, 0);
        }
    }
}
