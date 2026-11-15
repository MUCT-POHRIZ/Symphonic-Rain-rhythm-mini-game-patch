using System.Diagnostics;
using System.Drawing;
using System.Text;
using Microsoft.Win32;

namespace SRPerfectPatch;

/// <summary>
/// 主窗口：挂载/卸载补丁、显示设置、托盘、全局热键。
/// </summary>
internal sealed class MainForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SRPerfectPatch";

    private const int HOTKEY_RELOAD = 0x5A01;
    private const int HOTKEY_TOGGLE = 0x5A02;
    private const int HOTKEY_DEMO = 0x5A03;
    private const int HOTKEY_PREVIEW = 0x5A04;

    private readonly GamePatcher _patcher = new();
    private PatchConfig _cfg = new();
    private Overlay _overlay = null!;

    private readonly Label _lblStatus = new();
    private readonly Label _lblInfo = new();
    private readonly ListBox _log = new();

    private readonly NumericUpDown _numX = new();
    private readonly NumericUpDown _numY = new();
    private readonly NumericUpDown _numFont = new();
    private readonly NumericUpDown _numFade = new();
    private readonly TextBox _txtPerfect = new();
    private readonly TextBox _txtGood = new();
    private readonly TextBox _txtMiss = new();
    private readonly TextBox _txtCombo = new();

    private readonly CheckBox _chkAutoAttach = new();
    private readonly CheckBox _chkAutoStart = new();
    private readonly CheckBox _chkStorySummary = new();

    private readonly ComboBox _cmbFit = new();
    private readonly ComboBox _cmbPreset = new();
    private int _editingPreset;      // 0 = 全屏预设，1 = 窗口预设

    private readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 16 };
    private readonly NotifyIcon _tray = new();

    private int _lastSeq;
    private int _lastPid;
    private int _lastLoggedPid;
    private string _lastDiag = "";   // 上一条覆盖层诊断，用于去重
    private bool _overlayEnabled = true;
    private string _gameExe;
    private bool _reallyExit;

    // ---- 单场演奏统计 ----
    private int _cntPerfect, _cntGood, _cntMiss, _cntEarly, _cntLate, _cntEmpty;
    private int _playerNotes = -1;   // 本曲需要玩家自己弹的音符数（自动演奏的不算）
    private long _musicUnit;
    private string _sessionInfo = "";      // 「需弹 N 个音符 · 全长 X 秒」，首条判定时写进日志
    private long _sessionLoggedAt = -1;    // 已写过「本场开始」的 MusicUnit 指针
    private long _lastEventMs;
    private bool _resultShown;
    private int _songMode = -1;      // MusicUnit+0x28C：演奏场景类型（0 = free play）
    private int _chartNotes = -1;    // 谱面总音符数（含歌词行、自动演奏）
    private long _lastNoteTicks = -1; // 最后一个音符的时刻（1/88200 秒）
    private long _nowTicks;           // 最近一次采样到的播放位置（1/88200 秒）
    private int _lastProgress;       // 上次写进度日志时的判定数

    private readonly ComboBox _cmbLang = new ComboBox();
    private bool _langBusy;
    private string _statusZh = "未连接";
    private Size _lastNormalClient;   // 最后一次「非最大化/非最小化」的客户区大小

    public MainForm()
    {
        _gameExe = FindGameExe();
        _cfg = PatchConfig.Load();
        Lang.Code = PatchConfig.NormLang(_cfg.Language);

        Text = "SR Perfect Patch — 交响乐之雨 判定显示补丁";
        StartPosition = FormStartPosition.CenterScreen;
        // 宽度保持原样，只把高度加大，给日志留出空间；屏幕不够高时自动收缩。
        // 若上次手动调过窗口大小（config 里 windowwidth/windowheight），就照上次的来。
        MinimumSize = new Size(660, 600);
        ClientSize = DefaultClientSize(_cfg);
        _lastNormalClient = ClientSize;
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Theme.Paper;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildUi();

        _overlay = new Overlay(_cfg)
        {
            // 诊断只在「状态变化」时写一行，避免每秒刷屏
            Diag = s => { if (s != _lastDiag) { _lastDiag = s; Log(s); } }
        };
        _overlay.Show();
        _overlay.OverlayEnabled = true;

        _watchTimer.Tick += (_, _) => Watch();
        _pollTimer.Tick += (_, _) => PollEvents();
        _watchTimer.Start();
        _pollTimer.Start();

        LoadConfigToUi();
        Log("就绪。补丁只改内存，不改任何游戏文件。");
        Log(_gameExe.Length > 0 ? "游戏路径：" + _gameExe : "未找到 SR.exe，请手动启动游戏。");
    }

    // ---------------------------------------------------------------- UI
    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        ApplyStatus();
        _lblStatus.AutoSize = true;
        _lblStatus.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);

        _lblInfo.Text = "判定来源：SR.exe 内存钩子（Press / Miss 两处），显示的是游戏自己算出的档位";
        _lblInfo.AutoSize = true;

        var pnlTop = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        pnlTop.Controls.Add(_lblStatus);
        root.Controls.Add(pnlTop, 0, 0);

        var pnlButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var btnAttach = new Button { Text = "挂载补丁", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var btnLaunch = new Button { Text = "启动游戏并挂载", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var btnDetach = new Button { Text = "卸载补丁", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        btnAttach.Click += (_, _) => AttachNow(manual: true);
        btnLaunch.Click += (_, _) => LaunchGame();
        btnDetach.Click += (_, _) => DetachNow();
        pnlButtons.Controls.Add(btnAttach);
        pnlButtons.Controls.Add(btnLaunch);
        pnlButtons.Controls.Add(btnDetach);
        root.Controls.Add(pnlButtons, 0, 1);

        var pnlChecks = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        _chkAutoAttach.Text = "游戏启动时自动挂载";
        _chkAutoAttach.AutoSize = true;
        _chkAutoAttach.CheckedChanged += (_, _) =>
        {
            _cfg.AutoAttach = _chkAutoAttach.Checked;
            _cfg.Save();
        };
        _chkAutoStart.Text = "开机自动启动本程序（写入 HKCU\\...\\Run，可随时取消）";
        _chkAutoStart.AutoSize = true;
        _chkAutoStart.Checked = IsAutoStart();
        _chkAutoStart.CheckedChanged += (_, _) => SetAutoStart(_chkAutoStart.Checked);
        _chkStorySummary.Text = "剧情演奏也弹成绩单（默认关闭，只统计 free play）";
        _chkStorySummary.AutoSize = true;
        _chkStorySummary.Checked = _cfg.StorySummary;
        _chkStorySummary.CheckedChanged += (_, _) =>
        {
            _cfg.StorySummary = _chkStorySummary.Checked;
            _cfg.Save();
            Log(_cfg.StorySummary
                ? "已开启：剧情里的演奏也会弹成绩单。"
                : "已关闭：只有 free play 才弹成绩单。");
        };
        pnlChecks.Controls.Add(_chkAutoAttach);
        pnlChecks.Controls.Add(_chkAutoStart);
        pnlChecks.Controls.Add(_chkStorySummary);
        root.Controls.Add(pnlChecks, 0, 2);

        // 参数区：每行一组，按钮单独一行
        var pnlParams = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };

        // 第 0 行：界面语言（选项固定用英文，方便非中文玩家）
        var rowLang = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowLang.Controls.Add(new Label { Text = "Language", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        _cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbLang.Width = 168;
        _cmbLang.Items.AddRange(new object[] { "Simplified Chinese", "English", "Japanese" });
        _cmbLang.SelectedIndexChanged += (_, _) => SwitchLanguage();
        rowLang.Controls.Add(_cmbLang);
        pnlParams.Controls.Add(rowLang);

        // 第 1 行：显示模式 + 正在编辑的预设
        var rowMode = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowMode.Controls.Add(new Label { Text = "显示模式：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        _cmbFit.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbFit.Width = 150;
        _cmbFit.Items.AddRange(new object[] { "自动适配（按比例）", "全屏预设（绝对）", "窗口预设（绝对）" });
        _cmbFit.SelectedIndexChanged += (_, _) => { };
        rowMode.Controls.Add(_cmbFit);
        rowMode.Controls.Add(new Label { Text = "预设：", AutoSize = true, Margin = new Padding(18, 6, 2, 0) });
        _cmbPreset.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbPreset.Width = 110;
        _cmbPreset.Items.AddRange(new object[] { "全屏预设", "窗口预设" });
        _cmbPreset.SelectedIndex = 0;
        _cmbPreset.SelectedIndexChanged += (_, _) => SwitchEditingPreset();
        rowMode.Controls.Add(_cmbPreset);
        pnlParams.Controls.Add(rowMode);

        // 第 2 行：X、Y
        var rowPos = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowPos.Controls.Add(new Label { Text = "X：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        _numX.Minimum = -1; _numX.Maximum = 4000; _numX.Width = 64;
        rowPos.Controls.Add(_numX);
        rowPos.Controls.Add(new Label { Text = "Y：", AutoSize = true, Margin = new Padding(20, 6, 2, 0) });
        _numY.Minimum = -1; _numY.Maximum = 4000; _numY.Width = 64;
        rowPos.Controls.Add(_numY);
        pnlParams.Controls.Add(rowPos);

        // 第 2 行：字号、停留时间
        var rowFont = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowFont.Controls.Add(new Label { Text = "字号(px)：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        _numFont.Minimum = 10; _numFont.Maximum = 120; _numFont.Width = 64;
        rowFont.Controls.Add(_numFont);
        rowFont.Controls.Add(new Label { Text = "停留(ms)：", AutoSize = true, Margin = new Padding(20, 6, 2, 0) });
        _numFade.Minimum = 100; _numFade.Maximum = 10000; _numFade.Increment = 100; _numFade.Width = 64;
        rowFont.Controls.Add(_numFade);
        pnlParams.Controls.Add(rowFont);

        // 第 3 行：三种判定的颜色
        var rowColor = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowColor.Controls.Add(new Label { Text = "Perfect 色：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        _txtPerfect.Width = 84;
        rowColor.Controls.Add(_txtPerfect);
        rowColor.Controls.Add(new Label { Text = "Good 色：", AutoSize = true, Margin = new Padding(12, 6, 2, 0) });
        _txtGood.Width = 84;
        rowColor.Controls.Add(_txtGood);
        rowColor.Controls.Add(new Label { Text = "Miss 色：", AutoSize = true, Margin = new Padding(12, 6, 2, 0) });
        _txtMiss.Width = 84;
        rowColor.Controls.Add(_txtMiss);
        pnlParams.Controls.Add(rowColor);

        // 第 3.5 行：连击数颜色 + 提示
        var rowColor2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        rowColor2.Controls.Add(new Label { Text = "连击 色：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
        _txtCombo.Width = 84;
        rowColor2.Controls.Add(_txtCombo);
        rowColor2.Controls.Add(new Label
        {
            Text = "（判定文字带深色描边，白底/花背景也能看清）",
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 110, 110),
            Margin = new Padding(12, 8, 2, 0),
        });
        pnlParams.Controls.Add(rowColor2);

        // 第 4 行：保存按钮独占一行
        var rowSave = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 4) };
        var btnSave = new Button { Text = "保存并立即生效", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        btnSave.Click += (_, _) => SaveFromUi();
        rowSave.Controls.Add(btnSave);
        var btnDefault = new Button
        {
            Text = "恢复默认设置",
            AutoSize = true,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(8, 3, 3, 3),
        };
        btnDefault.Click += (_, _) => RestoreDefaults();
        rowSave.Controls.Add(btnDefault);
        pnlParams.Controls.Add(rowSave);

        // 参数卡：酒红标题 + 纸面卡片
        var cardFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        cardFlow.Controls.Add(new Label
        {
            Text = "显示设置",
            AutoSize = true,
            Font = Theme.BodyBold,
            ForeColor = Theme.Wine,
            Margin = new Padding(2, 0, 0, 6),
        });
        cardFlow.Controls.Add(pnlParams);

        var card = new Card
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 9, 12, 11),
            Margin = new Padding(0, 4, 0, 6),
        };
        card.Controls.Add(cardFlow);
        root.Controls.Add(card, 0, 3);

        root.Controls.Add(_lblInfo, 0, 4);

        _log.Dock = DockStyle.Fill;
        _log.IntegralHeight = false;
        _log.HorizontalScrollbar = true;
        _log.Font = new Font("Consolas", 8.5f);
        root.Controls.Add(_log, 0, 5);

        // 右下角署名：小字、淡灰，不抢视线
        var lblCredit = new Label
        {
            Text = "由B站@MUCT_POHRIZ使用DeepSeek Harness辅助开发",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Microsoft YaHei UI", 7.5f),
            Margin = new Padding(0, 4, 0, 0),
        };
        root.Controls.Add(lblCredit, 0, 6);

        // 顶部游戏风格横幅 + 内容区
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Paper,
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.Controls.Add(new ThemeHeader { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 0);
        outer.Controls.Add(root, 0, 1);

        Controls.Add(outer);

        // 统一外观（按钮/输入框/日志区）
        Theme.StyleAll(outer);
        _lblInfo.Font = Theme.Small;
        _lblInfo.ForeColor = Theme.InkSoft;
        _log.Font = Theme.Mono;

        _tray.Text = "SR Perfect Patch";
        _tray.Visible = true;
        try { _tray.Icon = new Icon(Icon, 16, 16); } catch { _tray.Icon = SystemIcons.Application; }
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => Restore());
        menu.Items.Add("挂载补丁", null, (_, _) => AttachNow(manual: true));
        menu.Items.Add("卸载补丁", null, (_, _) => DetachNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Restore();

        Lang.ApplyTo(this);
        Lang.ApplyTo(menu.Items);
        ApplyStatus();
    }

    private void Restore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RegisterHotKey(Handle, HOTKEY_RELOAD, 0, 0x78); // F9
        Native.RegisterHotKey(Handle, HOTKEY_TOGGLE, 0, 0x79); // F10
        Native.RegisterHotKey(Handle, HOTKEY_DEMO, 0, 0x77);   // F8
        Native.RegisterHotKey(Handle, HOTKEY_PREVIEW, 0, 0x76); // F7
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, HOTKEY_RELOAD);
        Native.UnregisterHotKey(Handle, HOTKEY_TOGGLE);
        Native.UnregisterHotKey(Handle, HOTKEY_DEMO);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_RELOAD)
            {
                var cfg = PatchConfig.Load();
                CopyInto(_cfg, cfg);
                _overlay.ReloadConfig(_cfg);
                LoadConfigToUi();
                Log("已重新加载 config.ini");
                return;
            }
            if (id == HOTKEY_TOGGLE)
            {
                _overlayEnabled = !_overlayEnabled;
                _overlay.OverlayEnabled = _overlayEnabled;
                Log(_overlayEnabled ? "覆盖层已显示" : "覆盖层已隐藏");
                return;
            }
            if (id == HOTKEY_DEMO)
            {
                _overlay.ShowDemo();
                Log("自检：显示一条 Perfect（F8）");
                return;
            }
            if (id == HOTKEY_PREVIEW)
            {
                ShowPreview();
                Log("预览：成绩单样式（F7，数字为示例）");
                return;
            }
        }
        base.WndProc(ref m);
    }

    private static void CopyInto(PatchConfig dst, PatchConfig src)
    {
        dst.X = src.X; dst.Y = src.Y; dst.FontSize = src.FontSize; dst.FadeMs = src.FadeMs;
        dst.XWin = src.XWin; dst.YWin = src.YWin; dst.FontWin = src.FontWin; dst.FitMode = src.FitMode;
        dst.ShowCombo = src.ShowCombo;
        dst.ColorPerfect = src.ColorPerfect; dst.ColorGood = src.ColorGood; dst.ColorMiss = src.ColorMiss;
        dst.ColorCombo = src.ColorCombo;
        dst.ClickThrough = src.ClickThrough; dst.AutoAttach = src.AutoAttach;
        dst.AlignLeft = src.AlignLeft;
        dst.StorySummary = src.StorySummary;
        dst.FreePlayModeValue = src.FreePlayModeValue;
        dst.WindowWidth = src.WindowWidth;
        dst.WindowHeight = src.WindowHeight;
    }

    // ---------------------------------------------------------------- 生命周期
    private void Watch()
    {
        Process? p = FindGame();
        if (p == null)
        {
            // Detach() 会把 Pid/Installed 清零，所以这段只会真正执行一次，不会每秒刷屏
            if (_patcher.Pid != 0 || _patcher.Installed)
            {
                _patcher.Detach();
                _lastPid = 0;
                _overlay.Reset();
                SetStatus("未连接", Color.DimGray);
                Log("游戏已退出，补丁已自动卸载。");
            }
            return;
        }

        if (_lastLoggedPid != p.Id)
        {
            _lastLoggedPid = p.Id;
            _lastPid = p.Id;
            Log("检测到 SR.exe (PID " + p.Id + ")");
        }

        _overlay.SetGameProcess((uint)p.Id);

        if (!_patcher.Installed && _cfg.AutoAttach)
            AttachNow(manual: false);

        // 兜底之一：演奏已经过了最后一个音符 → 直接出成绩单（不依赖判定条数）
        if (_patcher.Installed && !_resultShown)
            CheckFinished();

        // 兜底之二：MusicUnit 消失了（演奏结束或中途退出）——只有「播放位置已经到过最后一个音符」
        // 才算完成；中途退出（ESC / 重开）一律不出成绩单。
        if (_patcher.Installed && !_resultShown && _playerNotes > 0 && !_patcher.TryReadMusicUnit(out _))
        {
            int judged = _cntPerfect + _cntGood + _cntMiss;
            if (judged > 0)
            {
                _resultShown = true;
                bool reachedEnd = _lastNoteTicks <= 0 || _nowTicks >= _lastNoteTicks - 44100; // 距终点 0.5 秒内都算
                if (reachedEnd && judged >= _playerNotes * 0.9)
                {
                    if (SummaryAllowed()) ShowResult();
                    else Log(SummaryDeniedReason());
                }
                else
                {
                    Log($"本场中途退出，不出成绩单（已判定 {judged} / 需弹 {_playerNotes}）。");
                    _cntPerfect = _cntGood = _cntMiss = _cntEarly = _cntLate = _cntEmpty = 0;
                }
            }
        }
    }

    private void PollEvents()
    {
        if (!_patcher.Installed) return;

        // 每 16ms 顺手记一下播放位置，用于判断「是否真的演奏到了最后一个音符」
        if (TryReadNowTicks(out long tk)) _nowTicks = tk;

        // 模式是演奏开始后才写进 MusicUnit 的，读不到就每帧再试，别一直卡在"未知"
        if (_songMode < 0 && _playerNotes > 0) ReadSongMode();

        int seq = _lastSeq;
        for (int i = 0; i < 64; i++)
        {
            if (!_patcher.TryReadEvent(ref seq, out GamePatcher.JEvent ev)) break;
            OnEvent(ev);
        }
        _lastSeq = seq;
    }

    /// <summary>处理一条判定事件，并维护单场统计（Perfect / Good(Early,Late) / Miss / 空按）。</summary>
    private void OnEvent(GamePatcher.JEvent ev)
    {
        long nowMs = Environment.TickCount64;

        // 换曲 ⇒ 视为新的一场；只有「还没判过任何音符」时才用空闲超时兜底，
        // 否则中途停顿超过 6 秒会把本场统计清零。
        long mu = _patcher.TryReadMusicUnit(out IntPtr p) ? p.ToInt64() : 0;
        int judgedNow = _cntPerfect + _cntGood + _cntMiss;
        if (_resultShown
            || (judgedNow == 0 && _lastEventMs != 0 && nowMs - _lastEventMs > 6000)
            || (mu != 0 && _musicUnit != 0 && mu != _musicUnit)
            || (ev.NoteIdx == 0 && judgedNow > 0))
            BeginSession(mu);
        else if (mu != 0)
            _musicUnit = mu;

        _lastEventMs = nowMs;

        // 本场第一条判定出现时才写「本场开始」，避免选曲界面残留的 MusicUnit 刷日志
        if (_cntPerfect + _cntGood + _cntMiss == 0 && _sessionLoggedAt != _musicUnit)
        {
            if (_playerNotes < 0) ReadPlayerNotes();
            if (_playerNotes > 0 && _sessionInfo.Length > 0)
            {
                _sessionLoggedAt = _musicUnit;
                if (_songMode < 0) ReadSongMode();
                string mode = _songMode < 0 ? "模式未知"
                            : _songMode == _cfg.FreePlayModeValue ? "free play"
                            : "剧情演奏";
                Log($"本场开始 · {_sessionInfo} · {mode}");
            }
        }

        if (ev.Type == 2)
        {
            _cntMiss++;
            _overlay.OnMiss(ev.DeltaTicks);
        }
        else
        {
            _overlay.OnPress(ev.Grade, ev.DeltaTicks);
            if (ev.Grade >= 3)
            {
                _cntPerfect++;
            }
            else if (ev.Grade == 2)
            {
                _cntGood++;
                if (ev.DeltaTicks >= 0) _cntEarly++;
                else _cntLate++;
            }
            else if (ev.NoteIdx >= 0)
            {
                _cntMiss++;
            }
            else
            {
                // 空按：没有命中任何音符，游戏只扣灯，不计入曲目音符统计
                _cntEmpty++;
            }
        }

        CheckFinished();
        if (_playerNotes < 0) ReadPlayerNotes(); // 首个事件时音符表可能还没就绪，边打边补读
    }

    private void BeginSession(long mu)
    {
        _cntPerfect = _cntGood = _cntMiss = _cntEarly = _cntLate = _cntEmpty = 0;
        _playerNotes = -1;
        _chartNotes = -1;
        _lastProgress = 0;
        _resultShown = false;
        _nowTicks = 0;          // 上一场的播放位置不能带进本场，否则中途退出会被误判成"弹完了"
        _lastNoteTicks = -1;
        _musicUnit = mu;
        _songMode = -1;
        _overlay.ClearResult();
        _overlay.ResetCombo();
        ReadSongMode();
        ReadPlayerNotes();
    }

    /// <summary>
    /// MusicUnit+0x28C 是「演奏数据源/场景类型」：0/1 用 Song.pak 的谱面，2 用 Event.pak，≥3 用 event2/3/4。
    /// free play 是 0；剧情里的演奏是另一个值。读不到就返回 -1（按 free play 处理，宁可多弹一次）。
    /// </summary>
    private void ReadSongMode()
    {
        _songMode = -1;
        if (!_patcher.TryReadMusicUnit(out IntPtr mu)) return;
        if (_patcher.ReadInt(new IntPtr(mu.ToInt64() + 0x28C), out int m)) _songMode = m;
    }

    /// <summary>
    /// 本场是否允许弹成绩单。free play 一律允许；剧情演奏（以及模式读不到的情况）
    /// 由「剧情演奏也弹成绩单」开关决定，默认关闭。
    /// </summary>
    private bool SummaryAllowed()
    {
        if (_songMode == _cfg.FreePlayModeValue) return true;
        if (_songMode < 0) return _cfg.StorySummary;      // 读不到 → 按剧情处理
        return _cfg.StorySummary;                          // 明确的剧情演奏 → 看开关
    }

    /// <summary>本场不给成绩单时的说明，写日志用。</summary>
    private string SummaryDeniedReason()
    {
        if (_songMode < 0) return "本场读不到演奏模式（当作剧情演奏处理），不弹成绩单。";
        return "本场是剧情里的演奏，按设置不弹成绩单。";
    }

    /// <summary>直接从 MusicUnit 的音符表数出「需要玩家弹」的音符个数（+0x14 自动演奏标志为 0）。</summary>
    private void ReadPlayerNotes()
    {
        _playerNotes = -1;
        _chartNotes = -1;
        if (!_patcher.TryReadMusicUnit(out IntPtr mu))
        {
            Log("音符表读不到（游戏还没开始演奏），成绩单暂不启用。");
            return;
        }
        long b = mu.ToInt64();
        if (!_patcher.ReadInt(new IntPtr(b + 0x1B2C), out int count) || count <= 0 || count > 1024)
        {
            Log("音符表数量异常（count=" + count + "），成绩单暂不启用。");
            return;
        }
        if (!_patcher.ReadInt(new IntPtr(b + 0x1B28), out int arr) || arr < 0x10000)
        {
            Log("音符表指针异常（0x" + ((uint)arr).ToString("X8") + "），成绩单暂不启用。");
            return;
        }

        byte[] buf = new byte[count * 28];
        if (!_patcher.ReadMemory(new IntPtr((long)(uint)arr), buf, buf.Length))
        {
            Log("音符表读取失败，成绩单暂不启用。");
            return;
        }

        int players = 0, keyed = 0;
        int lastTicks = 0;
        for (int i = 0; i < count; i++)
        {
            int off = i * 28;
            int t = BitConverter.ToInt32(buf, off + 0x00);
            int key = BitConverter.ToInt32(buf, off + 0x0C);
            int auto = BitConverter.ToInt32(buf, off + 0x14);
            if (auto == 0) players++;
            if (auto == 0 && key >= 0 && key <= 29) keyed++;
            if (t > lastTicks) lastTicks = t;
        }

        _chartNotes = count;
        _lastNoteTicks = lastTicks;
        // 谱面里除了音符还有歌词行（每行 7 种语言各一条记录），它们不会被判定，
        // 所以要按「键位有效」来数真正需要弹的音符。
        _playerNotes = keyed > 0 ? keyed : players;

        // 先不写日志：选曲 / 读取存档时残留的 MusicUnit 也会走到这里，
        // 只有当真正出现第一个判定时才写「本场开始」（见 OnEvent）。
        _sessionInfo = $"需弹 {_playerNotes} 个音符 · 全长 {(lastTicks / 88200.0):0.0} 秒";
    }

    private void CheckFinished()
    {
        if (_resultShown || _playerNotes <= 0) return;
        int judged = _cntPerfect + _cntGood + _cntMiss;
        if (judged <= 0) return;

        long now = TryReadNowTicks(out long t) ? t : _nowTicks;

        // 主判据：所有需要弹的音符都出了结果（弹到 / 漏掉都算）
        bool done = judged >= _playerNotes;

        // 备用判据：播放位置已经越过最后一个音符 1 秒（丢了一两个事件也能收尾）
        // 注意：中途退出时播放位置到不了这里，所以不会误弹
        if (!done && _lastNoteTicks > 0 && now > _lastNoteTicks + 88200) done = true;

        if (!done) return;

        _resultShown = true;

        // 模式可能一开始读不到，结束前再试几次（剧情演奏里 0x28C 有时晚一步才写好）
        if (_songMode < 0) ReadSongMode();

        if (!SummaryAllowed())
        {
            Log(SummaryDeniedReason());
            return;
        }
        ShowResult();
    }

    /// <summary>读 MusicUnit 的当前播放时刻（单位 1/88200 秒）。</summary>
    private bool TryReadNowTicks(out long ticks)
    {
        ticks = 0;
        if (!_patcher.TryReadMusicUnit(out IntPtr mu)) return false;
        if (!_patcher.ReadInt(new IntPtr(mu.ToInt64() + 0x1B38), out int v)) return false;
        ticks = v;
        return ticks > 0;
    }

    private static string Row(string label, int value) => label.PadRight(8) + value.ToString().PadLeft(3);

    private void ShowResult()
    {
        Color cPerfect = PatchConfig.ParseColor(_cfg.ColorPerfect, Color.Gold);
        Color cGood = PatchConfig.ParseColor(_cfg.ColorGood, Color.DeepSkyBlue);
        Color cMiss = PatchConfig.ParseColor(_cfg.ColorMiss, Color.Gray);
        Color cTitle = Color.FromArgb(255, 250, 246, 232);
        Color cBody = Color.FromArgb(255, 240, 240, 240);

        int score = 0;
        bool hasScore = _patcher.TryReadMusicUnit(out IntPtr mu) &&
                        _patcher.ReadInt(new IntPtr(mu.ToInt64() + 0x2D64), out score);

        var lines = new List<(string Text, Color Color)>
        {
            ("演奏结束", cTitle),
            (Row("Perfect", _cntPerfect), cPerfect),
            (Row("Good", _cntGood), cGood),
            ("  Early " + _cntEarly.ToString().PadLeft(3) + "   Late " + _cntLate.ToString().PadLeft(3), cGood),
            (Row("Miss", _cntMiss), cMiss),
        };
        if (_cntEmpty > 0) lines.Add((Row("空按", _cntEmpty), cMiss));
        if (hasScore) lines.Add(("Score " + score.ToString("N0"), cBody));

        _overlay.ShowResult(lines, 9000);

        Log("本场成绩 · " + $"Perfect {_cntPerfect} · Good {_cntGood}（早 {_cntEarly} / 晚 {_cntLate}）" +
            $" · Miss {_cntMiss}" + (_cntEmpty > 0 ? $" · 空按 {_cntEmpty}" : "") +
            (hasScore ? $" · Score {score}" : ""));
    }

    /// <summary>F7：用示例数字预览成绩单排版，不用真的打一首歌。</summary>
    private void ShowPreview()
    {
        Color cPerfect = PatchConfig.ParseColor(_cfg.ColorPerfect, Color.Gold);
        Color cGood = PatchConfig.ParseColor(_cfg.ColorGood, Color.DeepSkyBlue);
        Color cMiss = PatchConfig.ParseColor(_cfg.ColorMiss, Color.Gray);
        var lines = new List<(string Text, Color Color)>
        {
            ("演奏结束", Color.FromArgb(255, 250, 246, 232)),
            (Row("Perfect", 235), cPerfect),
            (Row("Good", 12), cGood),
            ("  Early   7   Late   5", cGood),
            (Row("Miss", 3), cMiss),
            ("Score 68,441", Color.FromArgb(255, 240, 240, 240)),
        };
        _overlay.ShowResult(lines, 9000);
    }

    private void AttachNow(bool manual)
    {
        if (_patcher.Installed) return;
        if (!_patcher.Attach(out string msg))
        {
            SetStatus("未连接", Color.Firebrick);
            if (manual) Log(msg);
            return;
        }
        if (!string.IsNullOrEmpty(msg)) Log(msg);
        if (!_patcher.Install(out string m2))
        {
            Log(m2);
            SetStatus(string.IsNullOrEmpty(m2) ? "挂载失败" : "挂载失败", Color.Firebrick);
            return;
        }
        _lastSeq = 0;
        _patcher.TryReadSeq(out _lastSeq);
        SetStatus("已挂载 (PID " + _patcher.Pid + ")", Color.SeaGreen);
        Log(string.IsNullOrEmpty(m2) ? "补丁已安装（仅内存）。" : m2);
    }

    private void DetachNow()
    {
        _patcher.Detach();
        _overlay.Reset();
        _cntPerfect = _cntGood = _cntMiss = _cntEarly = _cntLate = _cntEmpty = 0;
        _playerNotes = -1;
        _musicUnit = 0;
        _resultShown = false;
        SetStatus("未连接", Color.DimGray);
        Log("补丁已卸载，内存已还原。");
    }

    private void LaunchGame()
    {
        if (FindGame() != null)
        {
            Log("游戏已在运行，直接挂载。");
            AttachNow(manual: true);
            return;
        }
        if (_gameExe.Length == 0)
        {
            Log("找不到 SR.exe，请手动启动游戏。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _gameExe,
                WorkingDirectory = Path.GetDirectoryName(_gameExe) ?? "",
                UseShellExecute = true,
            });
            Log("已启动游戏，等待窗口出现后自动挂载…");
        }
        catch (Exception ex)
        {
            Log("启动失败：" + ex.Message);
        }
    }

    private static Process? FindGame()
    {
        Process[] all = Process.GetProcessesByName("SR");
        foreach (Process p in all)
        {
            try
            {
                if (!p.HasExited) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>在常见 Steam 库位置里找 SR.exe；找不到就留空，让用户自己启动游戏。</summary>
    private static string FindGameExe()
    {
        var candidates = new List<string>();
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steam = k?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(steam))
            {
                candidates.Add(Path.Combine(steam, "steamapps", "common", "Symphonic Rain", "SR.exe"));
                string libFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libFile))
                {
                    foreach (string raw in File.ReadAllLines(libFile))
                    {
                        int q1 = raw.IndexOf('"');
                        int q2 = raw.LastIndexOf('"');
                        if (q1 < 0 || q2 <= q1) continue;
                        string v = raw.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\");
                        if (v.Length > 2 && v[1] == ':')
                            candidates.Add(Path.Combine(v, "steamapps", "common", "Symphonic Rain", "SR.exe"));
                    }
                }
            }
        }
        catch { }

        foreach (char drive in "CDEFGHIJK")
            candidates.Add($@"{drive}:\SteamLibrary\steamapps\common\Symphonic Rain\SR.exe");

        foreach (string c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return "";
    }

    private void SetStatus(string text, Color color)
    {
        _statusZh = text;
        _lblStatus.ForeColor = color;
        ApplyStatus();
    }

    /// <summary>按当前语言重刷状态栏（切换语言后也要调用）。</summary>
    private void ApplyStatus()
    {
        _lblStatus.Text = Lang.Tr("状态：") + Lang.Tr(_statusZh);
    }

    /// <summary>切换界面语言：就地翻译控件与托盘菜单，不重建界面（重建会重复挂事件）。</summary>
    private void SwitchLanguage()
    {
        if (_langBusy) return;
        int i = Math.Max(0, _cmbLang.SelectedIndex);
        string code = i == 1 ? "en" : i == 2 ? "ja" : "zh";
        if (code == Lang.Code) return;

        _langBusy = true;
        try
        {
            Lang.Code = code;
            _cfg.Language = code;
            _cfg.Save();
            Lang.ApplyTo(this);
            if (_tray != null && _tray.ContextMenuStrip != null)
                Lang.ApplyTo(_tray.ContextMenuStrip.Items);
            ApplyStatus();
            Invalidate(true);
            if (_log.Items.Count > 0) _log.TopIndex = _log.Items.Count - 1;
            Log("界面语言已切换为 " + Lang.Name(code));
        }
        finally
        {
            _langBusy = false;
        }
    }

    private void Log(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + Lang.Tr(s);
        _log.Items.Add(line);
        if (_log.Items.Count > 400) _log.Items.RemoveAt(0);
        _log.TopIndex = _log.Items.Count - 1;
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "patch.log"), line + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    // ---------------------------------------------------------------- 配置
    private void LoadConfigToUi()
    {
        _cmbFit.SelectedIndex = _cfg.FitMode switch { "full" => 1, "window" => 2, _ => 0 };
        _editingPreset = _cfg.FitMode == "window" ? 1 : 0;
        _cmbPreset.SelectedIndex = _editingPreset;
        LoadPresetToUi(_editingPreset);
        _numFade.Value = Math.Clamp(_cfg.FadeMs, 100, 10000);
        _txtPerfect.Text = _cfg.ColorPerfect;
        _txtGood.Text = _cfg.ColorGood;
        _txtMiss.Text = _cfg.ColorMiss;
        _txtCombo.Text = _cfg.ColorCombo;
        _chkAutoAttach.Checked = _cfg.AutoAttach;
        _chkStorySummary.Checked = _cfg.StorySummary;

        _langBusy = true;
        try
        {
            _cmbLang.SelectedIndex = Lang.Code == "en" ? 1 : Lang.Code == "ja" ? 2 : 0;
        }
        finally
        {
            _langBusy = false;
        }
    }

    /// <summary>把某个预设的坐标/字号读进编辑框。</summary>
    private void LoadPresetToUi(int preset)
    {
        if (preset == 1)
        {
            _numX.Value = Math.Clamp(_cfg.XWin, -1, 4000);
            _numY.Value = Math.Clamp(_cfg.YWin, -1, 4000);
            _numFont.Value = (decimal)Math.Clamp(_cfg.FontWin, 10f, 120f);
        }
        else
        {
            _numX.Value = Math.Clamp(_cfg.X, -1, 4000);
            _numY.Value = Math.Clamp(_cfg.Y, -1, 4000);
            _numFont.Value = (decimal)Math.Clamp(_cfg.FontSize, 10f, 120f);
        }
    }

    /// <summary>切换「预设」下拉时：先把编辑框里的值存回旧预设，再载入新预设。</summary>
    private void SwitchEditingPreset()
    {
        if (_cmbPreset.SelectedIndex == _editingPreset) return;
        StorePresetFromUi(_editingPreset);
        _editingPreset = _cmbPreset.SelectedIndex;
        LoadPresetToUi(_editingPreset);
    }

    /// <summary>把编辑框里的坐标/字号写回某个预设（尚未保存到 ini）。</summary>
    private void StorePresetFromUi(int preset)
    {
        if (preset == 1)
        {
            _cfg.XWin = (int)_numX.Value;
            _cfg.YWin = (int)_numY.Value;
            _cfg.FontWin = (float)_numFont.Value;
        }
        else
        {
            _cfg.X = (int)_numX.Value;
            _cfg.Y = (int)_numY.Value;
            _cfg.FontSize = (float)_numFont.Value;
        }
    }

    /// <summary>把界面上的所有设置恢复成程序内置的默认值（Config.cs 里的初值），并立即保存。</summary>
    private void RestoreDefaults()
    {
        if (MessageBox.Show(this,
                "把显示模式、两套预设的 X/Y/字号、停留时间、三种判定颜色、自动挂载、「剧情演奏也弹成绩单」、窗口大小全部恢复为默认值？\n当前配置会被覆盖。",
                "恢复默认设置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        CopyInto(_cfg, new PatchConfig());
        _cfg.WindowWidth = 0;          // 窗口大小也回到自动
        _cfg.WindowHeight = 0;
        _cfg.Save();
        _overlay.ReloadConfig(_cfg);
        LoadConfigToUi();
        if (WindowState == FormWindowState.Normal) ClientSize = DefaultClientSize(_cfg);
        Log("已恢复默认设置（显示模式 自动适配 / 全屏预设 440,456,27 / 窗口预设 440,444,26 / 停留 900 / 剧情演奏不弹成绩单 / 窗口大小 自动）。");
    }

    /// <summary>默认窗口尺寸：宽度固定 700，高度按屏幕工作区给日志留空间；有保存过的尺寸就用保存的。</summary>
    private static Size DefaultClientSize(PatchConfig cfg)
    {
        Rectangle wa;
        try { wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080); }
        catch { wa = new Rectangle(0, 0, 1920, 1080); }

        if (cfg.WindowWidth >= 200 && cfg.WindowHeight >= 200)
        {
            return new Size(
                Math.Min(cfg.WindowWidth, Math.Max(200, wa.Width)),
                Math.Min(cfg.WindowHeight, Math.Max(200, wa.Height)));
        }
        return new Size(700, Math.Clamp(wa.Height - 130, 600, 745));
    }

    /// <summary>把当前窗口大小写回 config（最大化/最小化时用最后一次正常大小）。</summary>
    private void StoreWindowSize()
    {
        try
        {
            Size sz = WindowState == FormWindowState.Normal ? ClientSize : _lastNormalClient;
            if (sz.Width < 200 || sz.Height < 200) return;
            _cfg.WindowWidth = sz.Width;
            _cfg.WindowHeight = sz.Height;
            _cfg.Save();
        }
        catch { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Normal && ClientSize.Width >= 200 && ClientSize.Height >= 200)
            _lastNormalClient = ClientSize;
    }

    private void SaveFromUi()
    {
        StorePresetFromUi(_editingPreset);
        _cfg.FitMode = _cmbFit.SelectedIndex switch { 1 => "full", 2 => "window", _ => "auto" };
        _cfg.FadeMs = (int)_numFade.Value;
        _cfg.ColorPerfect = _txtPerfect.Text.Trim();
        _cfg.ColorGood = _txtGood.Text.Trim();
        _cfg.ColorMiss = _txtMiss.Text.Trim();
        _cfg.ColorCombo = _txtCombo.Text.Trim();
        _cfg.AutoAttach = _chkAutoAttach.Checked;
        _cfg.Save();
        _overlay.ReloadConfig(_cfg);
        Log($"配置已保存并生效（显示模式：{_cmbFit.Text}）。");
    }

    // ---------------------------------------------------------------- 开机自启
    private static bool IsAutoStart()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool on)
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("修改开机启动失败：" + ex.Message, "SR Perfect Patch");
        }
    }

    // ---------------------------------------------------------------- 退出
    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StoreWindowSize();
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1500, "SR Perfect Patch", "已最小化到托盘，补丁仍在工作。", ToolTipIcon.Info);
            return;
        }

        try { _patcher.Detach(); } catch { }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchTimer.Dispose();
            _pollTimer.Dispose();
            _tray.Dispose();
            _overlay?.Dispose();
            _patcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
