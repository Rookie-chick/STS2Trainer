---
title: 76 · 两条路怎么动手：CE 手把手 / .NET(ClrMD) 手把手
aliases: [CE 教程, ClrMD 教程, Mono Dissect, 怎么拿字段偏移]
tags: [复刻, 杀戮尖塔2, CE, ClrMD, 教程]
created: 2026-11-01
---

上接：[[75-复刻-怎么一步一步做出这个外挂]] · 下接：[[00-杀戮尖塔2 索引 MOC]]

# 76 · 两条路怎么动手：CE 手把手 / .NET(ClrMD) 手把手

> [!info] 这篇是"给**人**看的手" —— 每一步都写清：**点哪里 → 看到什么 → 为什么这么做**
> 目的：让你自己能在 CE 里把偏移读出来、也能自己写个 .NET 小工具自动读。
> 两条路解决的是**同一件事**：从**正在运行的 .NET 游戏**里，拿到"字段在对象里的位置(偏移)"。

## 先讲原理（为什么必须问"运行中的游戏"）

```text
sts2.dll（磁盘上的文件）        只有：类名 / 字段名 / 方法名 / 签名
                                  ✗ 没有"字段在对象里的偏移"（那是运行时布局）
游戏跑起来（.NET 9 加载 dll）
   └─ CLR 给每个类型算好内存布局 → Creature 的大小、每个字段的偏移
   └─ 对象实例分配在托管堆上 → 每个对象有一个地址
⇒ 偏移和地址都只存在于"活着的进程"里
```

所以两条路的本质都是：**连上活着的进程，问它"Creature 这个类型的 `_currentHp` 在偏移几"**。

| 路 | 谁去问 | 你怎么操作 |
|---|---|---|
| **CE** | CE 的 Mono 插件 | **点界面**（下面第一节，6 大步骤） |
| **.NET + ClrMD** | 你自己写的 C# 程序 | **敲命令 + 写 30 行代码**（第二节） |

---

## 我是怎么做的（真实证据）

| 我做了什么 | 真实结果 |
|---|---|
| 从 `sts2.dll` 元数据里读字段名 | `_currentHp` / `_maxHp` / `_currentBlock` / `_gold` ✓ |
| 用 ClrMD 2.4 + .NET Framework 编译工具 | 编译成功（6 KB，0 error ✓）但**附不上 .NET 9** ✗ |
| 装 .NET 8 SDK（`dotnet-install.ps1 -Channel 8.0`，用户级） | **8.0.427 装好** ✓ 运行时 `Microsoft.NETCore.App 8.0.33` ✓ |
| 盲扫值找血量（int32/int64/邻近/对齐） | **全部失败** ✗（候选 239/68/3/0，画面一分不变）|
| 抢焦点后连拍两帧 | 差异 **32.77%** ✓ ⇒ 截图验证链可用 ✓ |

**结论**：这一步必须"问运行中的进程"⇒ 只能用 **CE 的 Mono 视图** 或 **ClrMD（需 .NET 8+）** ✓ —— 下面两条路都教。

## 你该怎么做

## 一、CE 路线（手把手，点界面）

### 步骤 0：准备（一次性）
| 做什么 | 具体 |
|---|---|
| 下载 CE | 打开 `cheatengine.org` → 下载 → **解压**（绿色版，不必安装）→ 运行 `CheatEngine.exe` |
| 让游戏就绪 | 用 `launch_opengl.bat` 启动（VM 无 GPU 必须 ✓），进到**任意一局**里（有角色在场 ✓） |
| 确认托管侧活着 | 进程模块里要能看到 `coreclr.dll` + `sts2.dll`（本项目脚本 `sts2_wait.py` 会告你 ✓） |
| 管理员 | 如果 CE 看不到游戏进程/读不到内存 ⇒ **右键 CE → 以管理员身份运行** |

### 步骤 1：让 CE 附到游戏上
1. CE 左上角有一个**闪烁的电脑小图标** → 点它
2. 弹出**进程列表**窗口
3. 在列表里找 **`SlayTheSpire2.exe`** → 单击选中 → 点右下 **`打开`**
   - 列表里没有？① 勾选窗口左下 **`显示所有进程`** ② 还是不行 → 以管理员重开 CE
4. 成功标志：CE 标题栏变成 `Cheat Engine 7.x - [SlayTheSpire2.exe]`（**标题里有进程名** ✓）

### 步骤 2：打开 Mono 视图（★关键，.NET 游戏专属）
1. 菜单栏点 **`Mono`**（在 `File / Edit / Table / D3D / Help` 那一排里）
2. 下拉里选 **`Dissect mono`**（有的版本叫 `Dissect Mono/IL2CPP`）
3. 弹出 **程序集(Assembly)列表**
4. 找到并**双击** **`sts2.dll`**（也可能显示为 `MegaCrit.Sts2.Core`）

> [!tip] `Dissect mono` 到底干了什么（理解这个就懂了整套原理）
> 它去游戏进程里**读 .NET 的元数据表**（Assembly → TypeDef → MethodDef → FieldDef），
> 把"这个程序集里有哪些类、哪些方法、方法的机器码地址、类型的字段"全列出来。
> 这也是**为什么它能给你"方法地址"和"字段偏移"** —— 因为 CLR 自己就持有这份表 ✓

**如果列表是空的/没有 sts2.dll** ✗：
- 游戏还没进托管阶段 → 用 `opengl3` 重启 ✓
- CE 位数不对 → 用 **64 位 CE**（`cheatengine-x86_64.exe`）✓
- 权限不够 → 管理员运行 CE ✓

### 步骤 3：找到类 → 读字段偏移（这就是我们要的数）
1. 进入 sts2 程序集后，会看到**命名空间树**（`MegaCrit` → `Sts2` → `Core` → …）
2. 用顶部的**搜索框**（或 `Ctrl+F`）输入 **`Creature`** → 双击结果里的 **`Creature` 类**
3. 右侧出现两块：**`Methods`（方法）** 和 **`Fields`（字段）**
4. 在 **`Fields`** 里找：
   ```
   _currentHp     ...
   _maxHp         ...
   _currentBlock  ...
   ```
   每一行右边会显示 **偏移（Offset）**，形如 `0x18`、`0x1C`、`0x20`
5. **把 `_currentHp` 和 `_maxHp` 的偏移记下来**（例如 `0x18` / `0x1C`）← 这就是缺的那个数

> [!example] 顺便能读到的东西
> - `Methods` 里的 **`set_CurrentHp`** 旁边会显示**方法地址**（形如 `sts2.dll+1A2B3C` 或绝对地址）→ AA 脚本里 `define(address,…)` 就是它
> - 点某个方法 → 可以 `Disassemble`（看汇编）→ 记下**方法头原字节**（写 `[DISABLE]` 段要用）

### 步骤 4：写第一段补丁（无限血）
1. 菜单 **`Memory View`**（或按 `Ctrl+M`）
2. 新窗口菜单 **`Tools`** → **`Auto Assemble`**（或 `Ctrl+A`）
3. 在空白处**粘贴**下面的脚本（把 `0x18/0x1C` 换成你在步骤 3 读到的值）：

```text
[ENABLE]
define(address,MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp)
label(return)
alloc(newmem,$1000,MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp)
alloc(infhp,4)
registersymbol(infhp)
newmem:
  cmp dword ptr [infhp], 0
  je code
  mov rdx, [rcx + 0x1C]        // 0x1C → _maxHp 的偏移
code:
  mov [rcx + 0x18], rdx        // 0x18 → _currentHp 的偏移（补回原指令）
  jmp return
MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp:
  jmp newmem
  nop
return:

[DISABLE]
MegaCrit.Sts2.Core.Entities.Creatures.Creature::set_CurrentHp:
  db 48 89 91 ?? ?? ?? ??      // ← 用菜单「恢复原始代码」让 CE 自动填
dealloc(newmem)
dealloc(infhp)
unregistersymbol(infhp)
```

4. 菜单 **`File`** → **`Assign to current cheat table`**（把脚本变成一个可勾选的条目 ✓）
5. 关掉 AA 窗口 → 回 CE 主界面 → 表格里出现你刚存的脚本 → **在它左边的方框打勾**（= 执行 `[ENABLE]`）
6. 再往表格里加一个开关变量（可选但推荐）：`Add Address Manually` → 填 `infhp` → 类型选 `4 Bytes` → 值改成 `1`

### 步骤 5：验证（唯一标准）
```
回游戏 → 故意让敌人打你一次 → 看血条
  血不掉 = 成功 ✓
  血照掉 = 偏移写错了 或 方法名写错 或 补丁没勾上
```
> [!danger] 别用"没报错"当成功
> 我在这个游戏上被"脚本无报错/值写进去了"骗过 4 次 ✗ —— **只认画面**。

### 步骤 6：关掉 & 收尾
取消勾选（执行 `[DISABLE]`）→ 血条恢复正常 ✓ → 如果崩过，重启游戏即恢复（补丁只在内存里 ✓ 不改文件 ✓）

---

## 二、.NET + ClrMD 路线（手把手，敲命令）

> 适合：想把"找偏移"自动化、想批量枚举对象、想以后做自己的 Trainer。
> 前提：已装 .NET 8 SDK（本机已装 ✓ 在 `C:\Users\Rookie\AppData\Local\Microsoft\dotnet\dotnet.exe`）

### 步骤 1：建项目
```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
mkdir D:\sts2\clr -Force
cd D:\sts2\clr
& $dotnet new console -n sts2clr
cd sts2clr
```
**预期**：打印 `The template "Console App" was created successfully.` ✓

### 步骤 2：装 ClrMD（这一步由 dotnet 自动处理依赖，不用手撸 nupkg）
```powershell
& $dotnet add package Microsoft.Diagnostics.Runtime
```
**预期**：`PackageReference for package 'Microsoft.Diagnostics.Runtime' version '4.x.x' added` ✓
（这一步会自动下 4.x ✓ —— **正是能附 .NET 9 的那个版本** ✓）

### 步骤 3：写代码（`Program.cs` 全量替换）
```csharp
using System;
using Microsoft.Diagnostics.Runtime;

int pid = int.Parse(args[0]);
using var dt = DataTarget.AttachToProcess(pid, suspend: false);
var clr = dt.ClrVersions[0];
Console.WriteLine($"CLR = {clr.Version}");
using var rt = clr.CreateRuntime();

int total = 0, hit = 0;
foreach (var obj in rt.Heap.EnumerateObjects())
{
    total++;
    var t = obj.Type;
    if (t is null) continue;
    if (t.Name is null || !t.Name.Contains("Creature")) continue;
    hit++;
    if (hit > 20) continue;
    Console.WriteLine($"\n== {t.Name}  ADDR=0x{obj.Address:X}");
    foreach (var f in t.Fields)
    {
        if (f.Name is null) continue;
        if (!f.Name.Contains("hp", StringComparison.OrdinalIgnoreCase)
         && !f.Name.Contains("block", StringComparison.OrdinalIgnoreCase)) continue;
        // 4.x 用 ReadField<T> 或 field.Read
        string val = "-";
        try { val = f.Read<int>(obj.Address, interior: false).ToString(); } catch { }
        Console.WriteLine($"   {f.Name,-16} offset=0x{f.Offset:X}  type={f.ElementType}  value={val}");
    }
}
Console.WriteLine($"\n枚举 {total} 个对象，命中 {hit} 个 Creature");
```

**逐段在讲什么**：
| 代码 | 含义 |
|---|---|
| `DataTarget.AttachToProcess(pid, false)` | 只读附加（`false` = 不暂停进程 ✓ 游戏不会卡） |
| `dt.ClrVersions[0]` | 这个进程里的 CLR 版本（.NET 9 ✓） |
| `clr.CreateRuntime()` | 打开 CLR 的运行时视图（堆 + 类型系统） |
| `rt.Heap.EnumerateObjects()` | **枚举托管堆上所有对象**（这就是"不用扫内存"的原因 ✓） |
| `obj.Type.Name` | 对象的类型名（`…Creatures.Creature` ✓ 用来过滤） |
| `obj.Address` | ★ 对象地址（写值时要用的基址） |
| `t.Fields` → `f.Offset` | ★ ★ 字段偏移（就是我们缺的那个数） |
| `f.Read<int>(obj.Address,false)` | 读字段当前值（顺便能看到 38 / 82 ✓ 用来交叉确认找对了 ✓） |

### 步骤 4：跑
```powershell
$pid2 = (Get-Process SlayTheSpire2).Id
& $dotnet run -- $pid2
```
**预期输出**：
```text
CLR = 9.0.x
== MegaCrit.Sts2.Core.Entities.Creatures.Creature  ADDR=0x1F2A3B4C50
   _currentHp       offset=0x18  type=Int32  value=38
   _maxHp           offset=0x1C  type=Int32  value=82
   _currentBlock    offset=0x20  type=Int32  value=0
枚举 123456 个对象，命中 3 个 Creature
```
**看到 `value=38 / 82` 与你画面上的血量一致 ⇒ 100% 找对了** ✓✓

### 步骤 5：用它写值（把工具改成写入版）
```csharp
// 找到目标对象后，直接写：需要另开一个"可写句柄"
using var h = System.Diagnostics.Process.GetProcessById(pid);
var mem = new Mem(h.Handle);           // 自己封装 WriteProcessMemory
mem.WriteInt32((IntPtr)(obj.Address + 0x18), 9999);
```
或更简单：把 `offset` 输出给 Python 脚本，用 `WriteProcessMemory` 写 ✓（本项目 `hp*.py` 就是干这个的 ✓）

> [!warning] 权限 & 位数
> - 读别的进程需要**同权限或更高**：CE/工具用管理员跑最省事 ✓
> - **必须 64 位**进程工具（游戏是 x64）✓

---

## 三、两条路怎么选（学习建议）

| | **CE** | **.NET + ClrMD** |
|---|---|---|
| 上手速度 | ★★★★★（点界面） | ★★★（要写代码） |
| 学到什么 | 内存/汇编/补丁/AA 脚本 ✓ | 托管运行时/类型系统/自动化 ✓ |
| 能做的事 | 打补丁、做 Trainer、实时调 | 批量枚举、自动找偏移、写自己的 Trainer |
| 抗更新 | 方法名 ✓ | 方法名 + 类型名 ✓ |
| 建议顺序 | **先 CE**（理解"补丁"这件事）→ **再 ClrMD**（理解"托管世界"） | |

## 四、常见问题（都会撞上）

| 现象 | 原因 | 解法 |
|---|---|---|
| CE 进程列表没有游戏 | CE 位数/权限 | 用 `cheatengine-x86_64.exe` + 管理员 |
| `Mono` 菜单里列表是空的 | 游戏没进托管阶段 | `opengl3` 重启，确认 `coreclr.dll` 已加载 |
| 打勾后游戏崩 | 偏移写错 / 原字节没补对 | 先用 CE 的"恢复原始代码"；偏移按步骤 3 重读 |
| 打勾了但没效果 | 补丁没真生效 / 改的是副本 | **看画面**验证；换方法级（别改值级） |
| 画面完全不动（截图像静止） | 游戏**失焦停帧** | 让游戏拿到焦点（`focus_check.py` 那种 `AttachThreadInput` 做法 ✓） |

## 关联

- 建造总流程：[[75-复刻-怎么一步一步做出这个外挂]]
- 每个功能的代码：[[73-复刻-20 个功能逐个实现（含代码）]]
- 环境与启动：[[74-入门-环境搭建与启动（虚拟机无GPU也能跑）]]
