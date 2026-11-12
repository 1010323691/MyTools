# 300HeroEdgeFix (EdgeFix)

修复老游戏（默认针对《300英雄》，进程名 `300`）在 Windows 高 DPI 缩放下，**鼠标移到屏幕右边缘 / 下边缘时无法卷屏**的问题。

## 问题原因

游戏本身不感知 DPI，Windows 会把物理坐标除以缩放比例、四舍五入后再交给游戏。

以 4K 屏 + 200% 缩放为例：物理坐标 `x = 3839` → `1919.5` → 四舍五入为 `1920`，超出了游戏认为的 `0..1919` 范围，于是右/下边缘卷屏失效。左/上边缘不受影响。

## 解决思路

游戏在前台时，EdgeFix 通过系统低级鼠标钩子（`WH_MOUSE_LL`）在每次鼠标移动**生效之前**检查目标位置：如果会越过右/下边界，就拦下这次移动，改为把光标放在边界内 1 个物理像素的位置。

- 光标从头到尾到不了最后一个像素，不会出现“先过去再被拽回来”的抢鼠标现象。
- 不注入游戏、不读写游戏进程内存、不修改任何游戏文件。
- 之前尝试过 `ClipCursor`，但游戏自己也会反复设置 `ClipCursor`，两边互相覆盖导致抢鼠标，因此改用钩子方案。

## 使用方法

1. 下载 [`EdgeFix.exe`](EdgeFix.exe)（或按下文自行编译）。
2. 双击运行（需要管理员权限，因为游戏本身以管理员身份运行；程序清单中已声明，会弹出 UAC 提示）。
3. 正常进入游戏即可。程序常驻系统托盘：
   - 托盘图标右键：启用 / 暂停 / 退出
   - 快捷键 `Ctrl + Alt + F10`：切换启用 / 暂停

只在目标游戏窗口处于前台、且未最小化时生效；切到其他窗口时鼠标行为完全不受影响。

### 用于其他游戏

可以通过命令行参数指定一个或多个进程名（带不带 `.exe` 都行）：

```bat
EdgeFix.exe 300
EdgeFix.exe game1 game2.exe
```

不带参数时默认目标进程名为 `300`。

## 自行编译

只需要 Windows 自带的 .NET Framework 4.x 编译器，无需安装 Visual Studio：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32manifest:EdgeFix.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:EdgeFix.exe EdgeFix.cs
```

## 文件说明

| 文件 | 说明 |
| --- | --- |
| `EdgeFix.cs` | 全部源代码 |
| `EdgeFix.manifest` | 应用清单：请求管理员权限、声明 Per-Monitor V2 DPI 感知 |
| `EdgeFix.exe` | 编译好的程序 |

## 运行环境

- Windows 10 1703 及以上（Per-Monitor V2 DPI 感知），Windows 11 可用
- .NET Framework 4.x（Windows 10/11 自带）
