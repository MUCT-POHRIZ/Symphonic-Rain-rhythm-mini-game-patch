using System.Drawing;
using System.Globalization;
using System.Text;

namespace SRPerfectPatch;

/// <summary>
/// 显示与行为配置。保存在 exe 同目录的 config.ini，随时可改；游戏内按 F9 重新加载。
/// 下面这些字段初值就是「默认设置」——界面上点「恢复默认设置」会整体回到这一组值。
/// </summary>
internal sealed class PatchConfig
{
    // 显示模式：auto = 自动适配（按客户区比例缩放，推荐）；full = 用全屏预设；window = 用窗口预设
    public string FitMode = "auto";
    public int X = 440;           // 全屏预设：相对游戏客户区左上角的像素；-1 = 水平居中
    public int Y = 456;           // -1 = 位于上方三分之一处
    // x>=0 时，把 x 当作文字左边缘（左对齐），而不是中心点
    public bool AlignLeft = true;
    public float FontSize = 27f;
    // 窗口预设（1280×701 这类非全屏客户区用的绝对坐标）
    public int XWin = 440;
    public int YWin = 444;
    public float FontWin = 26f;
    public int FadeMs = 900;      // 每次判定文字停留时间
    public bool ShowCombo = true;
    public string ColorPerfect = "#FFD24A";
    public string ColorGood = "#4DA6FF";
    public string ColorMiss = "#A0A0A0";
    public string ColorCombo = "#FFECBE";   // 连击数颜色（默认暖白）
    public bool ClickThrough = true;
    public bool AutoAttach = true;   // 游戏启动时自动挂载

    // 剧情里的演奏是否也弹成绩单。默认关闭：只有 free play 弹。
    // 模式读不到时按「不是 free play」处理 —— 宁可不弹，也不要在剧情里冒出来。
    public bool StorySummary = false;
    // MusicUnit+0x28C 里代表「free play」的取值；若哪天版本变了可在此改，无需重新编译
    public int FreePlayModeValue = 0;

    // 界面语言：zh = 简体中文（默认）、en = English、ja = 日本語
    public string Language = "zh";

    // 窗口大小（客户区像素）；0 = 自动（按屏幕工作区高度决定）
    public int WindowWidth = 0;
    public int WindowHeight = 0;

    /// <summary>把配置里的语言代码收敛到三种之一。</summary>
    public static string NormLang(string code) =>
        code is "en" or "ja" or "zh" ? code : "zh";

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.ini");

    public static PatchConfig Load()
    {
        var c = new PatchConfig();
        try
        {
            if (!File.Exists(ConfigPath))
            {
                c.Save();
                return c;
            }
            foreach (string raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line[..eq].Trim().ToLowerInvariant();
                string v = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "fitmode": c.FitMode = v.Trim().ToLowerInvariant(); break;
                    case "x": c.X = Int(v, c.X); break;
                    case "y": c.Y = Int(v, c.Y); break;
                    case "xwin": c.XWin = Int(v, c.XWin); break;
                    case "ywin": c.YWin = Int(v, c.YWin); break;
                    case "fontwin": c.FontWin = Float(v, c.FontWin); break;
                    case "alignleft": c.AlignLeft = Bool(v, c.AlignLeft); break;
                    case "fontsize": c.FontSize = Float(v, c.FontSize); break;
                    case "fadems": c.FadeMs = Int(v, c.FadeMs); break;
                    case "showcombo": c.ShowCombo = Bool(v, c.ShowCombo); break;
                    case "colorperfect": c.ColorPerfect = v; break;
                    case "colorgood": c.ColorGood = v; break;
                    case "colormiss": c.ColorMiss = v; break;
                    case "colorcombo": c.ColorCombo = v; break;
                    case "clickthrough": c.ClickThrough = Bool(v, c.ClickThrough); break;
                    case "autoattach": c.AutoAttach = Bool(v, c.AutoAttach); break;
                    // 旧键：summaryonlyfreeplay=0 等价于「所有演奏都弹」→ 打开新开关
                    case "summaryonlyfreeplay": if (!Bool(v, true)) c.StorySummary = true; break;
                    case "storysummary": c.StorySummary = Bool(v, c.StorySummary); break;
                    case "freeplaymodevalue": c.FreePlayModeValue = Int(v, c.FreePlayModeValue); break;
                    case "language": c.Language = v.Trim().ToLowerInvariant(); break;
                    case "windowwidth": c.WindowWidth = Int(v, c.WindowWidth); break;
                    case "windowheight": c.WindowHeight = Int(v, c.WindowHeight); break;
                }
            }
        }
        catch { /* 配置损坏时用默认值，绝不因此中断 */ }
        c.FontSize = Math.Clamp(c.FontSize, 10f, 120f);
        c.FontWin = Math.Clamp(c.FontWin, 10f, 120f);
        if (c.FitMode is not ("auto" or "full" or "window")) c.FitMode = "auto";
        c.FadeMs = Math.Clamp(c.FadeMs, 100, 10000);
        c.Language = NormLang(c.Language);
        return c;
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(Lang.Tr("# SR Perfect Patch 配置（修改后游戏内按 F9 重新加载）"));
            sb.AppendLine(Lang.Tr("# x / y 为相对游戏画面左上角的像素坐标，-1 表示自动（居中 / 上方三分之一）"));
            sb.AppendLine(Lang.Tr("# fitmode：auto=自动适配（按客户区比例缩放，推荐）；full=全屏预设；window=窗口预设"));
            sb.AppendLine("[display]");
            sb.AppendLine($"fitmode={FitMode}");
            sb.AppendLine(Lang.Tr("# 全屏预设"));
            sb.AppendLine($"x={X}");
            sb.AppendLine($"y={Y}");
            sb.AppendLine(Lang.Tr("# 窗口预设（全屏之外的窗口化客户区用这一套）"));
            sb.AppendLine($"xwin={XWin}");
            sb.AppendLine($"ywin={YWin}");
            sb.AppendLine(Lang.Tr("# x>=0 时是否把 x 当作文字左边缘（1=左对齐，0=x 为中心点）"));
            sb.AppendLine($"alignleft={(AlignLeft ? 1 : 0)}");
            sb.AppendLine($"fontsize={FontSize.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"fontwin={FontWin.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"fadems={FadeMs}");
            sb.AppendLine($"showcombo={(ShowCombo ? 1 : 0)}");
            sb.AppendLine($"colorperfect={ColorPerfect}");
            sb.AppendLine($"colorgood={ColorGood}");
            sb.AppendLine($"colormiss={ColorMiss}");
            sb.AppendLine($"colorcombo={ColorCombo}");
            sb.AppendLine($"clickthrough={(ClickThrough ? 1 : 0)}");
            sb.AppendLine();
            sb.AppendLine("[behavior]");
            sb.AppendLine($"autoattach={(AutoAttach ? 1 : 0)}");
            sb.AppendLine(Lang.Tr("# 剧情里的演奏是否也弹成绩单（0=不弹，只有 free play 弹；1=都弹）"));
            sb.AppendLine($"storysummary={(StorySummary ? 1 : 0)}");
            sb.AppendLine(Lang.Tr("# MusicUnit+0x28C 中代表 free play 的取值（一般不用改）"));
            sb.AppendLine($"freeplaymodevalue={FreePlayModeValue}");
            sb.AppendLine(Lang.Tr("# 界面语言：zh=简体中文 en=英文 ja=日文"));
            sb.AppendLine($"language={Language}");
            sb.AppendLine(Lang.Tr("# 主窗口大小（像素，客户区）；0 = 自动。手动改过窗口大小后会写回这里"));
            sb.AppendLine($"windowwidth={WindowWidth}");
            sb.AppendLine($"windowheight={WindowHeight}");
            File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static int Int(string s, int d) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : d;
    private static float Float(string s, float d) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : d;
    private static bool Bool(string s, bool d) =>
        s is "1" or "true" or "yes" or "on" ? true :
        s is "0" or "false" or "no" or "off" ? false : d;

    public static Color ParseColor(string s, Color fallback)
    {
        try
        {
            s = s.Trim();
            if (s.StartsWith('#')) s = s[1..];
            if (s.Length == 6)
            {
                int r = Convert.ToInt32(s[..2], 16);
                int g = Convert.ToInt32(s[2..4], 16);
                int b = Convert.ToInt32(s[4..], 16);
                return Color.FromArgb(255, r, g, b);
            }
        }
        catch { }
        return fallback;
    }
}
