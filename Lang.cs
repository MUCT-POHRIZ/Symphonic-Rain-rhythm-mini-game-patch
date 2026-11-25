using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SRPerfectPatch
{
    /// <summary>
    /// 三语文本表：以简体中文原文为键，提供英文 / 日文。
    /// 运行时用 Tr() 把「任意已知语言」的字符串翻成当前语言，因此切换语言不需要重建界面。
    /// </summary>
    public static class Lang
    {
        /// <summary>当前语言代码：zh / en / ja</summary>
        public static string Code = "zh";

        // 每条 = { 简体中文, English, 日本語 }
        static readonly List<string[]> T = new List<string[]>();
        // 前缀表：命中开头则替换开头，其余部分保持原样（用于拼了数字/路径的句子）
        static readonly List<string[]> P = new List<string[]>();
        // 片段表：句子里任意位置出现就替换（用于「本场成绩 · Perfect 230 · …」这类拼接句）
        static readonly List<string[]> S = new List<string[]>();

        static void A(string zh, string en, string ja) { T.Add(new[] { zh, en, ja }); }
        static void AP(string zh, string en, string ja) { P.Add(new[] { zh, en, ja }); }
        static void AS(string zh, string en, string ja) { S.Add(new[] { zh, en, ja }); }

        public static int Index(string code)
        {
            if (code == "en") return 1;
            if (code == "ja") return 2;
            return 0;
        }

        public static string Name(string code)
        {
            if (code == "en") return "English";
            if (code == "ja") return "Japanese";
            return "Simplified Chinese";
        }

        static Lang()
        {
            // ---------- 窗口 / 状态 ----------
            A("SR Perfect Patch — 交响乐之雨 判定显示补丁",
              "SR Perfect Patch — Symphonic Rain judgement overlay",
              "SR Perfect Patch — シンフォニック＝レイン 判定表示");
            A("状态：", "Status: ", "状態：");
            A("未连接", "not connected", "未接続");
            A("挂载失败", "attach failed", "適用に失敗");
            A("判定来源：游戏内存钩子（Press / Miss 两处）",
              "Source: in-memory hooks (press / miss) — the game's own grade",
              "取得元：メモリフック（押鍵 / 取りこぼし）— ゲーム自身の判定");

            // ---------- 按钮 / 复选框 ----------
            A("挂载补丁", "Attach patch", "パッチを適用");
            A("启动游戏并挂载", "Launch game && attach", "ゲームを起動して適用");
            A("卸载补丁", "Detach patch", "パッチを解除");
            A("游戏启动时自动挂载", "Attach automatically when the game starts", "ゲーム起動時に自動で適用する");
            A("开机自动启动", "Start with Windows", "Windows 起動時に自動起動");
            A("剧情演奏也弹成绩单", "Result card in story too", "ストーリー中もリザルト表示");
            A("Perfect 显示毫秒数", "Milliseconds on Perfect", "Perfect はミリ秒も表示");

            // ---------- 参数区 ----------
            A("显示设置", "Display settings", "表示設定");
            A("显示模式：", "Fit mode: ", "表示モード：");
            A("预设：", "Preset: ", "プリセット：");
            A("自动适配（按比例）", "Auto (scale to window)", "自動（拡大縮小）");
            A("全屏预设（绝对）", "Fullscreen preset (absolute px)", "全画面プリセット（絶対値）");
            A("窗口预设（绝对）", "Window preset (absolute px)", "ウィンドウプリセット（絶対値）");
            A("全屏预设", "Fullscreen", "全画面");
            A("窗口预设", "Window", "ウィンドウ");
            A("X：", "X: ", "X：");
            A("Y：", "Y: ", "Y：");
            A("字号(px)：", "Size (px): ", "文字サイズ(px)：");
            A("停留(ms)：", "Hold (ms): ", "表示時間(ms)：");
            A("Perfect 色：", "Perfect color: ", "Perfect 色：");
            A("Good 色：", "Good color: ", "Good 色：");
            A("Miss 色：", "Miss color: ", "Miss 色：");
            A("连击 色：", "Combo color: ", "コンボ色：");
            A("（判定文字带深色描边，白底/花背景也能看清）",
              "(text is outlined, readable on white or busy backgrounds)",
              "（文字は縁取り付き。白背景や賑やかな背景でも読みやすい）");
            A("保存并立即生效", "Save && apply", "保存して適用");
            A("恢复默认设置", "Restore defaults", "既定値に戻す");

            // ---------- 托盘 / 关闭 ----------
            A("显示主窗口", "Show main window", "メインウィンドウを表示");
            A("退出", "Exit", "終了");
            A("已最小化到托盘，补丁仍在工作。", "Minimized to the tray. The patch keeps running.",
              "トレイに格納しました。パッチは動作を続けます。");

            // ---------- 日志 / 提示 ----------
            A("就绪。补丁只改内存，不改任何游戏文件。",
              "Ready. The patch only touches process memory; no game file is modified.",
              "準備完了。パッチはメモリだけを書き換え、ゲームファイルには触れません。");
            A("未找到 SR.exe，请手动启动游戏。",
              "SR.exe was not found. Please start the game manually.",
              "SR.exe が見つかりません。手動でゲームを起動してください。");
            A("补丁已卸载，内存已还原。", "Patch removed; memory restored.",
              "パッチを解除し、メモリを元に戻しました。");
            A("已重新加载 config.ini", "config.ini reloaded", "config.ini を再読み込みしました");
            A("覆盖层已显示", "Overlay shown", "オーバーレイを表示");
            A("覆盖层已隐藏", "Overlay hidden", "オーバーレイを非表示");
            A("自检：显示一条 Perfect（F8）", "Self-test: one Perfect sample (F8)", "セルフテスト：Perfect を表示（F8）");
            A("预览：成绩单样式（F7，数字为示例）",
              "Preview: result card layout (F7, numbers are samples)",
              "プレビュー：リザルトの見た目（F7、数値はサンプル）");
            A("游戏已在运行，直接挂载。", "The game is already running; attaching.", "ゲームは起動済みです。適用します。");
            A("找不到 SR.exe，请手动启动游戏。", "SR.exe not found. Please start the game manually.",
              "SR.exe が見つかりません。手動でゲームを起動してください。");
            A("已启动游戏，稍后自动挂载。", "Game launched; it will attach automatically.",
              "ゲームを起動しました。自動で適用します。");
            A("游戏已退出，补丁已自动卸载。", "The game exited; the patch was detached automatically.",
              "ゲームが終了しました。パッチを自動で解除しました。");
            A("演奏结束", "Result", "リザルト");
            A("空按", "empty", "空打ち");
            A("Score", "Score", "スコア");

            // ---------- 完成判据相关 ----------
            A("本场读不到演奏模式（当作剧情演奏处理），不弹成绩单。",
              "Could not read the performance mode (treated as a story performance), so no result card.",
              "演奏モードを取得できないため（ストーリー演奏として扱います）、リザルトは出しません。");
            A("本场是剧情里的演奏，按设置不弹成绩单。",
              "This is a story performance; the result card is disabled by your setting.",
              "ストーリー中の演奏のため、設定によりリザルトは出しません。");
            A("音符表读不到（游戏还没开始演奏），成绩单暂不启用。",
              "Note table not readable yet (the performance has not started). Result card disabled.",
              "ノート表を読めません（まだ演奏が始まっていません）。リザルトは無効です。");
            A("音符表读取失败，成绩单暂不启用。",
              "Failed to read the note table. Result card disabled.",
              "ノート表の読み込みに失敗しました。リザルトは無効です。");
            A("音符表为空，成绩单暂不启用。", "The note table is empty. Result card disabled.",
              "ノート表が空です。リザルトは無効です。");

            // ---------- MessageBox ----------
            A("把显示模式、两套预设、字号、停留时间、颜色等全部恢复为默认值？\r\n\r\n当前配置会被覆盖。",
              "Restore fit mode, both presets, font size, hold time and colors to their defaults?\r\n\r\nYour current settings will be overwritten.",
              "表示モード、2 つのプリセット、文字サイズ、表示時間、色などをすべて既定値に戻しますか？\r\n\r\n現在の設定は上書きされます。");
            A("已恢复默认设置（自动适配 + 全屏/窗口预设 + 默认配色）。",
              "Defaults restored (auto fit + fullscreen/window presets + default colors).",
              "既定値に戻しました（自動 + 全画面/ウィンドウのプリセット + 既定の配色）。");

            // ---------- GamePatcher 提示 ----------
            A("未找到正在运行的 SR.exe", "No running SR.exe was found", "実行中の SR.exe が見つかりません");
            A("目标进程不是 32 位进程，版本可能不匹配。", "The target process is not 32-bit; the version may not match.",
              "対象プロセスが 32 ビットではありません。バージョンが一致しない可能性があります。");
            A("无法获取模块基址。", "Could not get the module base address.", "モジュールのベースアドレスを取得できません。");
            A("读取按键判定处内存失败。", "Failed to read memory at the key-judgement site.",
              "押鍵判定箇所のメモリ読み取りに失敗しました。");
            A("读取漏音结算处内存失败。", "Failed to read memory at the missed-note site.",
              "取りこぼし処理箇所のメモリ読み取りに失敗しました。");
            A("（还原失败，已中止）", " (restore failed, aborted)", "（復元に失敗したため中止しました）");
            A("（按键判定处不是本补丁的跳转，未接管）", " (the key-judgement site is not this patch's jump; not adopted)",
              "（押鍵判定箇所がこのパッチのジャンプではないため引き継ぎません）");
            A("（跳转目标不是本补丁的 code cave，未接管）", " (the jump target is not this patch's code cave; not adopted)",
              "（ジャンプ先がこのパッチのコードケーブではないため引き継ぎません）");
            A("（漏音结算处不是本补丁的跳转，未接管）", " (the missed-note site is not this patch's jump; not adopted)",
              "（取りこぼし箇所がこのパッチのジャンプではないため引き継ぎません）");
            A("（漏音结算处的跳转与本补丁不一致，未接管）", " (the missed-note jump does not match this patch; not adopted)",
              "（取りこぼし箇所のジャンプがこのパッチと一致しないため引き継ぎません）");
            A("（游戏内存里有来源不明的补丁，标记 0x", " (unknown patch marker in game memory: 0x",
              "（ゲームのメモリに由来不明のパッチがあります。マーカー 0x");
            A("（读不到补丁标记，为安全起见不接管也不释放，请重启游戏后再挂载）",
              " (the patch marker is unreadable; for safety nothing is adopted or freed. Please restart the game and attach again)",
              "（パッチのマーカーを読めないため、安全のため引き継ぎも解放も行いません。ゲームを再起動してから適用してください）");
            A("尚未连接游戏进程。", "The game process is not connected yet.", "ゲームプロセスに接続していません。");
            A("钩子已处于安装状态。", "The hook is already installed.", "フックは既に適用されています。");
            A("分配目标进程内存失败，错误码 ", "Failed to allocate memory in the target process, error ",
              "対象プロセスのメモリ確保に失敗しました。エラーコード ");
            A("分配到的新内存离游戏模块太远（超过 ±2GB），跳转无法构造，已放弃。请重启游戏后重试。",
              "The new memory is too far from the game module (beyond ±2GB), so no jump can be built. Aborted; please restart the game and try again.",
              "確保したメモリがゲームモジュールから遠すぎ（±2GB 超）、ジャンプを構成できません。中止しました。ゲームを再起動して再試行してください。");
            A("写入 code cave 失败。", "Failed to write the code cave.", "コードケーブの書き込みに失敗しました。");
            A("设置 cave 页属性失败，已放弃安装。", "Failed to set the cave page protection; installation aborted.",
              "ケーブのページ属性設定に失敗したため、適用を中止しました。");
            A("修改按键判定处页属性失败。", "Failed to change page protection at the key-judgement site.",
              "押鍵判定箇所のページ属性変更に失敗しました。");
            A("修改漏音结算处页属性失败。", "Failed to change page protection at the missed-note site.",
              "取りこぼし箇所のページ属性変更に失敗しました。");
            A("写入补丁失败，已回滚。", "Failed to write the patch; rolled back.", "パッチの書き込みに失敗したためロールバックしました。");
            A("补丁已安装（仅内存，游戏文件未改动）。", "Patch installed (memory only; no game file was modified).",
              "パッチを適用しました（メモリのみ。ゲームファイルは未変更）。");
            A("还没卸载", "not detached yet", "未解除");
            A("游戏已退出，补丁已自动卸载", "The game exited; the patch was detached", "ゲームが終了したためパッチを解除しました");

            // ---------- 前缀 ----------
            AP("状态：", "Status: ", "状態：");
            AP("已挂载 (PID ", "attached (PID ", "接続済み (PID ");
            AP("检测到 SR.exe (PID ", "Detected SR.exe (PID ", "SR.exe を検出 (PID ");
            AP("游戏路径：", "Game path: ", "ゲームのパス：");
            AP("启动失败：", "Launch failed: ", "起動に失敗：");
            AP("修改开机启动失败：", "Failed to change the startup entry: ", "自動起動の変更に失敗：");
            AP("配置已保存并生效（显示模式：", "Settings saved and applied (fit mode: ", "設定を保存して適用しました（表示モード：");
            AP("已恢复默认设置（显示模式：", "Defaults restored (fit mode: ", "既定値に戻しました（表示モード：");
            AP("检测到上次运行遗留的补丁（cave 0x", "Patch left by a previous run detected (cave 0x",
               "前回の実行で残ったパッチを検出（cave 0x");
            AP("检测到旧版补丁（cave 0x", "Older patch detected (cave 0x", "旧バージョンのパッチを検出（cave 0x");
            AP("已连接 SR.exe (PID ", "Connected to SR.exe (PID ", "SR.exe に接続 (PID ");
            AP("按键判定处字节不匹配（期望 ", "Byte mismatch at the key-judgement site (expected ",
               "押鍵判定箇所のバイトが一致しません（期待 ");
            AP("漏音结算处字节不匹配（期望 ", "Byte mismatch at the missed-note site (expected ",
               "取りこぼし箇所のバイトが一致しません（期待 ");
            AP("无法打开进程 (PID ", "Cannot open the process (PID ", "プロセスを開けません (PID ");
            AP("主模块不是 SR.exe（读到 ", "The main module is not SR.exe (got ", "メインモジュールが SR.exe ではありません（");
            AP("覆盖层 fg=0x", "Overlay fg=0x", "オーバーレイ fg=0x");

            // ---------- config.ini 注释 ----------
            A("# SR Perfect Patch 配置（修改后游戏内按 F9 重新加载）",
              "# SR Perfect Patch settings (press F9 in game to reload after editing)",
              "# SR Perfect Patch 設定（編集後はゲーム内で F9 を押すと再読み込み）");
            A("# x / y 为相对游戏画面左上角的像素坐标，-1 表示自动（居中 / 上方三分之一）",
              "# x / y are pixels from the top-left of the game window; -1 means auto (centered / upper third)",
              "# x / y はゲーム画面左上からのピクセル座標。-1 で自動（中央 / 上から 1/3）");
            A("# fitmode：auto=自动适配（按客户区比例缩放，推荐）；full=全屏预设；window=窗口预设",
              "# fitmode: auto = scale to the client area (recommended); full = fullscreen preset; window = window preset",
              "# fitmode: auto = クライアント領域に合わせて拡大縮小（推奨）/ full = 全画面プリセット / window = ウィンドウプリセット");
            A("# 全屏预设", "# fullscreen preset", "# 全画面プリセット");
            A("# 窗口预设（全屏之外的窗口化客户区用这一套）",
              "# window preset (used for windowed client areas)",
              "# ウィンドウプリセット（全画面以外のウィンドウ表示用）");
            A("# x>=0 时是否把 x 当作文字左边缘（1=左对齐，0=x 为中心点）",
              "# when x>=0, treat x as the left edge of the text (1 = left aligned, 0 = x is the center)",
              "# x>=0 のとき x を文字の左端として扱う（1 = 左揃え、0 = x は中心）");
            A("# 剧情里的演奏是否也弹成绩单（0=不弹，只有 free play 弹；1=都弹）",
              "# show the result card in story performances too (0 = free play only, 1 = both)",
              "# ストーリー中の演奏でもリザルトを表示（0 = フリープレイのみ、1 = 両方）");
            A("# MusicUnit+0x28C 中代表 free play 的取值（一般不用改）",
              "# value of MusicUnit+0x28C that means free play (usually no need to change)",
              "# MusicUnit+0x28C でフリープレイを表す値（通常は変更不要）");
            A("# 界面语言：zh=简体中文 en=英文 ja=日文",
              "# UI language: zh = Simplified Chinese, en = English, ja = Japanese",
              "# 表示言語: zh = 簡体字中国語, en = 英語, ja = 日本語");
            A("已开启：剧情里的演奏也会弹成绩单。", "On: story performances show the result card too.",
              "オン：ストーリー中の演奏でもリザルトを表示します。");
            A("已关闭：只有 free play 才弹成绩单。", "Off: only free play shows the result card.",
              "オフ：フリープレイのみリザルトを表示します。");
            A("# Perfect 判定时是否也显示 Early/Late 与毫秒数（0=只显示字样）",
              "# Also show Early/Late and milliseconds for Perfect judgements (0 = word only)",
              "# Perfect 判定でも Early/Late とミリ秒を表示するか（0=文字のみ）");
            A("已开启：Perfect 判定也会显示 Early/Late 与毫秒数。",
              "On: Perfect judgements also show Early/Late and milliseconds.",
              "オン：Perfect 判定でも Early/Late とミリ秒を表示します。");
            A("已关闭：Perfect 只显示字样。", "Off: Perfect shows the word only.",
              "オフ：Perfect は文字のみ表示します。");
            AP("界面语言已切换为 ", "Language switched to ", "表示言語を切り替えました：");

            // ---------- 标题栏自绘文字 ----------
            A("交响乐之雨 · 判定显示补丁   Perfect / Good(Early·Late) / Miss   ·   只改内存，不动游戏文件",
              "Symphonic Rain · judgement overlay   Perfect / Good(early·late) / Miss   ·   memory only, no file writes",
              "シンフォニック＝レイン · 判定表示   Perfect / Good(早·遅) / Miss   ·   メモリのみ、ファイルは変更しません");
            A("由B站@MUCT_POHRIZ使用DeepSeek Harness辅助开发",
              "by @MUCT_POHRIZ (bilibili) · built with DeepSeek Harness",
              "@MUCT_POHRIZ（bilibili）· DeepSeek Harness の支援で開発");

            // ---------- 片段 ----------
            AS("本场开始 · ", "Session start · ", "本番開始 · ");
            AS("本场成绩 · ", "Session result · ", "本番結果 · ");
            AS("本场中途退出，不出成绩单（已判定 ", "Stopped mid-song, no result card (judged ", "途中終了のためリザルトなし（判定 ");
            AS("，成绩单暂不启用。", ", result card disabled.", "。リザルトは無効です。");
            AS(" 个音符 · 全长 ", " notes · length ", " 音 · 全長 ");
            AS(" 秒", " s", " 秒");
            AS("需弹 ", "to play ", "要演奏 ");
            AS("已判定 ", "judged ", "判定 ");
            AS(" / 需弹 ", " / to play ", " / 要演奏 ");
            AS("（早 ", " (early ", "（早 ");
            AS(" / 晚 ", " / late ", " / 遅 ");
            AS("） · Miss ", ") · Miss ", "） · Miss ");
            AS(" · 空按 ", " · empty ", " · 空打ち ");
            AS(" · 模式未知", " · mode unknown", " · モード不明");
            AS(" · free play", " · free play", " · フリープレイ");
            AS(" · 剧情演奏", " · story performance", " · ストーリー演奏");
            AS("音符表：共 ", "Note table: ", "ノート表：全 ");
            AS(" 个，需玩家弹 ", " entries, to play ", " 件、演奏 ");
            AS("音符表数量异常（count=", "Unexpected note-table count (count=", "ノート表の件数が異常（count=");
            AS("音符表指针异常（0x", "Unexpected note-table pointer (0x", "ノート表のポインタが異常（0x");
            AS("最后音符 ", "last note ", "最後の音 ");
            AS("游戏内存里", "game memory: ", "ゲームメモリ内 ");
            AS("显示=是", "shown=yes", "表示=はい");
            AS("显示=否", "shown=no", "表示=いいえ");
            AS(" 矩形=", " rect=", " 矩形=");
            AS(" 字号=", " size=", " サイズ=");
            AS(" 覆盖层 fg=0x", " overlay fg=0x", " オーバーレイ fg=0x");
            AS("，原始字节校验通过。", ", original bytes verified.", "、元のバイト列を確認しました。");
            AS("已直接接管，未写入任何内存。", "adopted as is; nothing was written to memory.",
               "そのまま引き継ぎました（書き込みなし）。");
            AS("已还原并清除，正在安装新版…", "restored and cleared; installing the new version…",
               "復元・消去し、新しい版を適用しています…");
            AS("，模块基址 0x", ", module base 0x", "、モジュールベース 0x");
        }

        /// <summary>把「任意已知语言」的文本翻成当前语言；未知文本原样返回。</summary>
        public static string Tr(string any)
        {
            if (string.IsNullOrEmpty(any)) return any;
            int i = Index(Code);
            if (i == 0 && !HasCjk(any)) return any;

            for (int k = 0; k < T.Count; k++)
            {
                var r = T[k];
                if (r[0] == any || r[1] == any || r[2] == any) return r[i];
            }

            string s = any;
            for (int k = 0; k < P.Count; k++)
            {
                var r = P[k];
                for (int c = 0; c < 3; c++)
                {
                    if (r[c].Length > 0 && s.StartsWith(r[c], StringComparison.Ordinal))
                    {
                        s = r[i] + s.Substring(r[c].Length);
                        k = P.Count;   // 只替换一次前缀，随后仍要做片段替换
                        break;
                    }
                }
            }

            for (int k = 0; k < S.Count; k++)
            {
                var r = S[k];
                for (int c = 0; c < 3; c++)
                {
                    if (r[c].Length > 0 && c != i && s.IndexOf(r[c], StringComparison.Ordinal) >= 0)
                        s = s.Replace(r[c], r[i]);
                }
            }
            return s;
        }

        /// <summary>取当前语言文本并按参数格式化。</summary>
        public static string F(string tpl, params object[] args)
        {
            try { return string.Format(Tr(tpl), args); }
            catch { return Tr(tpl); }
        }

        static bool HasCjk(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x3000 && c <= 0x9FFF) return true;
                if (c >= 0xFF00 && c <= 0xFFEF) return true;
            }
            return false;
        }

        /// <summary>把窗体里所有静态文本（含下拉选项、托盘菜单）翻成当前语言。</summary>
        public static void ApplyTo(Control root)
        {
            if (root == null) return;
            var f = root as Form;
            if (f != null && !string.IsNullOrEmpty(f.Text)) f.Text = Tr(f.Text);

            var cmb = root as ComboBox;
            if (cmb != null && cmb.Items.Count > 0)
            {
                int sel = cmb.SelectedIndex;
                for (int i = 0; i < cmb.Items.Count; i++)
                {
                    string t = cmb.Items[i] as string;
                    if (t != null) cmb.Items[i] = Tr(t);
                }
                if (sel >= 0 && sel < cmb.Items.Count) cmb.SelectedIndex = sel;
            }

            var clb = root as CheckedListBox;
            if (clb != null)
            {
                for (int i = 0; i < clb.Items.Count; i++)
                {
                    string t = clb.Items[i] as string;
                    if (t != null) clb.Items[i] = Tr(t);
                }
            }

            var lb = root as ListBox;
            if (lb != null && !(root is CheckedListBox))
            {
                // 日志历史不翻译，避免半截英文半截中文
            }

            if (!string.IsNullOrEmpty(root.Text) && !(root is Form) && !(root is ComboBox))
                root.Text = Tr(root.Text);

            foreach (Control c in root.Controls) ApplyTo(c);
        }

        /// <summary>托盘菜单等不在 Controls 树里的元素。</summary>
        public static void ApplyTo(ToolStripItemCollection items)
        {
            if (items == null) return;
            foreach (ToolStripItem it in items)
            {
                if (!string.IsNullOrEmpty(it.Text)) it.Text = Tr(it.Text);
                var dd = it as ToolStripDropDownItem;
                if (dd != null) ApplyTo(dd.DropDownItems);
            }
        }
    }
}
