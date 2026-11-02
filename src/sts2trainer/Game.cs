// ============================================================================
//  Game.cs —— 用 ClrMD 摸清游戏内部（只读层，不改任何东西）
//
//  铁律：ClrMD 报的 ClrInstanceField.Offset 要 **+8** 才是真实偏移
//        （对象 +0x00 是 MethodTable，+0x08 才是第一个字段）
//        0xFFFFFFFFFFFFFFFF = "这个方法还没 JIT"，不是地址
// ============================================================================
using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

class Game
{
    // ---- 类型全名（从反编译源码抄，注意命名空间大小写） ----
    public const string T_PLAYER = "MegaCrit.Sts2.Core.Entities.Players.Player";
    public const string T_CREATURE = "MegaCrit.Sts2.Core.Entities.Creatures.Creature";
    public const string T_PCS = "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState";
    public const string T_HPBAR = "MegaCrit.sts2.Core.Nodes.TopBar.NTopBarHp";
    public const string T_GOLDBAR = "MegaCrit.sts2.Core.Nodes.TopBar.NTopBarGold";
    public const string T_ENERGY = "MegaCrit.sts2.Core.Nodes.Combat.NEnergyCounter";
    public const string T_STAR = "MegaCrit.sts2.Core.Nodes.Combat.NStarCounter";
    public const string T_TARGETMGR = "MegaCrit.Sts2.Core.Nodes.Combat.NTargetManager";
    public const string T_POTIONODDS = "MegaCrit.Sts2.Core.Odds.PotionRewardOdds";
    public const string T_UNKNOWNODDS = "MegaCrit.Sts2.Core.Odds.UnknownMapPointOdds";
    public const string T_RARITYODDS = "MegaCrit.Sts2.Core.Odds.CardRarityOdds";
    public const string T_HOOK = "MegaCrit.Sts2.Core.Hooks.Hook";
    public const string T_ENCOUNTER = "MegaCrit.Sts2.Core.Models.EncounterModel";
    public const string T_SCOREUTIL = "MegaCrit.Sts2.Core.Runs.ScoreUtility";
    public const string T_ENGINE = "Godot.Engine";

    // ---- 真实偏移 ----
    public const int OFF_HP = 0x84, OFF_MAXHP = 0x88, OFF_BLOCK = 0x80;
    public const int OFF_PLAYER_CREATURE = 0x68, OFF_PLAYER_GOLD = 0xE8, OFF_PLAYER_PCS = 0x98;
    public const int OFF_PCS_ENERGY = 0x68, OFF_PCS_STARS = 0x6C, OFF_PCS_STARSCHANGED = 0x30;
    public const int OFF_PLAYER_GOLDCHANGED = 0x40;
    public const int OFF_BAR_PLAYER = 0x58;
    public const int OFF_COUNTER_PLAYER = 0x20;
    public const int OFF_GOLDBAR_CURGOLD = 0x54;

    public DataTarget Target;
    public ClrRuntime Runtime;
    public Dictionary<string, List<ClrObject>> Heap = new();

    public ulong Player, Creature, Pcs;
    public ulong HpBar, GoldBar, EnergyUi, StarUi;

    // 方法地址（0 = 拿不到；未 JIT 的要靠延迟安装）
    public ulong FnUpdateHealth, FnUpdateGold, FnRefreshLabel, FnStarVisible;
    public ulong FnSetBlock, FnProcess;
    public ulong FnSetCurrentHp, FnSetGold, FnLoseEnergy, FnLoseStars;
    public ulong FnModifyDamage1, FnModifyDamage2;   // 原版挂的两个重载
    public ulong FnPotionRoll, FnUpgradeOdds, FnRarityOdds, FnUnknownRoll;
    public ulong FnExtraTurn, FnLoseHp;
    public ulong FnMinGold, FnMaxGold, FnMerchantPrice, FnMaxEnergy;
    public ulong FnSetPotionSlots, FnScore1, FnScore2, FnTimeScale;

    public bool Attach(int pid)
    {
        Target = DataTarget.AttachToProcess(pid, suspend: false);
        Runtime = Target.ClrVersions[0].CreateRuntime();
        Console.WriteLine("  CLR = " + Target.ClrVersions[0].Version);
        foreach (var obj in Runtime.Heap.EnumerateObjects())
        {
            var t = obj.Type;
            if (t == null || t.Name == null) continue;
            if (!Heap.TryGetValue(t.Name, out var l)) { l = new List<ClrObject>(); Heap[t.Name] = l; }
            l.Add(obj);
        }
        Console.WriteLine("  堆里 " + Heap.Count + " 种类型");
        return true;
    }

    public List<ClrObject> Of(string typeName)
        => Heap.TryGetValue(typeName, out var l) ? l : new List<ClrObject>();

    public long ReadI64(ulong addr)
    {
        var b = new byte[8];
        return Target.DataReader.Read(addr, b) == 8 ? BitConverter.ToInt64(b, 0) : 0;
    }

    public int ReadI32(ulong addr)
    {
        var b = new byte[4];
        return Target.DataReader.Read(addr, b) == 4 ? BitConverter.ToInt32(b, 0) : -1;
    }

    public bool Resolve()
    {
        if (!Heap.TryGetValue(T_PLAYER, out var players) || players.Count == 0) return false;
        Player = players[0].Address;
        Creature = (ulong)ReadI64(Player + OFF_PLAYER_CREATURE);
        Pcs = (ulong)ReadI64(Player + OFF_PLAYER_PCS);
        if (Creature < 0x10000 || Pcs < 0x10000) return false;

        HpBar = NodeOwned(T_HPBAR, OFF_BAR_PLAYER);
        GoldBar = NodeOwned(T_GOLDBAR, OFF_BAR_PLAYER);
        EnergyUi = NodeOwned(T_ENERGY, OFF_COUNTER_PLAYER);
        StarUi = NodeOwned(T_STAR, OFF_COUNTER_PLAYER);

        // ---- 1~5：HUD ----
        FnUpdateHealth = Method(T_HPBAR, "UpdateHealth");
        FnUpdateGold = Method(T_GOLDBAR, "UpdateGold");
        if (FnUpdateGold == 0) FnUpdateGold = DelegateFn(Player, OFF_PLAYER_GOLDCHANGED);
        FnRefreshLabel = Method(T_ENERGY, "RefreshLabel");
        FnStarVisible = Method(T_STAR, "RefreshVisibility");
        FnSetBlock = Method(T_CREATURE, "set_Block");
        FnProcess = Method(T_TARGETMGR, "_Process");

        // ---- ★ 原版 Plus20 用的那套（setter / 扣减方法），比 HUD 刷新更可靠 ----
        FnSetCurrentHp = Method(T_CREATURE, "set_CurrentHp");
        FnSetGold = Method(T_PLAYER, "set_Gold");
        FnLoseEnergy = Method(T_PCS, "LoseEnergy");
        FnLoseStars = Method(T_PCS, "LoseStars");

        // ---- 6~10：奖励与行为（bool/int/enum/float/decimal 返回） ----
        FnPotionRoll = Method(T_POTIONODDS, "Roll");
        FnUpgradeOdds = Method(T_HOOK, "ModifyCardRewardUpgradeOdds");
        FnRarityOdds = Method(T_RARITYODDS, "GetBaseOdds");
        FnUnknownRoll = Method(T_UNKNOWNODDS, "Roll");
        FnExtraTurn = Method(T_HOOK, "ShouldTakeExtraTurn");
        // ★ 原版 Plus20 的伤害三项挂这里（两个重载都要）
        var md = Methods(T_HOOK, "ModifyDamage");
        if (md.Count > 0) FnModifyDamage1 = md[0];
        if (md.Count > 1) FnModifyDamage2 = md[1];

        // ---- 11~13：伤害（同一个方法，三种玩法） ----
        FnLoseHp = Method(T_CREATURE, "LoseHpInternal");
        if (FnLoseHp == 0) FnLoseHp = Method(T_CREATURE, "LoseHp");

        // ---- 14~17：经济与上限 ----
        FnMinGold = Method(T_ENCOUNTER, "get_MinGoldReward");
        FnMaxGold = Method(T_ENCOUNTER, "get_MaxGoldReward");
        FnMerchantPrice = Method(T_HOOK, "ModifyMerchantPrice");
        FnMaxEnergy = Method(T_HOOK, "ModifyMaxEnergy");
        FnSetPotionSlots = Method(T_PLAYER, "SetMaxPotionCountInternal");

        // ---- 18~20：结算与引擎 ----
        var scores = Methods(T_SCOREUTIL, "CalculateScore");
        if (scores.Count > 0) FnScore1 = scores[0];
        if (scores.Count > 1) FnScore2 = scores[1];
        FnTimeScale = Method(T_ENGINE, "set_TimeScale");
        return true;
    }

    ulong NodeOwned(string typeName, int playerOffset)
    {
        foreach (var obj in Of(typeName))
        {
            var t = obj.Type;
            if (t == null || t.Name != typeName) continue;
            if ((ulong)ReadI64(obj.Address + (ulong)playerOffset) == Player) return obj.Address;
        }
        return 0;
    }

    public ulong Method(string typeName, string methName)
    {
        var list = Methods(typeName, methName);
        return list.Count > 0 ? list[0] : 0;
    }

    readonly Dictionary<string, ClrType> typeCache = new();

    /// <summary>取 ClrType：先生效堆里找实例；静态类（Hook / ScoreUtility）靠模块类型表兜底</summary>
    public ClrType TypeOf(string typeName)
    {
        if (typeCache.TryGetValue(typeName, out var t) && t != null) return t;
        t = null;
        if (Heap.TryGetValue(typeName, out var l) && l.Count > 0) t = l[0].Type;
        if (t == null)
            foreach (var m in Runtime.EnumerateModules())
            {
                t = m.GetTypeByName(typeName);
                if (t != null) break;
            }
        typeCache[typeName] = t;
        return t;
    }

    /// <summary>清掉类型缓存（延迟安装时用，逼 ClrMD 重新取一遍 NativeCode）</summary>
    public void InvalidateTypes() => typeCache.Clear();

    /// <summary>返回所有同名重载的方法地址（重载要全挂）</summary>
    public List<ulong> Methods(string typeName, string methName)
    {
        var res = new List<ulong>();
        var t = TypeOf(typeName);
        if (t == null) return res;
        foreach (var m in t.Methods)
        {
            if (m.Name != methName) continue;
            if (m.NativeCode > 0x10000 && m.NativeCode != ulong.MaxValue) res.Add((ulong)m.NativeCode);
        }
        return res;
    }

    /// <summary>从事件委托里抠方法入口（ClrMD 报 0xFFFF 时的备用法门）</summary>
    public ulong DelegateFn(ulong owner, int fieldOffset)
    {
        ulong d = (ulong)ReadI64(owner + (ulong)fieldOffset);
        if (d < 0x10000) return 0;
        ulong mptr = (ulong)ReadI64(d + 0x10);
        ulong aux = (ulong)ReadI64(d + 0x18);
        return mptr != 0 ? mptr : aux;
    }

    public void Dump()
    {
        Console.WriteLine("  Player   = 0x" + Player.ToString("X") + "  Creature = 0x" + Creature.ToString("X")
                          + "  hp=" + ReadI32(Creature + OFF_HP) + "/" + ReadI32(Creature + OFF_MAXHP)
                          + "  block=" + ReadI32(Creature + OFF_BLOCK));
        Console.WriteLine("  Combat   = 0x" + Pcs.ToString("X") + "  energy=" + ReadI32(Pcs + OFF_PCS_ENERGY)
                          + "  stars=" + ReadI32(Pcs + OFF_PCS_STARS) + "  gold=" + ReadI32(Player + OFF_PLAYER_GOLD));
        Console.WriteLine("  UI       = hp:0x" + HpBar.ToString("X") + " gold:0x" + GoldBar.ToString("X")
                          + " energy:0x" + EnergyUi.ToString("X") + " star:0x" + StarUi.ToString("X"));
        string M(string n, ulong a) => n + "=" + (a >= 0x10000 ? "0x" + a.ToString("X") : "未JIT");
        Console.WriteLine("  方法     : " + string.Join(" ", new[] {
            M("UpdateHealth", FnUpdateHealth), M("UpdateGold", FnUpdateGold), M("RefreshLabel", FnRefreshLabel),
            M("StarVisible", FnStarVisible), M("set_Block", FnSetBlock), M("_Process", FnProcess) }));
        Console.WriteLine("  原版套 : " + string.Join(" ", new[] {
            M("set_CurrentHp", FnSetCurrentHp), M("set_Gold", FnSetGold),
            M("LoseEnergy", FnLoseEnergy), M("LoseStars", FnLoseStars) }));
        Console.WriteLine("  方法     : " + string.Join(" ", new[] {
            M("MinGold", FnMinGold), M("MaxGold", FnMaxGold), M("MerchantPrice", FnMerchantPrice),
            M("MaxEnergy", FnMaxEnergy), M("SetPotionSlots", FnSetPotionSlots),
            M("Score1", FnScore1), M("Score2", FnScore2), M("TimeScale", FnTimeScale) }));
    }
}
