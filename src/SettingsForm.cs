using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PomoCC
{
    public class SettingsForm : Form
    {
        public Settings Result;

        /// <summary>保存时立即回调（让主窗口马上应用，不必等关窗）。</summary>
        private Action<Settings> onSave;
        private Label lblSaveState;

        /// <summary>由主窗口注入：当前是否正在专注（专注中保存 = 下一段才生效）。</summary>
        internal Func<bool> SessionActive;
        private FlatButton btnProbe, btnTestMail;
        private InputBox inSubjectTpl;
        private InputBox memoIntro;
        private InputBox memoOutro;
        private TextBox txtFixed;
        private Card cardContent;

        /// <summary>供自检滚动定位到「内容自定义」卡片。</summary>
        internal Control ContentCard { get { return cardContent; } }
        private bool savedAnything;

        private Settings draft;

        // 导航与页面
        private Panel pageHost;
        private Panel pageRules, pageMail, pageParams;
        private FlatButton navRules, navMail, navParams;

        // 监督名单页
        private DataGridView grid;
        private Label lblGridInfo;

        // 邮件页
        private ComboBox cmbMode;
        private InputBox inSupervisor, inUserName, inSender, inSmtpHost, inAuth, inApiKey, inHttpUrl;
        private NumberBox numPort;
        private CheckBox chkStartTls;
        private Card cardSmtp, cardHttp;
        private Label lblModeHint;

        // 规则页
        private NumberBox numFocus, numViolation, numSample;
        private CheckBox chkOnlyAfter, chkAutoStart, chkTray, chkNotify, chkSound;
        private LinkLabel lnkSoundTest;

        // 底部按钮行上的署名（版本号 + 作者链接）
        private LinkLabel lnkAuthor;
        private DateTime lastDataFolderOpenUtc = DateTime.MinValue;

        /// <summary>供自检：署名区的版本号与作者链接信息。</summary>
        internal string CreditInfo
        {
            get
            {
                if (lnkAuthor == null) return "(没建出来)";
                string seg = "";
                try { seg = lnkAuthor.Text.Substring(lnkAuthor.LinkArea.Start, lnkAuthor.LinkArea.Length); }
                catch { seg = "(取不到)"; }
                return string.Format("署名「{0}」｜可点区域「{1}」｜目标 {2}",
                    lnkAuthor.Text, seg, App.RepoUrl);
            }
        }

        /// <summary>用系统默认浏览器打开链接（设置窗口里的作者链接用）。</summary>
        private void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打不开浏览器：\r\n\r\n" + ex.Message,
                    App.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public SettingsForm(Settings current)
            : this(current, null)
        {
        }

        /// <param name="onSave">点「保存」时立即调用（写盘并让主窗口生效），窗口不会关闭。</param>
        public SettingsForm(Settings current, Action<Settings> onSave)
        {
            this.onSave = onSave;
            draft = current.Copy();

            AutoScaleMode = AutoScaleMode.None;
            Text = "设置 · " + App.ProductName + " " + App.Version;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(880, 640);
            MinimumSize = new Size(780, 540);
            BackColor = Theme.Bg;
            Font = Theme.Body;
            ForeColor = Theme.Text;
            MinimizeBox = false;

            BuildUi();
            Dpi.ApplyTo(this);
            Ui.DismissFocusOnBlankClick(this);
            LoadInto(draft);
            UpdateModeVisibility();
        }

        // ================================================================ 骨架

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.BackColor = Theme.Bg;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            Controls.Add(root);

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.Padding = new Padding(16, 16, 16, 0);
            body.BackColor = Theme.Bg;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.Controls.Add(body, 0, 0);

            // ---------- 左侧导航 ----------
            Card nav = new Card();
            nav.Dock = DockStyle.Fill;
            nav.Margin = new Padding(0, 0, 14, 0);
            nav.Padding = new Padding(10, 14, 10, 14);
            body.Controls.Add(nav, 0, 0);

            FlowLayoutPanel navFlow = new FlowLayoutPanel();
            navFlow.FlowDirection = FlowDirection.TopDown;
            navFlow.WrapContents = false;
            navFlow.AutoSize = true;
            navFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            navFlow.Dock = DockStyle.Top;
            navFlow.BackColor = Theme.Card;
            nav.Controls.Add(navFlow);

            Label navTitle = new Label();
            navTitle.Text = "设置";
            navTitle.Font = Theme.PageTitle;
            navTitle.ForeColor = Theme.Text;
            navTitle.AutoSize = true;
            navTitle.Margin = new Padding(6, 0, 0, 14);
            navFlow.Controls.Add(navTitle);

            navRules = NavButton("监督名单", delegate { ShowPage(0); });
            navMail = NavButton("邮件设置", delegate { ShowPage(1); });
            navParams = NavButton("规则与其他", delegate { ShowPage(2); });
            navFlow.Controls.Add(navRules);
            navFlow.Controls.Add(navMail);
            navFlow.Controls.Add(navParams);


            pageHost = new Panel();
            pageHost.Dock = DockStyle.Fill;
            pageHost.BackColor = Theme.Bg;
            body.Controls.Add(pageHost, 1, 0);

            TableLayoutPanel pageContent;

            // ============================================================ 页1
            pageRules = Ui.Page(out pageContent);
            pageHost.Controls.Add(pageRules);

            Card cardList = NewCard();
            TableLayoutPanel listStack = Ui.Stack();
            cardList.Controls.Add(listStack);
            Ui.Add(listStack, Section("监督名单"));
            Ui.Add(listStack, Hint("专注期间这些程序在跑，就按各自规则计时。「名称」列默认和「软件」列一样，" +
                                   "可以改成你认得出来的名字（比如把 game 改成「奶龙」），告状邮件里就用这个名字。"));

            grid = BuildGrid();
            Ui.Add(listStack, grid);

            lblGridInfo = new Label();
            lblGridInfo.Font = Theme.BodySmall;
            lblGridInfo.ForeColor = Theme.SubText;
            lblGridInfo.AutoSize = true;
            lblGridInfo.Margin = new Padding(0, 8, 0, 10);
            Ui.Add(listStack, lblGridInfo);

            FlatButton addApp = MakeButton("＋ 添加程序", FlatButton.Kind.Primary, 132);
            addApp.Click += delegate { AddFromRunning(); };
            FlatButton addFile = MakeButton("从 exe 文件选…", FlatButton.Kind.Secondary, 142);
            addFile.Click += delegate { AddFromFile(); };
            FlatButton delSel = MakeButton("删除选中", FlatButton.Kind.Danger, 100);
            delSel.Click += delegate { DeleteSelected(); };
            Ui.Add(listStack, Ui.ButtonRow(addApp, addFile, delSel));
            Ui.AddCard(pageContent, cardList);

            // ============================================================ 页2
            pageMail = Ui.Page(out pageContent);
            pageHost.Controls.Add(pageMail);

            Card cardWho = NewCard();
            TableLayoutPanel who = Ui.Stack();
            cardWho.Controls.Add(who);
            Ui.Add(who, Section("告状邮件发给谁"));
            inSupervisor = new InputBox(false, false);
            inUserName = new InputBox(false, false);
            Ui.Add(who, Ui.Row("监督人邮箱", inSupervisor));
            Ui.Add(who, Ui.Row("你的署名", inUserName));

            cmbMode = new ComboBox();
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.FlatStyle = FlatStyle.Flat;
            cmbMode.Font = Theme.Body;
            cmbMode.BackColor = Theme.Card;
            cmbMode.ForeColor = Theme.Text;
            cmbMode.Items.Add("邮箱授权码（SMTP）—— 推荐，完全免费");
            cmbMode.Items.Add("Resend 邮件 API");
            cmbMode.Items.Add("SendGrid 邮件 API");
            cmbMode.Items.Add("Brevo 邮件 API");
            cmbMode.Items.Add("自定义 HTTP 接口");
            cmbMode.SelectedIndexChanged += delegate { UpdateModeVisibility(); };
            Ui.Add(who, Ui.Row("发送方式", Ui.Field(cmbMode)));

            inSender = new InputBox(false, false);
            Ui.Add(who, Ui.Row("发件邮箱", inSender));

            lblModeHint = new Label();
            lblModeHint.Font = Theme.BodySmall;
            lblModeHint.ForeColor = Theme.SubText;
            lblModeHint.AutoSize = true;
            lblModeHint.MaximumSize = new Size(Dpi.Px(580), 0);
            lblModeHint.Margin = new Padding(0, 4, 0, 0);
            Ui.Add(who, lblModeHint);
            Ui.AddCard(pageContent, cardWho);

            cardSmtp = NewCard();
            TableLayoutPanel smtp = Ui.Stack();
            cardSmtp.Controls.Add(smtp);
            Ui.Add(smtp, Section("邮箱授权码（SMTP）"));
            inSmtpHost = new InputBox(false, false);
            numPort = new NumberBox(1, 65535, 587, "端口");
            inAuth = new InputBox(true, false);
            Ui.Add(smtp, Ui.Row("SMTP 服务器", inSmtpHost));
            Ui.Add(smtp, Ui.Row("端口", numPort));
            Ui.Add(smtp, Ui.Row("授权码", inAuth));

            chkStartTls = new CheckBox();
            StyleCheck(chkStartTls, "使用 STARTTLS（端口 465 请取消勾选，程序会自动改用 SSL）");
            Ui.Add(smtp, chkStartTls);

            Ui.Add(smtp, Hint("QQ 邮箱：网页版邮箱 → 设置 → 账户 → 开启 POP3/SMTP 服务 → 生成 16 位授权码；服务器 smtp.qq.com，端口 587。\r\n" +
                              "163 邮箱：smtp.163.com，端口 465（取消勾选 STARTTLS）。\r\n" +
                              "Gmail：需先开启两步验证并生成「应用专用密码」，smtp.gmail.com，端口 587。"));

            btnProbe = MakeButton("测试连接", FlatButton.Kind.Secondary, 100);
            btnProbe.Click += delegate { ProbeSmtp(); };
            btnTestMail = MakeButton("发送测试邮件", FlatButton.Kind.Secondary, 132);
            btnTestMail.Click += delegate { TestSend(); };
            FlatButton probe = btnProbe;
            FlatButton testMail = btnTestMail;
            FlatButton preview = MakeButton("预览告状邮件", FlatButton.Kind.Secondary, 132);
            preview.Click += delegate { TextForm.Show(this, "告状邮件预览（不会真的发送）", Mailer.PreviewBody(Collect())); };
            Ui.Add(smtp, Ui.ButtonRow(probe, testMail, preview));
            Ui.AddCard(pageContent, cardSmtp);

            cardHttp = NewCard();
            TableLayoutPanel http = Ui.Stack();
            cardHttp.Controls.Add(http);
            Ui.Add(http, Section("HTTP 接口"));
            inApiKey = new InputBox(true, false);
            inHttpUrl = new InputBox(false, false);
            Ui.Add(http, Ui.Row("API Key", inApiKey));
            Ui.Add(http, Ui.Row("自定义地址", inHttpUrl));
            Ui.AddCard(pageContent, cardHttp);

            // ---------------- 第三格：内容自定义 ----------------
            cardContent = NewCard();
            TableLayoutPanel content = Ui.Stack();
            cardContent.Controls.Add(content);
            Ui.Add(content, Section("内容自定义"));
            Ui.Add(content, Hint("下面这三段是**你自己的话**，会加进告状邮件。可以写占位符，发送时程序会换成真实数据；" +
                                "不写占位符也行，你的原文会原样出现。留空则用默认内容。"));

            inSubjectTpl = new InputBox(false, false);
            Ui.Add(content, Ui.Row("邮件标题", inSubjectTpl));

            memoIntro = new InputBox(false, true);
            memoIntro.Height = 72;
            Ui.Add(content, Ui.Row("开头", memoIntro));

            memoOutro = new InputBox(false, true);
            memoOutro.Height = 72;
            Ui.Add(content, Ui.Row("结尾 / 落款", memoOutro));

            FlatButton resetContent = MakeButton("恢复默认内容", FlatButton.Kind.Secondary, 132);
            resetContent.Click += delegate { ResetMailTemplate(); };
            FlatButton previewContent = MakeButton("预览效果", FlatButton.Kind.Primary, 106);
            previewContent.Click += delegate
            {
                TextForm.Show(this, "告状邮件预览（不会真的发送）", Mailer.PreviewBody(Collect()));
            };
            Ui.Add(content, Ui.ButtonRow(resetContent, previewContent));

            Ui.Add(content, Section("可用占位符（写进上面三段里，发送时替换）"));
            DataGridView ph = new DataGridView();
            ph.Dock = DockStyle.Top;
            ph.Height = 250;
            ph.Margin = new Padding(0, 2, 0, 0);
            Ui.StyleGrid(ph);
            ph.ReadOnly = true;
            ph.AllowUserToAddRows = false;
            ph.RowHeadersVisible = false;
            ph.MultiSelect = false;
            ph.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            DataGridViewTextBoxColumn phKey = new DataGridViewTextBoxColumn();
            phKey.HeaderText = "占位符";
            phKey.Name = "phKey";
            phKey.Width = 150;
            phKey.SortMode = DataGridViewColumnSortMode.NotSortable;
            ph.Columns.Add(phKey);
            DataGridViewTextBoxColumn phMean = new DataGridViewTextBoxColumn();
            phMean.HeaderText = "会替换成";
            phMean.Name = "phMean";
            phMean.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            phMean.SortMode = DataGridViewColumnSortMode.NotSortable;
            ph.Columns.Add(phMean);
            List<PlaceholderInfo> phs = Mailer.Placeholders();
            for (int i = 0; i < phs.Count; i++)
            {
                int idx = ph.Rows.Add(phs[i].Key, phs[i].Meaning + (phs[i].DataBlock ? "（程序统计，永远会出现在邮件里）" : ""));
                if (phs[i].DataBlock) ph.Rows[idx].DefaultCellStyle.ForeColor = Theme.Ok;
            }
            ph.ClearSelection();
            ph.CurrentCell = null;
            Ui.Add(content, ph);

            Ui.Add(content, Section("程序固定插入的内容（不可编辑，也不会丢）"));
            txtFixed = new TextBox();
            txtFixed.Multiline = true;
            txtFixed.ReadOnly = true;
            txtFixed.ScrollBars = ScrollBars.Vertical;
            txtFixed.WordWrap = true;
            txtFixed.BorderStyle = BorderStyle.None;
            txtFixed.BackColor = Theme.Bg;
            txtFixed.ForeColor = Theme.SubText;
            txtFixed.Font = Theme.BodySmall;
            txtFixed.Height = 186;
            txtFixed.Dock = DockStyle.Top;
            Card fixedCard = new Card();
            fixedCard.Dock = DockStyle.Top;
            fixedCard.Height = 202;
            fixedCard.Padding = new Padding(10, 8, 10, 8);
            fixedCard.Margin = new Padding(0, 2, 0, 0);
            fixedCard.Controls.Add(txtFixed);
            Ui.Add(content, fixedCard);
            Ui.Add(content, Hint("这一块由程序生成（含每个软件的累计使用时长、时间线、今日累计等），" +
                                "不来自上面三段自定义内容，所以你改不掉、也不会不小心删掉。"));

            Ui.AddCard(pageContent, cardContent);

            // ============================================================ 页3
            pageParams = Ui.Page(out pageContent);
            pageHost.Controls.Add(pageParams);

            Card cardRule = NewCard();
            TableLayoutPanel rule = Ui.Stack();
            cardRule.Controls.Add(rule);
            Ui.Add(rule, Section("专注规则"));
            numFocus = new NumberBox(1, 600, 25, "分钟");
            numViolation = new NumberBox(1, 600, 3, "分钟");
            numSample = new NumberBox(2, 120, 5, "秒");
            Ui.Add(rule, Ui.Row("专注时长", numFocus));
            Ui.Add(rule, Ui.Row("新程序默认规则时长", numViolation));
            Ui.Add(rule, Ui.Row("检测间隔", numSample));

            chkOnlyAfter = new CheckBox();
            StyleCheck(chkOnlyAfter, "只统计「专注开始之后」才启动的程序（默认关闭）");
            Ui.Add(rule, chkOnlyAfter);

            Ui.Add(rule, Hint("告状规则：只有「没坚持完」或「某个程序超过它自己的规则时长」才发邮件，一段专注最多发一封。" +
                              "电脑睡眠期间不计时，也不会因此告状。"));
            Ui.AddCard(pageContent, cardRule);

            Card cardOther = NewCard();
            TableLayoutPanel other = Ui.Stack();
            cardOther.Controls.Add(other);
            Ui.Add(other, Section("专注完成时"));
            chkNotify = new CheckBox();
            StyleCheck(chkNotify, "弹托盘气泡提醒我这一段走完了");
            chkSound = new CheckBox();
            StyleCheck(chkSound, "专注走完时播放");

            // 「提示音」三个字做成蓝色下划线链接：点一下试听（走的就是真触发时的播放实现）
            lnkSoundTest = new LinkLabel();
            lnkSoundTest.Text = "提示音";
            lnkSoundTest.Font = Theme.Body;
            lnkSoundTest.ForeColor = Theme.SubText;
            lnkSoundTest.LinkColor = Theme.Link;
            lnkSoundTest.ActiveLinkColor = Theme.LinkActive;
            lnkSoundTest.VisitedLinkColor = Theme.Link;          // 点过也保持蓝色
            lnkSoundTest.LinkBehavior = LinkBehavior.AlwaysUnderline;
            lnkSoundTest.AutoSize = true;
            lnkSoundTest.Cursor = Cursors.Hand;
            lnkSoundTest.Margin = new Padding(0, 5, 0, 10);
            lnkSoundTest.LinkArea = new LinkArea(0, 3);           // 正好是「提示音」三个字
            lnkSoundTest.LinkClicked += OnSoundPreviewClicked;

            // 一行两列：复选框 + 「提示音」链接。
            // 这里必须用 TableLayoutPanel：FlowLayoutPanel 按"顶边 + 外边距"摆放孩子，
            // 而复选框带着方框（控件更高）、链接只有文字，两者顶边一对齐文字就错开了
            // （用户一眼就看出来了）。TableLayoutPanel 里两个控件都 Anchor=Left 是**垂直居中**，
            // 文字基线才对得齐，而且不受 DPI 影响。
            TableLayoutPanel soundRow = new TableLayoutPanel();
            soundRow.ColumnCount = 2;
            soundRow.RowCount = 1;
            soundRow.AutoSize = true;
            soundRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            soundRow.BackColor = Theme.Card;
            soundRow.Margin = new Padding(0);
            soundRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            soundRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            chkSound.Anchor = AnchorStyles.Left;
            chkSound.Margin = new Padding(0, 0, 0, 6);
            lnkSoundTest.Anchor = AnchorStyles.Left;
            lnkSoundTest.Margin = new Padding(4, 0, 0, 6);
            soundRow.Controls.Add(chkSound, 0, 0);
            soundRow.Controls.Add(lnkSoundTest, 1, 0);

            Ui.Add(other, chkNotify);
            Ui.Add(other, soundRow);
            // 就一句话，别堆灰字（用户：那两行说明都多余）
            Ui.Add(other, Hint("点击提示音试听"));

            Ui.Add(other, Section("启动与安全"));
            chkAutoStart = new CheckBox();
            StyleCheck(chkAutoStart, "开机自动启动（缩在托盘里）");
            chkTray = new CheckBox();
            StyleCheck(chkTray, "点关闭按钮时缩到托盘，不退出");
            Ui.Add(other, chkAutoStart);
            Ui.Add(other, chkTray);

            FlatButton pw = MakeButton("修改密码", FlatButton.Kind.Secondary, 100);
            pw.Click += delegate { ChangePassword(); };
            FlatButton open = MakeButton("打开数据文件夹", FlatButton.Kind.Secondary, 132);
            open.Click += delegate { OpenDataFolder(); };
            Ui.Add(other, Ui.ButtonRow(pw, open));
            Ui.AddCard(pageContent, cardOther);

            // ---------- 底部 ----------
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            bottom.BackColor = Theme.Bg;
            bottom.Padding = new Padding(16, 0, 16, 14);
            root.Controls.Add(bottom, 0, 1);

            FlatButton save = MakeButton("保存", FlatButton.Kind.Primary, 96);
            save.Height = 36;
            save.Click += delegate { Save(); };
            FlatButton close = MakeButton("关闭", FlatButton.Kind.Secondary, 96);
            close.Height = 36;
            close.Click += delegate
            {
                DialogResult = savedAnything ? DialogResult.OK : DialogResult.Cancel;
                Close();
            };

            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.ColumnCount = 3;
            bar.RowCount = 1;
            bar.BackColor = Theme.Bg;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));   // 左：保存状态
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // 中：版本号 + 作者
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // 右：按钮
            bottom.Controls.Add(bar);

            lblSaveState = new Label();
            lblSaveState.Dock = DockStyle.Fill;
            lblSaveState.TextAlign = ContentAlignment.MiddleLeft;
            lblSaveState.Font = Theme.BodySmall;
            lblSaveState.ForeColor = Theme.Ok;
            lblSaveState.AutoSize = false;
            lblSaveState.Text = "";
            bar.Controls.Add(lblSaveState, 0, 0);

            // 版本号 + 作者链接（作者名可点，打开 GitHub 仓库）
            string creditText = "PomoCC " + App.Version + " · " + App.Author;
            lnkAuthor = new LinkLabel();
            lnkAuthor.Text = creditText;
            lnkAuthor.Font = Theme.BodySmall;
            lnkAuthor.ForeColor = Theme.SubText;
            lnkAuthor.LinkColor = Theme.Link;
            lnkAuthor.ActiveLinkColor = Theme.LinkActive;
            lnkAuthor.VisitedLinkColor = Theme.Link;          // 点过也保持蓝色
            lnkAuthor.LinkBehavior = LinkBehavior.AlwaysUnderline;
            lnkAuthor.AutoSize = true;
            lnkAuthor.Anchor = AnchorStyles.Right;            // 靠右、垂直居中
            lnkAuthor.Margin = new Padding(0, 0, 18, 0);      // 与按钮留出间距
            lnkAuthor.LinkArea = new LinkArea(creditText.IndexOf(App.Author), App.Author.Length);
            lnkAuthor.LinkClicked += delegate { OpenUrl(App.RepoUrl); };
            bar.Controls.Add(lnkAuthor, 1, 0);

            FlowLayoutPanel rightButtons = Ui.RightButtonRow(save, close);
            rightButtons.Dock = DockStyle.Fill;
            bar.Controls.Add(rightButtons, 2, 0);

            CancelButton = close;

            ShowPage(0);
        }

        // ================================================================ 小工具

        private static Card NewCard()
        {
            Card c = new Card();
            c.Margin = new Padding(0, 0, 0, 14);
            return c;
        }

        private static FlatButton MakeButton(string text, FlatButton.Kind kind, int width)
        {
            FlatButton b = new FlatButton(text, kind);
            b.Width = width;
            return b;
        }

        /// <summary>
        /// 打开数据目录。按钮事件若因 WinForms 消息重复到达，短时间内只允许启动一次外壳进程。
        /// 直接把目录交给 Shell，避免手动拼接 explorer.exe 参数造成额外窗口行为。
        /// </summary>
        private void OpenDataFolder()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastDataFolderOpenUtc).TotalMilliseconds < 800) return;
            lastDataFolderOpenUtc = now;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Store.Dir,
                    UseShellExecute = true
                });
            }
            catch
            {
                // 保留原行为：打开失败不打断设置窗口；失败后允许下次点击重试。
                lastDataFolderOpenUtc = DateTime.MinValue;
            }
        }

        private FlatButton NavButton(string text, EventHandler onClick)
        {
            FlatButton b = new FlatButton(text, FlatButton.Kind.Ghost);
            b.ContentAlign = ContentAlignment.MiddleLeft;
            b.Width = 156;
            b.Height = 36;
            b.Margin = new Padding(0, 0, 0, 4);
            b.Click += onClick;
            return b;
        }

        private static Label Section(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.SectionTitle;
            l.ForeColor = Theme.Text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 0, 0, 10);
            return l;
        }

        private static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.BodySmall;
            l.ForeColor = Theme.SubText;
            l.AutoSize = true;
            l.MaximumSize = new Size(Dpi.Px(600), 0);
            l.Margin = new Padding(0, 0, 0, 12);
            return l;
        }

        private static void StyleCheck(CheckBox c, string text)
        {
            c.Text = text;
            c.Font = Theme.Body;
            c.ForeColor = Theme.Text;
            c.FlatStyle = FlatStyle.Flat;
            c.AutoSize = true;
            c.Margin = new Padding(0, 2, 0, 10);
        }

        private void ShowPage(int index)
        {
            pageRules.Visible = (index == 0);
            pageMail.Visible = (index == 1);
            pageParams.Visible = (index == 2);
            navRules.Selected = (index == 0);
            navMail.Selected = (index == 1);
            navParams.Selected = (index == 2);
        }

        // ================================================================ 表格

        private DataGridView BuildGrid()
        {
            DataGridView g = new DataGridView();
            g.Dock = DockStyle.Top;
            g.Height = 196;
            Ui.StyleGrid(g);

            DataGridViewTextBoxColumn colName = new DataGridViewTextBoxColumn();
            colName.HeaderText = "名称";
            colName.Name = "colName";
            colName.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colName.FillWeight = 36;
            colName.SortMode = DataGridViewColumnSortMode.NotSortable;
            g.Columns.Add(colName);

            DataGridViewTextBoxColumn colApp = new DataGridViewTextBoxColumn();
            colApp.HeaderText = "软件";
            colApp.Name = "colApp";
            colApp.ReadOnly = true;
            colApp.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colApp.FillWeight = 40;
            colApp.SortMode = DataGridViewColumnSortMode.NotSortable;
            g.Columns.Add(colApp);

            DataGridViewTextBoxColumn colLimit = new DataGridViewTextBoxColumn();
            colLimit.HeaderText = "规则时长（分钟）";
            colLimit.Name = "colLimit";
            colLimit.Width = 168;      // 够宽，表头才会单行显示
            colLimit.SortMode = DataGridViewColumnSortMode.NotSortable;
            g.Columns.Add(colLimit);

            DataGridViewLinkColumn colDel = new DataGridViewLinkColumn();
            colDel.HeaderText = "操作";
            colDel.Name = "colDel";
            colDel.Text = "删除";
            colDel.UseColumnTextForLinkValue = true;
            colDel.LinkColor = Theme.Danger;
            colDel.ActiveLinkColor = Theme.AccentPressed;
            colDel.VisitedLinkColor = Theme.Danger;
            colDel.TrackVisitedState = false;
            colDel.Width = 72;
            colDel.SortMode = DataGridViewColumnSortMode.NotSortable;
            g.Columns.Add(colDel);

            g.CellContentClick += OnGridClick;
            g.CellEndEdit += OnGridEndEdit;
            g.RowsAdded += delegate { UpdateGridInfo(); };
            g.RowsRemoved += delegate { UpdateGridInfo(); };
            g.DataError += delegate(object s, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            return g;
        }

        private void UpdateGridInfo()
        {
            if (lblGridInfo == null) return;
            // 去掉"默认选中第一行"的高亮，界面更干净
            if (grid != null && grid.Rows.Count > 0)
            {
                grid.ClearSelection();
                grid.CurrentCell = null;
            }
            int n = grid == null ? 0 : grid.Rows.Count;
            lblGridInfo.Text = n == 0
                ? "还没有添加程序。点下面的「＋ 添加程序」从正在运行的程序里选。"
                : string.Format("共 {0} 个程序。「名称」列双击即可修改，邮件里用的就是这个名字。", n);
        }

        private void AddRow(WatchRule r)
        {
            r.ApplyDefaultName();
            int i = grid.Rows.Add(r.Name, r.SoftwareText, r.LimitMinutes, "删除");
            grid.Rows[i].Tag = r;
            grid.Rows[i].Cells["colApp"].ToolTipText = string.IsNullOrEmpty(r.ExePath) ? r.Exe : r.ExePath;
            grid.Rows[i].Cells["colName"].ToolTipText = "改成你认得出来的名字，例如「奶龙」";
            grid.Rows[i].Cells["colLimit"].ToolTipText = "专注期间这个程序累计跑超过多少分钟就告状";
            UpdateGridInfo();
        }

        private void OnGridClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "colDel") return;
            grid.Rows.RemoveAt(e.RowIndex);
            UpdateGridInfo();
        }

        private void OnGridEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            DataGridViewRow row = grid.Rows[e.RowIndex];
            WatchRule r = row.Tag as WatchRule;
            if (r == null) return;

            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "colName")
            {
                string v = Convert.ToString(row.Cells["colName"].Value);
                v = v == null ? "" : v.Trim();
                if (v.Length == 0) v = r.SoftwareText;      // 清空就还原成默认
                if (v.Length > 40) v = v.Substring(0, 40);
                r.Name = v;
                row.Cells["colName"].Value = v;
                return;
            }

            if (col == "colLimit")
            {
                int v;
                if (!int.TryParse(Convert.ToString(row.Cells["colLimit"].Value), out v) || v < 1 || v > 600)
                {
                    MessageBox.Show(this, "规则时长要填 1~600 之间的整数（分钟）。", "规则时长",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    row.Cells["colLimit"].Value = r.LimitMinutes > 0 ? r.LimitMinutes : 3;
                    return;
                }
                r.LimitMinutes = v;
                row.Cells["colLimit"].Value = v;
            }
        }

        private void AddRules(List<WatchRule> add)
        {
            for (int i = 0; i < add.Count; i++)
            {
                WatchRule r = add[i];
                if (r == null || string.IsNullOrEmpty(r.Exe)) continue;

                bool merged = false;
                for (int rowIndex = 0; rowIndex < grid.Rows.Count; rowIndex++)
                {
                    WatchRule existing = grid.Rows[rowIndex].Tag as WatchRule;
                    if (existing == null) continue;
                    if (!string.Equals(existing.Exe, r.Exe, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!string.IsNullOrEmpty(r.DisplayName)) existing.DisplayName = r.DisplayName;
                    if (!string.IsNullOrEmpty(r.ExePath)) existing.ExePath = r.ExePath;
                    existing.ApplyDefaultName();
                    grid.Rows[rowIndex].Cells["colApp"].Value = existing.SoftwareText;
                    grid.Rows[rowIndex].Cells["colName"].Value = existing.Name;
                    merged = true;
                    break;
                }
                if (!merged) AddRow(r);
            }
            grid.ClearSelection();
            UpdateGridInfo();
        }

        private void AddFromRunning()
        {
            using (AppPickerForm picker = new AppPickerForm(numViolation.Value))
            {
                if (picker.ShowDialog(this) == DialogResult.OK) AddRules(picker.Picked);
            }
        }

        private void AddFromFile()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要监督的程序";
                dlg.Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                List<WatchRule> add = new List<WatchRule>();
                for (int i = 0; i < dlg.FileNames.Length; i++)
                {
                    string path = dlg.FileNames[i];
                    WatchRule r = new WatchRule();
                    r.Exe = ProcessMonitor.NormalizeName(path);
                    r.ExePath = path;
                    r.DisplayName = AppCatalog.Describe(path);
                    if (string.IsNullOrEmpty(r.DisplayName)) r.DisplayName = Path.GetFileNameWithoutExtension(path);
                    r.LimitMinutes = numViolation.Value;
                    r.Enabled = true;
                    add.Add(r);
                }
                AddRules(add);
            }
        }

        private void DeleteSelected()
        {
            if (grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "先在表格里点一行（可按住 Ctrl 多选），再点「删除选中」。",
                    "删除监督程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<DataGridViewRow> rows = new List<DataGridViewRow>();
            for (int i = 0; i < grid.SelectedRows.Count; i++) rows.Add(grid.SelectedRows[i]);
            rows.Sort(delegate(DataGridViewRow a, DataGridViewRow b) { return b.Index.CompareTo(a.Index); });
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Index >= 0 && rows[i].Index < grid.Rows.Count) grid.Rows.RemoveAt(rows[i].Index);
            }
            grid.ClearSelection();
            UpdateGridInfo();
        }

        // ================================================================ 数据

        private void LoadInto(Settings s)
        {
            inSupervisor.Box.Text = s.SupervisorEmail;
            inUserName.Box.Text = s.UserName;
            inSender.Box.Text = s.SenderEmail;
            inSmtpHost.Box.Text = s.SmtpHost;
            numPort.Value = s.SmtpPort;
            chkStartTls.Checked = s.SmtpStartTls;
            inAuth.Box.Text = s.GetAuthCode();
            inApiKey.Box.Text = s.GetApiKey();
            inHttpUrl.Box.Text = s.HttpUrl;

            // 内容自定义：留空就显示默认模板（让用户看到默认长什么样，改起来有参照）
            inSubjectTpl.Box.Text = Mailer.TemplateOrDefault(s.MailSubjectTemplate, Mailer.DefaultSubject);
            memoIntro.Box.Text = Mailer.TemplateOrDefault(s.MailIntroTemplate, Mailer.DefaultIntro);
            memoOutro.Box.Text = Mailer.TemplateOrDefault(s.MailOutroTemplate, Mailer.DefaultOutro);
            if (txtFixed != null) txtFixed.Text = Mailer.FixedBlockPreview(s);

            grid.Rows.Clear();
            if (s.Rules != null)
            {
                for (int i = 0; i < s.Rules.Count; i++)
                {
                    if (s.Rules[i] == null || string.IsNullOrEmpty(s.Rules[i].Exe)) continue;
                    AddRow(s.Rules[i].Clone());
                }
            }
            UpdateGridInfo();

            numFocus.Value = s.FocusMinutes;
            numViolation.Value = (int)Math.Round(s.ViolationSeconds / 60.0);
            numSample.Value = s.SampleSeconds;
            chkOnlyAfter.Checked = s.OnlyCountAfterStart;
            chkAutoStart.Checked = s.AutoStart;
            chkTray.Checked = s.TrayOnClose;
            chkNotify.Checked = s.NotifyOnComplete;
            chkSound.Checked = s.NotifySound;

            switch (s.SendMode)
            {
                case "resend": cmbMode.SelectedIndex = 1; break;
                case "sendgrid": cmbMode.SelectedIndex = 2; break;
                case "brevo": cmbMode.SelectedIndex = 3; break;
                case "custom": cmbMode.SelectedIndex = 4; break;
                default: cmbMode.SelectedIndex = 0; break;
            }
        }

        private string ModeValue()
        {
            switch (cmbMode.SelectedIndex)
            {
                case 1: return "resend";
                case 2: return "sendgrid";
                case 3: return "brevo";
                case 4: return "custom";
                default: return "smtp";
            }
        }

        private void UpdateModeVisibility()
        {
            string mode = ModeValue();
            cardSmtp.Visible = (mode == "smtp");
            cardHttp.Visible = (mode != "smtp");
            inHttpUrl.Box.Enabled = (mode != "smtp");

            switch (mode)
            {
                case "smtp":
                    lblModeHint.Text = "用你自己的邮箱直接发信，完全免费，不需要第三方账号。";
                    break;
                case "resend":
                    lblModeHint.Text = "Resend 免费额度需要先绑定自己的域名才能发给别人，一般不建议。";
                    break;
                case "sendgrid":
                    lblModeHint.Text = "SendGrid 免费 100 封/天，验证一个发件邮箱即可，不用买域名。";
                    break;
                case "brevo":
                    lblModeHint.Text = "Brevo 免费 300 封/天，验证发件邮箱即可。";
                    break;
                default:
                    lblModeHint.Text = "程序会把 {\"from\":…,\"to\":…,\"subject\":…,\"text\":…} POST 到这个地址。";
                    break;
            }
        }

        /// <summary>文本框内容与默认模板一致（忽略首尾空白）时返回 true。</summary>
        private static bool SameAsDefault(string text, string fallback)
        {
            string a = text == null ? "" : text.Trim();
            return a == fallback.Trim();
        }

        /// <summary>把三段自定义内容恢复成默认模板。</summary>
        private void ResetMailTemplate()
        {
            inSubjectTpl.Box.Text = Mailer.DefaultSubject;
            memoIntro.Box.Text = Mailer.DefaultIntro;
            memoOutro.Box.Text = Mailer.DefaultOutro;
            lblSaveState.ForeColor = Theme.SubText;
            lblSaveState.Text = "已恢复默认内容（记得点「保存」）";
        }

        private Settings Collect()
        {
            Settings s = draft.Copy();
            s.SupervisorEmail = inSupervisor.Box.Text.Trim();
            s.UserName = inUserName.Box.Text.Trim();
            s.SendMode = ModeValue();
            s.SenderEmail = inSender.Box.Text.Trim();
            s.SmtpHost = inSmtpHost.Box.Text.Trim();
            s.SmtpPort = numPort.Value;
            s.SmtpStartTls = chkStartTls.Checked;
            s.SetAuthCode(inAuth.Box.Text);
            s.SetApiKey(inApiKey.Box.Text);
            s.HttpUrl = inHttpUrl.Box.Text.Trim();

            // 内容自定义：与默认模板完全一致就存空字符串（这样以后程序升级默认文案能跟着更新）
            s.MailSubjectTemplate = SameAsDefault(inSubjectTpl.Box.Text, Mailer.DefaultSubject) ? "" : inSubjectTpl.Box.Text.Trim();
            s.MailIntroTemplate = SameAsDefault(memoIntro.Box.Text, Mailer.DefaultIntro) ? "" : memoIntro.Box.Text.Trim();
            s.MailOutroTemplate = SameAsDefault(memoOutro.Box.Text, Mailer.DefaultOutro) ? "" : memoOutro.Box.Text.Trim();

            List<WatchRule> rules = new List<WatchRule>();
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                WatchRule r = grid.Rows[i].Tag as WatchRule;
                if (r == null) continue;
                WatchRule copy = r.Clone();
                copy.Exe = ProcessMonitor.NormalizeName(copy.Exe);
                if (copy.Exe.Length == 0) continue;

                string nameText = Convert.ToString(grid.Rows[i].Cells["colName"].Value);
                copy.Name = nameText == null ? "" : nameText.Trim();
                if (copy.Name.Length == 0) copy.ApplyDefaultName();

                int v;
                if (int.TryParse(Convert.ToString(grid.Rows[i].Cells["colLimit"].Value), out v) && v >= 1 && v <= 600)
                    copy.LimitMinutes = v;
                rules.Add(copy);
            }
            s.Rules = rules;
            s.WatchList = new List<string>();

            s.FocusMinutes = numFocus.Value;
            s.ViolationSeconds = numViolation.Value * 60;
            s.SampleSeconds = numSample.Value;
            s.OnlyCountAfterStart = chkOnlyAfter.Checked;
            s.AutoStart = chkAutoStart.Checked;
            s.TrayOnClose = chkTray.Checked;
            s.NotifyOnComplete = chkNotify.Checked;
            s.NotifySound = chkSound.Checked;
            return s;
        }

        /// <summary>
        /// 点「保存」：立即写盘并让主窗口生效，但**不关闭窗口** ——
        /// 用户可能还在继续浏览/修改其它设置，只在左下角给一个「已保存」的反馈。
        /// </summary>
        private void Save()
        {
            Settings s = Collect();
            List<string> errs = SettingsValidator.Validate(s);
            if (errs.Count > 0)
            {
                if (lblSaveState != null)
                {
                    lblSaveState.ForeColor = Theme.Danger;
                    lblSaveState.Text = "未保存：" + errs[0];
                }
                if (!App.Headless)
                {
                    MessageBox.Show(this, "还有几项要改：\r\n\r\n· " + string.Join("\r\n· ", errs.ToArray()),
                        "设置没保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    Store.Log("[save] 校验未通过：" + string.Join("；", errs.ToArray()));
                }
                return;
            }

            if (onSave != null)
            {
                try
                {
                    onSave(s);
                }
                catch (Exception ex)
                {
                    if (lblSaveState != null)
                    {
                        lblSaveState.ForeColor = Theme.Danger;
                        lblSaveState.Text = "保存失败：" + ex.Message;
                    }
                    return;
                }
            }

            Result = s;
            draft = s.Copy();
            savedAnything = true;
            bool focusingNow = SessionActive != null && SessionActive();
            if (lblSaveState != null)
            {
                lblSaveState.ForeColor = Theme.Ok;
                lblSaveState.Text = focusingNow
                    ? "已保存\r\n" + NextSessionNotice
                    : "已保存";
            }
        }

        /// <summary>
        /// 专注期间保存设置时的提示。产品决策：专注中允许改设置，但本段继续用原设置，
        /// 下一段专注才生效 —— 免得用户在专注中途改规则把当前这段"改没了"。
        /// </summary>
        internal const string NextSessionNotice = "本次专注继续使用原设置，新设置将在下一段专注开始时生效。";

        /// <summary>「提示音」链接绑定的处理函数（自检也触发这一处，保证验的是同一段代码）。</summary>
        private void OnSoundPreviewClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Supervisor.PlayNotifySound();
        }

        /// <summary>供自检：「提示音」试听链接（三个字必须是可点区域）。</summary>
        internal LinkLabel SoundPreviewLink { get { return lnkSoundTest; } }

        /// <summary>供自检：提示音复选框本身（开关仍然照常工作）。</summary>
        internal CheckBox SoundCheckBox { get { return chkSound; } }

        /// <summary>供自检：触发「提示音」链接绑定的处理函数（等价于真的点了链接）。</summary>
        internal void RaiseSoundPreviewForTest() { OnSoundPreviewClicked(lnkSoundTest, null); }

        /// <summary>供自检：是否保存过 / 保存状态文字。</summary>
        internal bool SavedAnything { get { return savedAnything; } }
        internal string SaveStateText { get { return lblSaveState == null ? "" : lblSaveState.Text; } }

        private void ProbeSmtp()
        {
            string host = inSmtpHost.Box.Text.Trim();
            if (host.Length == 0)
            {
                MessageBox.Show(this, "先填 SMTP 服务器地址。", "连接测试",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 放到后台线程连接，界面不卡；按钮变「测试中…」同时防重复点击
            int port = numPort.Value;
            bool startTls = chkStartTls.Checked;
            AsyncMail.Run(this, btnProbe, "测试中…", "测试连接",
                delegate { return SmtpTransport.Probe(host, port, startTls); },
                delegate(bool ok, string msg)
                {
                    if (ok) MessageBox.Show(this, msg, "连接测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show(this, "连不上：\r\n\r\n" + msg, "连接测试", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
        }

        private void ChangePassword()
        {
            if (draft.HasPassword())
            {
                string old = PasswordDialog.Ask(this, "修改密码", "先输入现在的密码：");
                if (old == null) return;
                if (!draft.CheckPassword(old))
                {
                    MessageBox.Show(this, "现在的密码不对。", "番茄钟监督", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            string pw = PasswordDialog.AskNew(this, "设置新密码");
            if (string.IsNullOrEmpty(pw)) return;
            draft.SetPassword(pw);
            MessageBox.Show(this, "密码已修改，点「保存」后生效。", "番茄钟监督",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void TestSend()
        {
            Settings s = Collect();

            List<string> errs = new List<string>();
            string why;
            if (!SettingsValidator.IsValidEmail(s.SupervisorEmail, out why)) errs.Add("监督人邮箱：" + why + "。");
            if (!SettingsValidator.IsValidEmail(s.SenderEmail, out why)) errs.Add("发件邮箱：" + why + "。");
            if (s.SendMode == "smtp")
            {
                if (string.IsNullOrEmpty(s.SmtpHost)) errs.Add("没填 SMTP 服务器。");
                if (string.IsNullOrEmpty(s.GetAuthCode())) errs.Add("没填授权码。");
            }
            else
            {
                if (s.SendMode == "custom" && !SettingsValidator.IsAllowedHttpUrl(s.HttpUrl, out why))
                    errs.Add("自定义接口地址：" + why + "。");
                if (string.IsNullOrEmpty(s.GetApiKey())) errs.Add("没填 API Key。");
            }
            if (errs.Count > 0)
            {
                MessageBox.Show(this, "还不能发：\r\n\r\n· " + string.Join("\r\n· ", errs.ToArray()),
                    "测试发信", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 后台线程发信：超时/不可达时界面照常可用，发送期间按钮显示「发送中…」
            AsyncMail.Run(this, btnTestMail, "发送中…", "发送测试邮件",
                delegate
                {
                    Mailer.Send(s, "【番茄钟监督】测试邮件",
                        "这是一封测试邮件。\r\n\r\n收到它说明「番茄钟监督」的发信通道已经配置好了。\r\n" +
                        "以后你在规定时间里没坚持完专注、或者某个程序超过了它自己的规则时长，都会通过这个通道自动发信。\r\n");
                    return "发送成功，去收件箱（和垃圾箱）看一眼。";
                },
                delegate(bool ok, string msg)
                {
                    if (ok) MessageBox.Show(this, msg, "测试发信", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show(this, "发送失败：\r\n\r\n" + msg, "测试发信", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
        }
    }
}
