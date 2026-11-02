---
title: 71 · Trainer 界面与边框是怎么做的
aliases: [Trainer 界面, 边框, WPF 无边框窗口]
tags: [拆解, 杀戮尖塔2, UI, 边框]
created: 2026-10-31
---

上接：[[70-拆解-Plus20 Trainer 的 20 个功能与改法]] · 下接：[[00-杀戮尖塔2 索引 MOC]]

# 71 · Trainer 的界面与边框是怎么做的

> [!warning] 先说清楚这条的结论强度
> **✅ 已验证**：外壳是 **.NET Framework 程序 + 运行期释放载荷**（`\FLiNGTrainer.tmp`），真正的 UI 代码**不在这个 exe 的明文里**。
> **⏳ 待验证**：边框具体用哪种技术 —— 要先把载荷解出来才能定论。下面第三节给出**三种常见做法 + 一眼判断的方法**（标注清楚是"通用做法"，不是本样本结论）。

## 一、本样本能确认的界面线索（✅ 已验证）

| 证据 | 含义 |
|---|---|
| 导入 `mscoree.dll` + 字符串 `Unable to load .NET Framework. This trainer requires .NET Framework 4.6.2 or higher.` | 外壳是 **.NET Framework** 程序 |
| 导入 `ole32.dll`(2) + `OLEAUT32.dll`(6) | 用到 **COM/OLE**（.NET 的 WPF/COM 互操作或系统对话框会用到） |
| 导入 **`USER32.dll` 只有 3 个函数**，且**没有** `gdi32` / `gdiplus` / `d3d` | ⇒ **不是** Win32 裸写窗口、**不是** GDI 自绘、**不是** DirectX 自绘 |
| 字符串里**完全搜不到** `PresentationFramework` / `System.Windows` / `WindowsForms` / `XAML` / `CornerRadius` / `MahApps` / `MaterialDesign` | ⇒ **明文里没有 UI 框架痕迹** ⇒ **UI 代码在载荷里** |
| 字符串 `\FLiNGTrainer.tmp`、`\trainer-logs\` | ★ **运行期会落地临时文件** ⇒ 这是**打包器/释放器**行为 |
| `.fptable` 节（非常规编译器节名） | ⏳ 疑似外壳/打包器留下的节 |
| 字符串 `SetProcessDPIAware` + 清单 `dpiAware` | 做了 **DPI 感知**（高分屏下界面不糊）—— 这是"精致小工具界面"的常见动作 |

**推论（✅）**：`xxx.exe`（3.34 MB）= **启动器**；它把真正的 UI 程序（大概率是另一个 .NET 可执行/程序集）**释放到临时目录再启动** ⇒ **想研究边框，必须先解这个载荷**。

## 二、想直接看边框，该怎么做（⏳ 下一步）

```powershell
# 1) 先看有没有内嵌的第二个 PE / 高熵压缩段（本项目脚本可扩展）
python - <<'PY'
d=open(r"D:\Downloads\Plus20Trainer).exe","rb").read()
import re
print("内嵌 MZ 头数量:", len(re.findall(b"MZ", d)))
PY

# 2) 运行它一次（自行决定是否运行！），然后立刻看临时目录有没有落地文件
Get-ChildItem $env:TEMP -Filter "*FLiNG*" -Recurse -ErrorAction SilentlyContinue | Select FullName,Length
Get-ChildItem "$env:LOCALAPPDATA" -Filter "*FLiNGTrainer*" -Recurse -ErrorAction SilentlyContinue | Select FullName,Length
```

拿到落地文件后：

- 工具：**dnSpyEx**（`https://github.com/dnSpyEx/dnSpy`）
- 操作：打开落地程序集 → 左侧找 `MainWindow` / `TrainerWindow` 之类 → 看 `InitializeComponent()` 附近的 **XAML 资源**（`Window.Resources` 里的 `Style`、`Border`、`CornerRadius`）→ 或直接 F12 看 `WindowStyle` / `AllowsTransparency`
- 预期：如果是 WPF，会看到 `WindowStyle="None"`、`AllowsTransparency="True"`、`Border CornerRadius="…"`

> [!danger] 运行第三方 exe 的风险自己担
> 该文件**无数字签名**（详见 [[70-拆解-Plus20 Trainer 的 20 个功能与改法]] 的样本事实）。
> 更稳的做法：先在**虚拟机/沙箱**里跑一次，把落地文件拷出来分析 ✓

## 三、这类"圆角深色边框"的三种常见做法（通用知识，非本样本结论）

| 做法 | 关键技术 | 怎么一眼判断 | 典型外观 |
|---|---|---|---|
| **A. WPF 无边框窗 + 圆角 Border** | `WindowStyle=None`、`AllowsTransparency=True`、`Background=Transparent`，根节点放 `Border CornerRadius="12" BorderBrush=…` | 程序是 .NET + 反编译能看到 XAML/`Border.CornerRadius`；导入里 **没有** gdi32 的圆角函数 | 圆角平滑、有阴影、可整体拖动 |
| **B. Win32 区域裁剪** | `CreateRoundRectRgn` + `SetWindowRgn`（圆角靠"挖掉四角"） | 导入表里**明确有** 这两个函数；圆角边缘**有锯齿** | 圆角硬边、无阴影、无透明 |
| **C. 分层窗口 + PNG 贴图** | `UpdateLayeredWindow` / `SetLayeredWindowAttributes` + 一张带边框的 PNG | 导入里出现分层窗口函数；资源里有整窗大小的 PNG | 边框就是一张图（可做金属/描边特效） |

> [!tip] 判断顺序（30 秒）
> ① 是不是 .NET？（导入 `mscoree`/DIE 看编译器）→ 是 ⇒ 优先怀疑 **A**（WPF/WinForms 自绘）
> ② 导入里有没有 `SetWindowRgn` / `UpdateLayeredWindow`？→ 有 ⇒ **B** 或 **C**
> ③ 资源里有没有整窗尺寸的位图？→ 有 ⇒ **C**

## 四、这次用到的工具（操作级）

| 工具 | 打开后做什么 | 看什么 |
|---|---|---|
| **Detect It Easy** | 拖 exe 进去 | `Compiler`＝.NET？有没有壳（`Packer`） |
| **Python（本项目脚本）** | `python trainer_full.py` | 宽/窄字符串里的 UI 关键词、`Num` 热键行、`define(address,…)` 补丁模板 |
| **PE 导入表**（本项目脚本 / `PE-bear`） | 看 `USER32`/`gdi32`/`dwmapi` 有没有圆角/分层窗口函数 | 有没有 `SetWindowRgn`、`CreateRoundRectRgn`、`UpdateLayeredWindow` |
| **dnSpyEx** | 打开载荷程序集 | `WindowStyle`、`AllowsTransparency`、`Border.CornerRadius`、XAML 资源 |
| **Process Monitor** | 运行 Trainer 时过滤 `Path contains FLiNG` | 它把载荷释放到哪个目录、释放了几个文件 |

## 我是怎么做的（真实命令 · 真实输出）

```powershell
cd D:\sts2
python trainer_analyze.py    # PE 事实 + 导入 DLL
python trainer_full.py       # 完整热键表 + UI 关键词证据
```

真实的、决定性的输出（原样摘录）：

```text
架构: x64 ｜ 子系统: GUI ｜ 节数: 7 ｜ 基址: 0x140000000
导入: KERNEL32(115) USER32(3) ADVAPI32(4) SHELL32(2) ole32(2) OLEAUT32(6) mscoree(1) VERSION(3) WINMM(1)
★ UI 相关 API: 无
未命中的关键词: ['PresentationFramework', 'PresentationWPF', 'System.Windows', 'WindowStyle',
                'CornerRadius', 'AllowsTransparency', 'XAML', 'gdiplus', 'SetWindowRgn',
                'CreateRoundRectRgn', 'UpdateLayeredWindow', …]
\FLiNGTrainer.tmp   \trainer-logs\   SetProcessDPIAware
```

**由此得出的三条硬结论**：
1. 它是 **.NET Framework 程序**（`mscoree` + "requires .NET Framework 4.6.2"）
2. 它**不用** GDI/DX 自绘、**不用** `SetWindowRgn` 挖圆角（导入表里都没有）
3. 明文里没有 UI 框架名 ⇒ **UI 代码在运行期释放的载荷里**（`\FLiNGTrainer.tmp` 是证据）

## 你该怎么做（零基础照抄，含工具）

| 步 | 用什么工具 | 具体操作 | 预期看到什么 |
|---|---|---|---|
| 1 | **Detect It Easy**（DIE） | 拖入 exe，看 `Compiler` / `Packer` 两栏 | `Compiler`≈.NET；`Packer` 若有名字 ⇒ 有壳 ⏳ |
| 2 | **Python**（本项目 `trainer_full.py`） | `python trainer_full.py` | 打印 `Num N - …` 热键行 与 UI 关键词命中情况 |
| 3 | **PE 导入表**（本项目脚本 / PE-bear） | 搜 `SetWindowRgn`、`CreateRoundRectRgn`、`UpdateLayeredWindow` | 本例：**全部没有** ⇒ 排除 Win32 区域裁剪与分层窗口 |
| 4 | **Process Monitor** | 过滤器 `Path contains FLiNG`，然后**在虚拟机里**运行一次 | 看到它把载荷写到哪个目录（几个文件、多大） |
| 5 | 资源管理器 | 去上一步的目录取落地文件 | 得到一个（或几个）新 exe/dll |
| 6 | **dnSpyEx**（`github.com/dnSpyEx/dnSpy`） | 拖入落地程序集 → 左侧找 `MainWindow`/`TrainerWindow` → 看 `InitializeComponent` 附近的 XAML 资源 | WPF 的话：`WindowStyle="None"`、`AllowsTransparency="True"`、`Border CornerRadius="…"` ⇒ **边框就是这条 Border 画的** |

> [!danger] 第 4 步要在虚拟机/沙箱里做
> 该 exe **无数字签名**。落地文件拿到手前，别在主力机上跑。

## 三类结论

| 类别 | 内容 |
|---|---|
| ✅ 已验证 | 外壳 = .NET Framework ✓ ｜ UI 框架名不在明文 ⇒ 载荷在壳内 ✓ ｜ 有临时文件释放行为（`FLiNGTrainer.tmp`）✓ ｜ 无 gdi32/gdiplus/d3d 导入 ⇒ 不是 GDI/DX 自绘 ✓ ｜ 做了 DPI 感知 ✓ |
| ❌ 已证伪 | **不是** Win32 裸窗口 + `SetWindowRgn`（导入表里没有该函数）✗ ｜ **不是** GDI+ 自绘（无 gdiplus）✗ |
| ⏳ 待验证 | 载荷是不是 WPF、边框是不是 `CornerRadius` 圆角 Border ｜ `.fptable` 是什么 ｜ 载荷释放到哪个具体目录 |

## 关联

- 功能与改法：[[70-拆解-Plus20 Trainer 的 20 个功能与改法]]
- 子库索引：[[00-杀戮尖塔2 索引 MOC]]
- 通用工具：[[00-通用索引 MOC]]
