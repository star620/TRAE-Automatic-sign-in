namespace TraeCheckin;

/// <summary>
/// 主界面：深色侧边栏 + 蓝色强调 + 浅色内容区。
/// 固定小窗（不可缩放），左侧导航可切换「仪表盘 / 签到记录 / 设置」。
/// </summary>
public partial class MainForm : Form
{
    private static readonly Color SidebarBg = Color.FromArgb(30, 41, 59);      // #1E293B
    private static readonly Color Accent = Color.FromArgb(59, 130, 246);       // #3B82F6
    private static readonly Color ContentBg = Color.FromArgb(241, 245, 249);   // #F1F5F9
    private static readonly Color CardBg = Color.White;
    private static readonly Color TextMain = Color.FromArgb(15, 23, 42);       // #0F172A
    private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);   // #64748B

    private AppConfig _config;
    private readonly AccountStore _accountStore;
    private readonly TraeApiClient _api;
    private readonly string _userDataDir;
    private NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();

    private readonly List<Panel> _navItems = new();
    private readonly List<Panel> _pages = new();

    // 仪表盘
    private readonly Label _lblRemaining = new() { Font = new Font("Segoe UI", 28, FontStyle.Bold) };
    private readonly Label _lblStatus = new() { Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private readonly Label _lblReward = new() { Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private readonly Label _lblStreak = new() { Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private readonly Label _lblCloudStatus = new() { Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private readonly HistoryChart _chart = new();
    private readonly ListBox _log = new();

    // 设置页（独立控件，避免跨页共享导致显示异常）
    private readonly CheckBox _chkAutoSet = new();
    private readonly DateTimePicker _dtpTimeSet = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
    private readonly CheckBox _chkAutoStart = new();

    // Token 信息（设置页）
    private readonly TextBox _txtToken = new();
    private readonly Label _lblTokenTime = new();

    // 账号管理（设置页）
    private readonly ComboBox _cmbAccount = new();
    private readonly CheckBox _chkMember = new() { Text = "会员（连签 +50 到账）", AutoSize = true, ForeColor = TextMain };
    private bool _syncingMember;

    // 飞书通知（设置页）
    private readonly TextBox _txtWebhook = new();

    private readonly Button _btnCheckin = new() { Font = new Font("Segoe UI", 12, FontStyle.Bold), Height = 44 };

    // 签到记录
    private readonly ListBox _historyList = new();
    private Label _lblLastCheckin = new();

    private System.Windows.Forms.Timer _autoTimer = new();
    /// <summary>签到进行中标志：防止手动按钮、托盘菜单、自动定时器三路并发触发同一轮签到。</summary>
    private bool _checkinBusy;
    private DateTime _lastAutoCheck = DateTime.MinValue;
    private bool _allowClose;

    /// <summary>账号签到历史文件（%APPDATA%\TraeCheckin\history_&lt;accountId&gt;.txt）。</summary>
    private static string HistoryPathFor(string accountId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin", "history_" + accountId + ".txt");

    /// <summary>账号总积分趋势文件（%APPDATA%\TraeCheckin\credits_total_&lt;accountId&gt;.txt）。</summary>
    private static string TotalHistoryPathFor(string accountId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin", "credits_total_" + accountId + ".txt");

    /// <summary>多账号改造前的全局历史文件（迁移源）。</summary>
    private static string LegacyHistoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin", "history.txt");

    /// <summary>多账号改造前的全局总积分文件（迁移源）。</summary>
    private static string LegacyTotalHistoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin", "credits_total.txt");

    private static readonly Icon AppIcon = LoadAppIcon();

    /// <summary>当前程序版本（从程序集版本号动态生成，如 v1.4.5）。</summary>
    private static string VersionText =>
        "v" + (typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?");

    private static Icon LoadAppIcon()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("TraeCheckin.app.ico");
            if (s != null) return new Icon(s);
        }
        catch { /* 读取嵌入图标失败时回退系统图标 */ }
        return SystemIcons.Application;
    }

    /// <summary>某账号专属的 WebView2 用户数据目录（账号间登录态隔离）。</summary>
    private string AccountWebViewDir(TraeAccount account)
        => Path.Combine(_userDataDir, "account_" + account.Id);

    public MainForm()
    {
        _config = AppConfig.Load();
        _accountStore = new AccountStore(_config);
        _api = new TraeApiClient();
        _userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraeCheckin", "WebView");

        MigrateLegacyHistoryFiles();

        Text = "Trae 每日签到助手";
        ClientSize = new Size(1200, 780);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ContentBg;
        Icon = AppIcon;

        BuildUi();
        BuildTray();
        ShowPage(0);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildSidebar(), 0, 0);
        root.Controls.Add(BuildContentHost(), 1, 0);
        Controls.Add(root);
    }

    private Control BuildSidebar()
    {
        var side = new Panel { Dock = DockStyle.Fill, BackColor = SidebarBg };

        // 标题区：单独 Panel 精确定位，避免 Dock 叠加 padding 导致截断
        var header = new Panel { Dock = DockStyle.Top, Height = 160, BackColor = SidebarBg };
        var title = new Label
        {
            Text = "Trae",
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(16, 28),
            Size = new Size(180, 46),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var subtitle = new Label
        {
            Text = "每日签到助手",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(16, 84),
            Size = new Size(180, 28),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var nav = new Panel { Dock = DockStyle.Top, Height = 200, BackColor = SidebarBg, Padding = new Padding(0, 14, 0, 0) };
        _navItems.Add(NavItem("仪表盘", 0));
        _navItems.Add(NavItem("签到记录", 1));
        _navItems.Add(NavItem("云端签到", 2));
        _navItems.Add(NavItem("设置", 3));
        for (int i = _navItems.Count - 1; i >= 0; i--)
            nav.Controls.Add(_navItems[i]);

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = SidebarBg, Padding = new Padding(14, 0, 14, 16) };
        _btnCheckin.Text = "立即签到";
        _btnCheckin.Dock = DockStyle.Bottom;
        _btnCheckin.Height = 46;
        _btnCheckin.FlatStyle = FlatStyle.Flat;
        _btnCheckin.FlatAppearance.BorderSize = 0;
        _btnCheckin.BackColor = Accent;
        _btnCheckin.ForeColor = Color.White;
        _btnCheckin.Cursor = Cursors.Hand;
        _btnCheckin.Click += async (_, _) => await DoCheckinAsync();
        bottom.Controls.Add(_btnCheckin);

        side.Controls.Add(bottom);
        side.Controls.Add(nav);
        side.Controls.Add(header);
        return side;
    }

    private Panel NavItem(string text, int index)
    {
        var p = new Panel
        {
            Height = 44,
            Dock = DockStyle.Top,
            BackColor = SidebarBg,
            Padding = new Padding(12, 0, 12, 0),
            Cursor = Cursors.Hand,
            Tag = index
        };
        var lbl = new Label
        {
            Text = "  " + text,
            Font = new Font("Segoe UI", 11),
            ForeColor = Color.FromArgb(203, 213, 225),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        p.Controls.Add(lbl);
        p.Click += (_, _) => ShowPage(index);
        lbl.Click += (_, _) => ShowPage(index);
        return p;
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visible = i == index;
            var nav = _navItems[i];
            nav.BackColor = i == index ? Accent : SidebarBg;
            if (nav.Controls[0] is Label l)
                l.ForeColor = i == index ? Color.White : Color.FromArgb(203, 213, 225);
        }
        if (index == 3) RefreshAccountCombo();   // 切到设置页时同步账号下拉
    }

    private Control BuildContentHost()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg };
        _pages.Add(BuildDashboard());
        _pages.Add(BuildHistory());
        _pages.Add(BuildCloud());
        _pages.Add(BuildSettings());
        foreach (var pg in _pages)
        {
            pg.Dock = DockStyle.Fill;
            host.Controls.Add(pg);
        }
        return host;
    }

    private Panel BuildDashboard()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Padding = new Padding(16) };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            BackColor = ContentBg,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 剩余积分（跨两列）
        var remCard = Card("剩余积分", _lblRemaining);
        _lblRemaining.TextAlign = ContentAlignment.MiddleLeft;
        _lblRemaining.Dock = DockStyle.Fill;
        _lblRemaining.ForeColor = TextMain;
        root.Controls.Add(remCard, 0, 0);
        root.SetColumnSpan(remCard, 2);

        // 今日签到状态
        var statusCard = Card("今日签到状态", _lblStatus);
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.Dock = DockStyle.Fill;
        root.Controls.Add(statusCard, 0, 1);

        // 单日签到奖励
        var rewardCard = Card("单日签到奖励", _lblReward);
        _lblReward.TextAlign = ContentAlignment.MiddleLeft;
        _lblReward.Dock = DockStyle.Fill;
        root.Controls.Add(rewardCard, 1, 1);

        // 连续签到天数
        var streakCard = Card("连续签到", _lblStreak);
        _lblStreak.TextAlign = ContentAlignment.MiddleLeft;
        _lblStreak.Dock = DockStyle.Fill;
        _lblStreak.ForeColor = Accent;
        root.Controls.Add(streakCard, 0, 2);

        // 云端签到状态
        var cloudCard = Card("云端签到状态", _lblCloudStatus);
        _lblCloudStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblCloudStatus.Dock = DockStyle.Fill;
        // 避免「授权失效，请重新授权」等较长文案在窄窗格时被右缘裁剪
        _lblCloudStatus.AutoSize = false;
        root.Controls.Add(cloudCard, 1, 2);

        // 积分趋势图（跨两列）
        _chart.Dock = DockStyle.Fill;
        var chartCard = Card("积分趋势（近 14 天）", _chart);
        root.Controls.Add(chartCard, 0, 3);
        root.SetColumnSpan(chartCard, 2);

        // 日志
        _log.Dock = DockStyle.Fill;
        _log.BackColor = CardBg;
        _log.ForeColor = TextMuted;
        _log.BorderStyle = BorderStyle.None;
        _log.HorizontalScrollbar = false;
        var logCard = Card("运行日志", _log);
        root.Controls.Add(logCard, 0, 4);
        root.SetColumnSpan(logCard, 2);

        p.Controls.Add(root);
        return p;
    }

    private Panel BuildHistory()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Padding = new Padding(16) };
        _lblLastCheckin = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Segoe UI", 11),
            ForeColor = TextMain,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _historyList.Dock = DockStyle.Fill;
        _historyList.BackColor = CardBg;
        _historyList.ForeColor = TextMain;
        _historyList.BorderStyle = BorderStyle.None;
        _historyList.HorizontalScrollbar = false;
        p.Controls.Add(_historyList);
        p.Controls.Add(_lblLastCheckin);
        ReloadHistory();
        return p;
    }

    private Panel BuildSettings()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Padding = new Padding(16) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = ContentBg };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var autoPanel = CardPanel("每日自动签到", BuildAutoRow());
        grid.Controls.Add(autoPanel, 0, 0);

        var autoStartPanel = CardPanel("开机自启动", BuildAutoStartRow());
        grid.Controls.Add(autoStartPanel, 0, 1);

        var tokenPanel = CardPanel("当前账号登录 Token", BuildTokenRow());
        grid.Controls.Add(tokenPanel, 0, 2);

        var acctPanel = CardPanel("多账号管理", BuildAccountRow());
        grid.Controls.Add(acctPanel, 0, 3);

        var notifyPanel = CardPanel("云端签到推送（飞书机器人）", BuildWebhookRow());
        grid.Controls.Add(notifyPanel, 0, 4);

        var hint = new Label
        {
            Text = $"版本：{VersionText}" + Environment.NewLine +
                   "提示：本程序固定小窗显示。关闭窗口后自动最小化到系统托盘，后台继续自动签到。",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9),
            Padding = new Padding(4, 12, 0, 0)
        };
        grid.Controls.Add(hint, 0, 5);

        p.Controls.Add(grid);
        return p;
    }

    private Control BuildTokenRow()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = CardBg };

        _txtToken.ReadOnly = true;
        _txtToken.Multiline = true;
        _txtToken.WordWrap = true;
        _txtToken.ScrollBars = ScrollBars.Vertical;
        _txtToken.BorderStyle = BorderStyle.FixedSingle;
        _txtToken.BackColor = Color.FromArgb(248, 250, 252);
        _txtToken.ForeColor = TextMain;
        _txtToken.Font = new Font("Consolas", 9);
        _txtToken.Dock = DockStyle.Top;
        _txtToken.Height = 54;

        var bottomRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = CardBg,
            Padding = new Padding(0, 8, 0, 0)
        };

        var btnCopy = new Button
        {
            Text = "复制 Token",
            Width = 100,
            Height = 34,
            Margin = new Padding(0, 0, 12, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btnCopy.FlatAppearance.BorderSize = 0;
        btnCopy.Click += (_, _) => CopyToken();

        _lblTokenTime.AutoSize = true;
        _lblTokenTime.ForeColor = TextMuted;
        _lblTokenTime.Font = new Font("Segoe UI", 9);
        _lblTokenTime.Margin = new Padding(0, 9, 0, 0);

        bottomRow.Controls.Add(btnCopy);
        bottomRow.Controls.Add(_lblTokenTime);

        panel.Controls.Add(bottomRow);
        panel.Controls.Add(_txtToken);

        UpdateTokenDisplay();
        return panel;
    }

    private void CopyToken()
    {
        var acc = CurAccount;
        if (acc == null || string.IsNullOrEmpty(acc.Token))
        {
            MessageBox.Show("暂无 token，请先登录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Clipboard.SetText(acc.Token);
            SetLog("[" + DisplayName(acc) + "] Token 已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            SetLog("复制失败：" + ex.Message);
        }
    }

    private void UpdateTokenDisplay()
    {
        var acc = CurAccount;
        _txtToken.Text = acc == null || string.IsNullOrEmpty(acc.Token) ? "未登录，暂无 token" : acc.Token;
        _lblTokenTime.Text = acc == null
            ? "未登录"
            : "最后更新：" + (acc.TokenUpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "从未");
    }

    private Control BuildAutoRow()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CardBg };
        var lbl = new Label { Text = "时间:", ForeColor = TextMuted, AutoSize = true, Margin = new Padding(0, 12, 6, 0) };
        _chkAutoSet.Text = "开启每日自动签到";
        _chkAutoSet.Checked = _config.AutoCheckinEnabled;
        _chkAutoSet.AutoSize = true;
        _chkAutoSet.ForeColor = TextMain;
        _chkAutoSet.Margin = new Padding(0, 10, 20, 0);
        _chkAutoSet.CheckedChanged += (_, _) => SyncAutoCheckin(_chkAutoSet.Checked, _dtpTimeSet.Value);
        if (TimeSpan.TryParse(_config.AutoCheckinTime, out var ts))
            _dtpTimeSet.Value = DateTime.Today.Add(ts);
        _dtpTimeSet.ValueChanged += (_, _) => SyncAutoCheckin(_chkAutoSet.Checked, _dtpTimeSet.Value);
        _dtpTimeSet.Width = 90;
        row.Controls.Add(_chkAutoSet);
        row.Controls.Add(lbl);
        row.Controls.Add(_dtpTimeSet);
        return row;
    }

    private Control BuildAutoStartRow()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CardBg };
        _chkAutoStart.Text = "登录 Windows 后自动启动本程序";
        _chkAutoStart.AutoSize = true;
        _chkAutoStart.ForeColor = TextMain;
        _chkAutoStart.Margin = new Padding(0, 8, 0, 0);
        _chkAutoStart.Checked = AutoStartManager.IsEnabled();
        _chkAutoStart.CheckedChanged += (_, _) => AutoStartManager.SetEnabled(_chkAutoStart.Checked);
        row.Controls.Add(_chkAutoStart);
        return row;
    }

    private void SyncAutoCheckin(bool enabled, DateTime time)
    {
        _config.AutoCheckinEnabled = enabled;
        _config.AutoCheckinTime = time.ToString("HH:mm");
        _config.Save();
        if (_chkAutoSet.Checked != enabled) _chkAutoSet.Checked = enabled;
        if (_dtpTimeSet.Value.ToString("HH:mm") != _config.AutoCheckinTime)
            _dtpTimeSet.Value = DateTime.Today.Add(TimeSpan.Parse(_config.AutoCheckinTime));
    }

    private Control BuildAccountRow()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = CardBg };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 第一行：下拉 + 操作按钮（不强制 ComboBox 高度，交给系统按字体计算，避免高 DPI 字形底部被裁）
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CardBg };

        _cmbAccount.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAccount.Width = 300;
        _cmbAccount.Margin = new Padding(0, 8, 10, 0);
        _cmbAccount.FlatStyle = FlatStyle.Flat;
        _cmbAccount.SelectedIndexChanged += (_, _) => OnAccountSelected();
        top.Controls.Add(_cmbAccount);

        var btnAdd = new Button { Text = "添加账号", Width = 96, Height = 34, Margin = new Padding(0, 6, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Cursor = Cursors.Hand };
        btnAdd.FlatAppearance.BorderSize = 0;
        btnAdd.Click += async (_, _) => await AddAccountAsync();
        top.Controls.Add(btnAdd);

        var btnLogin2 = new Button { Text = "重新登录", Width = 92, Height = 34, Margin = new Padding(0, 6, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = CardBg, ForeColor = TextMain, Cursor = Cursors.Hand };
        btnLogin2.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        btnLogin2.Click += async (_, _) => { if (CurAccount != null) await LoginAndRefreshAsync(CurAccount); };
        top.Controls.Add(btnLogin2);

        var btnDel = new Button { Text = "删除", Width = 70, Height = 34, Margin = new Padding(0, 6, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = CardBg, ForeColor = TextMain, Cursor = Cursors.Hand };
        btnDel.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        btnDel.Click += (_, _) => RemoveAccount();
        top.Controls.Add(btnDel);

        table.Controls.Add(top, 0, 0);

        // 第二行：会员开关 + 提示文字独占一行，避免与按钮同排被挤截
        var hintRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CardBg };
        _chkMember.Margin = new Padding(0, 12, 14, 0);
        _chkMember.CheckedChanged += (_, _) => OnMemberToggled();
        hintRow.Controls.Add(_chkMember);
        var lbl = new Label { Text = "切换账号即刷新仪表盘。", ForeColor = TextMuted, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        hintRow.Controls.Add(lbl);
        table.Controls.Add(hintRow, 0, 1);

        RefreshAccountCombo();
        return table;
    }

    private void RefreshAccountCombo()
    {
        var prev = _config.ActiveAccountId;
        _cmbAccount.Items.Clear();
        foreach (var a in _config.Accounts)
            _cmbAccount.Items.Add(ComboText(a));
        int idx = _config.Accounts.FindIndex(a => a.Id == prev);
        _cmbAccount.SelectedIndex = idx >= 0 ? idx : (_config.Accounts.Count > 0 ? 0 : -1);
        SyncMemberCheckbox();
        UpdateTokenDisplay();
    }

    /// <summary>把当前激活账号的会员状态同步到 CheckBox（不触发持久化事件）。</summary>
    private void SyncMemberCheckbox()
    {
        var acc = CurAccount;
        _syncingMember = true;
        try { _chkMember.Checked = acc != null && acc.IsMember; }
        finally { _syncingMember = false; }
    }

    /// <summary>会员开关变更：写入当前激活账号并保存配置。</summary>
    private void OnMemberToggled()
    {
        if (_syncingMember) return;
        var acc = CurAccount;
        if (acc == null) return;
        acc.IsMember = _chkMember.Checked;
        _config.Save();
        SetLog($"[{DisplayName(acc)}] 会员标记已改为：{(acc.IsMember ? "是（连签 +50 到账）" : "否（仅基础积分）")}");
    }

    private string ComboText(TraeAccount a)
    {
        var days = "";
        if (a.TokenUpdatedAt.HasValue)
            days = "（Token 更新于 " + a.TokenUpdatedAt.Value.ToString("MM-dd HH:mm") + "）";
        return DisplayName(a) + (a.Enabled ? "" : "（停用）") + days;
    }

    private void OnAccountSelected()
    {
        if (_cmbAccount.SelectedIndex < 0) return;
        var id = _config.Accounts[_cmbAccount.SelectedIndex].Id;
        if (_config.ActiveAccountId != id)
        {
            _config.ActiveAccountId = id;
            _config.Save();
            _ = RefreshAllAsync();
        }
        UpdateTokenDisplay();
    }

    private async Task AddAccountAsync()
    {
        var acc = _accountStore.AddNew();
        _accountStore.EnsureDeviceId(acc);
        _config.Save();
        RefreshAccountCombo();
        var status = await LoginAndRefreshAsync(acc);
        if (status == null)
        {
            // 登录取消/失败：移除刚建的空壳账号
            _accountStore.Remove(acc.Id);
            _config.Save();
            RefreshAccountCombo();
            return;
        }
        if (string.IsNullOrWhiteSpace(acc.Name))
        {
            acc.Name = "账号" + (_config.Accounts.Count);
            _config.Save();
        }
        RefreshAccountCombo();

        // 期望「添加后当日即签」：登录成功后立即让新账号参与签到
        // （今日已签则记录历史；未签则当场补签并输出日志）
        SetLog("正在检查新账号今日签到状态…");
        var immediate = new List<(TraeAccount, bool, double, double)>();
        await CheckinOneAsync(acc, immediate);

        await RefreshAllAsync();
        SetLog("已添加账号 " + DisplayName(acc));
    }

    private void RemoveAccount()
    {
        var acc = CurAccount;
        if (acc == null) return;
        if (_config.Accounts.Count <= 1)
        {
            MessageBox.Show("至少保留一个账号。要清空请删除后重新添加。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var name = DisplayName(acc);
        var r = MessageBox.Show($"确定删除账号「{name}」？（仅删除本地记录，不影响该 Trae 账号）", "删除账号",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        try { Directory.Delete(AccountWebViewDir(acc), recursive: true); } catch { /* 忽略删除失败 */ }
        _accountStore.Remove(acc.Id);
        _config.Save();
        RefreshAccountCombo();
        SetLog("已删除账号：" + name);
        _ = RefreshAllAsync();
    }

    private Control BuildWebhookRow()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = CardBg };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        _txtWebhook.Text = _config.FeishuWebhook ?? "";
        _txtWebhook.Dock = DockStyle.Fill;
        _txtWebhook.Margin = new Padding(0, 4, 10, 4);
        _txtWebhook.PlaceholderText = "粘贴飞书机器人 webhook 地址（留空则关闭推送）";

        var btnSave = new Button { Text = "保存", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 10, 4), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Cursor = Cursors.Hand };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => SaveWebhook();

        var btnTest = new Button { Text = "测试推送", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4), FlatStyle = FlatStyle.Flat, BackColor = CardBg, ForeColor = TextMain, Cursor = Cursors.Hand };
        btnTest.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        btnTest.Click += async (_, _) => await TestWebhookAsync();

        table.Controls.Add(_txtWebhook, 0, 0);
        table.Controls.Add(btnSave, 1, 0);
        table.Controls.Add(btnTest, 2, 0);
        return table;
    }

    private void SaveWebhook()
    {
        var url = _txtWebhook.Text.Trim();
        _config.FeishuWebhook = string.IsNullOrEmpty(url) ? null : url;
        _config.Save();
        SetLog("飞书 webhook 已保存。");
    }

    private async Task TestWebhookAsync()
    {
        var url = _txtWebhook.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("请先粘贴飞书机器人 webhook 地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ok = await FeishuNotifier.SendTextAsync(url, "Trae 签到助手测试通知：如果你看到这条消息，说明推送配置成功。");
        SetLog(ok ? "测试消息已发送，请查看飞书群。" : "测试消息发送失败，请检查 webhook 地址是否正确。");
    }

    private Panel CardPanel(string title, Control body)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = CardBg, Padding = new Padding(16, 12, 16, 6), Margin = new Padding(0, 0, 0, 10) };
        var t = new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = TextMain };
        body.Dock = DockStyle.Fill;
        p.Controls.Add(body);
        p.Controls.Add(t);
        return p;
    }

    private Panel Card(string title, Control body)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = CardBg, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(6) };
        var t = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 9),
            ForeColor = TextMuted
        };
        body.Dock = DockStyle.Fill;
        p.Controls.Add(body);
        p.Controls.Add(t);
        return p;
    }

    private void BuildTray()
    {
        _tray.Text = "Trae 每日签到助手";
        _tray.Icon = AppIcon;
        _tray.Visible = true;
        _trayMenu.Items.Add("显示主界面", null, (_, _) => ShowMainWindow());
        _trayMenu.Items.Add("立即签到", null, async (_, _) => await DoCheckinAsync());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ExitApp()
    {
        _allowClose = true;
        Close();
    }

    private void ShowMainWindow()
    {
        Show();
        ShowInTaskbar = true;
        Activate();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var anyToken = _config.Accounts.Any(a => !string.IsNullOrEmpty(a.Token));
        SetLog($"程序已启动，已{(anyToken ? "登录" : "未登录，请在设置页添加/登录账号")}");
        await RefreshAllAsync();
        StartAutoTimer();
    }

    private void StartAutoTimer()
    {
        _autoTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _autoTimer.Tick += async (_, _) => await CheckAutoCheckinAsync();
        _autoTimer.Start();
        _lastAutoCheck = DateTime.MinValue;
    }

    private async Task CheckAutoCheckinAsync()
    {
        if (!_config.AutoCheckinEnabled) return;
        var now = DateTime.Now;
        if (now.Date == _lastAutoCheck.Date) return;
        if (!TimeSpan.TryParse(_config.AutoCheckinTime, out var target)) return;
        var scheduled = now.Date.Add(target);
        if (now < scheduled) return;
        _lastAutoCheck = now.Date;
        var late = (now - scheduled).TotalSeconds > 120;
        SetLog(late
            ? $"已超过设定的自动签到时间 {target:hh\\:mm}，执行补签…"
            : $"到达自动签到时间 {target:hh\\:mm}，开始签到…");
        await DoCheckinAsync();   // DoCheckinAsync 遍历所有启用账号，结束时内部会刷新仪表盘
    }

    private async Task RefreshAllAsync()
    {
        var acc = CurAccount;
        var remaining = _config.LastRemaining;
        if (acc == null)
        {
            _lblRemaining.Text = "—";
            _lblStatus.Text = "未登录";
            _lblReward.Text = "—";
            await RefreshCloudStatusAsync();
            return;
        }

        // 兜底：激活账号也确保设备号非空（仪表盘请求同样带 x-device-id）
        if (string.IsNullOrWhiteSpace(acc.DeviceId))
        {
            _accountStore.EnsureDeviceId(acc);
            _config.Save();
        }

        var status = await GetStatusWithValidTokenAsync(acc);
        if (!string.IsNullOrEmpty(acc.Token))
        {
            var r = await _api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
            if (r >= 0) { remaining = r; _config.LastRemaining = r; _config.Save(); }
        }

        _lblRemaining.Text = remaining >= 0 ? remaining.ToString("0.##") : "—";
        if (status != null)
        {
            _lblStatus.Text = status.enable
                ? (status.checked_in ? "今日已签到 ✓" : "今日可签到")
                : "签到功能未开启";
            _lblStatus.ForeColor = status.checked_in ? Color.FromArgb(16, 185, 129) : Color.FromArgb(245, 158, 11);
            // 单日可得：非会员仅基础 credits；会员另加连签 extra_credits（实测 150+50）
            var daily = status.credits + (acc.IsMember ? status.extra_credits : 0);
            _lblReward.Text = daily > 0 ? $"{daily:0} 积分" : "—";
        }
        else _lblStatus.Text = "获取失败";

        // 每天首次刷新时记录当前总积分（当前激活账号），便于趋势图立即有数据
        if (remaining >= 0 && !TotalHistoryHasToday(acc))
            AppendTotalHistory(acc, DateTime.Now, remaining);

        ReloadHistory();   // 切换账号后同步刷新趋势图/连签天数/历史列表

        await RefreshCloudStatusAsync();
    }

    /// <summary>刷新仪表盘的云端签到状态（读取最近一次已完成的 GitHub Actions 运行）。</summary>
    private async Task RefreshCloudStatusAsync()
    {
        if (string.IsNullOrEmpty(_config.GitHubToken))
        {
            _lblCloudStatus.Text = CloudStatusFormatter.Describe(true, null).Text;
            _lblCloudStatus.ForeColor = TextMuted;
            return;
        }
        try
        {
            // 始终用 GET /user 探测授权：token 失效时返回 401。
            // 不能依赖 GitHubLogin 缓存跳过探测，否则授权失效会被 GetLatestRunAsync 的 null
            // 误判成「未部署」（而非「授权失效」）。
            bool authorized = true;
            string login = await _ghApi.GetLoginAsync(_config.GitHubToken) ?? "";
            if (string.IsNullOrEmpty(login))
            {
                authorized = false;
                // token 失效：清掉本地授权并让云端页同步回到未授权，
                // 避免云端页残留旧文案「已部署完成」与仪表盘「授权失效」不一致。
                if (!string.IsNullOrEmpty(_config.GitHubToken))
                {
                    ClearCloudAuth();
                    _lblCloudState.Text = "授权已失效，请重新授权";
                    _btnCloudAction.Text = "授权 GitHub 并部署";
                }
            }
            else if (_config.GitHubLogin != login)
            {
                _config.GitHubLogin = login;
                _config.Save();
            }

            WorkflowRunInfo? run = null;
            if (authorized)
                run = await _ghApi.GetLatestRunAsync(_config.GitHubToken, login);

            var (text, isError) = CloudStatusFormatter.Describe(authorized, run);
            _lblCloudStatus.Text = text;
            _lblCloudStatus.ForeColor = isError ? Color.FromArgb(239, 68, 68) : TextMuted;
            if (text.Contains("最近成功")) _lblCloudStatus.ForeColor = Color.FromArgb(16, 185, 129);
        }
        catch
        {
            _lblCloudStatus.Text = "状态未知";
            _lblCloudStatus.ForeColor = TextMuted;
        }
    }

    private async Task DoCheckinAsync()
    {
        if (_checkinBusy)
        {
            SetLog("签到正在进行中，请稍候（已忽略本次重复触发）。");
            return;
        }
        _checkinBusy = true;
        try
        {
            var enabled = _accountStore.EnabledAccounts().ToList();
            if (enabled.Count == 0)
            {
                SetLog("没有可签到的账号，请先在设置页添加账号。");
                return;
            }
            // 兜底：签到前确保每个账号都有不重复的 16 位设备号（缺号/重复会触发风控 9074）
            foreach (var acc in enabled)
            {
                if (string.IsNullOrWhiteSpace(acc.DeviceId))
                {
                    _accountStore.EnsureDeviceId(acc);
                    _config.Save();
                }
            }
            SetLog($"开始为 {enabled.Count} 个账号签到…");

            var results = new List<(TraeAccount Acc, bool Ok, double Gained, double Remaining)>();
            foreach (var acc in enabled)
            {
                await CheckinOneAsync(acc, results);
            }

            // 本地飞书推送：多账号汇总成一条
            await NotifyFeishuBatchAsync(results);

            SetLog("本轮全部账号签到结束。");
            await RefreshAllAsync();
        }
        finally
        {
            _checkinBusy = false;
        }
    }

    private async Task CheckinOneAsync(TraeAccount acc, List<(TraeAccount, bool, double, double)> results)
    {
        var name = DisplayName(acc);
        var status = await GetStatusWithValidTokenAsync(acc);
        if (status != null && status.checked_in)
        {
            SetLog($"[{name}] 今日已签到，无需重复签到。");
            var already = CheckinEvaluator.ResolveGainedCredits(status, acc.IsMember);
            if (acc.LastCheckinDate != DateTime.Today)
                RecordCheckin(acc, already);
            var rem = await RemainingOfAsync(acc);
            results.Add((acc, true, already, rem));
            return;
        }
        if (string.IsNullOrEmpty(acc.Token))
        {
            SetLog($"[{name}] 未登录，跳过。");
            results.Add((acc, false, 0, -1));
            return;
        }
        SetLog($"[{name}] 正在签到…");
        var result = await _api.ClaimAsync(acc.Token, acc.DeviceId);
        if (result == null)
        {
            SetLog($"[{name}] 签到失败：" + (_api.LastError ?? "未知错误"));
            results.Add((acc, false, 0, -1));
            return;
        }
        var after = await _api.GetStatusAsync(acc.Token, acc.DeviceId);
        var gained = CheckinEvaluator.ResolveGainedCredits(after ?? result, acc.IsMember);
        if (gained > 0)
        {
            SetLog($"[{name}] 签到成功！获得 {gained:0} 积分。");
            RecordCheckin(acc, gained);
            var total = await RemainingOfAsync(acc);
            if (total >= 0 && acc.Id == CurAccount?.Id) AppendTotalHistory(acc, DateTime.Now, total);
            results.Add((acc, true, gained, total));
            NotifyNativeCheckin(true, name, gained, total);
        }
        else
        {
            var msg = result.message;
            SetLog($"[{name}] 签到失败：{msg ?? (_api.LastError ?? "未知错误")}");
            results.Add((acc, false, 0, -1));
            NotifyNativeCheckin(false, name);
        }
    }

    private async Task<double> RemainingOfAsync(TraeAccount acc)
    {
        if (string.IsNullOrEmpty(acc.Token)) return -1;
        return await _api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
    }

    /// <summary>本地飞书推送：把一批账号结果汇总为一条消息（webhook 为空自动跳过）。</summary>
    private async Task NotifyFeishuBatchAsync(List<(TraeAccount Acc, bool Ok, double Gained, double Remaining)> results)
    {
        if (string.IsNullOrWhiteSpace(_config.FeishuWebhook) || results.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Trae 多账号签到结果");
        foreach (var r in results)
        {
            var name = DisplayName(r.Acc);
            sb.AppendLine((r.Ok ? "✅ " : "⚠️ ") + name + (r.Ok ? $"  获得 {r.Gained:0} 积分" : "  失败"));
        }
        var ok = await FeishuNotifier.SendTextAsync(_config.FeishuWebhook, sb.ToString());
        SetLog(ok ? "飞书推送已发送。" : "飞书推送发送失败，请检查 webhook。");
    }

    /// <summary>签到后弹出 Windows 原生通知（托盘气泡，单账号旧入口保留兼容）。</summary>
    private void NotifyNativeCheckin(bool success, double gainedCredits, double remaining = -1)
    {
        try
        {
            if (success)
            {
                string text = $"本次获得：{gainedCredits:0} 积分";
                if (remaining >= 0) text += $"\n当前剩余：{remaining:0.##} 积分";
                _tray.ShowBalloonTip(5000, "Trae 签到成功 ✓", text, ToolTipIcon.Info);
            }
            else
            {
                _tray.ShowBalloonTip(5000, "Trae 签到失败 ⚠", "请检查会话是否过期，重新登录后重试。", ToolTipIcon.Error);
            }
        }
        catch { /* 通知失败不影响签到主流程 */ }
    }

    /// <summary>签到后弹出 Windows 原生通知（多账号版，标题带账号名）。</summary>
    private void NotifyNativeCheckin(bool success, string accountName, double gainedCredits = 0, double remaining = -1)
    {
        try
        {
            if (success)
            {
                string text = $"[{accountName}] 本次获得：{gainedCredits:0} 积分";
                if (remaining >= 0) text += $"\n当前剩余：{remaining:0.##} 积分";
                _tray.ShowBalloonTip(5000, "Trae 签到成功 ✓", text, ToolTipIcon.Info);
            }
            else
            {
                _tray.ShowBalloonTip(5000, "Trae 签到失败 ⚠", $"[{accountName}] 请检查会话是否过期，重新登录后重试。", ToolTipIcon.Error);
            }
        }
        catch { /* 通知失败不影响签到主流程 */ }
    }

    /// <summary>当前激活账号（无账号时 null，UI 据此呈现未登录态）。</summary>
    private TraeAccount? CurAccount
        => _config.Accounts.Count > 0 ? _accountStore.ActiveAccount : null;

    /// <summary>账号展示名：无备注时用 Id 前 4 位大写。</summary>
    private static string DisplayName(TraeAccount acc)
        => string.IsNullOrWhiteSpace(acc.Name) ? "账号 " + acc.Id[..4].ToUpperInvariant() : acc.Name!;

    private async Task<CheckinStatus?> GetStatusWithValidTokenAsync(TraeAccount account)
    {
        if (!string.IsNullOrEmpty(account.Token))
        {
            var st = await _api.GetStatusAsync(account.Token, account.DeviceId);
            if (st != null && st.code == 0) return st;
            SetLog("[" + DisplayName(account) + "] token 已失效，尝试用会话 Cookie 静默换新…");
        }

        // 优先用 X-Cloudide-Session（约 14 天有效）静默换新 token，避免频繁重新登录
        if (!string.IsNullOrEmpty(account.Session))
        {
            var renewed = await _api.GetUserTokenAsync(account.Session);
            if (!string.IsNullOrEmpty(renewed))
            {
                account.Token = renewed;
                account.TokenUpdatedAt = DateTime.Now;
                _config.Save();
                SetLog("[" + DisplayName(account) + "] token 已通过会话 Cookie 静默换新。");
                if (account.Id == CurAccount?.Id) UpdateTokenDisplay();
                var st = await _api.GetStatusAsync(renewed, account.DeviceId);
                if (st != null && st.code == 0) return st;
            }
            else
            {
                SetLog("[" + DisplayName(account) + "] 会话 Cookie 已失效：" + (_api.LastError ?? "未知错误"));
            }
        }

        return null;
    }

    private async Task<CheckinStatus?> LoginAndRefreshAsync(TraeAccount account)
    {
        string token = string.Empty;
        string? session = null;
        using (var login = new LoginForm(AccountWebViewDir(account), account.Token, (t, s) => { token = t; session = s; }))
            login.ShowDialog();
        if (string.IsNullOrEmpty(token))
        {
            SetLog("登录取消。");
            return null;
        }
        account.Token = token;
        if (!string.IsNullOrEmpty(session)) account.Session = session;
        account.TokenUpdatedAt = DateTime.Now;
        _accountStore.EnsureDeviceId(account);
        _config.Save();
        SetLog("[" + DisplayName(account) + "] 登录成功，token 已保存。");
        if (account.Id == CurAccount?.Id) UpdateTokenDisplay();
        return await _api.GetStatusAsync(token, account.DeviceId);
    }

    private void SetLog(string msg)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(SetLog), msg); return; }
        _log.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        _log.TopIndex = _log.Items.Count - 1;
    }

    /// <summary>
    /// 一次性迁移：把多账号改造前的全局 history.txt / credits_total.txt 拆分到各账号独立文件。
    /// history.txt 中带 [账号名] 前缀的行归到对应账号；无前缀的旧单账号行归到首个启用账号。
    /// 迁移完成后原文件改名 .migrated，避免重复迁移。
    /// </summary>
    private void MigrateLegacyHistoryFiles()
    {
        try
        {
            if (_config.Accounts.Count == 0) return;
            var fallback = _config.Accounts.FirstOrDefault(a => a.Enabled) ?? _config.Accounts[0];

            // 1) history.txt 按 [账号名] 前缀拆分
            if (File.Exists(LegacyHistoryPath))
            {
                foreach (var line in File.ReadAllLines(LegacyHistoryPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var target = fallback;
                    int bIdx = line.IndexOf('[');
                    int eIdx = bIdx >= 0 ? line.IndexOf(']', bIdx) : -1;
                    if (bIdx >= 0 && eIdx > bIdx)
                    {
                        var name = line.Substring(bIdx + 1, eIdx - bIdx - 1);
                        var hit = _config.Accounts.FirstOrDefault(a => DisplayName(a) == name);
                        if (hit != null) target = hit;
                    }
                    var dir = Path.GetDirectoryName(HistoryPathFor(target.Id));
                    Directory.CreateDirectory(dir!);
                    File.AppendAllText(HistoryPathFor(target.Id), line + Environment.NewLine);
                }
                File.Move(LegacyHistoryPath, LegacyHistoryPath + ".migrated", overwrite: true);
            }

            // 2) credits_total.txt 是旧单账号时期数据，整体归首个账号
            if (File.Exists(LegacyTotalHistoryPath))
            {
                var dir = Path.GetDirectoryName(TotalHistoryPathFor(fallback.Id));
                Directory.CreateDirectory(dir!);
                File.Copy(LegacyTotalHistoryPath, TotalHistoryPathFor(fallback.Id), overwrite: true);
                File.Move(LegacyTotalHistoryPath, LegacyTotalHistoryPath + ".migrated", overwrite: true);
            }
        }
        catch { /* 迁移失败不阻断启动，历史稍后从空开始 */ }
    }

    private void RecordCheckin(TraeAccount account, double credits)
    {
        account.LastCheckinDate = DateTime.Today;
        _config.Save();
        AppendHistory(account, DateTime.Now, credits, DisplayName(account));
    }

    private void AppendHistory(TraeAccount account, DateTime time, double credits, string accountName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPathFor(account.Id))!);
            File.AppendAllText(HistoryPathFor(account.Id),
                $"{time:yyyy-MM-dd HH:mm}  [{accountName}]  签到成功  +{credits:0} 积分{Environment.NewLine}");
        }
        catch { }
        ReloadHistory();
    }

    /// <summary>记录一次签到后的总积分余额，用于绘制该账号的积分趋势图。</summary>
    private void AppendTotalHistory(TraeAccount account, DateTime time, double total)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TotalHistoryPathFor(account.Id))!);
            File.AppendAllText(TotalHistoryPathFor(account.Id), $"{time:yyyy-MM-dd},{total:0.##}{Environment.NewLine}");
        }
        catch { }
        ReloadHistory();
    }

    private void ReloadHistory()
    {
        try
        {
            var acc = CurAccount;
            var file = acc != null ? HistoryPathFor(acc.Id) : null;
            var lines = (file != null && File.Exists(file)) ? File.ReadAllLines(file).Reverse().ToList() : new List<string>();
            _historyList.Items.Clear();
            foreach (var line in lines)
                _historyList.Items.Add(line);
            _lblLastCheckin.Text = acc != null && acc.LastCheckinDate.HasValue
                ? $"最近签到（{DisplayName(acc)}）：{acc.LastCheckinDate:yyyy-MM-dd}"
                : "暂无签到记录";
        }
        catch { }

        var acc2 = CurAccount;
        var history = acc2 != null ? ParseHistory(acc2) : new List<(DateTime, double)>();
        _lblStreak.Text = ComputeStreak(history) + " 天";
        _chart.SetData(acc2 != null ? ParseTotalHistory(acc2) : new List<(DateTime, double)>());
    }

    /// <summary>解析某账号签到历史，返回 (日期, 积分) 列表。</summary>
    private List<(DateTime Date, double Credits)> ParseHistory(TraeAccount account)
    {
        var result = new List<(DateTime, double)>();
        try
        {
            var file = HistoryPathFor(account.Id);
            if (!File.Exists(file)) return result;
            foreach (var line in File.ReadAllLines(file))
            {
                if (line.Length < 10) continue;
                if (!DateTime.TryParseExact(line.Substring(0, 10), "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var date)) continue;

                double credits = 0;
                var plusIdx = line.IndexOf('+');
                if (plusIdx >= 0)
                {
                    var rest = line.Substring(plusIdx + 1);
                    var spaceIdx = rest.IndexOf(' ');
                    var numStr = spaceIdx >= 0 ? rest.Substring(0, spaceIdx) : rest;
                    double.TryParse(numStr, out credits);
                }
                result.Add((date, credits));
            }
        }
        catch { }
        return result;
    }

    /// <summary>解析某账号总积分历史（credits_total_&lt;id&gt;.txt，每行 yyyy-MM-dd,总积分）。</summary>
    private List<(DateTime Date, double Credits)> ParseTotalHistory(TraeAccount account)
    {
        var result = new List<(DateTime, double)>();
        try
        {
            var file = TotalHistoryPathFor(account.Id);
            if (!File.Exists(file)) return result;
            foreach (var line in File.ReadAllLines(file))
            {
                var idx = line.IndexOf(',');
                if (idx <= 0) continue;
                if (!DateTime.TryParseExact(line.Substring(0, idx).Trim(), "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var date)) continue;
                if (!double.TryParse(line.Substring(idx + 1).Trim(), out var total)) continue;
                result.Add((date, total));
            }
        }
        catch { }
        return result;
    }

    /// <summary>该账号今天是否已记录过总积分（用于每天只补记一次）。</summary>
    private bool TotalHistoryHasToday(TraeAccount account)
    {
        var today = DateTime.Today;
        foreach (var (date, _) in ParseTotalHistory(account))
            if (date.Date == today) return true;
        return false;
    }

    /// <summary>计算连续签到天数（今天未签则从昨天起算）。</summary>
    private int ComputeStreak(List<(DateTime Date, double Credits)> history)
    {
        var dates = new HashSet<DateTime>(history.Select(h => h.Date));
        if (dates.Count == 0) return 0;
        int streak = 0;
        var day = DateTime.Today;
        if (!dates.Contains(day)) day = day.AddDays(-1);
        while (dates.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing && _config.AutoCheckinEnabled)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            _tray.Visible = true;
            return;
        }
        _tray?.Dispose();
        base.OnFormClosing(e);
    }
}
