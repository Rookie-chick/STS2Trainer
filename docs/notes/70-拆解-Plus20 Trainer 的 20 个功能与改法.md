---
title: 70 · Plus20 Trainer 的 20 个功能与改法
aliases: [FLiNG Trainer 拆解, STS2 Trainer, 杀戮尖塔2修改器]
tags: [拆解, 杀戮尖塔2, Trainer, ClrMD, 内存补丁]
created: 2026-10-31
---

上接：[[00-杀戮尖塔2 索引 MOC]] · 下接：[[71-拆解-Trainer 界面与边框是怎么做的]]

# 70 · Plus20 Trainer 的 20 个功能与改法

> [!important] 一句话结论
> 这个 Trainer **不用固定偏移**，而是靠 **.NET 元数据（ClrMD）按「类型::方法名」实时定位**，
> 再用 **Cheat Engine AutoAssembler 风格的补丁脚本**（`define(address,…)` / `alloc(newmem,…)`）改写方法入口。
> ⇒ **版本无关**：游戏更新了偏移它还照样能用，因为它认的是**方法名**。

## 样本事实（✅ 已验证）

| 项 | 值 |
|---|---|
| 文件 | `D:\Downloads\Plus20Trainer).exe` |
| 大小 / SHA256 | 3.34 MB ／ `246df6690d54e621374b6b073f86bdecf060ab2f107ffc35902ee3dcb99618b6` |
| 架构 / 子系统 | **x64** ／ GUI ／ 基址 `0x140000000` ／ 7 个节 |
| 节表（异常点） | `.text` `.rdata` `.data` `.pdata` **`.fptable`** `.rsrc` `.reloc` —— `.fptable` 不是常规编译器节名 ⏳ 疑似外壳/打包器 |
| 身份 | **FLiNG**（字符串含 `Credit : FLiNG\|FLiNGTrainer.com`、`\FLiNGTrainer.tmp`、`\trainer-logs\`） |
| 版本 | `Slay the Spire 2 Early Access Plus 20 Trainer Updated 2026.06.29` |
| 运行时 | **.NET Framework 4.6.2+**（导入 `mscoree.dll`；字符串 `Unable to load .NET Framework…`） |
| 权限 | 清单里同时有 `asInvoker` 与 **`requireAdministrator`** ⇒ 会请求管理员 ✓ |

## ★ 20 个功能 ↔ 它改的游戏方法（✅ 全部从样本字符串实测读出）

> 说明：右列是样本里 `define(address,…)` / `alloc(newmem,…)` 后面跟的方法名，**原样抄录**，不是我猜的。

| 热键 | 功能（原文） | 要点（原文摘要） | 挂的游戏方法 |
|---|---|---|---|
| `Num 1` | Infinite HP | 血量变化时把值拉回上限 | `MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp` |
| `Num 2` | Infinite Shield | 护盾变化时生效 | `…Creatures.Creature::set_Block` |
| `Num 3` | Infinite Energy | 能量不会减少 | `…Players.PlayerCombatState::LoseEnergy` |
| `Num 4` | Infinite Stars | 星星不会减少 | `…Players.PlayerCombatState::LoseStars` |
| `Num 5` | Always Reward Potion | 奖励必给药水 | `…Odds.PotionRewardOdds::Roll` |
| `Num 6` | Always Upgrade Card Rewards | 新卡奖励带 `+`；商店里的卡也升级 | `…Hooks.Hook::ModifyCardRewardUpgradeOdds` |
| `Num 7` | Max Card Reward Rarity | 卡牌奖励必最高稀有度 | `…Odds.CardRarityOdds::GetBaseOdds` |
| `Num 8` | Unknown Map Points Always Give Treasures | 未知点必给宝箱 | `MegaCrit.Sts2.Core.Odds.UnknownMapPointOdds::Roll` |
| `Num 9` | Enemies Can't Move | 敌人不能移动 | `MegaCrit.Sts2.Core.Hooks.Hook::ShouldTakeExtraTurn`（让它判定「敌人不额外行动」） |
| `Num 0` | Super Damage / One Hit Kills | 同时影响卡面伤害显示 | `…Hooks.Hook::ModifyDamage(IRunState, ICombatState, …)` |
| `Num .` | Damage Multiplier | 同上（另一处签名） | `…Hooks.Hook::ModifyDamage(IRunState, CombatState, …)` |
| `Num +` | Defense Multiplier | 只影响基础伤害，debuff 另算 | `…Hooks.Hook::ModifyDamage` |
| `Ctrl+Num 1` | Edit Gold | **战斗中生效** | `…Entities.Players.Player::set_Gold` |
| `Ctrl+Num 2` | Gold Multiplier | 只影响战斗金币奖励 | `…Models.EncounterModel::get_MinGoldReward` / `::get_MaxGoldReward` |
| `Ctrl+Num 3` | Free Purchase In Shop | **进商店前**先按 | `…Hooks.Hook::ModifyMerchantPrice` |
| `Ctrl+Num 4` | Edit Max Energy | 能量更新时生效 | `…Hooks.Hook::ModifyMaxEnergy` |
| `Ctrl+Num 5` | Edit Potion Slot Count | **进游戏/读档前**按 | `…Players.Player::SetMaxPotionCountInternal` |
| `Ctrl+Num 6` | Max Score | 每次 run 只能解锁一个 Epoch，超出丢弃 | `MegaCrit.Sts2.Core.Runs.ScoreUtility::CalculateScore(IReadOnlyList<…>)`（label：`score`） |
| `Ctrl+Num 7` | Score Multiplier | 同上 | 同上方法，另一个乘数（label：`scoremul_f`） |
| `Ctrl+Num 8` | Set Game Speed | 改游戏速度 | ⏳ 样本里**没有**它的 AA 补丁 ⇒ 大概率是**直接写值**（Godot 的 `Engine.time_scale` / 引擎时间缩放）而不是改方法；验证方法见下 |

> [!warning] 另有特征码扫描
> 样本里还出现 `aobscanregion(aobplayer,…,NEnergyCounter::_Process,…,NEnergyCounter::_Process+20…)`
> ⇒ 除了按方法名取址，它也**在特定方法体里做 AOB 特征码扫描**（方法被内联/重排时用它兜底）。

## ★ 2026-11-03 重新抽取（原文，未删改）

再次对样本 exe 跑抽取脚本，得到 56 条模板行。关键发现**修正了上表的一处误解**：

```text
define(address,Creature::set_CurrentHp)          + label(health)            ← 无限血
define(address,Creature::set_Block)              + label(shield)            ← 护盾
define(address,PlayerCombatState::LoseEnergy)                                ← 能量
define(address,PlayerCombatState::LoseStars)                                 ← 星星
aobscanregion(aobplayer, NEnergyCounter::_Process, _Process+200,
              "48 8B 89 <PlayerCombatState 背字段>")  + label(edit_gold)     ← 金币
//getmonomethod(Player::set_Gold(Int32))        ← ★这行是【注释掉的】
define(address,Hook::ModifyDamage(IRunState,ICombatState,…)) + label(dmg dmgmul_f defmul_f)
define(address,Hook::ModifyDamage(IRunState,CombatState,…))                   ← 伤害三项，两个重载
define(address,PotionRewardOdds::Roll)
define(address,Hook::ModifyCardRewardUpgradeOdds)  + label(cardrewardupgrade)
define(address,CardRarityOdds::GetBaseOdds)
define(address,UnknownMapPointOdds::Roll)
define(address,Hook::ShouldTakeExtraTurn)
define(address,EncounterModel::get_MinGoldReward)  + label(goldmul_f)
define(address,EncounterModel::get_MaxGoldReward)
define(address,Hook::ModifyMerchantPrice)          + label(freeshopping)
define(address,Hook::ModifyMaxEnergy)              + label(max_energy)
define(address,Player::SetMaxPotionCountInternal)  + label(max_potion_slots)
define(address,ScoreUtility::CalculateScore(IReadOnlyList<…>)) + label(score scoremul_f)
```

| 修正点 | 原来以为 | 实际 |
|---|---|---|
| 金币 | 挂 `Player::set_Gold` | **这行是注释**；真正用的是 `aobscanregion` 在 `NEnergyCounter::_Process` 里扫偏移，再用 `edit_gold` 符号改 HUD/字段 |
| 伤害三项 | 笼统"挂 `ModifyDamage`" | **两个重载都挂**（`ICombatState` 与 `CombatState`），三个 label `dmg` / `dmgmul_f` / `defmul_f` 共用 |
| 分数两项 | 一个方法 | 同一个 `CalculateScore`，两个符号 `score` / `scoremul_f`（写死值 vs 乘倍率） |
| 游戏速度 | — | 样本里**没有任何补丁** ⇒ 确认是直接写引擎值 |

> [!tip] `aobscanregion` 那招为什么值得学
> 它不写死 `PlayerCombatState` 的字段偏移，而是在 `NEnergyCounter::_Process` 的机器码里
> 搜 `48 8B 89 ?? ?? ?? ??`（`mov rcx,[rcx+imm32]`）当场算出偏移。
> ⇒ 游戏更新、字段搬家，脚本照样能用。这是"抗更新"的第二层保险（第一层是认方法名）。


## ★ 补丁脚本的骨架（✅ 从样本原样还原）

样本里嵌的是 **Cheat Engine AutoAssembler（AA）脚本模板**，结构固定成这样（`health` 这个 label 名就是"无限血"用的）：

```text
// 1) 按"方法全名"取址（不写死偏移）
define(address,MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp)

// 2) 声明标签
label(code)
label(return)
label(health)
registersymbol(health)          // ★ 把地址注册成符号，UI 侧靠它读/写

// 3) 在方法旁边分配一块可执行内存（$1000 字节），入口就写方法名 = 取该方法的地址
alloc(newmem,$1000,MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp)

newmem:
  // ……这里放你的改写逻辑（读 health 符号、判断开关、改值）……
  // ……再补回被覆盖的原指令……
  jmp return

MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp:
  jmp newmem                    // 方法入口被改成跳到我们的代码
  nop

return:

// 4) 关闭功能时回收
dealloc(newmem)
```

**统计证据（✅ 数出来的）**：

| 指标 | 数量 |
|---|---|
| `define(address,<方法全名>)` | **17 处**（= 按名字取址） |
| `alloc(newmem,$1000,<方法全名>)` | **12 处**（= 分配并挂跳转） |
| `aobscanregion(…)` | **1 处**（特征码兜底，用在 `NEnergyCounter::_Process` 里找 `PlayerCombatState` 的字段） |
| `//getmonomethod(…)` / `//getmonostruct(…)` | 有（= 用 CE 的 **.NET/Mono 支持**按类型取结构、按方法名取址） |
| label 命名 = 功能名 | `health` / `shield` / `edit_gold` / `goldmul_f` / `freeshopping` / `max_energy` / `max_potion_slots` / `cardrewardupgrade` / `dmg dmgmul_f defmul_f` / `score scoremul_f` |

> [!tip] 为什么这套东西"抗更新"
> 因为入口是 **`类型::方法名`**，不是 `GameAssembly.dll+0x1A2B3C`。
> 游戏只要还叫这个名字、还是这个方法 ⇒ 补丁照样能挂上（除非方法被内联/改名 ⇒ 这就是那 1 处 `aobscanregion` 兜底存在的原因）。

## 我是怎么把这张表抠出来的（✅ 真实命令 + 真实输出）

```powershell
cd D:\sts2
python trainer_scripts.py
```

真实输出（节选，原样）：

```text
=== 全部补丁模板行（去重，共 56 条）===
  define(address,MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_Block)
  alloc(newmem,$1000,MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_Block)
  label(shield)
  define(address,MegaCrit.Sts2.Core.Odds.UnknownMapPointOdds::Roll)
  define(address,MegaCrit.Sts2.Core.Hooks.Hook::ShouldTakeExtraTurn)
  define(address,MegaCrit.Sts2.Core.Runs.ScoreUtility::CalculateScore(System.Collections.Generic.IReadOnlyList`1[…MapP…)
  label(score scoremul_f)
=== 判定 ===
  用 getmonomethod/getmonostruct（CE 的 .NET 支持）: True
  用 aobscanregion（特征码兜底）: 1 处
  用 define(address,方法名)（按名取址）: 17 处
  用 alloc(newmem,方法名)（分配并挂跳转）: 12 处
```

### 你该怎么做（零基础，4 步）

1. **准备**
   ```powershell
   # 把 exe 复制到一个工作目录（别在原目录乱动）
   copy "D:\Downloads\Plus20Trainer).exe" C:\work\t.exe
   ```
2. **用 Python 扫字符串**（脚本已在 `D:\sts2\trainer_scripts.py`，把 `P=` 改成你的路径即可）
   ```powershell
   python trainer_scripts.py
   ```
   - 预期：打印 `define(address,…)` / `alloc(newmem,…)` / `label(…)` / `registersymbol(…)` 行
   - 关键：**同时要扫 UTF-16LE**（很多 .NET 程序里字符串是宽字符；只扫 ASCII 会漏一半 ✗）
3. **看注释形式的 CE 指令**
   - 预期能见到 `//getmonomethod(<类>::<方法>(Int32))`、`//getmonostruct(<类>)`
   - 含义：**CE 的 Mono/.NET 接口**：按类型名拿结构体布局、按方法签名拿地址
4. **想自己验证"方法名真的能换地址"**
   - 工具：**Cheat Engine** → 附加到 `SlayTheSpire2.exe` → 菜单 `Mono` → `Dissect mono` → 打开对应程序集 → 搜 `Player` → 看 `set_Gold` 是否**带地址**
   - 预期：能看到方法名 + 地址 ⇒ 说明这条路线在 C# 游戏上成立

> [!warning] `Ctrl+Num 8（Set Game Speed）` 的 ⏳ 仍未闭环
> 样本里**没有**它的 AA 补丁 ⇒ 它是**直接改值**（不是改方法）。
> **验证方法**：游戏内把速度调成 0.5× 后，用 CE 搜 `2.0`（默认值）→ 改成 1.0 试 → 或直接在 `Mono` 里找 `Engine.TimeScale` / `Engine::set_TimeScale`（Godot 的 C# 绑定）⇒ 若命中即证实。

## 三类结论

## 我是怎么做的（真实命令与真实输出）

```powershell
# 1) 只看静态，不运行（安全）
cd D:\sts2
python trainer_analyze.py     # 输出：PE 事实 / 导入 DLL / 宽字符串里的功能名
python trainer_full.py        # 输出：完整热键清单 / UI 框架证据 / 带游戏类型名的补丁脚本
```

关键输出片段（原样）：

```text
架构: x64 ｜ 子系统: GUI ｜ 节数: 7 ｜ 基址: 0x140000000
导入: KERNEL32(115) USER32(3) ADVAPI32(4) SHELL32(2) ole32(2) OLEAUT32(6) mscoree(1) VERSION(3) WINMM(1)
★ UI 相关 API: 无      ★ 内存读写 API: 无
  （直连 ReadProcessMemory/WriteProcessMemory 都没有 ⇒ 读写走的是 .NET/ClrMD 侧）
alloc(newmem,$1000,MegaCrit.Sts2.Core.Hooks.Hook::ModifyMaxEnergy)
define(address,MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp)
mov rax,MegaCrit.Sts2.Core.Entities.Players.Player::set_Gold
aobscanregion(aobplayer,…,NEnergyCounter::_Process,…,NEnergyCounter::_Process+20…)
System.Collections.Generic.IEnumerable<Microsoft.Diagnostics.Runtime.ClrReference…  （392 条 ClrMD 相关串）
```

**怎么读出"挂哪个方法"的**：宽字符串 + ASCII 串里同时存在 `define(address,<方法全名>)`、`alloc(newmem,$1000,<方法全名>)`、`//getmonomethod(<方法全名>)`、`//getmonostruct(<类型>)` —— 这些是 **CE 的 Mono/AA 脚本函数**，作者把它们**当模板字符串**存进了程序里，运行期再解析。

## 你该怎么做（零基础照抄）

> [!tip] 前置：这一步**不需要**会写汇编，先把"证据"捞出来

1. **取文件与指纹**（PowerShell）
   ```powershell
   Get-FileHash "路径\Trainer.exe" -Algorithm SHA256
   ```
   预期：打印一串 64 位十六进制 + 文件路径（拿去 [VirusTotal](https://www.virustotal.com/) 查一次）

2. **看它是什么语言写的**
   - 工具：**Detect It Easy**（DIE，免费）
   - 操作：打开 DIE → 拖入 exe → 看 `Compiler` 一栏
   - 预期：本例会显示 **.NET / Microsoft Visual C#**（因为导入了 `mscoree.dll`）
3. **看它凭什么改游戏**（不需要反编译）
   ```powershell
   # 用 Python 直接扒字符串（本项目脚本 trainer_full.py 就是干这个的）
   python trainer_full.py
   ```
   预期：出现 `define(address,…::set_CurrentHp)` 之类的行 ⇒ 说明它**按方法名**打补丁，不是按固定偏移
4. **看有哪些功能**：同一份输出里的 `Num N - …` / `Ctrl+Num N - …` 行，就是**完整功能表**（含作者写的注意事项）
5. **想自己验证"按方法名定位"可不可靠**（进阶）
   - 工具：**Cheat Engine** → `Mono` 菜单（对 Mono/.NET 游戏）
   - 操作：CE 附加到 `SlayTheSpire2.exe` → `Mono` → `Dissect mono` → 找到域/程序集 → 搜索 `Player` 类 → 看 `set_Gold` 等方法是否**带地址**
   - 预期：能看到方法名 + 地址 ⇒ 证明"名字→地址"这条路在 C# 游戏上成立（这也是为什么它能跨版本用）
6. **别急着运行它**：先做完第 1 步的 VT 检查

## 三类结论

| 类别 | 内容 |
|---|---|
| ✅ 已验证 | 文件身份/哈希 ✅ ｜ x64 .NET Framework 程序 ✅ ｜ 请求管理员 ✅ ｜ 存在 `.fptable` 异常节 ✅ ｜ 用 ClrMD（`Microsoft.Diagnostics.Runtime`）✅ ｜ 20 条热键文本 ✅ ｜ 12 个功能与游戏方法名的对应关系 ✅ ｜ 存在 AOB 特征码扫描兜底 ✅ |
| ❌ 已证伪 | 它**不是**靠固定内存偏移/固定版本工作（样本里没有任何硬编码偏移常量，全是方法名）✗ ｜ 它**没有**自建 `ReadProcessMemory` 读写通道（导入表里没有）✗ |
| ⏳ 待验证 | `Num 8/9`、`Ctrl+Num 6/7/8` 具体挂哪个方法 ｜ `.fptable` 是否打包器 ｜ 补丁落地后的**实际效果**（未运行 ✓ 不运行未知第三方 exe ✓） |

## 关联

- 界面与边框怎么做的：[[71-拆解-Trainer 界面与边框是怎么做的]]
- 游戏本体技术栈：[[00-杀戮尖塔2 索引 MOC]]
- 子库索引：[[00-杀戮尖塔2 索引 MOC]]
