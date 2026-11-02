---
title: 74 · 入门：环境搭建与启动（虚拟机无 GPU 也能跑）
aliases: [STS2 启动, 视频驱动报错, opengl3, 环境搭建]
tags: [入门, 杀戮尖塔2, 环境, 启动]
created: 2026-10-31
---

上接：[[73-复刻-20 个功能逐个实现（含代码）]] · 下接：[[00-杀戮尖塔2 索引 MOC]]

# 74 · 环境搭建与启动（虚拟机无 GPU 也能跑）

> [!important] 一句话
> 这台机器是 **VMware 虚拟机**，Godot 4 默认用 **Vulkan/D3D12** 渲染 ⇒ 直接双击 exe 会弹
> **`Unable to initialize video driver`**，游戏在引擎初始化阶段就死掉（`.NET` 永远不会加载）。
> **解法：加 `--rendering-driver opengl3`**（游戏目录里现成的 `launch_opengl.bat` 就是这个）。

## 我是怎么做的（真实命令 + 真实输出）

### ① 直接启动 → 报错
```powershell
& "D:\Games\Slay the Spire 2\SlayTheSpire2.exe"
```
随后枚举窗口，看到错误框（真实输出）：
```text
hwnd=328308 pid=5624 vis=1 class='#32770' title='Unable to initialize video driver' size=454x370
```
此时进程虽然活着，但**模块只有 64 个**，且 **没有 `coreclr` / `sts2.dll`** ✗ —— 说明托管侧根本没起来。

### ② 换渲染后端 → 秒起
```powershell
& "D:\Games\Slay the Spire 2\SlayTheSpire2.exe" --rendering-driver opengl3 --rendering-method gl_compatibility
```
真实输出（轮询模块）：
```text
[ 0s] pid=7608 模块=68  coreclr=False godotsharp=False sts2=False
[ 2s] pid=7608 模块=100 coreclr=True  godotsharp=True  sts2=True
★★ .NET 已就绪，可以开始改游戏了 ✓
   coreclr.dll  clrjit.dll  sts2.dll  GodotSharp.dll
```

### ③ 顺带确认：三个启动脚本就是这么写的（游戏自带）
```text
launch_opengl.bat -> "%~dp0SlayTheSpire2.exe" --rendering-driver opengl3 %*
launch_vulkan.bat -> "%~dp0SlayTheSpire2.exe" --rendering-driver vulkan %*
launch_d3d12.bat  -> "%~dp0SlayTheSpire2.exe" --rendering-driver d3d12 %*
```

> [!tip] 判定"托管侧起来了没有"的硬指标
> 看进程模块里有没有 **`coreclr.dll` + `sts2.dll`**。
> 没有 ⇒ 游戏还没进托管阶段，**此时任何 .NET 侧的分析/修改都不会成功** ✗（先解决启动）。

## 你该怎么做（零基础照抄）

| 步 | 用什么工具 | 具体操作 | 预期看到 |
|---|---|---|---|
| 1 | 资源管理器 | 进 `D:\Games\Slay the Spire 2\`，**双击 `launch_opengl.bat`** | 游戏窗口出现（不再是 454×370 的错误框） |
| 2 | 任务管理器（详细信息） | 找 `SlayTheSpire2.exe` → 右键「属性/打开文件位置」确认 pid | 进程存在 |
| 3 | **本项目脚本** | `python D:\sts2\sts2_wait.py` | 打印 `coreclr=True godotsharp=True sts2=True` ⇒ 可开始 |
| 4 | **本项目脚本** | `python D:\sts2\sts2_recon.py` | 打印 pid / 模块数 / `读 PE 头: OK`（= 能读写内存） |
| 5 | 若还要指定后端 | 命令行加 `--rendering-driver opengl3` / `vulkan` / `d3d12` | 对应启动脚本里的写法 |

> [!warning] 两个前置条件（缺一不可）
> 1. **Python 必须是 64 位**（`python -c "import struct;print(struct.calcsize('P')*8)"` → 要输出 `64`），否则读不了 64 位进程 ✗
> 2. 读内存要用**管理员权限**运行脚本（`OpenProcess` 返回 0 就是权限不够）

## 三类结论

| 类别 | 内容 |
|---|---|
| ✅ 已验证 | 直接启动 → 弹 `Unable to initialize video driver` ✅ ｜ 加 `--rendering-driver opengl3` 后 **2 秒内** `coreclr/clrjit/sts2.dll/GodotSharp.dll` 全部加载 ✅ ｜ 64 位 Python + `OpenProcess` + `ReadProcessMemory` 均可读主模块 PE 头 ✅ |
| ❌ 已证伪 | "进程活着 = 游戏在跑" ✗（错误框阶段进程也在，但托管侧未启动）｜ `.NET` 会在启动时自动加载 ✗（渲染初始化失败就不加载） |
| ⏳ 待验证 | 这台 VM 上 OpenGL 后端能否**稳定进到主菜单并开局**（下一步实测）｜`0Harmony.dll` 是否被游戏加载（当前未加载 ⇒ Harmony 路线需自己注入） |

## 关联

- 下一步（功能复刻）：[[73-复刻-20 个功能逐个实现（含代码）]]
- 原理：[[72-原理-为什么按方法名改游戏有效]]
- 子库索引：[[00-杀戮尖塔2 索引 MOC]]
