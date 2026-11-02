// ============================================================================
//  Hook.cs —— 注入器（20 功能完整版）
//
//  五种钩子：
//    ① InstallFrameHook            帧钩子（多动作 + 开关变量 + 可顺手调 double 方法）
//    ② InstallSetterHook           setter 钩子（改 int 参数）
//    ③ InstallForceReturnHook      bool / int / enum 返回值
//    ④ InstallForceFloatReturnHook float 返回值（xmm0）
//    ⑤ InstallForceDecimalReturnHook decimal 返回值（rcx 隐藏指针）
//    ⑥ InstallDamageHook           decimal 参数三合一（超级伤害 / 伤害倍率 / 防御倍率）
//
//  统一约定：每个功能在游戏进程里有一个 int 开关（1=开 0=关），代码洞里先判断再干活。
//            ⇒ 开/关功能只是写 4 个字节，不用重新打补丁。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class Hook
{
    const int PAGE_EXECUTE_READWRITE = 0x40, PAGE_READWRITE = 0x04;
    const int MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, int type, int prot);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, int newProt, out int oldProt);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);

    public static IntPtr Open(int pid) => OpenProcess(0x1F0FFF, false, pid);

    public static byte[] Read(IntPtr h, ulong addr, int n)
    {
        var b = new byte[n]; IntPtr got;
        return ReadProcessMemory(h, (IntPtr)(long)addr, b, (IntPtr)n, out got) ? b : null;
    }

    public static int ReadI32(IntPtr h, ulong addr)
    {
        var b = Read(h, addr, 4);
        return b == null ? -1 : BitConverter.ToInt32(b, 0);
    }

    public static bool WriteI32(IntPtr h, ulong addr, int v)
    {
        IntPtr w;
        return WriteProcessMemory(h, (IntPtr)(long)addr, BitConverter.GetBytes(v), (IntPtr)4, out w);
    }

    public static string Hex(byte[] b)
    {
        if (b == null) return "<读不到>";
        var s = new System.Text.StringBuilder();
        foreach (var x in b) s.Append(x.ToString("X2")).Append(' ');
        return s.ToString().Trim();
    }

    static ulong AllocNear(IntPtr h, ulong target, int size, int prot = PAGE_EXECUTE_READWRITE)
    {
        long t = (long)target;
        for (long d = 0; d < 0x7000_0000L; d += 0x10000)
            for (int s = 0; s < 2; s++)
            {
                long cand = s == 0 ? t - d : t + d;
                if (cand < 0x10000) continue;
                var p = VirtualAllocEx(h, (IntPtr)cand, (IntPtr)size, MEM_RESERVE | MEM_COMMIT, prot);
                if (p != IntPtr.Zero) return (ulong)p;
            }
        return 0;
    }

    public static ulong AllocFlags(IntPtr h, int count)
    {
        var p = VirtualAllocEx(h, IntPtr.Zero, (IntPtr)(count * 4 + 64), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        return (ulong)p;
    }

    // ---------------------------------------------------------------- 指令长度
    static int InsnLen(byte[] b, int i)
    {
        byte op = b[i];
        if (op >= 0x50 && op <= 0x57) return 1;
        if (op == 0x41 && b[i + 1] >= 0x50 && b[i + 1] <= 0x57) return 2;
        if (op == 0x48 && b[i + 1] == 0x83 && (b[i + 2] == 0xEC || b[i + 2] == 0xC4)) return 4;
        if (op == 0x48 && b[i + 1] == 0x81 && (b[i + 2] == 0xEC || b[i + 2] == 0xC4)) return 7;
        if (op == 0x48 && b[i + 1] >= 0xB8 && b[i + 1] <= 0xBF) return 10;
        if (op == 0x4C && b[i + 1] == 0x8B && b[i + 2] == 0xDC) return 3;
        return -1;
    }

    static int PrologueLen(byte[] head)
    {
        int n = 0;
        while (n < 5)
        {
            int len = InsnLen(head, n);
            if (len <= 0) return -1;
            n += len;
            if (n > 15) return -1;
        }
        return n;
    }

    static bool Bad(ulong a) => a < 0x10000 || a == ulong.MaxValue;

    // ---------------------------------------------------------------- 帧动作
    public class Action
    {
        public string Name;
        public ulong FlagAddr;         // 开关（1=开）
        public ulong CheckAddr;        // 读这里判断
        public int Target;             // 期望值
        public ulong WriteAddr;        // 不等于就写（0=不写）
        public ulong PreAddr;          // 调用前先设（金币动画用）
        public int PreValue;
        public ulong CallThis;         // 刷新方法 this
        public ulong CallFn;           // 刷新方法
        public int CallEdx;
        public ulong CallFnXmm0;       // 只调一次 double 方法（游戏速度用）
        public ulong Xmm0Bits;         // 那个 double 的位模式
    }

    // ---------------------------------------------------------------- ① 帧钩子
    public static byte[] InstallFrameHook(IntPtr h, ulong patchAddr, List<Action> actions, out ulong caveOut)
    {
        caveOut = 0;
        var head = Read(h, patchAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到挂点"); return null; }
        if (head[0] == 0xE9) { Console.WriteLine("  ✗ 挂点已被占用（上次补丁残留），请重启游戏"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 挂点序言无法覆盖 ≥5 字节: " + Hex(head)); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);

        ulong cave = AllocNear(h, patchAddr, 0x2000);
        if (cave == 0) { Console.WriteLine("  ✗ 分配代码洞失败"); return null; }
        caveOut = cave;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x51); B(0x52); B(0x41, 0x50); B(0x41, 0x51);   // push rcx/rdx/r8/r9
        B(0x48, 0x83, 0xEC, 0x38);                        // sub rsp,38h

        var jeAt = new List<int>();
        var jeTo = new List<int>();
        foreach (var a in actions)
        {
            B(0x48, 0xB8); I64(a.FlagAddr);        // mov rax,&flag
            B(0x83, 0x38, 0x00);                   // cmp dword [rax],0
            jeAt.Add(c.Count); B(0x74, 0x00);      // je skip

            if (a.CallFnXmm0 != 0)
            {
                // 只调 double 方法：Engine.set_TimeScale(xmm0)
                B(0x48, 0xB8); I64(a.Xmm0Bits);    // mov rax,bits
                B(0x66, 0x48, 0x0F, 0x6E, 0xC0);   // movq xmm0,rax
                B(0x48, 0xB8); I64(a.CallFnXmm0);  // mov rax,fn
                B(0xFF, 0xD0);                     // call rax
            }
            else
            {
                if (Bad(a.CheckAddr) || (a.WriteAddr == 0 && Bad(a.CallFn)))
                { Console.WriteLine("  ✗ 动作地址非法，中止: " + a.Name); return null; }

                B(0x48, 0xB8); I64(a.CheckAddr);
                B(0x8B, 0x10);                     // mov edx,[rax]
                B(0x81, 0xFA); I32(a.Target);      // cmp edx,target
                jeAt.Add(c.Count); B(0x74, 0x00);  // je skip

                if (a.WriteAddr != 0) { B(0x48, 0xB8); I64(a.WriteAddr); B(0xC7, 0x00); I32(a.Target); }
                if (a.PreAddr != 0) { B(0x48, 0xB8); I64(a.PreAddr); B(0xC7, 0x00); I32(a.PreValue); }

                if (!Bad(a.CallFn) && !Bad(a.CallThis))   // ★ CallFn=0 时走"只写值"模式
                {
                    B(0x0F, 0x11, 0x44, 0x24, 0x20);   // movups [rsp+20h],xmm0
                    B(0x48, 0xB9); I64(a.CallThis);
                    B(0xBA); I32(a.CallEdx);
                    B(0x45, 0x31, 0xC0);               // xor r8d,r8d
                    B(0x48, 0xB8); I64(a.CallFn);
                    B(0xFF, 0xD0);                     // call rax
                    B(0x0F, 0x10, 0x44, 0x24, 0x20);   // movups xmm0,[rsp+20h]
                }
            }
            jeTo.Add(c.Count);
        }
        for (int i = 0; i < jeAt.Count; i++)
        {
            int d = jeTo[i / 2] - (jeAt[i] + 2);
            c[jeAt[i] + 1] = (byte)d;
        }

        B(0x48, 0x83, 0xC4, 0x38);
        B(0x41, 0x59); B(0x41, 0x58); B(0x5A); B(0x59);
        foreach (var b in original) B(b);
        int jmpAt = c.Count;
        B(0xE9); I32(0);
        int back = (int)((long)(patchAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w))
        { Console.WriteLine("  ✗ 写代码洞失败"); return null; }

        Patch(h, patchAddr, cave, n);
        Console.WriteLine("  帧钩子: 0x" + patchAddr.ToString("X") + " → 0x" + cave.ToString("X") + "（" + actions.Count + " 动作）");
        return original;
    }

    static void Patch(IntPtr h, ulong addr, ulong cave, int n)
    {
        var patch = new byte[n];
        patch[0] = 0xE9;
        var d = BitConverter.GetBytes((int)((long)cave - (long)(addr + 5)));
        patch[1] = d[0]; patch[2] = d[1]; patch[3] = d[2]; patch[4] = d[3];
        for (int i = 5; i < n; i++) patch[i] = 0x90;
        int old; IntPtr w;
        VirtualProtectEx(h, (IntPtr)(long)addr, (IntPtr)n, PAGE_EXECUTE_READWRITE, out old);
        WriteProcessMemory(h, (IntPtr)(long)addr, patch, (IntPtr)n, out w);
        int tmp; VirtualProtectEx(h, (IntPtr)(long)addr, (IntPtr)n, old, out tmp);
        FlushInstructionCache(h, (IntPtr)(long)addr, (IntPtr)n);
    }

    // ---------------------------------------------------------------- ② setter 钩子
    public static byte[] InstallSetterHook(IntPtr h, ulong methodAddr, ulong thisAddr, int forcedValue, ulong flagAddr)
    {
        var head = Read(h, methodAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到方法入口"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 序言无法覆盖"); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);

        ulong cave = AllocNear(h, methodAddr, 0x1000);
        if (cave == 0) return null;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x50);                                   // push rax
        B(0x48, 0xB8); I64(flagAddr);              // mov rax,&flag
        B(0x83, 0x38, 0x00);                       // cmp dword [rax],0
        int je1 = c.Count; B(0x74, 0x00);          // je 原样
        int je2 = -1;
        if (thisAddr != 0)
        {
            B(0x48, 0xB8); I64(thisAddr);          // mov rax,目标对象
            B(0x48, 0x39, 0xC1);                   // cmp rcx,rax
            je2 = c.Count; B(0x75, 0x00);          // jne 原样
        }
        B(0xBA); I32(forcedValue);                 // mov edx,固定值
        int origAt = c.Count;
        c[je1 + 1] = (byte)(origAt - (je1 + 2));
        if (je2 >= 0) c[je2 + 1] = (byte)(origAt - (je2 + 2));
        B(0x58);                                   // pop rax
        foreach (var b in original) B(b);
        int jmpAt = c.Count; B(0xE9); I32(0);
        int back = (int)((long)(methodAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w)) return null;
        Patch(h, methodAddr, cave, n);
        Console.WriteLine("  setter 钩子: 0x" + methodAddr.ToString("X") + " → 0x" + cave.ToString("X") + "（edx=" + forcedValue + "）");
        return original;
    }

    // ---------------------------------------------------------------- ③ bool/int/enum 返回
    public static byte[] InstallForceReturnHook(IntPtr h, ulong methodAddr, ulong flagAddr, int retValue)
    {
        var head = Read(h, methodAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到入口"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 序言无法覆盖: " + Hex(head)); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);
        ulong cave = AllocNear(h, methodAddr, 0x1000);
        if (cave == 0) return null;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x50);
        B(0x48, 0xB8); I64(flagAddr);
        B(0x83, 0x38, 0x00);
        int jeAt = c.Count; B(0x74, 0x00);
        B(0x58);
        B(0xB8); I32(retValue);
        B(0xC3);
        int origAt = c.Count;
        c[jeAt + 1] = (byte)(origAt - (jeAt + 2));
        foreach (var b in original) B(b);
        int jmpAt = c.Count; B(0xE9); I32(0);
        int back = (int)((long)(methodAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w)) return null;
        Patch(h, methodAddr, cave, n);
        Console.WriteLine("  强制返回(int): 0x" + methodAddr.ToString("X") + " → 0x" + cave.ToString("X") + "（返回 " + retValue + "）");
        return original;
    }

    // ---------------------------------------------------------------- ④ float 返回
    public static byte[] InstallForceFloatReturnHook(IntPtr h, ulong methodAddr, ulong flagAddr, float value)
    {
        var head = Read(h, methodAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到入口"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 序言无法覆盖: " + Hex(head)); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);
        ulong cave = AllocNear(h, methodAddr, 0x1000);
        if (cave == 0) return null;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x50);
        B(0x48, 0xB8); I64(flagAddr);
        B(0x83, 0x38, 0x00);
        int jeAt = c.Count; B(0x74, 0x00);
        B(0x58);
        B(0xB8); I32(BitConverter.SingleToInt32Bits(value));
        B(0x66, 0x0F, 0x6E, 0xC0);      // movd xmm0,eax
        B(0xC3);
        int origAt = c.Count;
        c[jeAt + 1] = (byte)(origAt - (jeAt + 2));
        foreach (var b in original) B(b);
        int jmpAt = c.Count; B(0xE9); I32(0);
        int back = (int)((long)(methodAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w)) return null;
        Patch(h, methodAddr, cave, n);
        Console.WriteLine("  强制返回(float): 0x" + methodAddr.ToString("X") + "（返回 " + value + "）");
        return original;
    }

    // ---------------------------------------------------------------- ⑤ decimal 返回
    public static byte[] InstallForceDecimalReturnHook(IntPtr h, ulong methodAddr, ulong flagAddr, decimal value)
    {
        int[] parts = decimal.GetBits(value);
        var head = Read(h, methodAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到入口"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 序言无法覆盖: " + Hex(head)); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);
        ulong cave = AllocNear(h, methodAddr, 0x1000);
        if (cave == 0) return null;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x50);
        B(0x48, 0xB8); I64(flagAddr);
        B(0x83, 0x38, 0x00);
        int jeAt = c.Count; B(0x74, 0x00);
        B(0x58);
        B(0xC7, 0x01); I32(parts[0]);              // [rcx]   = lo
        B(0xC7, 0x41, 0x04); I32(parts[1]);        // [rcx+4] = mid
        B(0xC7, 0x41, 0x08); I32(parts[2]);        // [rcx+8] = hi
        B(0xC7, 0x41, 0x0C); I32(parts[3]);        // [rcx+12]= flags
        B(0x48, 0x89, 0xC8);                       // mov rax,rcx
        B(0xC3);
        int origAt = c.Count;
        c[jeAt + 1] = (byte)(origAt - (jeAt + 2));
        foreach (var b in original) B(b);
        int jmpAt = c.Count; B(0xE9); I32(0);
        int back = (int)((long)(methodAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w)) return null;
        Patch(h, methodAddr, cave, n);
        Console.WriteLine("  强制返回(decimal): 0x" + methodAddr.ToString("X") + "（返回 " + value + "）");
        return original;
    }

    // ---------------------------------------------------------------- ⑥ 伤害三合一
    // rcx = 受伤的 Creature，rdx = &decimal amount
    //   玩家侧：flagDef 开 → amount /= 4
    //   敌方侧：flagSuper 开 → amount = 9999；否则 flagMul 开 → amount *= 10
    public static byte[] InstallDamageHook(IntPtr h, ulong methodAddr, ulong playerCreature,
                                           ulong flagSuper, ulong flagMul, ulong flagDef,
                                           int superValue = 9999, int mulFactor = 10, int divFactor = 4)
    {
        var head = Read(h, methodAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到入口"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 序言无法覆盖: " + Hex(head)); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);
        ulong cave = AllocNear(h, methodAddr, 0x1000);
        if (cave == 0) return null;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x50);                                   // push rax
        B(0x53);                                   // push rbx
        B(0x52);                                   // push rdx
        B(0x51);                                   // push rcx
        B(0x48, 0xB8); I64(playerCreature);        // mov rax,玩家
        B(0x48, 0x39, 0xC1);                       // cmp rcx,rax
        int jePlayer = c.Count; B(0x74, 0x00);     // je 玩家侧

        // 敌方侧：超级伤害
        B(0x48, 0xB8); I64(flagSuper); B(0x83, 0x38, 0x00);
        int jeNoSuper = c.Count; B(0x74, 0x00);    // je 看倍率
        B(0xC7, 0x02); I32(superValue);            // mov dword [rdx],9999
        B(0xC7, 0x42, 0x04); I32(0);
        B(0xC7, 0x42, 0x08); I32(0);
        B(0xC7, 0x42, 0x0C); I32(0);
        int jmpDone1 = c.Count; B(0xE9, 0x00, 0x00, 0x00, 0x00);

        int mulAt = c.Count;                       // 看倍率
        c[jeNoSuper + 1] = (byte)(mulAt - (jeNoSuper + 2));
        B(0x48, 0xB8); I64(flagMul); B(0x83, 0x38, 0x00);
        int jeDone = c.Count; B(0x74, 0x00);       // je 完成
        B(0x8B, 0x02);                             // mov eax,[rdx]
        B(0x69, 0xC0); I32(mulFactor);             // imul eax,eax,N
        B(0x89, 0x02);                             // mov [rdx],eax
        B(0xC7, 0x42, 0x04); I32(0);
        B(0xC7, 0x42, 0x08); I32(0);
        B(0xC7, 0x42, 0x0C); I32(0);
        int jmpDone2 = c.Count; B(0xE9, 0x00, 0x00, 0x00, 0x00);

        // 玩家侧：防御倍率
        int playerAt = c.Count;
        c[jePlayer + 1] = (byte)(playerAt - (jePlayer + 2));
        B(0x48, 0xB8); I64(flagDef); B(0x83, 0x38, 0x00);
        int jeDone3 = c.Count; B(0x74, 0x00);
        B(0x8B, 0x02);                             // mov eax,[rdx]
        B(0x99);                                   // cdq
        B(0xBB); I32(divFactor);                   // mov ebx,4
        B(0xF7, 0xFB);                             // idiv ebx
        B(0x89, 0x02);                             // mov [rdx],eax
        B(0xC7, 0x42, 0x04); I32(0);
        B(0xC7, 0x42, 0x08); I32(0);
        B(0xC7, 0x42, 0x0C); I32(0);

        int done = c.Count;
        c[jeDone + 1] = (byte)(done - (jeDone + 2));
        c[jeDone3 + 1] = (byte)(done - (jeDone3 + 2));
        foreach (var j in new[] { jmpDone1, jmpDone2 })
        {
            int d = done - (j + 5);
            var b = BitConverter.GetBytes(d);
            c[j + 1] = b[0]; c[j + 2] = b[1]; c[j + 3] = b[2]; c[j + 4] = b[3];
        }

        B(0x59); B(0x5A); B(0x5B); B(0x58);        // pop rcx/rdx/rbx/rax
        foreach (var b in original) B(b);
        int jmpAt = c.Count; B(0xE9); I32(0);
        int back = (int)((long)(methodAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w)) return null;
        Patch(h, methodAddr, cave, n);
        Console.WriteLine("  damage 钩子: 0x" + methodAddr.ToString("X") + " → 0x" + cave.ToString("X")
                          + "（超伤 " + superValue + " / ×" + mulFactor + " / ÷" + divFactor + "）");
        return original;
    }

    // ---------------------------------------------------------------- ⑦ 改 decimal 参数（定值）
    // 适用于 LoseEnergy(decimal) / LoseStars(decimal) 这类"要扣多少"的方法
    // rcx = this，rdx = &amount(decimal)
    public static byte[] InstallDecimalConstHook(IntPtr h, ulong methodAddr, ulong thisAddr, ulong flagAddr, decimal value)
    {
        int[] parts = decimal.GetBits(value);
        var head = Read(h, methodAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到入口"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 序言无法覆盖: " + Hex(head)); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);
        ulong cave = AllocNear(h, methodAddr, 0x1000);
        if (cave == 0) return null;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x50);                                   // push rax
        B(0x48, 0xB8); I64(flagAddr);              // mov rax,&flag
        B(0x83, 0x38, 0x00);                       // cmp dword [rax],0
        int je1 = c.Count; B(0x74, 0x00);          // je 原样
        int je2 = -1;
        if (thisAddr != 0)
        {
            B(0x48, 0xB8); I64(thisAddr);          // mov rax,this
            B(0x48, 0x39, 0xC1);                   // cmp rcx,rax
            je2 = c.Count; B(0x75, 0x00);          // jne 原样
        }
        B(0xC7, 0x02); I32(parts[0]);              // mov dword [rdx],lo
        B(0xC7, 0x42, 0x04); I32(parts[1]);
        B(0xC7, 0x42, 0x08); I32(parts[2]);
        B(0xC7, 0x42, 0x0C); I32(parts[3]);
        int origAt = c.Count;
        c[je1 + 1] = (byte)(origAt - (je1 + 2));
        if (je2 >= 0) c[je2 + 1] = (byte)(origAt - (je2 + 2));
        B(0x58);                                   // pop rax
        foreach (var b in original) B(b);
        int jmpAt = c.Count; B(0xE9); I32(0);
        int back = (int)((long)(methodAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w)) return null;
        Patch(h, methodAddr, cave, n);
        Console.WriteLine("  decimal 参数钩子: 0x" + methodAddr.ToString("X") + "（amount=" + value + "）");
        return original;
    }

    // ---------------------------------------------------------------- ⑧ Hook::ModifyDamage（原版 Plus20 的挂法）
    // 签名（含 decimal 返回，故 rcx 是隐藏返回缓冲）：
    //   rcx = &ret(decimal)   rdx = IRunState   r8 = ICombatState   r9 = target(受伤者)
    //   栈：[rsp+0x28]=dealer  [rsp+0x30]=&damage(decimal)  [rsp+0x38]=props ...
    // 逻辑：target == 玩家 → 可 ÷N（防御倍率）；否则 → 9999（超级伤害）或 ×N（伤害倍率）
    public static byte[] InstallModifyDamageHook(IntPtr h, ulong methodAddr, ulong playerCreature,
                                                 ulong flagSuper, ulong flagMul, ulong flagDef,
                                                 int superValue = 9999, int mulFactor = 10, int divFactor = 4)
    {
        var head = Read(h, methodAddr, 16);
        if (head == null) { Console.WriteLine("  ✗ 读不到 ModifyDamage 入口"); return null; }
        if (head[0] == 0xE9) { Console.WriteLine("  ✗ ModifyDamage 已被占用（残留补丁）"); return null; }
        int n = PrologueLen(head);
        if (n < 5) { Console.WriteLine("  ✗ 序言无法覆盖: " + Hex(head)); return null; }
        var original = new byte[n];
        Array.Copy(head, 0, original, 0, n);
        ulong cave = AllocNear(h, methodAddr, 0x1000);
        if (cave == 0) return null;

        var c = new List<byte>();
        void B(params byte[] bs) => c.AddRange(bs);
        void I32(int v) => c.AddRange(BitConverter.GetBytes(v));
        void I64(ulong v) => c.AddRange(BitConverter.GetBytes(v));

        B(0x50); B(0x53); B(0x51); B(0x52); B(0x41, 0x50); B(0x41, 0x51);   // push rax,rbx,rcx,rdx,r8,r9
        B(0x4C, 0x8B, 0x44, 0x24, 0x60);          // mov r8,[rsp+0x60]   ← &damage（6×8+0x30）
        B(0x4D, 0x85, 0xC0);                      // test r8,r8
        int jeNull = c.Count; B(0x74, 0x00);      // je done
        B(0x48, 0x8B, 0x1C, 0x24);                // mov rbx,[rsp]       ← 保存的 r9 = target
        B(0x48, 0xB9); I64(playerCreature);       // mov rcx,玩家
        B(0x48, 0x39, 0xCB);                      // cmp rbx,rcx
        int jePlayer = c.Count; B(0x74, 0x00);    // je 玩家侧

        // 敌方侧：超级伤害
        B(0x48, 0xB9); I64(flagSuper); B(0x83, 0x39, 0x00);   // cmp dword [rcx],0
        int jeNoSuper = c.Count; B(0x74, 0x00);               // je 看倍率
        B(0x41, 0xC7, 0x00); I32(superValue);                 // mov dword [r8],9999
        B(0x41, 0xC7, 0x40, 0x04); I32(0);
        B(0x41, 0xC7, 0x40, 0x08); I32(0);
        B(0x41, 0xC7, 0x40, 0x0C); I32(0);
        int jmpDone1 = c.Count; B(0xE9, 0, 0, 0, 0);

        int mulAt = c.Count;
        c[jeNoSuper + 1] = (byte)(mulAt - (jeNoSuper + 2));
        B(0x48, 0xB9); I64(flagMul); B(0x83, 0x39, 0x00);
        int jeNoMul = c.Count; B(0x74, 0x00);
        B(0x41, 0x8B, 0x00);                       // mov eax,[r8]
        B(0x69, 0xC0); I32(mulFactor);             // imul eax,eax,N
        B(0x41, 0x89, 0x00);                       // mov [r8],eax
        B(0x41, 0xC7, 0x40, 0x04); I32(0);
        B(0x41, 0xC7, 0x40, 0x08); I32(0);
        B(0x41, 0xC7, 0x40, 0x0C); I32(0);
        int jmpDone2 = c.Count; B(0xE9, 0, 0, 0, 0);

        // 玩家侧：防御倍率 ÷N
        int playerAt = c.Count;
        c[jePlayer + 1] = (byte)(playerAt - (jePlayer + 2));
        B(0x48, 0xB9); I64(flagDef); B(0x83, 0x39, 0x00);
        int jeDone = c.Count; B(0x74, 0x00);
        B(0x41, 0x8B, 0x00);                       // mov eax,[r8]
        B(0x99);                                   // cdq
        B(0xB9); I32(divFactor);                   // mov ecx,N   （rcx 已入栈，可随便用）
        B(0xF7, 0xF9);                             // idiv ecx
        B(0x41, 0x89, 0x00);                       // mov [r8],eax
        B(0x41, 0xC7, 0x40, 0x04); I32(0);
        B(0x41, 0xC7, 0x40, 0x08); I32(0);
        B(0x41, 0xC7, 0x40, 0x0C); I32(0);

        int done = c.Count;
        c[jeNull + 1] = (byte)(done - (jeNull + 2));
        c[jeNoMul + 1] = (byte)(done - (jeNoMul + 2));
        c[jeDone + 1] = (byte)(done - (jeDone + 2));
        foreach (var j in new[] { jmpDone1, jmpDone2 })
        {
            var b = BitConverter.GetBytes(done - (j + 5));
            c[j + 1] = b[0]; c[j + 2] = b[1]; c[j + 3] = b[2]; c[j + 4] = b[3];
        }

        B(0x41, 0x59); B(0x41, 0x58); B(0x5A); B(0x59); B(0x5B); B(0x58);   // pop r9,r8,rdx,rcx,rbx,rax
        foreach (var b in original) B(b);
        int jmpAt = c.Count; B(0xE9); I32(0);
        int back = (int)((long)(methodAddr + (ulong)n) - (long)(cave + (ulong)jmpAt + 5));
        var rb = BitConverter.GetBytes(back);
        c[jmpAt + 1] = rb[0]; c[jmpAt + 2] = rb[1]; c[jmpAt + 3] = rb[2]; c[jmpAt + 4] = rb[3];

        IntPtr w;
        if (!WriteProcessMemory(h, (IntPtr)(long)cave, c.ToArray(), (IntPtr)c.Count, out w)) return null;
        Patch(h, methodAddr, cave, n);
        Console.WriteLine("  ModifyDamage 钩子: 0x" + methodAddr.ToString("X") + " → 0x" + cave.ToString("X")
                          + "（超伤 " + superValue + " / ×" + mulFactor + " / ÷" + divFactor + "）");
        return original;
    }

    // ---------------------------------------------------------------- 还原
    public static void Restore(IntPtr h, ulong addr, byte[] original)
    {
        if (original == null || addr == 0) return;
        int old; IntPtr w;
        VirtualProtectEx(h, (IntPtr)(long)addr, (IntPtr)original.Length, PAGE_EXECUTE_READWRITE, out old);
        WriteProcessMemory(h, (IntPtr)(long)addr, original, (IntPtr)original.Length, out w);
        int tmp; VirtualProtectEx(h, (IntPtr)(long)addr, (IntPtr)original.Length, old, out tmp);
        FlushInstructionCache(h, (IntPtr)(long)addr, (IntPtr)original.Length);
    }
}
