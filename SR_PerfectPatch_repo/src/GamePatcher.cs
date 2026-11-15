using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SRPerfectPatch;

/// <summary>
/// 与运行中的 SR.exe 交互：定位模块、校验原始字节、安装/卸载判定钩子、读取判定事件。
/// 全程只操作目标进程的内存，不触碰任何游戏文件。
/// </summary>
internal sealed class GamePatcher : IDisposable
{
    // ---- 目标地址（RVA，相对 SR.exe 模块基址；基址运行时读取，通常 0xEC0000） ----
    public const uint PressSiteRva = 0x053B34; // mov eax,[ebx+2BD8h]   —— 按键判定分派前
    public const uint PressRetRva = 0x053B3A;  // cmp eax,3
    public const uint MissSiteRva = 0x0536FE;  // dec dword [esi+2D60h]  —— 漏音结算
    public const uint MissRetRva = 0x053704;   // mov dword [esi+2D68h],0

    private static readonly byte[] PressOrig = { 0x8B, 0x83, 0xD8, 0x2B, 0x00, 0x00 };
    private static readonly byte[] MissOrig = { 0xFF, 0x8E, 0x60, 0x2D, 0x00, 0x00 };

    // 分配大小与标记：标记用来识别“上一次运行遗留的补丁”，从而接管它而不是拒绝工作
    //
    // 布局（页对齐，代码与数据分开，安装后代码页降为只读，不做 RWX）：
    //   0x0000  按键 cave 代码        ┐ 第 0 页：安装后 PAGE_EXECUTE_READ
    //   0x0100  漏音 cave 代码        │
    //   0x0F80  标记 "SRP4"（只读）   ┘
    //   0x1000  控制块 seq / MusicUnit ┐ 第 1~2 页：PAGE_READWRITE（游戏侧只写这里）
    //   0x1040  事件环形缓冲 128×32   ┘
    // 注意：标记必须放在环形缓冲之外，否则第 108 个事件就会把它踩掉 —— 那会让下次启动
    // 误判成「旧版补丁」并去释放一块游戏可能仍在执行的 cave。
    private const int CaveSize = 0x3000;
    private const int MagicOffset = 0x0F80;
    private const int CtrlOffset = 0x1000;   // cave+0x1000：u32 seq，cave+0x1004：u32 MusicUnit(this)
    private const uint Magic = 0x34505253;   // "SRP4"（升级 cave 布局时改这个值，旧补丁会被自动清除并重装）

    // 事件环形缓冲（相对 caveBase + 0x1040）：128 槽 × 32 字节 = 0x1000，正好落在第 1~2 页
    public const int BufOffset = 0x1040;
    public const int RingSlots = 128;
    public const int EventSize = 32;

    public int Pid { get; private set; }
    public IntPtr ModuleBase { get; private set; }
    public bool Installed { get; private set; }
    public string LastError { get; private set; } = "";

    private IntPtr _hProc = IntPtr.Zero;
    private IntPtr _cave = IntPtr.Zero;
    private IntPtr _buf = IntPtr.Zero;
    private IntPtr _ctrl = IntPtr.Zero;
    private IntPtr _ring = IntPtr.Zero;
    private IntPtr _pressSite, _missSite;
    private bool _patchedSites;

    public readonly record struct JEvent(int Key4, int Grade, int NoteIdx, int DeltaTicks, int Type);

    // ---------------------------------------------------------------- attach
    public bool Attach(out string message)
    {
        message = "";
        Detach();
        LastError = "";

        Process proc = FindGameProcess();
        if (proc == null)
        {
            message = "未找到正在运行的 SR.exe";
            return false;
        }

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

        if (!TryGetMainModuleBase(Pid, out IntPtr baseAddr, out string modName))
        {
            message = "无法获取模块基址。";
            Detach();
            return false;
        }
        if (!modName.Equals("SR.exe", StringComparison.OrdinalIgnoreCase))
        {
            message = $"主模块不是 SR.exe（读到 {modName}）。";
            Detach();
            return false;
        }

        ModuleBase = baseAddr;
        _pressSite = ModuleBase + (int)PressSiteRva;
        _missSite = ModuleBase + (int)MissSiteRva;

        // ---- 校验原始字节，任何不符立即中止，绝不盲目写入 ----
        byte[] cur = new byte[6];
        if (!ReadMemory(_pressSite, cur, 6))
        {
            message = "读取按键判定处内存失败。";
            Detach();
            return false;
        }
        if (!cur.AsSpan().SequenceEqual(PressOrig))
        {
            // 可能上次运行崩溃/被强杀，补丁仍留在游戏内存里：尝试接管，避免“必须先重启游戏”
            string overMsg = "";
            TakeOverResult r = cur[0] == 0xE9 ? TryTakeOver(out overMsg) : TakeOverResult.NotOurs;
            if (r == TakeOverResult.Adopted)
            {
                message = overMsg;
                return true;
            }
            if (r == TakeOverResult.Upgraded)
            {
                // 旧版已还原，重新确认字节后再继续安装
                message = overMsg;
                if (!ReadMemory(_pressSite, cur, 6) || !cur.AsSpan().SequenceEqual(PressOrig))
                {
                    message += "（还原失败，已中止）";
                    Detach();
                    return false;
                }
            }
            else
            {
                message = $"按键判定处字节不匹配（期望 {Hex(PressOrig)}，实际 {Hex(cur)}），已中止，未做任何写入。{overMsg}";
                Detach();
                return false;
            }
        }
        if (!ReadMemory(_missSite, cur, 6))
        {
            message = "读取漏音结算处内存失败。";
            Detach();
            return false;
        }
        if (!cur.AsSpan().SequenceEqual(MissOrig))
        {
            message = $"漏音结算处字节不匹配（期望 {Hex(MissOrig)}，实际 {Hex(cur)}），已中止，未做任何写入。";
            Detach();
            return false;
        }

        string okMsg = $"已连接 SR.exe (PID {Pid})，模块基址 0x{ModuleBase.ToInt64():X8}，原始字节校验通过。";
        message = message.Length > 0 ? message + " " + okMsg : okMsg;
        return true;
    }

    private static Process FindGameProcess()
    {
        Process[] all;
        try { all = Process.GetProcessesByName("SR"); }
        catch { return null; }
        foreach (Process p in all)
        {
            try { if (!p.HasExited) return p; }
            catch { /* ignore */ }
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

    /// <summary>
    /// 若两处站点已经是我们上次留下的 jmp、且 code cave 里标记吻合，就直接接管它（全程不写内存）。
    /// 若是更旧的版本（cave 布局不同、无标记），则先还原并释放，交给调用方重新安装。
    /// 这样即使上次运行被强杀，也无需重启游戏。
    /// </summary>
    private TakeOverResult TryTakeOver(out string message)
    {
        message = "";

        byte[] b = new byte[6];
        if (!ReadMemory(_pressSite, b, 6) || b[0] != 0xE9)
        {
            message = "（按键判定处不是本补丁的跳转，未接管）";
            return TakeOverResult.NotOurs;
        }
        long target = _pressSite.ToInt64() + 5 + BitConverter.ToInt32(b, 1);
        IntPtr cave = new IntPtr(target);

        byte[] head = new byte[6];
        if (!ReadMemory(cave, head, 6) || !head.AsSpan().SequenceEqual(PressOrig))
        {
            message = "（跳转目标不是本补丁的 code cave，未接管）";
            return TakeOverResult.NotOurs;
        }
        if (!ReadMemory(_missSite, b, 6) || b[0] != 0xE9)
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

        // 只有能确认「这块 cave 是本程序旧版本留下的」才敢还原+释放它。
        // 认不出来就什么都不做 —— 释放别人的（或无法确认归属的）可执行内存可能把游戏带崩。
        bool knownOld = gotMagic && (magic == 0x33505253  // SRP3
                                     || magic == 0x32505253  // SRP2
                                     || magic == 0x31505253); // SRP1
        if (!knownOld)
        {
            message = gotMagic
                ? $"（游戏内存里有来源不明的补丁，标记 0x{magic:X8}。为安全起见既不接管也不释放它，请重启游戏后再挂载）"
                : "（读不到补丁标记，为安全起见不接管也不释放，请重启游戏后再挂载）";
            return TakeOverResult.NotOurs;
        }

        // 旧版本：先安全还原，再释放旧 cave，随后由调用方安装新版
        _cave = cave;
        _patchedSites = true;
        RestoreSites();
        FreeCave();   // 内含 150ms 收尾等待，避免在途线程还在 cave 里
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

        // 1) 先以读写权限分配（不是 RWX），写完代码后再把代码页降为「可执行 + 只读」
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

        // 32 位进程里 jmp rel32 只能跨 ±2GB；万一分配到的地址离模块太远，跳转会算错 → 立刻放弃
        if (!Rel32Fits(_pressSite, _cave) || !Rel32Fits(_missSite, _cave + 0x100))
        {
            FreeCave(drainFirst: false);
            message = "分配到的新内存离游戏模块太远（超过 ±2GB），跳转无法构造，已放弃。请重启游戏后重试。";
            return false;
        }

        byte[] pressCave = BuildPressCave(_cave, _ctrl, ModuleBase + (int)PressRetRva);
        byte[] missCave = BuildMissCave(_cave + 0x100, _ctrl, ModuleBase + (int)MissRetRva);

        byte[] blob = new byte[0x200];
        Array.Copy(pressCave, 0, blob, 0, pressCave.Length);
        Array.Copy(missCave, 0, blob, 0x100, missCave.Length);

        if (!WriteMemory(_cave, blob, blob.Length))
        {
            message = "写入 code cave 失败。";
            FreeCave(drainFirst: false);
            return false;
        }

        // 写识别标记：下次运行（例如本程序被强杀过）可以接管这块 cave，而不是判定为“未知补丁”
        WriteMemory(_cave + MagicOffset, BitConverter.GetBytes(Magic), 4);

        // 代码页降为「可执行+只读」，数据页保持可读写：不做 RWX，减少被利用的可能
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

        // 2) 挂起目标进程所有线程后再改代码段，避免改到一半时正好有线程执行到这里
        byte[] pressPatch = JmpPatch(_pressSite, _cave);
        byte[] missPatch = JmpPatch(_missSite, _cave + 0x100);

        List<IntPtr> threads = SuspendAllThreads(Pid);
        bool ok = false;
        try
        {
            if (!Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(6), Native.PAGE_EXECUTE_READWRITE, out uint protPress))
            {
                message = "修改按键判定处页属性失败。";
                return false;
            }
            if (!Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(6), Native.PAGE_EXECUTE_READWRITE, out uint protMiss))
            {
                Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(6), SaferCodeProtect(protPress), out _);
                message = "修改漏音结算处页属性失败。";
                return false;
            }

            bool w = WriteMemory(_pressSite, pressPatch, pressPatch.Length);
            w &= WriteMemory(_missSite, missPatch, missPatch.Length);
            Native.FlushInstructionCache(_hProc, _pressSite, new IntPtr(6));
            Native.FlushInstructionCache(_hProc, _missSite, new IntPtr(6));

            byte[] back = new byte[6];
            if (w && ReadMemory(_pressSite, back, 6)) w = back.AsSpan().SequenceEqual(pressPatch);
            if (w && ReadMemory(_missSite, back, 6)) w = back.AsSpan().SequenceEqual(missPatch);

            if (w)
            {
                _patchedSites = true;
                ok = true;
            }
            else
            {
                // 回滚
                WriteMemory(_pressSite, PressOrig, PressOrig.Length);
                WriteMemory(_missSite, MissOrig, MissOrig.Length);
                Native.FlushInstructionCache(_hProc, _pressSite, new IntPtr(6));
                Native.FlushInstructionCache(_hProc, _missSite, new IntPtr(6));
                message = "写入补丁失败，已回滚。";
            }

            Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(6), SaferCodeProtect(protPress), out _);
            Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(6), SaferCodeProtect(protMiss), out _);
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

    /// <summary>
    /// 还原页属性时不要把代码页留在「可写」状态。若当初读到的就是 RWX（上一次补丁留下的），
    /// 也一律收回到「只读 + 可执行」——游戏 .text 原本就是 PAGE_EXECUTE_READ。
    /// </summary>
    private static uint SaferCodeProtect(uint p)
    {
        const uint writable = 0x04 | 0x08 | 0x40 | 0x80; // RW / WRITECOPY / EXECUTE_READWRITE / EXECUTE_WRITECOPY
        if ((p & writable) != 0) return Native.PAGE_EXECUTE_READ;
        if ((p & Native.PAGE_EXECUTE_READ) == 0 && (p & 0x10) == 0) return Native.PAGE_EXECUTE_READ;
        return p;
    }

    // ---------------------------------------------------------------- uninstall
    /// <summary>把两处站点还原成原始指令（挂起线程后写，保证不会改到一半被执行）。</summary>
    private void RestoreSites()
    {
        if (_hProc == IntPtr.Zero || !_patchedSites) return;

        List<IntPtr> threads = SuspendAllThreads(Pid);
        try
        {
            if (Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(6), Native.PAGE_EXECUTE_READWRITE, out uint p1)
                && Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(6), Native.PAGE_EXECUTE_READWRITE, out uint p2))
            {
                WriteMemory(_pressSite, PressOrig, PressOrig.Length);
                WriteMemory(_missSite, MissOrig, MissOrig.Length);
                Native.FlushInstructionCache(_hProc, _pressSite, new IntPtr(6));
                Native.FlushInstructionCache(_hProc, _missSite, new IntPtr(6));
                Native.VirtualProtectEx(_hProc, _pressSite, new IntPtr(6), SaferCodeProtect(p1), out _);
                Native.VirtualProtectEx(_hProc, _missSite, new IntPtr(6), SaferCodeProtect(p2), out _);

                byte[] back = new byte[6];
                bool ok1 = ReadMemory(_pressSite, back, 6) && back.AsSpan().SequenceEqual(PressOrig);
                bool ok2 = ReadMemory(_missSite, back, 6) && back.AsSpan().SequenceEqual(MissOrig);
                LastError = ok1 && ok2 ? "" : "还原校验失败";
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
            // 安全前提：调用前两处站点必须已经还原，否则游戏线程可能正停在这块 cave 里执行。
            // 还原后仍给一小段时间让「已经在 cave 里」的线程跑完，再释放，
            // 避免线程恢复后跳进已释放的内存 —— 那会直接把游戏带崩。
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
        // 必须把状态彻底清干净：留着 Pid 会让 Watch() 每秒都以为"还没卸载"，
        // 于是日志里会疯狂重复"游戏已退出，补丁已自动卸载"。
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
    /// <summary>从环形缓冲里取「下一条」事件。返回 false 表示暂时没有新事件。</summary>
    public bool TryReadEvent(ref int lastSeq, out JEvent ev)
    {
        ev = default;
        if (!Installed || _hProc == IntPtr.Zero || _ring == IntPtr.Zero) return false;

        byte[] s = new byte[4];
        if (!ReadMemory(_ctrl, s, 4)) return false;
        int seq = BitConverter.ToInt32(s, 0);
        if (seq <= lastSeq) return false;

        // 落后太多说明读得比写得慢，直接跳到最新，避免追一堆陈旧事件
        if (seq - lastSeq > RingSlots) lastSeq = seq - RingSlots;

        int want = lastSeq + 1;
        int idx = want & (RingSlots - 1);
        byte[] b = new byte[EventSize];
        if (!ReadMemory(_ring + idx * EventSize, b, EventSize)) return false;
        if (BitConverter.ToInt32(b, 0) != want)   // 该槽已被更新的写入覆盖
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

    /// <summary>cave 里记下的 MusicUnit(this) 指针；用它读音符表、分数等。</summary>
    public bool TryReadMusicUnit(out IntPtr mu)
    {
        mu = IntPtr.Zero;
        if (_hProc == IntPtr.Zero || _ctrl == IntPtr.Zero) return false;
        byte[] b = new byte[4];
        if (!ReadMemory(_ctrl + 4, b, 4)) return false;
        uint v = BitConverter.ToUInt32(b, 0);
        if (v < 0x10000) return false; // 明显不是有效指针
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
    private static byte[] JmpPatch(IntPtr from, IntPtr to)
    {
        long rel = to.ToInt64() - (from.ToInt64() + 5);
        var b = new byte[6];
        b[0] = 0xE9;
        BitConverter.GetBytes((int)rel).CopyTo(b, 1);
        b[5] = 0x90;
        return b;
    }

    /// <summary>jmp rel32 只能跨 ±2GB；超出就必须放弃，否则游戏的执行流会跳到错误地址。</summary>
    private static bool Rel32Fits(IntPtr from, IntPtr to)
    {
        long rel = to.ToInt64() - (from.ToInt64() + 5);
        return rel >= int.MinValue && rel <= int.MaxValue;
    }

    /// <summary>
    /// 按键判定 cave：进入时 esi=this, ebx=this+key*4，先复现被覆盖的 mov eax,[ebx+2BD8h]，
    /// 记录 (键位, 档位, 音符序号, Δ)，再跳回 0xF13B3A 继续原有分派。
    /// </summary>
    private static byte[] BuildPressCave(IntPtr caveBase, IntPtr ctrl, IntPtr retAddr)
    {
        var c = new List<byte>();
        c.AddRange(new byte[] { 0x8B, 0x83, 0xD8, 0x2B, 0x00, 0x00 }); // mov eax,[ebx+2BD8h]
        c.Add(0x60);                                                   // pushad
        AddRingPrologue(c, ctrl);
        c.AddRange(new byte[] { 0x8B, 0xD3 });                         // mov edx,ebx
        c.AddRange(new byte[] { 0x2B, 0xD6 });                         // sub edx,esi      ; edx = key*4
        c.AddRange(new byte[] { 0x89, 0x57, 0x04 });                   // mov [edi+4],edx
        c.AddRange(new byte[] { 0x89, 0x47, 0x08 });                   // mov [edi+8],eax  ; grade
        c.AddRange(new byte[] { 0x8B, 0x8C, 0x16, 0x60, 0x2B, 0x00, 0x00 }); // mov ecx,[esi+edx+2B60h]
        c.AddRange(new byte[] { 0x89, 0x4F, 0x0C });                   // mov [edi+0Ch],ecx
        c.AddRange(new byte[] { 0x33, 0xD2 });                         // xor edx,edx
        c.AddRange(new byte[] { 0x85, 0xC9 });                         // test ecx,ecx
        int jsPos = c.Count;
        c.AddRange(new byte[] { 0x0F, 0x88, 0, 0, 0, 0 });             // js L2
        c.AddRange(new byte[] { 0x6B, 0xC9, 0x1C });                   // imul ecx,ecx,28
        c.AddRange(new byte[] { 0x03, 0x8E, 0x28, 0x1B, 0x00, 0x00 }); // add ecx,[esi+1B28h]
        c.AddRange(new byte[] { 0x8B, 0x11 });                         // mov edx,[ecx]    ; note time
        c.AddRange(new byte[] { 0x2B, 0x96, 0x38, 0x1B, 0x00, 0x00 }); // sub edx,[esi+1B38h]
        int l2 = c.Count;
        c.AddRange(new byte[] { 0x89, 0x57, 0x10 });                   // mov [edi+10h],edx
        c.AddRange(new byte[] { 0xC7, 0x47, 0x14, 0x01, 0x00, 0x00, 0x00 }); // mov dword [edi+14h],1
        c.Add(0x61);                                                   // popad
        c.Add(0xE9); AddRel32(c, retAddr, caveBase);                   // jmp 0xF13B3A

        PatchRel32(c, jsPos + 2, l2 - jsPos - 6);
        return c.ToArray();
    }

    /// <summary>
    /// 环形缓冲写入序言（pushad 之后）：seq++ → 记 MusicUnit → 算出本次槽位地址放进 edi。
    /// </summary>
    private static void AddRingPrologue(List<byte> c, IntPtr ctrl)
    {
        c.Add(0xBF); AddImm32(c, ctrl);                                // mov edi, ctrl
        c.AddRange(new byte[] { 0x8B, 0x0F });                         // mov ecx,[edi]      ; seq
        c.Add(0x41);                                                   // inc ecx
        c.AddRange(new byte[] { 0x89, 0x0F });                         // mov [edi],ecx
        c.AddRange(new byte[] { 0x89, 0x77, 0x04 });                   // mov [edi+4],esi    ; MusicUnit
        c.AddRange(new byte[] { 0x8D, 0xBF, 0x40, 0x00, 0x00, 0x00 }); // lea edi,[edi+40h]  ; 环形缓冲基址
        c.AddRange(new byte[] { 0x8B, 0xD1 });                         // mov edx,ecx
        c.AddRange(new byte[] { 0x83, 0xE2, 0x7F });                   // and edx,7Fh
        c.AddRange(new byte[] { 0xC1, 0xE2, 0x05 });                   // shl edx,5
        c.AddRange(new byte[] { 0x03, 0xFA });                         // add edi,edx        ; edi = 槽位
        c.AddRange(new byte[] { 0x89, 0x0F });                         // mov [edi],ecx      ; slot.seq
    }

    /// <summary>
    /// 漏音 cave：进入时 esi=this, edi=音符指针, eax=0；记录 Δ，再复现被覆盖的 dec [esi+2D60h]
    /// （必须由这条 dec 产生标志位，0xF1370E 的 jns 依赖它），最后跳回 0xF13704。
    /// </summary>
    private static byte[] BuildMissCave(IntPtr caveBase, IntPtr ctrl, IntPtr retAddr)
    {
        var c = new List<byte>();
        c.Add(0x60);                                                   // pushad
        c.AddRange(new byte[] { 0x8B, 0xDF });                         // mov ebx,edi      ; 音符指针先存到 ebx（序言会用掉 edx）
        AddRingPrologue(c, ctrl);
        c.AddRange(new byte[] { 0x8B, 0x03 });                         // mov eax,[ebx]    ; note time
        c.AddRange(new byte[] { 0x2B, 0x86, 0x38, 0x1B, 0x00, 0x00 }); // sub eax,[esi+1B38h]
        c.AddRange(new byte[] { 0x89, 0x47, 0x10 });                   // mov [edi+10h],eax
        c.AddRange(new byte[] { 0xC7, 0x47, 0x08, 0x00, 0x00, 0x00, 0x00 }); // mov dword [edi+8],0
        c.AddRange(new byte[] { 0xC7, 0x47, 0x0C, 0xFF, 0xFF, 0xFF, 0xFF }); // mov dword [edi+0Ch],-1
        c.AddRange(new byte[] { 0xC7, 0x47, 0x14, 0x02, 0x00, 0x00, 0x00 }); // mov dword [edi+14h],2
        c.Add(0x61);                                                   // popad
        c.AddRange(new byte[] { 0xFF, 0x8E, 0x60, 0x2D, 0x00, 0x00 }); // dec dword [esi+2D60h]
        c.Add(0xE9); AddRel32(c, retAddr, caveBase);                   // jmp 0xF13704
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
