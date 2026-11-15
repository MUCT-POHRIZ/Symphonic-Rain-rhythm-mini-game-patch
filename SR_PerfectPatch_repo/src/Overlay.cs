using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SRPerfectPatch;

/// <summary>
/// 覆盖在游戏画面上的透明层（每像素 alpha 的分层窗口，默认点击穿透、不抢焦点）。
/// 只在有判定文字存活时显示，平时完全隐藏，不绘制、不刷新。
/// </summary>
internal sealed class Overlay : Form
{
    private sealed class Msg
    {
        public string Main = "";
        public string Sub = "";
        public Color Color;
        public long BornMs;
    }

    /// <summary>演奏结束后的成绩单：多行、带半透明底板，停留时间独立于判定文字。</summary>
    private sealed class Result
    {
        public readonly List<(string Text, Color Color)> Lines = new();
        public long BornMs;
        public int HoldMs = 9000;
    }

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly List<Msg> _msgs = new();
    private Result? _result;
    private readonly System.Windows.Forms.Timer _timer;
    private PatchConfig _cfg = new();

    private Bitmap? _bmp;
    private Graphics? _g;
    private IntPtr _screenDc, _memDc, _dib, _oldObj;
    private int _bmpW, _bmpH;

    private Rectangle _gameRect = Rectangle.Empty;
    private float _effFont = 27f;      // 本帧实际使用的字号（自动适配后会缩放）
    private float _lastX, _lastY;      // 本帧实际使用的坐标（写进诊断日志便于调参）
    private long _lastRenderMs;
    private bool _visible;

    public int Combo { get; private set; }

    public Overlay(PatchConfig cfg)
    {
        _cfg = cfg;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        BackColor = Color.Magenta;
        Opacity = 1;
        SetStyle(ControlStyles.Opaque, true);

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Render();
        _timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            if (_cfg.ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            cp.Width = 1;
            cp.Height = 1;
            return cp;
        }
    }

    public void ReloadConfig(PatchConfig cfg)
    {
        _cfg = cfg;
        FormBorderStyle = FormBorderStyle.None;
        // 重建窗口以应用新的 clickthrough 样式
        if (IsHandleCreated)
        {
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE,
                Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW |
                (_cfg.ClickThrough ? Native.WS_EX_TRANSPARENT : 0));
        }
    }

    /// <summary>游戏窗口客户区在屏幕上的矩形（含尺寸）。</summary>
    public void SetGameRect(Rectangle rect)
    {
        _gameRect = rect;
    }

    public void Reset()
    {
        _msgs.Clear();
        _result = null;
    }

    /// <summary>演奏结束后显示成绩单；lines 为 (文本, 颜色)，holdMs 为停留时间。</summary>
    public void ShowResult(IEnumerable<(string Text, Color Color)> lines, int holdMs = 9000)
    {
        var r = new Result { BornMs = NowMs(), HoldMs = holdMs };
        r.Lines.AddRange(lines);
        _result = r;
        // 清掉残留的判定文字，免得和成绩单叠在一起
        _msgs.Clear();
    }

    public void ClearResult() => _result = null;

    public void ResetCombo() => Combo = 0;

    // ---------------------------------------------------------------- events
    /// <summary>按键判定事件。grade: 3=Perfect, 2=Good, 0/1=Miss(但按键空打也归 Miss)。</summary>
    public void OnPress(int grade, int deltaTicks)
    {
        double ms = deltaTicks / 88.2;
        if (grade >= 3)
        {
            Combo++;
            Add("Perfect", "", PatchConfig.ParseColor(_cfg.ColorPerfect, Color.Gold));
        }
        else if (grade == 2)
        {
            Combo = 0;
            string side = ms >= 0 ? "Early" : "Late";
            Add("Good", $"{side} {Math.Abs(ms):0}ms", PatchConfig.ParseColor(_cfg.ColorGood, Color.DeepSkyBlue));
        }
        else
        {
            Combo = 0;
            Add("Miss", "", PatchConfig.ParseColor(_cfg.ColorMiss, Color.Gray));
        }
    }

    public void OnMiss(int deltaTicks)
    {
        Combo = 0;
        Add("Miss", "", PatchConfig.ParseColor(_cfg.ColorMiss, Color.Gray));
    }

    /// <summary>自检用：轮流显示 Perfect / Good(带 Early 与连击) / Miss，验证覆盖层与描边效果。</summary>
    public void ShowDemo()
    {
        _demoStep = (_demoStep + 1) % 3;
        switch (_demoStep)
        {
            case 1:
                Combo = 27;
                Add("Good", "Early 37ms", PatchConfig.ParseColor(_cfg.ColorGood, Color.DeepSkyBlue));
                break;
            case 2:
                Combo = 0;
                Add("Miss", "", PatchConfig.ParseColor(_cfg.ColorMiss, Color.Gray));
                break;
            default:
                Combo = 27;
                Add("Perfect", "", PatchConfig.ParseColor(_cfg.ColorPerfect, Color.Gold));
                break;
        }
    }

    private int _demoStep = 2;

    public bool HasMessages => _msgs.Count > 0;

    private void Add(string main, string sub, Color c)
    {
        _msgs.Add(new Msg { Main = main, Sub = sub, Color = c, BornMs = NowMs() });
        if (_msgs.Count > 8) _msgs.RemoveRange(0, _msgs.Count - 8);
    }

    private static long NowMs() => Environment.TickCount64;

    private uint _gamePid;
    private bool _enabled = true;
    private long _lastDiagMs;
    private string _lastDiag = "";

    public Action<string>? Diag;

    /// <summary>游戏进程 ID；0 表示未知（则任何前台窗口都接受）。</summary>
    public void SetGameProcess(uint pid) => _gamePid = pid;

    /// <summary>F10 开关。</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool OverlayEnabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) HideOverlay(); }
    }

    // ---------------------------------------------------------------- render
    private void Render()
    {
        long now = NowMs();
        // 清理过期
        _msgs.RemoveAll(m => now - m.BornMs > _cfg.FadeMs);
        if (_result != null && now - _result.BornMs > _result.HoldMs) _result = null;

        if (!_enabled || (_msgs.Count == 0 && _result == null))
        {
            if (_visible) HideOverlay();
            return;
        }

        // 跟随「属于游戏进程的前台窗口」，而不依赖缓存下来的 MainWindowHandle：
        // 游戏切场景／换分辨率／重建窗口时句柄可能变，缓存会失效。
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !Native.IsWindowVisible(fg) || Native.IsIconic(fg))
        {
            if (_visible) HideOverlay();
            Diag_(fg, 0, Rectangle.Empty, false, "无前台窗口");
            return;
        }
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (_gamePid != 0 && pid != _gamePid)
        {
            if (_visible) HideOverlay();
            Diag_(fg, pid, Rectangle.Empty, false, "前台不是游戏");
            return;
        }

        if (!TryClientRect(fg, out Rectangle rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            if (_visible) HideOverlay();
            Diag_(fg, pid, rect, false, "客户区为空");
            return;
        }
        _gameRect = rect;

        EnsureSurface(_gameRect.Width, _gameRect.Height);
        if (_bmp == null || _g == null) return;

        _g.Clear(Color.Transparent);

        ResolveGeometry(_gameRect.Width, _gameRect.Height, out float cfgX, out float cfgY, out float cfgFont);
        _effFont = cfgFont;
        _lastX = cfgX;
        _lastY = cfgY;

        int anchorX = cfgX >= 0 ? (int)MathF.Round(cfgX) : _bmp.Width / 2;
        int anchorY = cfgY >= 0 ? (int)MathF.Round(cfgY) : _bmp.Height / 3;

        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.SmoothingMode = SmoothingMode.AntiAlias;

        using var fontBig = new Font("Segoe UI", cfgFont, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fontSub = new Font("Segoe UI", Math.Max(15f, cfgFont * 0.58f), FontStyle.Bold, GraphicsUnit.Pixel);

        if (_result != null)
        {
            DrawResult(_result, now);
        }
        else
        {
            Msg newest = _msgs[^1];
            float fade = 1f - Math.Clamp((now - newest.BornMs) / (float)_cfg.FadeMs, 0f, 1f);
            int alpha = (int)(255 * Math.Clamp(fade * 1.6f, 0f, 1f));

            SizeF sz = _g.MeasureString(newest.Main, fontBig);
            float left = cfgX >= 0 && _cfg.AlignLeft ? anchorX : anchorX - sz.Width / 2f;
            var pos = new PointF(left, anchorY);
            DrawTextOutlined(_g, newest.Main, fontBig, pos, newest.Color, alpha);

            // 第二行：Early/Late+毫秒 与 combo 并排，字号/基线完全一致，避免向下压到五线谱
            float y2 = anchorY + sz.Height - 2f;
            float x2 = left;

            if (!string.IsNullOrEmpty(newest.Sub))
            {
                SizeF s2 = _g.MeasureString(newest.Sub, fontSub);
                DrawTextOutlined(_g, newest.Sub, fontSub, new PointF(x2, y2), newest.Color, alpha);
                x2 += s2.Width + 18f;
            }

            if (_cfg.ShowCombo && Combo >= 2)
            {
                Color cCombo = PatchConfig.ParseColor(_cfg.ColorCombo, Color.FromArgb(255, 236, 190));
                DrawTextOutlined(_g, $"{Combo} COMBO", fontSub, new PointF(x2, y2), cCombo, alpha);
            }
        }

        bool shown = Push(Color.White);
        Diag_(fg, pid, _gameRect, shown, shown ? "" : "UpdateLayeredWindow 失败");
    }

    /// <summary>
    /// 按显示模式换算本帧的坐标与字号。
    /// auto：以 1280×720 为基准，按客户区等比缩放（任何分辨率都落在同一相对位置）；
    /// full / window：直接用对应预设的绝对像素值。
    /// </summary>
    private void ResolveGeometry(int cw, int ch, out float x, out float y, out float font)
    {
        const float BaseW = 1280f, BaseH = 720f;
        string mode = (_cfg.FitMode ?? "auto").Trim().ToLowerInvariant();

        if (mode == "full")
        {
            x = _cfg.X; y = _cfg.Y; font = _cfg.FontSize;
        }
        else if (mode == "window")
        {
            x = _cfg.XWin; y = _cfg.YWin; font = _cfg.FontWin;
        }
        else
        {
            float sx = cw > 0 ? cw / BaseW : 1f;
            float sy = ch > 0 ? ch / BaseH : 1f;
            x = _cfg.X < 0 ? -1f : _cfg.X * sx;
            y = _cfg.Y < 0 ? -1f : _cfg.Y * sy;
            font = _cfg.FontSize * sy;
        }

        if (font < 8f) font = 8f;
        if (font > 200f) font = 200f;
    }

    /// <summary>成绩单：半透明圆角底板 + 居中的多行文本，最后 20% 时间淡出。</summary>
    private void DrawResult(Result r, long now)
    {
        if (_g == null || _bmp == null) return;

        // 出现时用 0.55 秒淡入 + 轻微上浮，避免"啪"地一下弹出来
        float appear = Math.Clamp((now - r.BornMs) / 550f, 0f, 1f);
        appear = appear * appear * (3f - 2f * appear);            // smoothstep
        float fade = 1f - Math.Clamp((now - r.BornMs) / (float)r.HoldMs, 0f, 1f);
        int alpha = (int)(255 * appear * Math.Clamp(fade * 5f, 0f, 1f));

        float titlePx = Math.Max(18f, _effFont * 1.15f);
        float bodyPx = Math.Max(14f, _effFont * 0.92f);
        using var fTitle = new Font("Microsoft YaHei UI", titlePx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fBody = new Font("Consolas", bodyPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fSub = new Font("Consolas", bodyPx * 0.8f, FontStyle.Bold, GraphicsUnit.Pixel);

        var rows = new List<(string Text, Color Color, Font Font, SizeF Size, bool Center)>();
        for (int i = 0; i < r.Lines.Count; i++)
        {
            var (text, color) = r.Lines[i];
            bool center = i == 0;
            Font f = center ? fTitle : text.StartsWith("  ") ? fSub : fBody;
            rows.Add((text, color, f, _g.MeasureString(text, f), center));
        }

        float padX = bodyPx * 1.5f;
        float padY = bodyPx * 1.0f;
        float gap = bodyPx * 0.3f;

        float w = 0f, h = 0f;
        foreach (var row in rows)
        {
            w = Math.Max(w, row.Size.Width);
            h += row.Size.Height + gap;
        }
        w += padX * 2f;
        h += padY * 2f;

        float left = (_bmp.Width - w) / 2f;
        float top = Math.Max(8f, (_bmp.Height - h) * 0.42f) + (1f - appear) * 16f;   // 淡入时从下方 16px 浮上来

        using (var path = RoundedRect(new RectangleF(left, top, w, h), bodyPx * 0.6f))
        {
            using var fill = new SolidBrush(Color.FromArgb((int)(alpha * 0.72f), 12, 16, 24));
            _g.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb((int)(alpha * 0.85f), 255, 214, 120), Math.Max(1.5f, bodyPx * 0.06f));
            _g.DrawPath(pen, path);
        }

        float y = top + padY;
        foreach (var row in rows)
        {
            float x = row.Center ? left + (w - row.Size.Width) / 2f : left + padX;
            DrawTextWithShadow(_g, row.Text, row.Font, new PointF(x, y), row.Color, alpha);
            y += row.Size.Height + gap;
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>每秒最多写一条诊断到日志，用来定位「有判定但看不见」的问题。</summary>
    private void Diag_(IntPtr fg, uint pid, Rectangle rect, bool shown, string note)
    {
        if (Diag == null) return;
        long now = NowMs();
        if (now - _lastDiagMs < 1000) return;
        _lastDiagMs = now;
        string line = $"覆盖层 fg=0x{fg.ToInt64():X} pid={pid} 矩形={rect.X},{rect.Y},{rect.Width}x{rect.Height} 显示={(shown ? "是" : "否")}"
                    + $" [{_cfg.FitMode} x={_lastX:0} y={_lastY:0} 字号={_effFont:0.0}]"
                    + (note.Length > 0 ? " (" + note + ")" : "");
        if (line == _lastDiag) return;
        _lastDiag = line;
        Diag(line);
    }

    private static bool TryClientRect(IntPtr h, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        Native.RECT crc;
        if (!Native.GetClientRect(h, out crc)) return false;
        var pt = new Native.POINT(0, 0);
        if (!Native.ClientToScreen(h, ref pt)) return false;
        rect = new Rectangle(pt.X, pt.Y, crc.Right - crc.Left, crc.Bottom - crc.Top);
        return true;
    }

    private static void DrawTextWithShadow(Graphics g, string text, Font f, PointF p, Color c, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        using (var shadow = new SolidBrush(Color.FromArgb((int)(alpha * 0.75f), 0, 0, 0)))
            g.DrawString(text, f, shadow, p.X + 2f, p.Y + 2f);
        using var body = new SolidBrush(Color.FromArgb(alpha, c));
        g.DrawString(text, f, body, p);
    }

    /// <summary>
    /// HUD 用的描边字：先投影，再沿 8 个方向描一圈深色边，最后填字色。
    /// 亮背景（云、雪、白裙子）和花背景上都能看清。
    /// </summary>
    private static void DrawTextOutlined(Graphics g, string text, Font f, PointF p, Color c, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);

        using (var shadow = new SolidBrush(Color.FromArgb((int)(alpha * 0.45f), 0, 0, 0)))
            g.DrawString(text, f, shadow, p.X + 2.5f, p.Y + 2.5f);

        using (var edge = new SolidBrush(Color.FromArgb((int)(alpha * 0.9f), 10, 12, 18)))
        {
            // 描边宽度跟着字号走：小字 1.2px，大字最多 3px
            float r = Math.Clamp(f.Size * 0.07f, 1.2f, 3f);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    g.DrawString(text, f, edge, p.X + dx * r, p.Y + dy * r);
                }
        }

        using var body = new SolidBrush(Color.FromArgb(alpha, c));
        g.DrawString(text, f, body, p);
    }

    private void EnsureSurface(int w, int h)
    {
        if (_bmp != null && _bmpW == w && _bmpH == h) return;
        DisposeSurface();
        _bmpW = w; _bmpH = h;
        _bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _g = Graphics.FromImage(_bmp);
        _g.Clear(Color.Transparent);
    }

    private bool Push(Color _)
    {
        if (_bmp == null) return false;

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr dib = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            var bmi = new Native.BITMAPINFO
            {
                bmiColors = new uint[3],
            };
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = _bmp.Width;
            bmi.bmiHeader.biHeight = -_bmp.Height;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;

            dib = Native.CreateDIBSection(memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
            oldObj = Native.SelectObject(memDc, dib);

            // 拷贝像素（PArgb 即预乘 alpha，符合 AC_SRC_ALPHA 要求）
            var data = _bmp.LockBits(new Rectangle(0, 0, _bmp.Width, _bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                int rowBytes = _bmp.Width * 4;
                for (int y = 0; y < _bmp.Height; y++)
                {
                    IntPtr src = data.Scan0 + y * data.Stride;
                    IntPtr dst = bits + y * rowBytes;
                    CopyMem(dst, src, rowBytes);
                }
            }
            finally { _bmp.UnlockBits(data); }

            var dstPt = new Native.POINT(_gameRect.Left, _gameRect.Top);
            var size = new Native.SIZE(_bmp.Width, _bmp.Height);
            var srcPt = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };

            bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, Native.ULW_ALPHA);
            if (ok)
            {
                // 游戏经常自己 SetWindowPos 到最前，这里每次绘制都把覆盖层重新顶到 topmost，
                // 否则会出现「判定在记录、屏幕上什么都没有」的情况。
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                _visible = true;
            }
            return ok;
        }
        finally
        {
            if (oldObj != IntPtr.Zero) Native.SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void CopyMem(IntPtr dst, IntPtr src, int bytes) => Native.CopyMemory(dst, src, (UIntPtr)bytes);

    private void HideOverlay()
    {
        _visible = false;
        try
        {
            Native.SetWindowPos(Handle, IntPtr.Zero, -32000, -32000, 1, 1, 0x0010 /*SWP_NOACTIVATE*/ | 0x0004 /*SWP_NOZORDER*/);
        }
        catch { }
    }

    private void DisposeSurface()
    {
        _g?.Dispose(); _g = null;
        _bmp?.Dispose(); _bmp = null;
        _bmpW = _bmpH = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            DisposeSurface();
        }
        base.Dispose(disposing);
    }
}
