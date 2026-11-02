---
title: 29 · 踩坑-五个真坑：静态类查不到 / 残留补丁 / 未JIT / 只写值 / decimal 参数右移
aliases: [踩坑, 静态类, 残留补丁, 未JIT, ModifyDamage ABI]
tags: [踩坑, 杀戮尖塔2, ClrMD, 落地, 排错]
created: 2026-11-03
---

上接：[[08-教程-20个功能总纲与方法论]] · 下接：[[19-复刻-超级伤害]]

# 29 · 五个真坑（做成品 trainer 时才暴露出来的）

> [!important] 这篇是"把教程跑成成品"过程中撞出来的坑，全部有现场证据
> 前 20 篇教程讲的是"原理与挂法"，但真做一个能用的 trainer 时，会在这五处卡住。
> 每一条都给出：**现象 → 为什么 → 怎么改 → 怎么自查**。

---

## 坑 1 ★ 静态类的方法，堆里查不到（影响最大）

### 现象

`Hook::ModifyDamage`、`Hook::ModifyCardRewardUpgradeOdds`、`Hook::ShouldTakeExtraTurn`、
`Hook::ModifyMerchantPrice`、`Hook::ModifyMaxEnergy`、`ScoreUtility::CalculateScore`
——这些方法明明存在，`methods` 命令却**一个都查不出来**，trainer 里全显示"未JIT"。

### 为什么

```csharp
// 我原来的取法：从"堆里该类型的第一个实例"拿到 ClrType
if (!Heap.TryGetValue(typeName, out var list) || list.Count == 0) return 结果为空;
foreach (var m in list[0].Type.Methods) { ... }
```

`MegaCrit.Sts2.Core.Hooks.Hook` 是 **static class**，`MegaCrit.Sts2.Core.Runs.ScoreUtility` 也是
⇒ **它们永远不会有实例** ⇒ `Heap[typeName]` 是空的 ⇒ 拿不到 `ClrType` ⇒ 查不到方法。

### 怎么改（关键三行）

```csharp
ClrType TypeOf(string typeName)
{
    if (Heap.TryGetValue(typeName, out var l) && l.Count > 0) return l[0].Type;   // ① 有实例就用实例
    foreach (var m in Runtime.EnumerateModules())                                // ② 没有就查模块类型表
    {
        var t = m.GetTypeByName(typeName);
        if (t != null) return t;
    }
    return null;
}
```

> [!tip] 为什么"模块类型表"能查到
> `EnumerateModules()` 列出已加载的托管模块（这里就是 `sts2.dll`），
> `GetTypeByName()` 直接读**元数据类型定义表**——和有没有实例无关，静态类也在里面。

### 自查

```powershell
# 如果这条命令有输出，但你的程序查不到 ⇒ 就是踩了这个坑
dotnet run --project D:\sts2\clr\sts2clr -- <pid> allmethods MegaCrit.Sts2.Core.Hooks.Hook
```

真实输出（本机）：

```text
MegaCrit.Sts2.Core.Hooks.Hook::ModifyDamage native=0x7FF8E56DE460
MegaCrit.Sts2.Core.Hooks.Hook::ModifyDamageInternal native=0x7FF8E56DEAE0
```

---

## 坑 2 ★ 残留补丁：强杀 trainer 后，游戏里还留着 jmp

### 现象

新开 trainer，除了一两个功能，其余全部"待机/安装失败"；更奇怪的是**旧功能还在生效**。

### 为什么

内存补丁**不落盘**，但**也不会因为 trainer 退出而消失**。如果 trainer 是被强杀/崩溃的，
它没走到"还原"那一步 → 方法入口仍然是 `E9 <rel32>`（jmp 到已经没人管的代码洞）。

**现场证据**（只读 dump 一个正在跑的游戏进程）：

```text
Creature::set_Block       入口 = E9 AB E8 49 00 90 …     ← 已经被打过补丁
Creature::LoseHpInternal  入口 = E9 5B 7E 4C 00 56 …     ← 也被打过补丁
```

`E9` 就是 `jmp`。这时新实例读入口算"序言长度"会失败（第一条指令就是 5 字节 jmp），或者我们主动检测到 `E9` 就放弃。

### 怎么改

1. **检测**：装钩子前先看首字节，`0xE9` 就直接报"挂点已被占用"，不要硬写。
2. **提示**：让 UI 明确显示"挂点被占用 → 重启游戏"，而不是笼统"待机"。
3. **恢复**：正常关窗口时逐个 `Restore`（本项目已做）；强杀无法挽回，只能重启游戏。

```csharp
var head = Read(h, patchAddr, 16);
if (head[0] == 0xE9) { 提示("挂点已被占用，请重启游戏"); return null; }
```

> [!danger] 副作用：新旧补丁混在一起会"看起来能用一半"
> 旧补丁还在改血量，新补丁的"无限血"却没装上 —— 用户会以为"这个功能坏了"。
> 所以**一定要把原因显示出来**，别只说"待机"。

---

## 坑 3 ★ 未 JIT：.NET 是"用到才编译"

### 现象

新开一局，`PotionRewardOdds::Roll`、`CalculateScore`、`ShouldTakeExtraTurn` 全是"未JIT"。
过一会儿（打完一场 / 进奖励面板）它们就变成可用。

### 为什么

`NativeCode == 0xFFFFFFFFFFFFFFFF` 就是"这个方法还没被编译成机器码"。
奖励、结算、敌人回合调度这类方法，**只有走到那个流程才会第一次 JIT**。

### 怎么改：延迟安装 + 清缓存重试

```csharp
// 主循环里每 3 秒扫一遍"还没装上的"
if (Environment.TickCount64 - lastRetry > 3000) {
    lastRetry = Environment.TickCount64;
    g.InvalidateTypes();          // ★ 关键：清掉 ClrType 缓存，否则一直读到旧的 0xFFFF
    g.Resolve();
    foreach (var f in feats) if (!f.Ready) TryInstall(f);
}
```

> [!warning] 不清缓存会怎样
> `ClrType` 会被缓存，`ClrMethod.NativeCode` 读到的可能还是旧值 ⇒ 明明已经 JIT 了，你的程序仍然认为"未JIT"，永远不装。

---

## 坑 4 ★ 帧钩子的"只写值"模式（刷新方法查不到时的退路）

### 现象

能量/星星的刷新方法 `NEnergyCounter::RefreshLabel` / `NStarCounter::RefreshVisibility`
在 ClrMD 的方法枚举里**查不到**（和坑 1 同类，但这次是"私有的、枚举不全"），
于是帧动作因为"CallFn 地址非法"整组作废。

### 怎么改：允许 CallFn = 0

```csharp
// 校验放宽：只要有"要写的地方"，就允许不调刷新方法
if (Bad(a.CheckAddr) || (a.WriteAddr == 0 && Bad(a.CallFn))) { 跳过; }

// 生成机器码时：CallFn 有效才发 call，否则只写值
if (!Bad(a.CallFn) && !Bad(a.CallThis)) { ... movups 保存 xmm0 ... call rax ... }
```

什么时候可以只写值：

| 功能 | 只写值够不够 |
|---|---|
| 星星 | ✅ 够——`NStarCounter._Process` **每帧自己刷数字** |
| 能量 | ⚠️ 值够打牌就行，但**显示的数字要等下一次事件**才更新 |
| 血量 | ❌ 不够——`NTopBarHp` 纯事件驱动，必须调刷新方法（或用 setter 钩子） |

---

## 坑 5 ★ decimal 返回值会让参数整体右移（ModifyDamage 的 ABI）

### 现象

按"普通方法"的思路去 `ModifyDamage` 里找 `&damage`，位置全错，改不到伤害。

### 为什么

```csharp
public static decimal ModifyDamage(IRunState runState, ICombatState combatState,
                                   Creature target, Creature dealer, decimal damage, ...)
```

C# 的 `decimal` 是 16 字节结构体 ⇒ x64 上**返回值**要靠"调用者给的缓冲"，
约定把**缓冲指针放进 `rcx`**，于是**所有参数整体右移一格**：

```text
rcx = &ret(decimal)        ← 不是 runState！
rdx = IRunState
r8  = ICombatState
r9  = target（受伤者）      ★ 门禁就靠它
栈：[rsp+0x28] = dealer
    [rsp+0x30] = &damage(decimal)   ★ 伤害值在这里
    [rsp+0x38] = props
    [rsp+0x40] = cardSource
    [rsp+0x48] = hookType
    [rsp+0x50] = previewMode
    [rsp+0x58] = out modifiers
```

### 怎么改（本项目最终用的机器码）

压 6 个寄存器后，栈参数偏移整体 +0x30 ⇒ `&damage = [rsp+0x60]`：

```asm
50 53 51 52 41 50 41 51        push rax,rbx,rcx,rdx,r8,r9
4C 8B 44 24 60                 mov  r8,[rsp+0x60]     ; &damage
4D 85 C0                       test r8,r8 / je 结束
48 8B 1C 24                    mov  rbx,[rsp]         ; target（刚压进去的 r9）
48 B9 <玩家Creature>            mov  rcx,玩家
48 39 CB                       cmp  rbx,rcx
74 ..                          je   玩家侧
; 敌方侧
cmp dword [flagSuper],0 / jne → mov dword [r8],9999 + 清零 4/8/0xC   ; 超级伤害
cmp dword [flagMul],0   / jne → eax=[r8]; imul eax,eax,N; 写回      ; 伤害倍率
; 玩家侧
cmp dword [flagDef],0   / jne → eax=[r8]; cdq; idiv N; 写回         ; 防御倍率
; 还原 6 个寄存器 → 补回原指令 → jmp 回
```

> [!tip] 两条重载都要挂
> 原版样本里就是两条：`ModifyDamage(IRunState,ICombatState,…)` 和 `(IRunState,CombatState,…)`。
> 只挂一个，另一半调用路径不受影响 ⇒ 表现为"有时候生效有时候不生效"。

---

## 附：状态列怎么写才有用

| 写法 | 用户能做什么 |
|---|---|
| 统一写"待机" | ❌ 什么也做不了 |
| `未JIT` | 知道要等场景触发，会等 |
| `缺地址` | 知道是我方查不到方法（坑 1/4） |
| `挂点被占用` | 知道要**重启游戏**（坑 2） |
| `安装失败` | 知道我代码有 bug，会报给我 |

> [!important] 一句话总结
> 做一个能用的 trainer，**"能不能挂上"比"挂得对不对"更常出问题**：
> 先保证"每种失败都有明确原因"，再谈功能逻辑。

## 关联

- 方法论总纲：[[08-教程-20个功能总纲与方法论]]
- 伤害三项的正确挂法：[[19-复刻-超级伤害]]
- 原版样本的方法清单：[[70-拆解-Plus20 Trainer 的 20 个功能与改法]]
- 子库索引：[[00-杀戮尖塔2 索引 MOC]]
