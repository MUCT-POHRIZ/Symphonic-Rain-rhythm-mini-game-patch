using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;

namespace SRPerfectPatch;

/// <summary>
/// 界面配色与控件外观。取色自游戏《交响乐之雨》的乐谱界面：
/// 米色纸面 + 酒红标题条 + 描金边框，帘幕装饰与立绘图标取自解包的 Game.pak / EvCG.pak。
/// </summary>
internal static class Theme
{
    // 纸面 / 酒红 / 描金 / 墨色
    public static readonly Color Paper = Color.FromArgb(0xF7, 0xF1, 0xE1);
    public static readonly Color PaperDeep = Color.FromArgb(0xF0, 0xE6, 0xD0);
    public static readonly Color PaperLight = Color.FromArgb(0xFD, 0xF9, 0xEF);
    public static readonly Color Wine = Color.FromArgb(0x7A, 0x1F, 0x2B);
    public static readonly Color WineSoft = Color.FromArgb(0x9B, 0x30, 0x40);
    public static readonly Color Gold = Color.FromArgb(0xC3, 0x9A, 0x4E);
    public static readonly Color GoldPale = Color.FromArgb(0xE2, 0xD3, 0xB4);
    public static readonly Color Ink = Color.FromArgb(0x3B, 0x2B, 0x22);
    public static readonly Color InkSoft = Color.FromArgb(0x6B, 0x55, 0x45);
    public static readonly Color Mint = Color.FromArgb(0x1F, 0x7A, 0x4D);

    public static readonly Font Body = new("Microsoft YaHei UI", 9f);
    public static readonly Font BodyBold = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    public static readonly Font Small = new("Microsoft YaHei UI", 8.25f);
    public static readonly Font Title = new("Georgia", 15f, FontStyle.Bold);
    public static readonly Font SubTitle = new("Microsoft YaHei UI", 8.5f);
    public static readonly Font Mono = new("Consolas", 8.5f);

    public static Stream Res(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;

    private static Image _frame;
    private static Image _iconImg;

    public static Image Frame => _frame ??= Image.FromStream(Res("frame_top.png"));
    public static Image IconImage => _iconImg ??= Image.FromStream(Res("header_icon.png"));

    /// <summary>把整套控件刷成游戏风格（可重复调用）。</summary>
    public static void StyleAll(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Gold;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xF4, 0xE8, 0xCB);
                    b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xEA, 0xDA, 0xB6);
                    b.BackColor = PaperLight;
                    b.ForeColor = Wine;
                    b.Font = BodyBold;
                    b.Cursor = Cursors.Hand;
                    break;
                case CheckBox cb:
                    cb.BackColor = Color.Transparent;
                    cb.ForeColor = Ink;
                    cb.Font = Body;
                    cb.FlatStyle = FlatStyle.System;
                    break;
                case TextBox tb:
                    tb.BackColor = PaperLight;
                    tb.ForeColor = Ink;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    tb.Font = Mono;
                    break;
                case NumericUpDown nu:
                    nu.BackColor = PaperLight;
                    nu.ForeColor = Ink;
                    nu.BorderStyle = BorderStyle.FixedSingle;
                    nu.Font = Mono;
                    break;
                case ComboBox co:
                    co.BackColor = PaperLight;
                    co.ForeColor = Ink;
                    co.FlatStyle = FlatStyle.Flat;
                    co.Font = Body;
                    break;
                case ListBox lb:
                    lb.BackColor = PaperLight;
                    lb.ForeColor = Color.FromArgb(0x46, 0x33, 0x28);
                    lb.BorderStyle = BorderStyle.FixedSingle;
                    lb.Font = Mono;
                    break;
                case Label l:
                    if (l.BackColor != Color.Transparent) l.BackColor = Color.Transparent;
                    if (l.ForeColor.ToArgb() == Color.Black.ToArgb()) l.ForeColor = InkSoft;
                    if (l.Font.Equals(SystemFonts.DefaultFont)) l.Font = Body;
                    break;
                case Panel p:
                    p.BackColor = Color.Transparent;
                    break;
            }

            if (c.HasChildren) StyleAll(c);
        }
    }
}

/// <summary>参数区的纸面卡片：淡纸底 + 描金细边。</summary>
internal sealed class Card : Panel
{
    public Card()
    {
        DoubleBuffered = true;
        BackColor = Theme.PaperLight;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = Width, h = Height;
        using var path = Rounded(new Rectangle(0, 0, w - 1, h - 1), 7);
        using (var br = new SolidBrush(Theme.PaperLight)) g.FillPath(br, path);
        using (var pen = new Pen(Theme.GoldPale, 1f)) g.DrawPath(pen, path);
        // 左侧一道酒红细条，呼应游戏里的标题条
        using (var br2 = new SolidBrush(Color.FromArgb(200, Theme.Wine)))
            g.FillRectangle(br2, 0, 6, 3, Math.Max(0, h - 12));
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>顶部横幅：游戏里的帘幕边框 + 标题 + 立绘图标。</summary>
internal sealed class ThemeHeader : Panel
{
    public ThemeHeader()
    {
        DoubleBuffered = true;
        Height = 94;
        BackColor = Theme.Paper;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int w = Width, h = Height;

        // 纸面渐变
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Theme.PaperLight, Theme.PaperDeep, 90f))
            g.FillRectangle(br, 0, 0, w, h);

        // 帘幕（等比拉伸到横幅宽度，保留木梁与灯）
        const int band = 42;
#if NOART
        // 公开版：不打包任何游戏美术，用五线谱横幅 + 音符头自绘出“乐谱”横幅
        DrawSheetBand(g, w, band);
#else
        if (Theme.Frame is Image img)
            g.DrawImage(img, new Rectangle(0, 0, w, band));

        // 帘幕下沿一道淡淡的内阴影，让横幅有厚度
        using (var sh = new LinearGradientBrush(new Rectangle(0, band, w, 8), Color.FromArgb(56, 30, 20, 10), Color.Transparent, 90f))
            g.FillRectangle(sh, 0, band, w, 8);
#endif

        // 标题
        using (var fNote = new Font("Segoe UI Symbol", 13f, FontStyle.Bold))
        using (var brNote = new SolidBrush(Theme.Gold))
            g.DrawString("♪", fNote, brNote, 12f, band + 6f);

        using (var brTitle = new SolidBrush(Theme.Wine))
            g.DrawString("SR Perfect Patch", Theme.Title, brTitle, 34f, band + 2f);

        using (var brSub = new SolidBrush(Color.FromArgb(0x8A, 0x6A, 0x3F)))
            g.DrawString(Lang.Tr("交响乐之雨 · 判定显示补丁   Perfect / Good(Early·Late) / Miss   ·   只改内存，不动游戏文件"),
                Theme.SubTitle, brSub, 35f, band + 27f);

        // 右侧图标：带美术版用裁自立绘的头像；公开版用自绘的金环音符徽记
        const int isz = 50;
        var iconRect = new Rectangle(w - isz - 14, band + 2, isz, isz);
#if NOART
        DrawEmblem(g, iconRect);
#else
        if (Theme.IconImage is Image ic)
            g.DrawImage(ic, iconRect);
#endif

        // 底部描金细线
        using (var penG = new Pen(Theme.Gold, 2f))
            g.DrawLine(penG, 0, h - 2, w, h - 2);
        using (var penS = new Pen(Theme.GoldPale, 1f))
            g.DrawLine(penS, 0, h - 4, w, h - 4);
        using (var penW = new Pen(Color.FromArgb(60, Theme.Wine), 1f))
            g.DrawLine(penW, 0, h - 6, w, h - 6);
    }

#if NOART
    // 音符头的五种配色，取自游戏里判定音符的颜色（粉红/明黄/草绿/紫/灰）
    private static readonly Color[] NoteInk =
    {
        Color.FromArgb(0xDE, 0x7C, 0x90),
        Color.FromArgb(0xDE, 0xBB, 0x63),
        Color.FromArgb(0x7B, 0xBC, 0x8B),
        Color.FromArgb(0xA2, 0x88, 0xD2),
        Color.FromArgb(0xB4, 0xB4, 0xB4),
    };

    /// <summary>无美术版的横幅：纸面 + 顶部酒红细条 + 五线谱 + 高音谱号 + 音符头。</summary>
    private static void DrawSheetBand(Graphics g, int w, int band)
    {
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, band),
                   Color.FromArgb(0xF4, 0xEA, 0xD6), Theme.PaperDeep, 90f))
            g.FillRectangle(br, 0, 0, w, band);

        // 顶部酒红细条（与记分板同色）+ 一道金线
        using (var wine = new SolidBrush(Theme.Wine))
            g.FillRectangle(wine, 0, 0, w, 3);
        using (var gold = new Pen(Color.FromArgb(130, Theme.Gold), 1f))
            g.DrawLine(gold, 0, 3, w, 3);

        // 五线谱（谱号之后到角标之前）
        int staffL = 38, staffR = w - 106;
        using (var staff = new Pen(Color.FromArgb(215, 0xC0, 0xA4, 0x7A), 1f))
            for (int i = 0; i < 5; i++)
                g.DrawLine(staff, staffL, 12 + i * 5, staffR, 12 + i * 5);

        // 高音谱号：Segoe UI Symbol 的 U+1D11E（字体字形，不是图片素材）
        using (var fClef = new Font("Segoe UI Symbol", 21f))
        using (var brClef = new SolidBrush(Color.FromArgb(205, Theme.Wine)))
            g.DrawString("\U0001D11E", fClef, brClef, 5f, 0f);

        // 谱线上的音符头 + 符干，模拟沿谱线滑动的音符
        int x = staffR - 16;
        for (int i = 0; i < 4; i++)
        {
            Color c = NoteInk[i % NoteInk.Length];
            int cy = 12 + (i % 3) * 5;                 // 交替落在线上
            bool up = i % 2 == 0;
            using (var nb = new SolidBrush(Color.FromArgb(240, c)))
                g.FillEllipse(nb, x - 5, cy - 4, 10, 8);
            using (var np = new Pen(Color.FromArgb(215, c), 1.7f))
                g.DrawLine(np, up ? x + 4 : x - 4, cy - 2, up ? x + 4 : x - 4, up ? cy - 21 : cy + 17);
            x -= 27;
        }
    }

    /// <summary>无美术版的角标：纸底金环 + 酒红音符。</summary>
    private static void DrawEmblem(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.PaperLight))
            g.FillEllipse(bg, r);
        using (var pen = new Pen(Theme.Gold, 2f))
            g.DrawEllipse(pen, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
        using (var pen2 = new Pen(Color.FromArgb(95, Theme.Wine), 1f))
            g.DrawEllipse(pen2, r.X + 6, r.Y + 6, r.Width - 13, r.Height - 13);
        using (var f = new Font("Segoe UI Symbol", r.Height * 0.40f, FontStyle.Bold))
        using (var br = new SolidBrush(Theme.Wine))
        {
            SizeF sz = g.MeasureString("♪", f);
            g.DrawString("♪", f, br,
                r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f - 1f);
        }
    }
#endif
}
