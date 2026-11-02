// ============================================================================
//  sts2trainer —— 杀戮尖塔 2 修改器（20 功能版）
//
//  用法： sts2trainer.exe <游戏pid> [能量锁值] [初始开关掩码]
//     例： sts2trainer.exe 1234 4 1048575      （掩码全 1 = 20 个功能全开）
//
//  热键（对齐原版 Plus20 的编号习惯）：
//      Num 1 无限血      Num 2 无限护盾     Num 3 无限能量    Num 4 无限星星
//      Num 5 奖励必给药水  Num 6 奖励卡必升级  Num 7 奖励最高稀有度  Num 8 未知点必宝箱
//      Num 9 敌人不额外行动 Num 0 超级伤害   Num . 伤害倍率     Num + 防御倍率
//      Ctrl+Num 1 金币锁  Ctrl+Num 2 金币倍率  Ctrl+Num 3 商店免费  Ctrl+Num 4 最大能量
//      Ctrl+Num 5 药水槽   Ctrl+Num 6 最高分数  Ctrl+Num 8 游戏速度(2x)
//      Q 退出（自动还原所有补丁）
//
//  结构：Game.cs 只读侦查 / Hook.cs 注入 / Program.cs 功能表 + 热键
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

class Prog
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    const int VK_CONTROL = 0x11;
    const int VK_NUMPAD0 = 0x60, VK_NUMPAD1 = 0x61, VK_NUMPAD2 = 0x62, VK_NUMPAD3 = 0x63,
              VK_NUMPAD4 = 0x64, VK_NUMPAD5 = 0x65, VK_NUMPAD6 = 0x66, VK_NUMPAD7 = 0x67,
              VK_NUMPAD8 = 0x68, VK_NUMPAD9 = 0x69, VK_DECIMAL = 0x6E, VK_ADD = 0x6B;
    const int VK_Q = 0x51;

    // 目标值
    const int HP_VALUE = 9999, SHIELD_VALUE = 9999, GOLD_VALUE = 9999999, STAR_VALUE = 99;
    const int GOLD_REWARD = 2000, POTION_SLOTS = 20, SCORE_VALUE = 999999;
    const decimal MAX_ENERGY = 10m, UPGRADE_ODDS = 1m, MERCHANT_FREE = 0m;
    const float RARITY_ODDS = 999f;
    const int TREASURE_ROOM = 3;
    const double TIME_SCALE = 2.0;

    class Feat
    {
        public string Name;
        public int Vk;
        public bool Ctrl;
        public int Flag;            // 开关变量下标
        public bool On;
        public ulong Addr;          // 被挂钩的方法地址（还原用）
        public byte[] Orig;         // 原字节（还原用）
        public string Note;         // 未安装原因 / 状态
    }

    static List<Feat> feats = new();
    static IntPtr h;
    static ulong flags;
    static Game g;

    static ulong F(int i) => flags + (ulong)(i * 4);

    static void Main(string[] args)
    {
        Console.Title = "STS2 Trainer — 20 features";
        if (args.Length < 1)
        {
            Console.WriteLine("用法: sts2trainer.exe <游戏pid> [能量锁值] [初始开关掩码]");
            Console.WriteLine("      掩码: 1=无限血 2=金币锁 4=护盾 8=能量 16=星 32=药水 64=卡升级 128=稀有度");
            Console.WriteLine("            256=未知点 512=敌人不动 1024=超伤 2048=伤害倍率 4096=防御倍率");
            Console.WriteLine("            8192=金币倍率 16384=商店免费 32768=最大能量 65536=药水槽");
            Console.WriteLine("            131072=分数 262144=游戏速度");
            Console.WriteLine("      不填掩码 = 前 5 个功能开");
            return;
        }
        int pid = int.Parse(args[0]);
        int energyLock = args.Length > 1 ? int.Parse(args[1]) : 4;
        int mask = args.Length > 2 ? Convert.ToInt32(args[2]) : (1 | 2 | 4 | 8 | 16);

        Console.WriteLine("[1] 附加游戏 pid=" + pid);
        g = new Game();
        if (!g.Attach(pid)) return;

        Console.WriteLine("[2] 找对象 / 字段 / 方法");
        if (!g.Resolve())
        {
            Console.WriteLine("  ✗ 没找到玩家（先进战斗，让血量有数字）");
            return;
        }
        g.Dump();

        h = Hook.Open(pid);
        if (h == IntPtr.Zero) { Console.WriteLine("  ✗ OpenProcess 失败（用管理员运行）"); return; }
        flags = Hook.AllocFlags(h, 24);
        Hook.WriteI32(h, g.Creature + Game.OFF_MAXHP, HP_VALUE);

        Console.WriteLine("[3] 装配 20 个功能");
        BuildFeatures(energyLock);
        InstallAll(mask);

        Console.WriteLine();
        Console.WriteLine("=================== 功能表 ===================");
        for (int i = 0; i < feats.Count; i++)
        {
            var f = feats[i];
            string key = (f.Ctrl ? "Ctrl+Num " : "Num ") + KeyName(f.Vk);
            Console.WriteLine(string.Format("  {0,-13} {1,-16} [{2}] {3}",
                key, f.Name, f.On ? "开" : "关", f.Note ?? ""));
        }
        Console.WriteLine("  Q            退出（还原所有补丁）");
        Console.WriteLine("=============================================");

        var wasDown = new bool[feats.Count];
        long lastRetry = 0;
        while (true)
        {
            if (Down(VK_Q)) break;

            // 延迟安装：未 JIT 的方法，每 3 秒重试一次
            if (Environment.TickCount64 - lastRetry > 3000)
            {
                lastRetry = Environment.TickCount64;
                foreach (var f in feats)
                    if (f.Orig == null && f.Note != null && f.Note.Contains("未JIT"))
                        TryInstall(f);
            }

            for (int i = 0; i < feats.Count; i++)
            {
                var f = feats[i];
                bool down = Down(f.Vk) && (!f.Ctrl || Down(VK_CONTROL));
                if (down && !wasDown[i])
                {
                    if (f.Orig == null) { Console.WriteLine("[!] " + f.Name + " 未安装：" + (f.Note ?? "地址拿不到")); }
                    else
                    {
                        f.On = !f.On;
                        Hook.WriteI32(h, F(f.Flag), f.On ? 1 : 0);
                        Console.WriteLine((f.On ? "[开] " : "[关] ") + f.Name);
                    }
                }
                wasDown[i] = down;
            }
            Thread.Sleep(40);
        }

        Console.WriteLine("退出：还原 " + feats.Count + " 个补丁…");
        foreach (var f in feats) Hook.Restore(h, f.Addr, f.Orig);
        Console.WriteLine("已还原。");
    }

    static string KeyName(int vk) => vk switch
    {
        VK_NUMPAD0 => "0", VK_NUMPAD1 => "1", VK_NUMPAD2 => "2", VK_NUMPAD3 => "3", VK_NUMPAD4 => "4",
        VK_NUMPAD5 => "5", VK_NUMPAD6 => "6", VK_NUMPAD7 => "7", VK_NUMPAD8 => "8", VK_NUMPAD9 => "9",
        VK_DECIMAL => ".", VK_ADD => "+", _ => vk.ToString()
    };

    // ---------------------------------------------------------------- 功能表
    static void BuildFeatures(int energyLock)
    {
        energyLockValue = energyLock;
        feats = new List<Feat>
        {
            new Feat{ Name="无限血",         Vk=VK_NUMPAD1, Flag=0  },
            new Feat{ Name="金币锁",         Vk=VK_NUMPAD1, Ctrl=true, Flag=1 },
            new Feat{ Name="无限护盾",       Vk=VK_NUMPAD2, Flag=2  },
            new Feat{ Name="无限能量",       Vk=VK_NUMPAD3, Flag=3  },
            new Feat{ Name="无限星星",       Vk=VK_NUMPAD4, Flag=4  },
            new Feat{ Name="奖励必给药水",   Vk=VK_NUMPAD5, Flag=5  },
            new Feat{ Name="奖励卡必升级",   Vk=VK_NUMPAD6, Flag=6  },
            new Feat{ Name="奖励最高稀有度", Vk=VK_NUMPAD7, Flag=7  },
            new Feat{ Name="未知点必宝箱",   Vk=VK_NUMPAD8, Flag=8  },
            new Feat{ Name="敌人不额外行动", Vk=VK_NUMPAD9, Flag=9  },
            new Feat{ Name="超级伤害",       Vk=VK_NUMPAD0, Flag=10 },
            new Feat{ Name="伤害倍率x10",    Vk=VK_DECIMAL, Flag=11 },
            new Feat{ Name="防御倍率/4",     Vk=VK_ADD,     Flag=12 },
            new Feat{ Name="金币倍率",       Vk=VK_NUMPAD2, Ctrl=true, Flag=13 },
            new Feat{ Name="商店免费",       Vk=VK_NUMPAD3, Ctrl=true, Flag=14 },
            new Feat{ Name="最大能量10",     Vk=VK_NUMPAD4, Ctrl=true, Flag=15 },
            new Feat{ Name="药水槽20",       Vk=VK_NUMPAD5, Ctrl=true, Flag=16 },
            new Feat{ Name="最高分数",       Vk=VK_NUMPAD6, Ctrl=true, Flag=17 },
            new Feat{ Name="游戏速度2x",     Vk=VK_NUMPAD8, Ctrl=true, Flag=18 },
        };
    }

    // ---------------------------------------------------------------- 安装
    static int energyLockValue = 4;

    static void InstallAll(int mask)
    {
        // ---- A. 帧钩子：血 / 金币 / 能量 / 星星 / 游戏速度（1 个挂点，多个动作） ----
        var acts = new List<Hook.Action>();
        if (g.FnProcess >= 0x10000)
        {
            acts.Add(new Hook.Action{ Name="无限血", FlagAddr=F(0), CheckAddr=g.Creature+Game.OFF_HP, Target=HP_VALUE,
                                      WriteAddr=g.Creature+Game.OFF_HP, CallThis=g.HpBar, CallFn=g.FnUpdateHealth });
            acts.Add(new Hook.Action{ Name="金币锁", FlagAddr=F(1), CheckAddr=g.Player+Game.OFF_PLAYER_GOLD, Target=GOLD_VALUE,
                                      WriteAddr=g.Player+Game.OFF_PLAYER_GOLD,
                                      PreAddr=g.GoldBar+Game.OFF_GOLDBAR_CURGOLD, PreValue=GOLD_VALUE-1,
                                      CallThis=g.GoldBar, CallFn=g.FnUpdateGold });
            acts.Add(new Hook.Action{ Name="无限能量", FlagAddr=F(3), CheckAddr=g.Pcs+Game.OFF_PCS_ENERGY, Target=energyLockValue,
                                      WriteAddr=g.Pcs+Game.OFF_PCS_ENERGY, CallThis=g.EnergyUi, CallFn=g.FnRefreshLabel });
            acts.Add(new Hook.Action{ Name="无限星星", FlagAddr=F(4), CheckAddr=g.Pcs+Game.OFF_PCS_STARS, Target=STAR_VALUE,
                                      WriteAddr=g.Pcs+Game.OFF_PCS_STARS, CallThis=g.StarUi, CallFn=g.FnStarVisible });
            if (g.FnTimeScale >= 0x10000)
                acts.Add(new Hook.Action{ Name="游戏速度", FlagAddr=F(18), CallFnXmm0=g.FnTimeScale,
                                          Xmm0Bits=(ulong)BitConverter.DoubleToInt64Bits(TIME_SCALE) });

            var orig = Hook.InstallFrameHook(h, g.FnProcess, acts, out _);
            Mark("无限血", g.FnProcess, orig); Mark("金币锁", g.FnProcess, orig);
            Mark("无限能量", g.FnProcess, orig); Mark("无限星星", g.FnProcess, orig);
            Mark("游戏速度2x", g.FnProcess, orig);
        }
        else { Note("无限血", "未JIT"); Note("金币锁", "未JIT"); Note("无限能量", "未JIT"); Note("无限星星", "未JIT"); }

        // ---- B. 护盾 setter ----
        if (g.FnSetBlock >= 0x10000) Set("无限护盾", g.FnSetBlock, Hook.InstallSetterHook(h, g.FnSetBlock, g.Creature, SHIELD_VALUE, F(2)));
        else Note("无限护盾", "未JIT");

        // ---- C. 药水 / 卡升级 / 稀有度 / 未知点 / 额外回合 ----
        Set("奖励必给药水", g.FnPotionRoll, g.FnPotionRoll >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnPotionRoll, F(5), 1) : null);
        Set("奖励卡必升级", g.FnUpgradeOdds, g.FnUpgradeOdds >= 0x10000 ? Hook.InstallForceDecimalReturnHook(h, g.FnUpgradeOdds, F(6), UPGRADE_ODDS) : null);
        Set("奖励最高稀有度", g.FnRarityOdds, g.FnRarityOdds >= 0x10000 ? Hook.InstallForceFloatReturnHook(h, g.FnRarityOdds, F(7), RARITY_ODDS) : null);
        Set("未知点必宝箱", g.FnUnknownRoll, g.FnUnknownRoll >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnUnknownRoll, F(8), TREASURE_ROOM) : null);
        Set("敌人不额外行动", g.FnExtraTurn, g.FnExtraTurn >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnExtraTurn, F(9), 0) : null);

        // ---- D. 伤害三合一 ----
        if (g.FnLoseHp >= 0x10000)
        {
            var dmg = Hook.InstallDamageHook(h, g.FnLoseHp, g.Creature, F(10), F(11), F(12));
            Mark("超级伤害", g.FnLoseHp, dmg);
            Mark("伤害倍率x10", g.FnLoseHp, dmg);
            Mark("防御倍率/4", g.FnLoseHp, dmg);
        }
        else { Note("超级伤害", "未JIT"); Note("伤害倍率x10", "未JIT"); Note("防御倍率/4", "未JIT"); }

        // ---- E. 经济与上限 ----
        Set("金币倍率", g.FnMinGold, g.FnMinGold >= 0x10000 ? Hook.InstallForceReturnHook(h, g.FnMinGold, F(13), GOLD_REWARD) : null);
        if (g.FnMaxGold >= 0x10000) Hook.InstallForceReturnHook(h, g.FnMaxGold, F(13), GOLD_REWARD);
        Set("商店免费", g.FnMerchantPrice, g.FnMerchantPrice >= 0x10000 ? Hook.InstallForceDecimalReturnHook(h, g.FnMerchantPrice, F(14), MERCHANT_FREE) : null);
        Set("最大能量10", g.FnMaxEnergy, g.FnMaxEnergy >= 0x10000 ? Hook.InstallForceDecimalReturnHook(h, g.FnMaxEnergy, F(15), MAX_ENERGY) : null);
        Set("药水槽20", g.FnSetPotionSlots, g.FnSetPotionSlots >= 0x10000 ? Hook.InstallSetterHook(h, g.FnSetPotionSlots, 0, POTION_SLOTS, F(16)) : null);

        // ---- F. 分数（两个重载都挂） ----
        if (g.FnScore1 >= 0x10000) Set("最高分数", g.FnScore1, Hook.InstallForceReturnHook(h, g.FnScore1, F(17), SCORE_VALUE));
        else Note("最高分数", "未JIT");
        if (g.FnScore2 >= 0x10000) Hook.InstallForceReturnHook(h, g.FnScore2, F(17), SCORE_VALUE);

        // ---- 应用掩码（开关初值） ----
        foreach (var f in feats)
        {
            f.On = f.Orig != null && (mask & (1 << f.Flag)) != 0;
            Hook.WriteI32(h, F(f.Flag), f.On ? 1 : 0);
        }
    }

    static void Set(string name, ulong addr, byte[] orig)
    {
        var f = feats.Find(x => x.Name == name);
        if (f == null) return;
        if (orig == null) { f.Note = addr >= 0x10000 ? "安装失败" : "未JIT"; return; }
        f.Addr = addr; f.Orig = orig; f.Note = "";
    }

    static void Mark(string name, ulong addr, byte[] orig)
    {
        var f = feats.Find(x => x.Name == name);
        if (f == null) return;
        if (orig == null) { f.Note = "安装失败"; return; }
        f.Addr = addr; f.Orig = orig; f.Note = "";
    }

    static void Note(string name, string note)
    {
        var f = feats.Find(x => x.Name == name);
        if (f != null) f.Note = note;
    }

    static void TryInstall(Feat f)
    {
        // 只重试"未JIT"的：重新解析地址后再装一次
        g.Resolve();
        ulong addr = 0;
        if (f.Name == "奖励必给药水") addr = g.FnPotionRoll = g.Method(Game.T_POTIONODDS, "Roll");
        else if (f.Name == "奖励卡必升级") addr = g.FnUpgradeOdds = g.Method(Game.T_HOOK, "ModifyCardRewardUpgradeOdds");
        else if (f.Name == "奖励最高稀有度") addr = g.FnRarityOdds = g.Method(Game.T_RARITYODDS, "GetBaseOdds");
        else if (f.Name == "未知点必宝箱") addr = g.FnUnknownRoll = g.Method(Game.T_UNKNOWNODDS, "Roll");
        else if (f.Name == "敌人不额外行动") addr = g.FnExtraTurn = g.Method(Game.T_HOOK, "ShouldTakeExtraTurn");
        else if (f.Name == "最高分数") addr = g.FnScore1 = g.Method(Game.T_SCOREUTIL, "CalculateScore");
        else if (f.Name == "药水槽20") addr = g.FnSetPotionSlots = g.Method(Game.T_PLAYER, "SetMaxPotionCountInternal");
        else if (f.Name == "商店免费") addr = g.FnMerchantPrice = g.Method(Game.T_HOOK, "ModifyMerchantPrice");
        else if (f.Name == "最大能量10") addr = g.FnMaxEnergy = g.Method(Game.T_HOOK, "ModifyMaxEnergy");
        else if (f.Name == "金币倍率") addr = g.FnMinGold = g.Method(Game.T_ENCOUNTER, "get_MinGoldReward");
        if (addr < 0x10000) return;

        byte[] orig = null;
        switch (f.Name)
        {
            case "奖励必给药水": orig = Hook.InstallForceReturnHook(h, addr, F(f.Flag), 1); break;
            case "奖励卡必升级": orig = Hook.InstallForceDecimalReturnHook(h, addr, F(f.Flag), UPGRADE_ODDS); break;
            case "奖励最高稀有度": orig = Hook.InstallForceFloatReturnHook(h, addr, F(f.Flag), RARITY_ODDS); break;
            case "未知点必宝箱": orig = Hook.InstallForceReturnHook(h, addr, F(f.Flag), TREASURE_ROOM); break;
            case "敌人不额外行动": orig = Hook.InstallForceReturnHook(h, addr, F(f.Flag), 0); break;
            case "最高分数": orig = Hook.InstallForceReturnHook(h, addr, F(f.Flag), SCORE_VALUE); break;
            case "药水槽20": orig = Hook.InstallSetterHook(h, addr, 0, POTION_SLOTS, F(f.Flag)); break;
            case "商店免费": orig = Hook.InstallForceDecimalReturnHook(h, addr, F(f.Flag), MERCHANT_FREE); break;
            case "最大能量10": orig = Hook.InstallForceDecimalReturnHook(h, addr, F(f.Flag), MAX_ENERGY); break;
            case "金币倍率": orig = Hook.InstallForceReturnHook(h, addr, F(f.Flag), GOLD_REWARD); break;
        }
        if (orig != null)
        {
            f.Addr = addr; f.Orig = orig; f.Note = "";
            Hook.WriteI32(h, F(f.Flag), f.On ? 1 : 0);
            Console.WriteLine("[✓] 延迟安装成功：" + f.Name);
        }
    }
}
