using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PomodoroSupervisor
{
    /// <summary>
    /// 「从正在运行的程序里选」——不用手敲进程名。
    /// 默认只列有窗口的程序，可切到全部后台进程；支持搜索、多选、双击添加。
    /// </summary>
    public class AppPickerForm : Form
    {
        public List<WatchRule> Picked = new List<WatchRule>();

        private int defaultLimit;
        private List<RunningAppInfo> apps = new List<RunningAppInfo>();
        private bool loaded;

        private DataGridView grid;
        private InputBox search;
        private CheckBox showBackground;
        private Label status;
        private FlatButton okButton;

        public bool HasApps { get { return apps.Count > 0; } }
        public int AppCount { get { return apps.Count; } }

        public AppPickerForm(int defaultLimitMinutes)
        {
            defaultLimit = defaultLimitMinutes < 1 ? 3 : defaultLimitMinutes;

            AutoScaleMode = AutoScaleMode.None;
            Text = "添加监督程序";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 580);
            MinimumSize = new Size(600, 440);
            BackColor = Theme.Bg;
            Font = Theme.Body;
            ForeColor = Theme.Text;
            MinimizeBox = false;
            ShowInTaskbar = false;

            BuildUi();
            Dpi.ApplyTo(this);
            Ui.DismissFocusOnBlankClick(this);
            Shown += delegate { Reload(false); };
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(16, 16, 16, 14);
            root.BackColor = Theme.Bg;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            Controls.Add(root);

            // ---------- 顶部：标题 + 搜索 ----------
            Card head = new Card();
            head.Dock = DockStyle.Fill;
            head.Margin = new Padding(0, 0, 0, 12);
            head.Padding = new Padding(16, 10, 16, 10);
            root.Controls.Add(head, 0, 0);

            TableLayoutPanel headRow = new TableLayoutPanel();
            headRow.Dock = DockStyle.Fill;
            headRow.ColumnCount = 3;
            headRow.RowCount = 1;
            headRow.BackColor = Theme.Card;
            headRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            headRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300F));
            headRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            headRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 固定行高，避免 AutoSize 把卡片撑爆
            head.Controls.Add(headRow);

            Label title = new Label();
            title.Text = "选择要监督的程序";
            title.Font = Theme.SectionTitle;
            title.ForeColor = Theme.Text;
            title.Dock = DockStyle.Fill;
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleLeft;
            headRow.Controls.Add(title, 0, 0);

            search = new InputBox(false, false);
            search.Box.Font = Theme.Body;
            search.Dock = DockStyle.Fill;
            search.Margin = new Padding(0, 4, 12, 4);
            search.Box.TextChanged += delegate { FillList(); };
            SetPlaceholder(search.Box, "搜索程序名…");
            headRow.Controls.Add(search, 1, 0);

            showBackground = new CheckBox();
            showBackground.Text = "显示后台进程";
            showBackground.Font = Theme.Body;
            showBackground.ForeColor = Theme.Text;
            showBackground.FlatStyle = FlatStyle.Flat;
            showBackground.AutoSize = false;
            showBackground.Dock = DockStyle.Fill;
            showBackground.CheckedChanged += delegate { Reload(showBackground.Checked); };
            headRow.Controls.Add(showBackground, 2, 0);

            // ---------- 列表 ----------
            Card listCard = new Card();
            listCard.Dock = DockStyle.Fill;
            listCard.Margin = new Padding(0, 0, 0, 12);
            listCard.Padding = new Padding(8);
            root.Controls.Add(listCard, 0, 1);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            Ui.StyleGrid(grid);
            grid.ReadOnly = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0) Accept();
            };

            DataGridViewImageColumn colIcon = new DataGridViewImageColumn();
            colIcon.HeaderText = "";
            colIcon.Name = "colIcon";
            colIcon.Width = 44;
            colIcon.ImageLayout = DataGridViewImageCellLayout.Zoom;
            colIcon.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(colIcon);

            DataGridViewTextBoxColumn colName = new DataGridViewTextBoxColumn();
            colName.HeaderText = "程序";
            colName.Name = "colName";
            colName.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colName.FillWeight = 40;
            colName.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(colName);

            DataGridViewTextBoxColumn colExe = new DataGridViewTextBoxColumn();
            colExe.HeaderText = "进程名";
            colExe.Name = "colExe";
            colExe.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colExe.FillWeight = 26;
            colExe.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(colExe);

            DataGridViewTextBoxColumn colTitle = new DataGridViewTextBoxColumn();
            colTitle.HeaderText = "窗口标题 / 状态";
            colTitle.Name = "colTitle";
            colTitle.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            colTitle.FillWeight = 34;
            colTitle.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(colTitle);

            grid.SelectionChanged += delegate { UpdateStatus(); };
            listCard.Controls.Add(grid);

            // ---------- 底部 ----------
            TableLayoutPanel foot = new TableLayoutPanel();
            foot.Dock = DockStyle.Fill;
            foot.ColumnCount = 2;
            foot.RowCount = 1;
            foot.BackColor = Theme.Bg;
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.Controls.Add(foot, 0, 2);

            status = new Label();
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Font = Theme.BodySmall;
            status.ForeColor = Theme.SubText;
            status.AutoSize = false;
            foot.Controls.Add(status, 0, 0);

            FlatButton reload = new FlatButton("刷新列表", FlatButton.Kind.Ghost);
            reload.Width = 92;
            reload.Click += delegate { Reload(showBackground.Checked); };
            FlatButton browse = new FlatButton("从 exe 文件选…", FlatButton.Kind.Secondary);
            browse.Width = 132;
            browse.Click += delegate { BrowseExe(); };
            okButton = new FlatButton("添加选中", FlatButton.Kind.Primary);
            okButton.Width = 110;
            okButton.Click += delegate { Accept(); };

            FlowLayoutPanel buttons = Ui.RightButtonRow(okButton, browse, reload);
            buttons.Dock = DockStyle.Fill;
            foot.Controls.Add(buttons, 1, 0);

            AcceptButton = okButton;
        }

        /// <summary>输入框里的灰色提示文字（不是滚轮事件，只是视觉提示）。</summary>
        private static void SetPlaceholder(TextBox box, string text)
        {
            box.ForeColor = Theme.FaintText;
            box.Text = text;
            box.GotFocus += delegate
            {
                if (box.ForeColor == Theme.FaintText)
                {
                    box.Text = "";
                    box.ForeColor = Theme.Text;
                }
            };
            box.LostFocus += delegate
            {
                if (box.Text.Trim().Length == 0)
                {
                    box.ForeColor = Theme.FaintText;
                    box.Text = text;
                }
            };
        }

        private string SearchText()
        {
            if (search.Box.ForeColor == Theme.FaintText) return "";
            return search.Box.Text.Trim();
        }

        // ------------------------------------------------------------- 数据

        private void Reload(bool includeBackground)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                apps = AppCatalog.Enumerate(includeBackground);
                loaded = true;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            FillList();
        }

        private static Image IconImage(Icon ic)
        {
            if (ic == null) return null;
            try
            {
                using (Bitmap b = ic.ToBitmap())
                {
                    return new Bitmap(b, new Size(Dpi.Px(20), Dpi.Px(20)));
                }
            }
            catch
            {
                return null;
            }
        }

        private void FillList()
        {
            if (!loaded) return;

            string q = SearchText().ToLowerInvariant();

            grid.SuspendLayout();
            try
            {
                grid.Rows.Clear();
                for (int i = 0; i < apps.Count; i++)
                {
                    RunningAppInfo a = apps[i];
                    if (q.Length > 0)
                    {
                        bool hit = (a.Exe != null && a.Exe.ToLowerInvariant().Contains(q))
                                || (a.Description != null && a.Description.ToLowerInvariant().Contains(q))
                                || (a.Title != null && a.Title.ToLowerInvariant().Contains(q));
                        if (!hit) continue;
                    }

                    string caption = a.Caption;
                    string state = string.IsNullOrEmpty(a.Title) ? (a.HasWindow ? "" : "后台运行") : a.Title;
                    int idx = grid.Rows.Add(IconImage(a.Icon), caption, a.Exe, state);
                    grid.Rows[idx].Tag = a;
                }
            }
            finally
            {
                grid.ResumeLayout();
            }

            okButton.Enabled = grid.Rows.Count > 0;
            status.Text = grid.Rows.Count == 0
                ? "没找到程序，换个关键词或勾选「显示后台进程」"
                : string.Format("共 {0} 个程序，可按住 Ctrl 多选、双击直接添加", grid.Rows.Count);
        }

        private void UpdateStatus()
        {
            int n = grid.SelectedRows.Count;
            if (n == 0) return;
            status.Text = string.Format("已选中 {0} 个（规则时长先按 {1} 分钟，加入后可在表格里逐行改）",
                n, defaultLimit);
        }

        // ------------------------------------------------------------- 动作

        private void BrowseExe()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要监督的程序";
                dlg.Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                for (int i = 0; i < dlg.FileNames.Length; i++)
                {
                    string path = dlg.FileNames[i];
                    WatchRule r = new WatchRule();
                    r.Exe = ProcessMonitor.NormalizeName(path);
                    r.ExePath = path;
                    r.DisplayName = AppCatalog.Describe(path);
                    if (string.IsNullOrEmpty(r.DisplayName)) r.DisplayName = Path.GetFileNameWithoutExtension(path);
                    r.LimitMinutes = defaultLimit;
                    r.Enabled = true;
                    Picked.Add(r);
                }
                DialogResult = DialogResult.OK;
            }
        }

        private void Accept()
        {
            List<DataGridViewRow> chosen = new List<DataGridViewRow>();
            for (int i = 0; i < grid.SelectedRows.Count; i++) chosen.Add(grid.SelectedRows[i]);

            if (chosen.Count == 0)
            {
                if (grid.CurrentRow == null)
                {
                    MessageBox.Show(this, "先在上面的列表里点一个程序（可按住 Ctrl 多选）。",
                        "添加监督程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                chosen.Add(grid.CurrentRow);
            }

            for (int i = 0; i < chosen.Count; i++)
            {
                RunningAppInfo a = chosen[i].Tag as RunningAppInfo;
                if (a == null) continue;

                WatchRule r = new WatchRule();
                r.Exe = a.Exe;
                r.ExePath = a.Path;
                r.DisplayName = a.Description;
                if (string.IsNullOrEmpty(r.DisplayName)) r.DisplayName = Path.GetFileNameWithoutExtension(a.Exe);
                r.LimitMinutes = defaultLimit;
                r.Enabled = true;
                r.ApplyDefaultName();
                Picked.Add(r);
            }
            DialogResult = DialogResult.OK;
        }
    }
}
