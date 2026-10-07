using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PomoCC
{
    public class MainForm : Form
    {
        private Supervisor sup;
        private System.Windows.Forms.Timer tick;

        private TimerDial dial;
        private LinkLabel lblStatus;
        private Label lblWatch;
        private Label lblToday;
        private FlatButton btnStart;
        private FlatButton btnAbandon;
        private Card editPanel;
        private ComboBox cboMinutes;
        private int dialHeight;
        private Card mainCard;

        private NotifyIcon tray;
        private ContextMenuStrip trayMenu;
        private Icon appIcon;
        private FlatButton btnTestMailTool;

        /// <summary>供自检：主窗口的「测试发信」按钮（验证异步发送时的按钮状态）。</summary>
        internal FlatButton TestMailButton { get { return btnTestMailTool; } }

        private bool reallyExit;
        private bool startHidden;
        private bool trayTipShown;

        public MainForm()
        {
            // DPI 适配：AutoScaleMode 交给 Dpi.ApplyTo 显式缩放（原因见 Dpi.cs）。
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            BackColor = Theme.Bg;

            sup = new Supervisor(Store.LoadSettings(), Store.LoadToday());
            sup.Changed += OnSupChanged;
            sup.Notified += OnNotified;

            BuildUi();
            Dpi.ApplyTo(this);
            Ui.DismissFocusOnBlankClick(this);

            // 窗口不许被缩到装不下内容（MinimumSize 是窗口尺寸，按当前实际窗口尺寸取）
            dialHeight = dial.Height;
            MinimumSize = new Size(Width, Height);
            AutoStart.Apply(sup.Settings.AutoStart);
            StartTicking();
            RefreshUi();
        }

        /// <summary>供自检使用：直接拿到核心逻辑。</summary>
        public Supervisor Core { get { return sup; } }

        // ---------------------------------------------------------------- UI

        private void BuildUi()
        {
            Text = App.ProductName + " " + App.Version;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 580);
            MinimumSize = new Size(420, 520);

            appIcon = MakeIcon();
            Icon = appIcon;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(18, 18, 18, 16);
            root.BackColor = Theme.Bg;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            Controls.Add(root);

            // ---------- 主卡片：环形倒计时 ----------
            Card main = new Card();
            main.Dock = DockStyle.Fill;
            main.Margin = new Padding(0, 0, 0, 14);
            main.Padding = new Padding(20, 18, 20, 16);
            main.Resize += delegate { if (editPanel != null && editPanel.Visible) LayoutEditOverlay(); };
            mainCard = main;
            root.Controls.Add(main, 0, 0);

            TableLayoutPanel cardStack = Ui.Stack();
            cardStack.Dock = DockStyle.Fill;
            main.Controls.Add(cardStack);

            dial = new TimerDial();
            dial.Height = 240;
            dial.HintText = "双击可改时长";
            dial.DoubleClick += delegate { BeginEditMinutes(); };
            dial.Cursor = Cursors.Hand;
            Ui.Add(cardStack, dial);

            // ---------- 双击时间后浮在圆环上的编辑条 ----------
            // 关键：它是浮层（不占布局），所以出现/消失时圆环和整个卡片尺寸都不变。
            editPanel = new Card();
            editPanel.Radius = 10;
            editPanel.Padding = new Padding(14, 9, 14, 9);
            editPanel.Visible = false;
            main.Controls.Add(editPanel);

            FlowLayoutPanel editRow = new FlowLayoutPanel();
            editRow.Dock = DockStyle.Fill;
            editRow.FlowDirection = FlowDirection.LeftToRight;
            editRow.WrapContents = false;
            editRow.BackColor = Theme.Card;
            editPanel.Controls.Add(editRow);

            Label editLabel = new Label();
            editLabel.Text = "时长：";
            editLabel.Font = Theme.Body;
            editLabel.ForeColor = Theme.SubText;
            editLabel.AutoSize = true;
            editLabel.Margin = new Padding(0, 10, 0, 0);
            editRow.Controls.Add(editLabel);

            cboMinutes = new ComboBox();
            cboMinutes.DropDownStyle = ComboBoxStyle.DropDown;   // 可选可填
            cboMinutes.FlatStyle = FlatStyle.Flat;
            cboMinutes.Font = Theme.Body;
            cboMinutes.BackColor = Theme.Card;
            cboMinutes.ForeColor = Theme.Text;
            cboMinutes.Width = 76;
            cboMinutes.Margin = new Padding(0, 6, 6, 0);
            int[] presets = new int[] { 10, 15, 20, 25, 30, 45, 60, 90, 120 };
            for (int i = 0; i < presets.Length; i++) cboMinutes.Items.Add(presets[i]);
            cboMinutes.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitMinutes(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelEditMinutes(); }
            };
            editRow.Controls.Add(cboMinutes);

            Label unit = new Label();
            unit.Text = "分钟";
            unit.Font = Theme.Body;
            unit.ForeColor = Theme.SubText;
            unit.AutoSize = true;
            unit.Margin = new Padding(0, 10, 8, 0);
            editRow.Controls.Add(unit);

            FlatButton okMinutes = new FlatButton("确定", FlatButton.Kind.Primary);
            okMinutes.Width = 68;
            okMinutes.Height = 32;
            okMinutes.Margin = new Padding(0, 5, 8, 0);
            okMinutes.Click += delegate { CommitMinutes(); };
            editRow.Controls.Add(okMinutes);

            FlatButton cancelMinutes = new FlatButton("取消", FlatButton.Kind.Secondary);
            cancelMinutes.Width = 68;
            cancelMinutes.Height = 32;
            cancelMinutes.Margin = new Padding(0, 5, 0, 0);
            cancelMinutes.Click += delegate { CancelEditMinutes(); };
            editRow.Controls.Add(cancelMinutes);

            // 「程序」二字做成蓝色下划线的链接：点它就看看正在监视哪些程序
            lblStatus = new LinkLabel();
            lblStatus.Dock = DockStyle.Top;
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            lblStatus.Font = Theme.Body;
            lblStatus.ForeColor = Theme.Text;
            lblStatus.LinkColor = Theme.Link;
            lblStatus.ActiveLinkColor = Theme.LinkActive;
            lblStatus.VisitedLinkColor = Theme.Link;          // 点过之后也保持蓝色
            lblStatus.LinkBehavior = LinkBehavior.AlwaysUnderline;
            lblStatus.AutoSize = false;
            lblStatus.Height = 26;
            lblStatus.LinkClicked += delegate { ShowWatchList(); };
            Ui.Add(cardStack, lblStatus);

            lblWatch = new Label();
            lblWatch.Dock = DockStyle.Top;
            lblWatch.TextAlign = ContentAlignment.MiddleCenter;
            lblWatch.Font = Theme.BodySmall;
            lblWatch.ForeColor = Theme.Warn;
            lblWatch.AutoSize = false;
            lblWatch.Height = 48;
            Ui.Add(cardStack, lblWatch);

            // ---------- 两个大按钮 ----------
            TableLayoutPanel actions = new TableLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.ColumnCount = 2;
            actions.RowCount = 1;
            actions.BackColor = Theme.Bg;
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            root.Controls.Add(actions, 0, 1);

            btnStart = new FlatButton("开始专注", FlatButton.Kind.Primary);
            btnStart.Dock = DockStyle.Fill;
            btnStart.Margin = new Padding(0, 4, 6, 4);
            btnStart.Radius = 10;
            btnStart.Click += delegate { StartFocusClicked(); };
            actions.Controls.Add(btnStart, 0, 0);

            btnAbandon = new FlatButton("放弃（会告状）", FlatButton.Kind.Secondary);
            btnAbandon.Dock = DockStyle.Fill;
            btnAbandon.Margin = new Padding(6, 4, 0, 4);
            btnAbandon.Radius = 10;
            btnAbandon.Click += delegate { AbandonWithConfirm(); };
            actions.Controls.Add(btnAbandon, 1, 0);

            // ---------- 底部工具按钮 ----------
            TableLayoutPanel tools = new TableLayoutPanel();
            tools.Dock = DockStyle.Fill;
            tools.ColumnCount = 4;
            tools.RowCount = 1;
            tools.BackColor = Theme.Bg;
            for (int i = 0; i < 4; i++) tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            root.Controls.Add(tools, 0, 2);

            tools.Controls.Add(ToolButton("设置", delegate { OpenSettings(); }), 0, 0);
            tools.Controls.Add(ToolButton("告状记录", delegate { OpenHistory(); }), 1, 0);
            tools.Controls.Add(ToolButton("预览邮件", delegate { PreviewMail(); }), 2, 0);
            btnTestMailTool = ToolButton("测试发信", delegate { TestMail(); });
            tools.Controls.Add(btnTestMailTool, 3, 0);

            // 今日统计放在卡片底部
            lblToday = new Label();
            lblToday.Dock = DockStyle.Top;
            lblToday.TextAlign = ContentAlignment.MiddleCenter;
            lblToday.Font = Theme.BodySmall;
            lblToday.ForeColor = Theme.FaintText;
            lblToday.AutoSize = false;
            lblToday.Height = 22;
            Ui.Add(cardStack, lblToday);

            BuildTray();
        }

        private FlatButton ToolButton(string text, EventHandler onClick)
        {
            FlatButton b = new FlatButton(text, FlatButton.Kind.Ghost);
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(2, 4, 2, 4);
            b.Radius = 8;
            b.Click += onClick;
            return b;
        }

        private void BuildTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示主窗口", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("开始专注", null, delegate { StartFocusClicked(); });
            trayMenu.Items.Add("放弃专注（会告状）", null, delegate { AbandonWithConfirm(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("设置…（需要密码）", null, delegate { OpenSettings(); });
            trayMenu.Items.Add("告状记录…", null, delegate { OpenHistory(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出（需要密码）", null, delegate { ExitWithPassword(); });

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = App.ProductName + " " + App.Version;
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
        }

        internal static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    IconArt.DrawTomato(g, 32);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void StartTicking()
        {
            // 这个 UI 定时器**只负责刷新界面**。
            // 计时/采样/规则判定由 Supervisor 自己的后台线程完成（见 Supervisor.StartLoop），
            // 所以界面卡住时专注时间照常走。
            tick = new System.Windows.Forms.Timer();
            tick.Interval = 1000;
            tick.Tick += delegate { RefreshUi(); };
            tick.Start();
        }

        private void RefreshUi()
        {
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(RefreshUi));
                return;
            }
            if (IsDisposed) return;

            // 只读快照：界面绝不直接读后台线程正在改的 Session/Stats
            Supervisor.Snapshot snap = sup.Read();

            dial.Active = snap.Focusing;
            dial.Progress = snap.ProgressPercent;
            dial.SetState(snap.TimerText, snap.Focusing ? "专注中" : "待机");
            dial.HintText = snap.Focusing ? "" : "双击可改时长";

            lblStatus.Text = snap.StatusLine;
            // 把「程序」两个字设成可点区域（蓝色下划线）；句子变了就重算位置
            string statusText = lblStatus.Text;
            int linkAt = statusText.LastIndexOf("程序", StringComparison.Ordinal);
            lblStatus.LinkArea = linkAt >= 0 ? new LinkArea(linkAt, 2) : new LinkArea(0, 0);
            lblWatch.Text = snap.WatchSummary;
            lblToday.Text = snap.TodayText;

            btnStart.Enabled = !snap.Focusing;
            btnAbandon.Enabled = snap.Focusing;

            if (tray != null)
            {
                string t = snap.Focusing ? "专注中 " + snap.TimerText : "番茄钟监督 · 待机";
                tray.Text = t.Length > 62 ? t.Substring(0, 62) : t;
            }
        }

        /// <summary>把编辑浮层摆在圆环正中间（不动圆环尺寸、不动其他控件）。</summary>
        private void LayoutEditOverlay()
        {
            if (editPanel == null || mainCard == null || dial == null) return;
            int w = Math.Min(Dpi.Px(380), mainCard.ClientSize.Width - mainCard.Padding.Horizontal);
            int h = Dpi.Px(58);
            if (w < Dpi.Px(240)) w = Dpi.Px(240);
            Point dialPos = mainCard.PointToClient(dial.PointToScreen(Point.Empty));
            int x = mainCard.Padding.Left + (mainCard.ClientSize.Width - mainCard.Padding.Horizontal - w) / 2;
            int y = dialPos.Y + (dial.Height - h) / 2;
            editPanel.Bounds = new Rectangle(x, y, w, h);
        }

        /// <summary>双击时间进入编辑模式：用下拉框调整专注时长。</summary>
        internal void BeginEditMinutes()
        {
            if (sup.IsFocusing)
            {
                MessageBox.Show(this, "正在专注中，等这一段结束再改时长。", "番茄钟监督",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (editPanel == null || editPanel.Visible) return;

            cboMinutes.Text = sup.Settings.FocusMinutes.ToString();
            LayoutEditOverlay();
            editPanel.Visible = true;
            editPanel.BringToFront();
            cboMinutes.Focus();
            cboMinutes.SelectAll();
        }

        private void EndEditMinutes()
        {
            editPanel.Visible = false;
            RefreshUi();
        }

        /// <summary>供自检：状态行「程序」链接的信息。</summary>
        internal string StatusLinkInfo
        {
            get
            {
                LinkLabel l = lblStatus as LinkLabel;
                if (l == null) return "状态行不是链接";
                string seg = "";
                try { seg = l.Text.Substring(l.LinkArea.Start, l.LinkArea.Length); }
                catch { seg = "(取不到)"; }
                return string.Format("文本「{0}」｜可点区域「{1}」｜颜色 {2}", l.Text, seg, l.LinkColor);
            }
        }

        /// <summary>供自检：编辑条是否可见。</summary>
        internal bool EditingMinutes { get { return editPanel != null && editPanel.Visible; } }

        /// <summary>供自检：编辑浮层的位置与尺寸。</summary>
        internal string EditBoundsInfo
        {
            get
            {
                if (editPanel == null) return "(null)";
                return string.Format("visible={0} bounds={1} cardClient={2} dial={3}",
                    editPanel.Visible, editPanel.Bounds, mainCard.ClientSize, dial.Bounds);
            }
        }

        private void CommitMinutes()
        {
            int v;
            if (!int.TryParse(cboMinutes.Text.Trim(), out v) || v < 1 || v > 600)
            {
                MessageBox.Show(this, "请填 1~600 之间的整数（分钟）。", "专注时长",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                cboMinutes.Focus();
                cboMinutes.SelectAll();
                return;
            }

            sup.Settings.FocusMinutes = v;
            Store.SaveSettings(sup.Settings);
            Store.Log("专注时长改为 " + v + " 分钟");
            EndEditMinutes();
        }

        private void CancelEditMinutes()
        {
            EndEditMinutes();
        }

        private void OnSupChanged()
        {
            RefreshUi();
        }

        private void OnNotified(string msg)
        {
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke(new MethodInvoker(delegate { OnNotified(msg); }));
                return;
            }
            Store.Log("提示：" + msg);
            if (tray != null) tray.ShowBalloonTip(6000, "番茄钟监督", msg, ToolTipIcon.Info);
        }

        // ------------------------------------------------------------ 行为

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshUi();
        }

        public void StartHiddenInTray()
        {
            startHidden = true;
        }

        public void SmokeClose()
        {
            reallyExit = true;
            if (tray != null) tray.Visible = false;
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (App.EditMinutesOnStart && !App.Headless)
            {
                BeginInvoke(new MethodInvoker(delegate { BeginEditMinutes(); }));
                return;
            }
            if (App.OpenSettingsOnStart && !App.Headless)
            {
                // 截取自检用：直接打开设置窗口
                BeginInvoke(new MethodInvoker(delegate { OpenSettings(); }));
                return;
            }
            if (App.Headless) return;

            if (startHidden)
            {
                Hide();
                ShowInTaskbar = false;
            }
            else if (!sup.Settings.HasPassword())
            {
                BeginInvoke(new MethodInvoker(FirstRunSetup));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && sup.Settings.TrayOnClose && !App.Headless)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                if (!trayTipShown && tray != null)
                {
                    trayTipShown = true;
                    tray.ShowBalloonTip(4000, "番茄钟监督",
                        "已缩到托盘，继续在后台盯着。要真正退出请右键托盘图标 →「退出」（需要密码）。",
                        ToolTipIcon.Info);
                }
                return;
            }
            // 真正退出：先等在途的告状邮件发完（关窗退出这条路径也要等，
            // 否则邮件还没发完进程就没了，记录会停在「待发送」）
            WaitForMailThenQuitIfNeeded();
            base.OnFormClosing(e);
        }

        private void WaitForMailThenQuitIfNeeded()
        {
            if (!App.Headless && Supervisor.PendingSends > 0)
            {
                bool done = Supervisor.WaitForSendsToFinish(15000);
                Store.Log(done
                    ? "退出前已在途告状邮件发送完毕"
                    : "退出时仍有告状邮件未发完（等了 15 秒），可在告状记录里重发");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (sup != null) sup.StopLoop();   // 停掉后台监督线程
                if (tick != null) tick.Dispose();
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.Dispose();
                }
                if (trayMenu != null) trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------ 菜单动作

        private void FirstRunSetup()
        {
            DialogResult r = MessageBox.Show(this,
                "第一次运行，先设一个「退出 / 改设置」用的密码。\r\n\r\n" +
                "没有密码的话，自己心烦时顺手就把程序关了，监督就没意义了。\r\n\r\n" +
                "现在设置吗？（点「取消」将使用默认密码 123456）",
                "番茄钟监督", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

            string pw;
            if (r == DialogResult.OK)
            {
                pw = PasswordDialog.AskNew(this, "设置密码");
                if (string.IsNullOrEmpty(pw)) pw = "123456";
            }
            else
            {
                pw = "123456";
            }

            sup.Settings.SetPassword(pw);
            Store.SaveSettings(sup.Settings);
            MessageBox.Show(this,
                pw == "123456"
                    ? "已使用默认密码 123456。建议在「设置」里改成自己的密码。"
                    : "密码已设置，请记牢（忘记后只能删掉配置文件重来）。",
                "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool RequirePassword(string what)
        {
            if (App.Headless) return true;
            if (!sup.Settings.HasPassword()) return true;

            string pw = PasswordDialog.Ask(this, what, "请输入密码：");
            if (pw == null) return false;
            if (sup.Settings.CheckPassword(pw)) return true;

            MessageBox.Show(this, "密码不对。", "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private bool MailConfigured()
        {
            Settings s = sup.Settings;
            if (string.IsNullOrEmpty(s.SupervisorEmail) || s.SupervisorEmail.IndexOf('@') < 0) return false;
            if (string.IsNullOrEmpty(s.SenderEmail) || s.SenderEmail.IndexOf('@') < 0) return false;
            if (s.SendMode == "smtp")
                return !string.IsNullOrEmpty(s.SmtpHost) && !string.IsNullOrEmpty(s.GetAuthCode());
            return !string.IsNullOrEmpty(s.GetApiKey());
        }

        private void StartFocusClicked()
        {
            if (sup.IsFocusing) return;

            if (!MailConfigured() || sup.WatchNames.Count == 0)
            {
                MessageBox.Show(this,
                    "还没配置好：需要填监督人邮箱、发件邮箱、授权码，以及至少一个监督程序。\r\n\r\n先打开设置填一下吧。",
                    "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                OpenSettings();
                return;
            }

            sup.StartFocus();
            RefreshUi();
            if (WindowState == FormWindowState.Minimized) ShowFromTray();
        }

        private void AbandonWithConfirm()
        {
            if (!sup.IsFocusing)
            {
                MessageBox.Show(this, "当前没有正在进行的专注。", "番茄钟监督",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!App.Headless)
            {
                DialogResult r = MessageBox.Show(this,
                    "确定放弃这次专注吗？\r\n\r\n这会立刻给监督人发一封告状邮件。",
                    "确认放弃", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }

            sup.Abandon();
            RefreshUi();
        }

        private void OpenSettings()
        {
            if (!RequirePassword("打开设置")) return;

            // 传入回调：设置窗口里点「保存」时立即生效，不必等窗口关闭
            using (SettingsForm f = new SettingsForm(sup.Settings, ApplySettings))
            {
                // 让设置窗口知道当前是否在专注：专注中保存只对下一段生效，界面上要说清楚
                f.SessionActive = delegate { return sup.IsFocusing; };
                f.ShowDialog(this);
            }
            RefreshUi();
        }

        /// <summary>点状态行里的「程序」：弹窗看看正在监视哪些程序。</summary>
        private void ShowWatchList()
        {
            using (WatchListForm f = new WatchListForm(sup.Settings))
            {
                f.ShowDialog(this);
            }
        }

        /// <summary>把设置窗口保存的内容应用到运行中的实例（走 Supervisor 的统一入口）。</summary>
        private void ApplySettings(Settings s)
        {
            bool saved = Store.SaveSettings(s);          // 原子写：失败时原配置不会被破坏
            sup.ApplySettings(s);                        // 专注中保存 = 下一段生效，本段规则不变
            AutoStart.Apply(s.AutoStart);
            RefreshUi();

            if (!saved)
            {
                // 关键写入失败必须让用户看见，不能悄悄当成功
                MessageBox.Show(this,
                    "设置没能写入磁盘（文件可能被占用或磁盘只读）。\r\n\r\n" +
                    "这次修改只在内存里生效，重启后会丢失；原来的配置文件没有被破坏。",
                    "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenHistory()
        {
            HistoryForm f = new HistoryForm();
            f.ShowDialog(this);
        }

        private void PreviewMail()
        {
            TextForm.Show(this, "告状邮件预览（不会真的发送）", Mailer.PreviewBody(sup.Settings));
        }

        private void TestMail()
        {
            if (!MailConfigured())
            {
                MessageBox.Show(this, "请先把邮件相关设置填好（收件人、发件邮箱、授权码）。",
                    "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Settings snapshot = sup.Settings.Copy();
            DialogResult r = MessageBox.Show(this,
                string.Format("马上给 {0} 发一封测试邮件，确认一下通道是否可用？", snapshot.SupervisorEmail),
                "测试发信", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            SendTestMailAsync(snapshot, null, null);
        }

        /// <summary>
        /// 「测试发信」的异步外壳（与设置窗口、历史重发共用 AsyncMail 的行为）。
        /// 自检可以传替身函数与自己的结果处理，避免真发信和弹确认框。
        /// </summary>
        internal void SendTestMailAsync(Settings snapshot, Func<string> workOverride, Action<bool, string> onDone)
        {
            Func<string> work = workOverride != null ? workOverride : (Func<string>)delegate
            {
                Mailer.Send(snapshot, "【番茄钟监督】测试邮件",
                    "这是一封测试邮件。\r\n\r\n收到它说明「番茄钟监督」的发信通道已经配置好了，\r\n" +
                    "以后你在规定时间里没坚持完专注、或者偷玩超时，都会通过这个通道自动发信。\r\n");
                return "测试邮件已发送成功，去收件箱（和垃圾箱）看看。";
            };

            Action<bool, string> done = onDone != null ? onDone : (Action<bool, string>)delegate(bool ok, string msg)
            {
                if (ok)
                    MessageBox.Show(this, msg, "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "发送失败：\r\n\r\n" + msg, "番茄钟监督",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            // 后台线程发信：网络超时时主窗口不假死；期间按钮显示「发送中…」防重复点击
            AsyncMail.Run(this, btnTestMailTool, "发送中…", "测试发信", work, done);
        }

        private void ExitWithPassword()
        {
            if (!RequirePassword("退出程序")) return;

            if (!App.Headless && sup.IsFocusing)
            {
                DialogResult r = MessageBox.Show(this,
                    "现在还在专注中，退出就等于放弃，会给监督人发告状邮件。\r\n\r\n真的要退出吗？",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }

            reallyExit = true;
            if (sup.IsFocusing) sup.Abandon();
            // 在途邮件的等待统一放在 OnFormClosing 里（Close() 一定会走到），只等一次
            Close();
            Application.Exit();
        }
    }
}
