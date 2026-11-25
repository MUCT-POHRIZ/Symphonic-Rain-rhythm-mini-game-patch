using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SRPerfectPatch;

/// <summary>
/// 一个受支持的《交响乐之雨》版本。不同版本（Steam 2023 / 2007 流行版）的进程名、
/// 判定站点 RVA、MusicUnit 字段偏移都不一样，所以用「配置档」把它们分开描述。
/// 所有 RVA 都相对主模块基址；所有偏移都相对 MusicUnit(this)。
/// </summary>
internal sealed class GameProfile
{
    public string Id = "";
    public string Display = "";
    public string[] ProcessNames = Array.Empty<string>();

    // 判定站点
    public uint PressSiteRva;
    public uint PressRetRva;
    public byte[] PressOrig = Array.Empty<byte>();

    public uint MissSiteRva;
    public uint MissRetRva;
    public byte[] MissOrig = Array.Empty<byte>();

    // 帧率计数点（call 到「每帧更新」函数）。HasTick=false 时该版本不显示 FPS。
    public bool HasTick;
    public uint TickSiteRva;
    public uint TickTargetRva;

    // MusicUnit 字段偏移
    public int OffNoteArray;    // 音符数组指针
    public int OffNoteCount;    // 音符数量
    public int OffFrameTime;    // 本帧时间（1/88200 秒）
    public int OffKeyNoteIdx;   // 每键待判音符序号，-1 表示无
    public int OffKeyGrade;     // 每键待判档位 0..3
    public int OffLamp;         // 灯计数 0..59
    public int OffScore;        // 分数
    public int OffCombo;        // 连击等级
    public int OffSongMode;     // 演奏场景类型；-1 表示该版本未知

    // 判定窗口（1/88200 秒为单位，来自各版本的比较指令）
    public int WinPerfect;
    public int WinGood;
    public int WinBad;

    // 计分模型：gain = Base + PerCombo*min(combo,ComboMax)，灯满时 +LampBonus
    public bool HasScoreModel;
    public int ScoreBase;
    public int ScorePerCombo;
    public int ScoreComboMax;
    public int ScoreLampBonus;

    /// <summary>被 jmp 覆盖的站点字节数（6 或 7）。</summary>
    public int PatchLen => PressOrig.Length;

    public static readonly GameProfile Steam = new()
    {
        Id = "steam",
        Display = "Steam 2023 版",
        ProcessNames = new[] { "SR" },
        PressSiteRva = 0x053B34, PressRetRva = 0x053B3A,
        PressOrig = new byte[] { 0x8B, 0x83, 0xD8, 0x2B, 0x00, 0x00 },   // mov eax,[ebx+2BD8h]
        MissSiteRva = 0x0536FE, MissRetRva = 0x053704,
        MissOrig = new byte[] { 0xFF, 0x8E, 0x60, 0x2D, 0x00, 0x00 },     // dec dword [esi+2D60h]
        HasTick = true, TickSiteRva = 0x05056A, TickTargetRva = 0x053330,
        OffNoteArray = 0x1B28, OffNoteCount = 0x1B2C, OffFrameTime = 0x1B38,
        OffKeyNoteIdx = 0x2B60, OffKeyGrade = 0x2BD8,
        OffLamp = 0x2D60, OffScore = 0x2D64, OffCombo = 0x2D68, OffSongMode = 0x28C,
        WinPerfect = 1176, WinGood = 4704, WinBad = 9408,
        HasScoreModel = true, ScoreBase = 15, ScorePerCombo = 20, ScoreComboMax = 10, ScoreLampBonus = 96,
    };

    public static readonly GameProfile V2007 = new()
    {
        Id = "v2007",
        Display = "2007 流行版",
        ProcessNames = new[] { "[交响乐之雨].SR_Cracked", "[交响乐之雨].SR", "SR" },
        PressSiteRva = 0x044804, PressRetRva = 0x04480B,
        PressOrig = new byte[] { 0x8B, 0x94, 0xB3, 0xA8, 0x1C, 0x00, 0x00 }, // mov edx,[ebx+esi*4+1CA8h]
        MissSiteRva = 0x044387, MissRetRva = 0x04438E,
        MissOrig = new byte[] { 0xC7, 0x46, 0x18, 0x01, 0x00, 0x00, 0x00 }, // mov dword [esi+18h],1
        HasTick = true, TickSiteRva = 0x04146C, TickTargetRva = 0x044130,
        OffNoteArray = 0x0BFC, OffNoteCount = 0x0C00, OffFrameTime = 0x0C0C,
        OffKeyNoteIdx = 0x1C30, OffKeyGrade = 0x1CA8,
        OffLamp = 0x2030, OffScore = 0x2034, OffCombo = 0x2038, OffSongMode = -1,
        WinPerfect = 1764, WinGood = 7056, WinBad = 14112,
        // 计分公式与 Steam 版完全一致（反汇编 0x444026 起确认）：
        // 连击等级 +1（上限 10）→ 得分 = 15 + 20×等级，灯计数 == 59 时再 +96；Good 固定 +8 并清空连击
        HasScoreModel = true, ScoreBase = 15, ScorePerCombo = 20, ScoreComboMax = 10, ScoreLampBonus = 96,
    };

    public static readonly GameProfile[] All = { Steam, V2007 };
}

/// <summary>
/// 与运行中的游戏进程交互：定位模块、校验原始字节、安装/卸载判定钩子、读取判定事件。
/// 全程只操作目标进程的内存，不触碰任何游戏文件。
/// </summary>
internal sealed class GamePatcher : IDisposable
{
    // 分配大小与标记：标记用来识别“上一次运行遗留的补丁”，从而接管它而不是拒绝工作
    //
    // 布局（页对齐，代码与数据分开，安装后代码页降为只读，不做 RWX）：
    //   0x0000  按键 cave 代码        ┐ 第 0 页：安装后 PAGE_EXECUTE_READ
    //   0x0100  漏音 cave 代码        │
    //   0x0200  帧率计数 cave 代码    │
    //   0x0F80  标记 "SRP5"（只读）   ┘
    //   0x1000  控制块 seq / MusicUnit ┐ 第 1~2 页：PAGE_READWRITE（游戏侧只写这里）
    //   0x1020  帧计数 u32            │
    //   0x1040  事件环形缓冲 128×32   ┘
    private const int CaveSize = 0x3000;
    private const int MagicOffset = 0x0F80;
    private const int CtrlOffset = 0x1000;
    private const int FrameCountOffset = 0x1020;
    private const uint Magic = 0x36505253;   // "SRP6"（加入 2007 版支持：事件语义不变，但魔数换新以便升级）

    public const int BufOffset = 0x1040;
    public const int RingSlots = 128;
    public const int EventSize = 32;

    public int Pid { get; private set; }
    public IntPtr ModuleBase { get; private set; }
    public bool Installed { get; private set; }
    public string LastError { get; private set; } = "";
    public GameProfile Prof { get; private set; } = GameProfile.Steam;

    private IntPtr _hProc = IntPtr.Zero;
    private IntPtr _cave = IntPtr.Zero;
    private IntPtr _buf = IntPtr.Zero;
    private IntPtr _ctrl = IntPtr.Zero;
    private IntPtr _ring = IntPtr.Zero;
    private IntPtr _pressSite, _missSite, _tickSite;
    private IntPtr _tickTarget;
    private byte[] _tickOrig = Array.Empty<byte>();
    private bool _patchedSites;

    public readonly record struct JEvent(int Key4, int Grade, int NoteIdx, int DeltaTicks, int Type);

    // ---------------------------------------------------------------- attach
    public bool Attach(out string message)
    {
        message = "";
        Detach();
        LastError = "";

        GameProfile prof = null;
        Process proc = null;
        foreach (GameProfile p in GameProfile.All)
        {
            proc = FindProcess(p.ProcessNames);
            if (proc != null) { prof = p; break; }
        }
        if (proc == null || prof == null)
        {
            message = "未找到正在运行的《交响乐之雨》进程（SR.exe 或 2007 版）。";
            return false;
        }

        Prof = prof;
        Pid = proc.Id;
        _hProc = Native.OpenProcess(
            Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE |
            Native.PROCESS_QUERY_INFORMATION, false, Pid);
        if (_hProc == IntPtr.Zero)
            _hProc = Native.OpenProcess(Native.PROCESS_ALL_ACCESS, false, Pid);
        if (_hProc == IntPtr.Zero)
        {
            message = $"无法打开进程 (PID {Pid})，错误码 {Marshal.GetLastWin32Error()}。请尝试以管理员身份运行本程序。";
            return false;
        }

        Native.IsWow64Process(_hProc, out bool wow64);
        if (!wow64)
        {
            message = "目标进程不是 32 位进程，版本可能不匹配。";
            Detach();
            return false;
        }

        if (!TryGetMainModuleBase(Pid, out IntPtr baseAddr, out _))
        {
            message = "无法获取模块基址。";
            Detach();
            return false;
        }

        ModuleBase = baseAddr;
        _pressSite = ModuleBase + (int)Prof.PressSiteRva;
        _missSite = ModuleBase + (int)Prof.MissSiteRva;
        _tickSite = ModuleBase + (int)Prof.TickSiteRva;
        _tickTarget = ModuleBase + (int)Prof.TickTargetRva;

        // ---- 校验原始字节，任何不符立即中止，绝不盲目写入 ----
        byte[] cur = new byte[Prof.PatchLen];
        if (!ReadMemory(_pressSite, cur, cur.Length))
        {
            message = "读取按键判定处内存失败。";
            Detach();
            return false;
        }
        if (!cur.AsSpan().SequenceEqual(Prof.PressOrig))
        {
            string overMsg = "";
            TakeOverResult r = cur[0] == 0xE9 ? TryTakeOver(out overMsg) : TakeOverResult.NotOurs;
            if (r == TakeOverResult.Adopted)
            {
                // 接管时三个站点都还是「已打过补丁」的状态，原始字节由配置档推得
                // （call 目标就是该版本每帧更新函数的固定 RVA），不需要再从内存里读。
                if (Prof.HasTick) _tickOrig = CallPatch(_tickSite, _tickTarget);
                message = overMsg;
                return true;
            }
            if (r == TakeOverResult.Upgraded)
            {
                message = overMsg;
                if (!ReadMemory(_pressSite, cur, cur.Length) || !cur.AsSpan().SequenceEqual(Prof.PressOrig))
                {
                    message += "（还原失败，已中止）";
                    Detach();
                    return false;
                }
            }
            else
            {
                message = $"按键判定处字节不匹配（期望 {Hex(Prof.PressOrig)}，实际 {Hex(cur)}），已中止，未做任何写入。{overMsg}";
                Detach();
                return false;
            }
        }
        byte[] curMiss = new byte[Prof.PatchLen];
        if (!ReadMemory(_missSite, curMiss, curMiss.Length))
        {
            message = "读取漏音结算处内存失败。";
            Detach();
            return false;
        }
        if (!curMiss.AsSpan().SequenceEqual(Prof.MissOrig))
        {
            message = $"漏音结算处字节不匹配（期望 {Hex(Prof.MissOrig)}，实际 {Hex(curMiss)}），已中止，未做任何写入。";
            Detach();
            return false;
        }

        // ---- 帧率计数点：call 到「每帧更新」函数（这里读到的必定是原始字节） ----
        _ = _tickOrig.Length;
        if (Prof.HasTick && !VerifyTickSite(out string tickErr))
        {
            message = tickErr;
            Detach();
            return false;
        }

        string okMsg = $"已连接 {Prof.Display}（PID {Pid}），模块基址 0x{ModuleBase.ToInt64():X8}，原始字节校验通过。";
        message = message.Length > 0 ? message + " " + okMsg : okMsg;
        return true;
    }

    /// <summary>校验帧率计数点（call 到每帧更新函数）的原始字节，并把原 5 字节记下来备还原。</summary>
    private bool VerifyTickSite(out string message)
    {
        message = "";
        _tickOrig = Array.Empty<byte>();
        if (!Prof.HasTick) return true;

        _tickSite = ModuleBase + (int)Prof.TickSiteRva;
        byte[] tick = new byte[5];
        if (!ReadMemory(_tickSite, tick, 5))
        {
            message = "读取帧率计数点内存失败。";
            return false;
        }
        _tickTarget = ModuleBase + (int)Prof.TickTargetRva;
        long callTo = _tickSite.ToInt64() + 5 + BitConverter.ToInt32(tick, 1);
        if (tick[0] != 0xE8 || callTo != _tickTarget.ToInt64())
        {
            message = $"帧率计数点处字节不匹配（期望 E8 … 指向 0x{_tickTarget.ToInt64():X8}，实际 {Hex(tick)}），已中止，未做任何写入。";
            return false;
        }
        _tickOrig = tick;
        return true;
    }

    private static Process FindProcess(string[] names)
    {
        foreach (string n in names)
        {
            Process[] all;
            try { all = Process.GetProcessesByName(n); }
            catch { continue; }
            foreach (Process p in all)
            {
                try { if (!p.HasExited) return p; }
                catch { /* ignore */ }
            }
        }
        return null;
    }

    private static bool TryGetMainModuleBase(int pid, out IntPtr baseAddr, out string moduleName)
    {
        baseAddr = IntPtr.Zero;
        moduleName = "";
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, pid);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;
        try
        {
            var me = new Native.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>() };
            if (!Native.Module32FirstW(snap, ref me)) return false;
            baseAddr = me.modBaseAddr;
            moduleName = me.szModule ?? "";
            return baseAddr != IntPtr.Zero;
        }
        finally { Native.CloseHandle(snap); }
    }

    private TakeOverResult TryTakeOver(out string message)
    {
        message = "";
        int len = Prof.PatchLen;

        byte[] b = new byte[len];
        if (!ReadMemory(_pressSite, b, len) || b[0] != 0xE9)
        {
            message = "（按键判定处不是本补丁的跳转，未接管）";
            return TakeOverResult.NotOurs;
        }
        long target = _pressSite.ToInt64() + 5 + BitConverter.ToInt32(b, 1);
        IntPtr cave = new IntPtr(target);

        byte[] head = new byte[len];
        if (!ReadMemory(cave, head, len) || !head.AsSpan().SequenceEqual(Prof.PressOrig))
        {
            message = "（跳转目标不是本补丁的 code cave，未接管）";
            return TakeOverResult.NotOurs;
        }
        if (!ReadMemory(_missSite, b, len) || b[0] != 0xE9)
        {
            message = "（漏音结算处不是本补丁的跳转，未接管）";
            return TakeOverResult.NotOurs;
        }
        long missTarget = _missSite.ToInt64() + 5 + BitConverter.ToInt32(b, 1);
        if (missTarget != target + 0x100)
        {
            message = "（漏音结算处的跳转与本补丁不一致，未接管）";
            return TakeOverResult.NotOurs;
        }

        byte[] mk = new byte[4];
        bool gotMagic = ReadMemory(cave + MagicOffset, mk, 4);
        uint magic = gotMagic ? BitConverter.ToUInt32(mk, 0) : 0;

        if (gotMagic && magic == Magic)
        {
            _cave = cave;
            _ctrl = cave + CtrlOffset;
            _ring = cave + BufOffset;
            _buf = _ring;
            _patchedSites = true;
            Installed = true;
            message = $"检测到上次运行遗留的补丁（cave 0x{cave.ToInt64():X8}），已直接接管，未写入任何内存。";
            return TakeOverResult.Adopted;
        }

        bool knownOld = gotMagic && (magic == 0x35505253  // SRP5
                                     || magic == 0x34505253  // SRP4
                                     || magic == 0x33505253  // SRP3
                                     || magic == 0x32505253  // SRP2
                                     || magic == 0x31505253); // SRP1
        if (!knownOld)
        {
            message = gotMagic
                ? $"（游戏内存里有来源不明的补丁，标记 0x{magic:X8}。为安全起见既不接管也不释放它，请重启游戏后再挂载）"
                : "（读不到补丁标记，为安全起见不接管也不释放，请重启游戏后再挂载）";
            return TakeOverResult.NotOurs;
        }

        _cave = cave;
        _patchedSites = true;
        RestoreSites();
        FreeCave();
        message = $"检测到旧版补丁（cave 0x{cave.ToInt64():X8}），已还原并清除，正在安装新版…";
        return TakeOverResult.Upgraded;
    }

    private enum TakeOverResult { NotOurs, Adopted, Upgraded }

    // ---------------------------------------------------------------- install
    public bool Install(out string message)
    {
        message = "";
        if (_hProc == IntPtr.Zero)
        {
            message = "尚未连接游戏进程。";
            return false;
        }
        if (Installed)
        {
            message = "钩子已处于安装状态。";
            return true;
        }

        _cave = Native.VirtualAllocEx(_hProc, IntPtr.Zero, CaveSize,
            Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
        if (_cave == IntPtr.Zero)
        {
            message = $"分配目标进程内存失败，错误码 {Marshal.GetLastWin32Error()}。";
            return false;
        }
        _buf = _cave + BufOffset;
        _ctrl = _cave + CtrlOffset;
        _ring = _cave + BufOffset;

        bool needTick = Prof.HasTick && _tickOrig.Length == 5;
        if (!Rel32Fits(_pressSite, _cave) || !Rel32Fits(_missSite, _cave + 0x100) ||
            (needTick && !Rel32Fits(_tickSite, _cave + 0x200)))
        {
            FreeCave(drainFirst: false);
            message = "分配到的新内存离游戏模块太远（超过 ±2GB），跳转无法构造，已放弃。请重启游戏后重试。";
            return false;
        }

        byte[] pressCave = BuildPressCave(_cave, _ctrl, ModuleBase + (int)Prof.PressRetRva);
        byte[] missCave = BuildMissCave(_cave + 0x100, _ctrl, ModuleBase + (int)Prof.MissRetRva);

        byte[] blob = new byte[0x300];
        Array.Copy(pressCave, 0, blob, 0, pressCave.Length);
        Array.Copy(missCave, 0, blob, 0x100, missCave.Length);
        byte[] tickCave = needTick
            ? BuildTickCave(_cave + 0x200, _cave + FrameCountOffset, _tickTarget)
            : Array.Empty<byte>();
        if (needTick) Array.Copy(tickCave, 0, blob, 0x200, tickCave.Length);

        if (!WriteMemory(_cave, blob, blob.Length))
        {
            message = "写入 code cave 失败。";
            FreeCave(drainFirst: false);
            return false;
        }

        WriteMemory(_cave + MagicOffset, BitConverter.GetBytes(Magic), 4);

        bool pgCode = Native.VirtualProtectEx(_hProc, _cave, new IntPtr(0x1000),
            Native.PAGE_EXECUTE_READ, out _);
        bool pgData = pgCode && Native.VirtualProtectEx(_hProc, _cave + 0x1000, new IntPtr(0x2000),
            Native.PAGE_READWRITE, out _);
        if (!pgCode || !pgData)
        {
            if (pgCode) Native.VirtualProtectEx(_hProc, _cave, new IntPtr(0x1000), Native.PAGE_READWRITE, out _);
            FreeCave(drainFirst: false);
            message = "设置 cave 页属性失败，已放弃安装。";
            return false;
        }
        Native.FlushInstructionCache(_hProc, _cave, new IntPtr(0x300));

        int plen = Prof.PatchLen;
        byte[] pressPatch = JmpPatch(_pressSite, _cave, plen);
        byte[] missPatch = JmpPatch(_missSite, _cave + 0x100, plen);
        byte[] tickPatch = needTick ? CallPatch(_tickSite, _cave + 0x200) : Array.Empty<byte>();

        List<IntPtr> threads = SuspendAllThreads(Pid);
        bool ok = false;
        try
        {
            if (!Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(plen), Native.PAGE_EXECUTE_READWRITE, out uint protPress))
            {
                message = "修改按键判定处页属性失败。";
                return false;
            }
            if (!Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(plen), Native.PAGE_EXECUTE_READWRITE, out uint protMiss))
            {
                Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(plen), SaferCodeProtect(protPress), out _);
                message = "修改漏音结算处页属性失败。";
                return false;
            }
            uint protTick = Native.PAGE_EXECUTE_READ;
            bool okTickProt = true;
            if (needTick && !Native.VirtualProtectEx(_hProc, _tickSite, new IntPtr(5), Native.PAGE_EXECUTE_READWRITE, out protTick))
            {
                Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(plen), SaferCodeProtect(protPress), out _);
                Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(plen), SaferCodeProtect(protMiss), out _);
                message = "修改帧率计数点处页属性失败。";
                return false;
            }

            bool w = WriteMemory(_pressSite, pressPatch, pressPatch.Length);
            w &= WriteMemory(_missSite, missPatch, missPatch.Length);
            if (needTick) w &= WriteMemory(_tickSite, tickPatch, tickPatch.Length);
            Native.FlushInstructionCache(_hProc, _pressSite, new IntPtr(plen));
            Native.FlushInstructionCache(_hProc, _missSite, new IntPtr(plen));
            if (needTick) Native.FlushInstructionCache(_hProc, _tickSite, new IntPtr(5));

            byte[] back = new byte[plen];
            if (w && ReadMemory(_pressSite, back, plen)) w = back.AsSpan().SequenceEqual(pressPatch);
            if (w && ReadMemory(_missSite, back, plen)) w = back.AsSpan().SequenceEqual(missPatch);
            if (w && needTick)
            {
                byte[] back5 = new byte[5];
                if (ReadMemory(_tickSite, back5, 5)) w = back5.AsSpan().SequenceEqual(tickPatch);
            }

            if (w)
            {
                _patchedSites = true;
                ok = true;
            }
            else
            {
                WriteMemory(_pressSite, Prof.PressOrig, Prof.PressOrig.Length);
                WriteMemory(_missSite, Prof.MissOrig, Prof.MissOrig.Length);
                if (needTick) WriteMemory(_tickSite, _tickOrig, 5);
                Native.FlushInstructionCache(_hProc, _pressSite, new IntPtr(plen));
                Native.FlushInstructionCache(_hProc, _missSite, new IntPtr(plen));
                if (needTick) Native.FlushInstructionCache(_hProc, _tickSite, new IntPtr(5));
                message = "写入补丁失败，已回滚。";
            }

            Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(plen), SaferCodeProtect(protPress), out _);
            Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(plen), SaferCodeProtect(protMiss), out _);
            if (needTick) Native.VirtualProtectEx(_hProc, _tickSite, new IntPtr(5), SaferCodeProtect(protTick), out _);
        }
        finally
        {
            ResumeAllThreads(threads);
        }

        if (!ok)
        {
            FreeCave();
            return false;
        }

        Installed = true;
        message = "补丁已安装（仅内存，游戏文件未改动）。";
        return true;
    }

    /// <summary>还原页属性时不要把代码页留在「可写」状态。</summary>
    private static uint SaferCodeProtect(uint p)
    {
        const uint writable = 0x04 | 0x08 | 0x40 | 0x80;
        if ((p & writable) != 0) return Native.PAGE_EXECUTE_READ;
        if ((p & Native.PAGE_EXECUTE_READ) == 0 && (p & 0x10) == 0) return Native.PAGE_EXECUTE_READ;
        return p;
    }

    // ---------------------------------------------------------------- uninstall
    private void RestoreSites()
    {
        if (_hProc == IntPtr.Zero || !_patchedSites) return;

        int plen = Prof.PatchLen;
        List<IntPtr> threads = SuspendAllThreads(Pid);
        try
        {
            if (Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(plen), Native.PAGE_EXECUTE_READWRITE, out uint p1)
                && Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(plen), Native.PAGE_EXECUTE_READWRITE, out uint p2))
            {
                uint p3 = Native.PAGE_EXECUTE_READWRITE;
                bool okTick = _tickOrig.Length == 5 &&
                              Native.VirtualProtectEx(_hProc, _tickSite, new IntPtr(5), Native.PAGE_EXECUTE_READWRITE, out p3);
                WriteMemory(_pressSite, Prof.PressOrig, Prof.PressOrig.Length);
                WriteMemory(_missSite, Prof.MissOrig, Prof.MissOrig.Length);
                if (okTick) WriteMemory(_tickSite, _tickOrig, 5);
                Native.FlushInstructionCache(_hProc, _pressSite, new IntPtr(plen));
                Native.FlushInstructionCache(_hProc, _missSite, new IntPtr(plen));
                if (okTick) Native.FlushInstructionCache(_hProc, _tickSite, new IntPtr(5));
                Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(plen), SaferCodeProtect(p1), out _);
                Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(plen), SaferCodeProtect(p2), out _);
                if (okTick) Native.VirtualProtectEx(_hProc, _tickSite, new IntPtr(5), SaferCodeProtect(p3), out _);

                byte[] back = new byte[plen];
                bool ok1 = ReadMemory(_pressSite, back, plen) && back.AsSpan().SequenceEqual(Prof.PressOrig);
                bool ok2 = ReadMemory(_missSite, back, plen) && back.AsSpan().SequenceEqual(Prof.MissOrig);
                bool ok3 = !okTick || (ReadMemory(_tickSite, back, 5) && back.AsSpan(0, 5).SequenceEqual(_tickOrig));
                LastError = ok1 && ok2 && ok3 ? "" : "还原校验失败";
            }
        }
        catch { /* 进程可能已退出 */ }
        finally { ResumeAllThreads(threads); }
        _patchedSites = false;
    }

    public void Uninstall()
    {
        if (_hProc == IntPtr.Zero) return;
        RestoreSites();
        FreeCave();
        Installed = false;
    }

    private void FreeCave(bool drainFirst = true)
    {
        if (_hProc != IntPtr.Zero && _cave != IntPtr.Zero)
        {
            if (drainFirst) Thread.Sleep(150);
            try { Native.VirtualFreeEx(_hProc, _cave, IntPtr.Zero, Native.MEM_RELEASE); } catch { }
        }
        _cave = IntPtr.Zero;
        _buf = IntPtr.Zero;
        _ctrl = IntPtr.Zero;
        _ring = IntPtr.Zero;
    }

    public void Detach()
    {
        Uninstall();
        if (_hProc != IntPtr.Zero)
        {
            try { Native.CloseHandle(_hProc); } catch { }
            _hProc = IntPtr.Zero;
        }
        Installed = false;
        Pid = 0;
        _patchedSites = false;
    }

    public void Dispose() => Detach();

    // ---------------------------------------------------------------- threads
    private static List<IntPtr> SuspendAllThreads(int pid)
    {
        var handles = new List<IntPtr>();
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPTHREAD, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return handles;
        try
        {
            var te = new Native.THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.THREADENTRY32>() };
            if (!Native.Thread32First(snap, ref te)) return handles;
            do
            {
                if (te.th32OwnerProcessID != (uint)pid) continue;
                IntPtr h = Native.OpenThread(Native.THREAD_SUSPEND_RESUME, false, te.th32ThreadID);
                if (h == IntPtr.Zero) continue;
                if (Native.SuspendThread(h) != 0xFFFFFFFF) handles.Add(h);
                else Native.CloseHandle(h);
            } while (Native.Thread32Next(snap, ref te));
        }
        catch { }
        finally { Native.CloseHandle(snap); }
        return handles;
    }

    private static void ResumeAllThreads(List<IntPtr> handles)
    {
        foreach (IntPtr h in handles)
        {
            try { Native.ResumeThread(h); Native.CloseHandle(h); } catch { }
        }
        handles.Clear();
    }

    // ---------------------------------------------------------------- events
    public bool TryReadEvent(ref int lastSeq, out JEvent ev)
    {
        ev = default;
        if (!Installed || _hProc == IntPtr.Zero || _ring == IntPtr.Zero) return false;

        byte[] s = new byte[4];
        if (!ReadMemory(_ctrl, s, 4)) return false;
        int seq = BitConverter.ToInt32(s, 0);
        if (seq <= lastSeq) return false;

        if (seq - lastSeq > RingSlots) lastSeq = seq - RingSlots;

        int want = lastSeq + 1;
        int idx = want & (RingSlots - 1);
        byte[] b = new byte[EventSize];
        if (!ReadMemory(_ring + idx * EventSize, b, EventSize)) return false;
        if (BitConverter.ToInt32(b, 0) != want)
        {
            lastSeq = want;
            return false;
        }

        lastSeq = want;
        ev = new JEvent(
            BitConverter.ToInt32(b, 4),
            BitConverter.ToInt32(b, 8),
            BitConverter.ToInt32(b, 12),
            BitConverter.ToInt32(b, 16),
            BitConverter.ToInt32(b, 20));
        return true;
    }

    public bool TryReadSeq(out int seq)
    {
        seq = 0;
        if (_hProc == IntPtr.Zero || _ctrl == IntPtr.Zero) return false;
        byte[] b = new byte[4];
        if (!ReadMemory(_ctrl, b, 4)) return false;
        seq = BitConverter.ToInt32(b, 0);
        return true;
    }

    public bool TryReadMusicUnit(out IntPtr mu)
    {
        mu = IntPtr.Zero;
        if (_hProc == IntPtr.Zero || _ctrl == IntPtr.Zero) return false;
        byte[] b = new byte[4];
        if (!ReadMemory(_ctrl + 4, b, 4)) return false;
        uint v = BitConverter.ToUInt32(b, 0);
        if (v < 0x10000) return false;
        mu = new IntPtr(v);
        return true;
    }

    public bool ReadInt(IntPtr addr, out int value)
    {
        value = 0;
        byte[] b = new byte[4];
        if (!ReadMemory(addr, b, 4)) return false;
        value = BitConverter.ToInt32(b, 0);
        return true;
    }

    public int TickOrigLength => _tickOrig.Length;

    public bool TryReadTick(out uint ticks)
    {
        ticks = 0;
        if (!Installed || _hProc == IntPtr.Zero || _ctrl == IntPtr.Zero) return false;
        if (_tickOrig.Length != 5) return false;
        byte[] b = new byte[4];
        if (!ReadMemory(_ctrl + (FrameCountOffset - CtrlOffset), b, 4)) return false;
        ticks = BitConverter.ToUInt32(b, 0);
        return true;
    }

    public bool CanReport => Installed && _buf != IntPtr.Zero;

    // ---------------------------------------------------------------- memory helpers
    public bool ReadMemory(IntPtr addr, byte[] buf, int len)
    {
        if (_hProc == IntPtr.Zero) return false;
        return Native.ReadProcessMemory(_hProc, addr, buf, new IntPtr(len), out IntPtr read) && read.ToInt64() == len;
    }

    public bool WriteMemory(IntPtr addr, byte[] buf, int len)
    {
        if (_hProc == IntPtr.Zero) return false;
        return Native.WriteProcessMemory(_hProc, addr, buf, new IntPtr(len), out IntPtr written) && written.ToInt64() == len;
    }

    // ---------------------------------------------------------------- code caves
    private static byte[] JmpPatch(IntPtr from, IntPtr to, int len)
    {
        long rel = to.ToInt64() - (from.ToInt64() + 5);
        var b = new byte[len];
        b[0] = 0xE9;
        BitConverter.GetBytes((int)rel).CopyTo(b, 1);
        for (int i = 5; i < len; i++) b[i] = 0x90;
        return b;
    }

    private static bool Rel32Fits(IntPtr from, IntPtr to)
    {
        long rel = to.ToInt64() - (from.ToInt64() + 5);
        return rel >= int.MinValue && rel <= int.MaxValue;
    }

    private static byte[] CallPatch(IntPtr from, IntPtr to)
    {
        long rel = to.ToInt64() - (from.ToInt64() + 5);
        var b = new byte[5];
        b[0] = 0xE8;
        BitConverter.GetBytes((int)rel).CopyTo(b, 1);
        return b;
    }

    private static byte[] BuildTickCave(IntPtr caveBase, IntPtr counter, IntPtr target)
    {
        var c = new List<byte>();
        c.Add(0x9C);
        c.AddRange(new byte[] { 0xFF, 0x05 });
        AddImm32(c, counter);
        c.Add(0x9D);
        c.Add(0xE9); AddRel32(c, target, caveBase);
        return c.ToArray();
    }

    /// <summary>
    /// 按键判定 cave。两个版本被覆盖的指令不同：
    ///   Steam：mov eax,[ebx+2BD8h]，进入时 esi=this，ebx=this+key*4
    ///   2007 ：mov edx,[ebx+esi*4+1CA8h]，进入时 ebx=this，esi=键位
    /// 记录 (键位*4, 档位, 音符序号, Δ) 后跳回分派处。
    /// </summary>
    private byte[] BuildPressCave(IntPtr caveBase, IntPtr ctrl, IntPtr retAddr)
    {
        bool is2007 = Prof.Id == "v2007";
        var c = new List<byte>();

        if (!is2007)
        {
            c.AddRange(Prof.PressOrig);                     // mov eax,[ebx+2BD8h]
            c.Add(0x60);                                    // pushad
            AddRingPrologue(c, ctrl, selfInEsi: true);
            c.AddRange(new byte[] { 0x8B, 0xD3, 0x2B, 0xD6 });          // mov edx,ebx; sub edx,esi  (key*4)
            c.AddRange(new byte[] { 0x89, 0x57, 0x04 });                // mov [edi+4],edx
            c.AddRange(new byte[] { 0x89, 0x47, 0x08 });                // mov [edi+8],eax (grade)
            AddMovEcxFromSelfKey(c, Prof.OffKeyNoteIdx, selfInEsi: true);
            c.AddRange(new byte[] { 0x89, 0x4F, 0x0C });                // mov [edi+0Ch],ecx
            c.AddRange(new byte[] { 0x33, 0xD2, 0x85, 0xC9 });          // xor edx,edx; test ecx,ecx
            int jsPos = c.Count;
            c.AddRange(new byte[] { 0x0F, 0x88, 0, 0, 0, 0 });          // js L2
            c.AddRange(new byte[] { 0x6B, 0xC9, 0x1C });                // imul ecx,ecx,28
            c.AddRange(new byte[] { 0x03, 0x8E }); AddImm32(c, new IntPtr(Prof.OffNoteArray)); // add ecx,[esi+arr]
            c.AddRange(new byte[] { 0x8B, 0x11 });                      // mov edx,[ecx]
            c.AddRange(new byte[] { 0x2B, 0x96 }); AddImm32(c, new IntPtr(Prof.OffFrameTime)); // sub edx,[esi+frame]
            int l2 = c.Count;
            c.AddRange(new byte[] { 0x89, 0x57, 0x10 });                // mov [edi+10h],edx
            c.AddRange(new byte[] { 0xC7, 0x47, 0x14, 0x01, 0x00, 0x00, 0x00 });
            c.Add(0x61);                                                // popad
            c.Add(0xE9); AddRel32(c, retAddr, caveBase);
            PatchRel32(c, jsPos + 2, l2 - jsPos - 6);
        }
        else
        {
            c.AddRange(Prof.PressOrig);                     // mov edx,[ebx+esi*4+1CA8h]
            c.Add(0x60);                                    // pushad
            AddRingPrologue(c, ctrl, selfInEsi: false);     // 记录 MusicUnit = ebx
            c.AddRange(new byte[] { 0x8B, 0x44, 0x24, 0x14 });          // mov eax,[esp+14h]  (pushad 里的 edx = 档位)
            c.AddRange(new byte[] { 0x89, 0x47, 0x08 });                // mov [edi+8],eax
            c.AddRange(new byte[] { 0x8B, 0xD6, 0xC1, 0xE2, 0x02 });    // mov edx,esi; shl edx,2  (key*4)
            c.AddRange(new byte[] { 0x89, 0x57, 0x04 });                // mov [edi+4],edx
            AddMovEcxFromSelfKey(c, Prof.OffKeyNoteIdx, selfInEsi: false);
            c.AddRange(new byte[] { 0x89, 0x4F, 0x0C });                // mov [edi+0Ch],ecx
            c.AddRange(new byte[] { 0x33, 0xD2, 0x85, 0xC9 });          // xor edx,edx; test ecx,ecx
            int jsPos = c.Count;
            c.AddRange(new byte[] { 0x0F, 0x88, 0, 0, 0, 0 });
            c.AddRange(new byte[] { 0x6B, 0xC9, 0x1C });                // imul ecx,ecx,28
            c.AddRange(new byte[] { 0x03, 0x8B }); AddImm32(c, new IntPtr(Prof.OffNoteArray)); // add ecx,[ebx+arr]
            c.AddRange(new byte[] { 0x8B, 0x11 });                      // mov edx,[ecx]
            c.AddRange(new byte[] { 0x2B, 0x93 }); AddImm32(c, new IntPtr(Prof.OffFrameTime)); // sub edx,[ebx+frame]
            int l2 = c.Count;
            c.AddRange(new byte[] { 0x89, 0x57, 0x10 });
            c.AddRange(new byte[] { 0xC7, 0x47, 0x14, 0x01, 0x00, 0x00, 0x00 });
            c.Add(0x61);                                                // popad
            c.Add(0xE9); AddRel32(c, retAddr, caveBase);
            PatchRel32(c, jsPos + 2, l2 - jsPos - 6);
        }
        return c.ToArray();
    }

    /// <summary>mov ecx,[self + key*4 + off]：self 在 esi 时用 [esi+edx+off]，在 ebx 时用 [ebx+esi*4+off]。</summary>
    private static void AddMovEcxFromSelfKey(List<byte> c, int off, bool selfInEsi)
    {
        if (selfInEsi)
        {
            c.AddRange(new byte[] { 0x8B, 0x8C, 0x16 });   // mov ecx,[esi+edx+disp32]
        }
        else
        {
            c.AddRange(new byte[] { 0x8B, 0x8C, 0xB3 });   // mov ecx,[ebx+esi*4+disp32]
        }
        AddImm32(c, new IntPtr(off));
    }

    /// <summary>
    /// 环形缓冲写入序言（pushad 之后）：seq++ → 记 MusicUnit → 算槽位地址进 edi。
    /// selfInEsi=true 表示 this 在 esi（Steam），false 表示在 ebx（2007）。
    /// </summary>
    private static void AddRingPrologue(List<byte> c, IntPtr ctrl, bool selfInEsi)
    {
        c.Add(0xBF); AddImm32(c, ctrl);                                // mov edi, ctrl
        c.AddRange(new byte[] { 0x8B, 0x0F });                         // mov ecx,[edi]
        c.Add(0x41);                                                   // inc ecx
        c.AddRange(new byte[] { 0x89, 0x0F });                         // mov [edi],ecx
        if (selfInEsi) c.AddRange(new byte[] { 0x89, 0x77, 0x04 });    // mov [edi+4],esi
        else c.AddRange(new byte[] { 0x89, 0x5F, 0x04 });              // mov [edi+4],ebx
        c.AddRange(new byte[] { 0x8D, 0xBF, 0x40, 0x00, 0x00, 0x00 }); // lea edi,[edi+40h]
        c.AddRange(new byte[] { 0x8B, 0xD1 });                         // mov edx,ecx
        c.AddRange(new byte[] { 0x83, 0xE2, 0x7F });                   // and edx,7Fh
        c.AddRange(new byte[] { 0xC1, 0xE2, 0x05 });                   // shl edx,5
        c.AddRange(new byte[] { 0x03, 0xFA });                         // add edi,edx
        c.AddRange(new byte[] { 0x89, 0x0F });                         // mov [edi],ecx
    }

    /// <summary>
    /// 漏音 cave。
    ///   Steam：esi=this, edi=音符指针, eax=0；被覆盖指令 dec [esi+2D60h]（标志位供后续 jns 使用）
    ///   2007 ：ebx=this, esi=音符指针；被覆盖指令 mov dword [esi+18h],1
    /// </summary>
    private byte[] BuildMissCave(IntPtr caveBase, IntPtr ctrl, IntPtr retAddr)
    {
        bool is2007 = Prof.Id == "v2007";
        var c = new List<byte>();
        c.Add(0x60);                                                   // pushad
        if (!is2007)
        {
            c.AddRange(new byte[] { 0x8B, 0xDF });                     // mov ebx,edi (音符指针)
            AddRingPrologue(c, ctrl, selfInEsi: true);
            c.AddRange(new byte[] { 0x8B, 0x03 });                     // mov eax,[ebx]
            c.AddRange(new byte[] { 0x2B, 0x86 }); AddImm32(c, new IntPtr(Prof.OffFrameTime)); // sub eax,[esi+frame]
        }
        else
        {
            c.AddRange(new byte[] { 0x8B, 0xEE });                     // mov ebp,esi (音符指针)
            AddRingPrologue(c, ctrl, selfInEsi: false);
            c.AddRange(new byte[] { 0x8B, 0x45, 0x00 });               // mov eax,[ebp]
            c.AddRange(new byte[] { 0x2B, 0x83 }); AddImm32(c, new IntPtr(Prof.OffFrameTime)); // sub eax,[ebx+frame]
        }
        c.AddRange(new byte[] { 0x89, 0x47, 0x10 });                   // mov [edi+10h],eax
        c.AddRange(new byte[] { 0xC7, 0x47, 0x08, 0x00, 0x00, 0x00, 0x00 }); // grade=0
        c.AddRange(new byte[] { 0xC7, 0x47, 0x0C, 0xFF, 0xFF, 0xFF, 0xFF }); // idx=-1
        c.AddRange(new byte[] { 0xC7, 0x47, 0x14, 0x02, 0x00, 0x00, 0x00 }); // type=2
        c.Add(0x61);                                                   // popad
        c.AddRange(Prof.MissOrig);                                     // 复现被覆盖的指令
        c.Add(0xE9); AddRel32(c, retAddr, caveBase);
        return c.ToArray();
    }

    private static void AddImm32(List<byte> c, IntPtr v) => c.AddRange(BitConverter.GetBytes((uint)v.ToInt64()));

    private static void AddRel32(List<byte> c, IntPtr target, IntPtr instrStart)
    {
        long endOfInstr = instrStart.ToInt64() + c.Count + 4;
        c.AddRange(BitConverter.GetBytes((int)(target.ToInt64() - endOfInstr)));
    }

    private static void PatchRel32(List<byte> c, int at, int rel)
    {
        byte[] v = BitConverter.GetBytes(rel);
        for (int i = 0; i < 4; i++) c[at + i] = v[i];
    }

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
}
