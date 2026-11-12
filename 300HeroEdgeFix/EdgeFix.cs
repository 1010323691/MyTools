// EdgeFix - 修复老游戏在高 DPI 缩放下屏幕右/下边缘无法卷屏的问题
// 原理：游戏不感知 DPI，Windows 把物理坐标除以缩放比例再四舍五入后交给游戏。
//       200% 缩放时物理 x=3839 -> 1919.5 -> 1920，超出游戏 0..1919 的范围，于是边缘卷屏失效。
// 做法：游戏在前台时，用系统低级鼠标钩子 (WH_MOUSE_LL) 在鼠标移动"发生之前"检查目标位置，
//       如果会越过 右/下 边界，就拦下这次移动，改为把光标放到边界内的位置。
//       光标从头到尾到不了最后一个像素，不会出现"先过去再被拽回来"的抢鼠标现象。
//       不注入游戏、不读写游戏进程内存、不修改任何游戏文件。
//       （上一版用 ClipCursor，但游戏自己也会反复设置 ClipCursor，两边互相覆盖导致抢鼠标。）
// 用法：EdgeFix.exe [进程名 ...]   默认进程名：300
//       托盘图标右键可启用/暂停/退出；Ctrl+Alt+F10 切换启用。
//       游戏以管理员身份运行，所以本程序也需要管理员权限（已在清单中声明）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class EdgeFix
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x0200;
    const int INSET = 1;                 // 右/下各让出的物理像素
    static HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static bool enabled = true;
    static volatile bool active = false; // 游戏在前台且已启用
    static int maxX, maxY;               // 光标允许到达的最大物理坐标（含）
    static uint lastPid = 0; static bool lastMatch = false;
    static NotifyIcon tray;
    static ToolStripMenuItem toggleItem;
    static HookProc hookProc;            // 持有引用，防止被 GC
    static IntPtr hook = IntPtr.Zero;

    [STAThread]
    static void Main(string[] args)
    {
        // 必须为 Per-Monitor DPI 感知，钩子里拿到的才是真实物理像素（清单里也声明了）
        try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
        catch { SetProcessDPIAware(); }

        foreach (string a in args) targets.Add(a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a.Substring(0, a.Length - 4) : a);
        if (targets.Count == 0) targets.Add("300");

        Application.EnableVisualStyles();
        var menu = new ContextMenuStrip();
        toggleItem = new ToolStripMenuItem("已启用 (Ctrl+Alt+F10 切换)", null, delegate { Toggle(); });
        toggleItem.Checked = true;
        menu.Items.Add(toggleItem);
        menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { tray.Visible = false; Application.Exit(); }));
        tray = new NotifyIcon();
        tray.Icon = SystemIcons.Application;
        tray.Text = "EdgeFix - " + string.Join(",", targets);
        tray.ContextMenuStrip = menu;
        tray.Visible = true;

        hookProc = HookCallback;
        hook = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            MessageBox.Show("安装鼠标钩子失败，错误码 " + Marshal.GetLastWin32Error(), "EdgeFix");
            return;
        }
        tray.ShowBalloonTip(3000, "EdgeFix 已运行", "目标进程: " + string.Join(", ", targets) + "\n游戏在前台时，鼠标不会进入右/下边缘最后 " + INSET + " 像素。", ToolTipIcon.Info);

        var hk = new HotkeyWindow();
        RegisterHotKey(hk.Handle, 1, 0x0001 | 0x0002 | 0x4000, (uint)Keys.F10); // Alt|Ctrl|NoRepeat

        var timer = new Timer();
        timer.Interval = 50;
        timer.Tick += delegate { Refresh(); };
        timer.Start();
        Refresh();

        Application.ApplicationExit += delegate { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); };
        Application.Run();
    }

    class HotkeyWindow : NativeWindow
    {
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) Toggle(); // WM_HOTKEY
            base.WndProc(ref m);
        }
    }

    static void Toggle()
    {
        enabled = !enabled;
        toggleItem.Checked = enabled;
        toggleItem.Text = (enabled ? "已启用" : "已暂停") + " (Ctrl+Alt+F10 切换)";
        Refresh();
        tray.ShowBalloonTip(1000, "EdgeFix", enabled ? "已启用" : "已暂停", ToolTipIcon.None);
    }

    static bool IsTarget(IntPtr h)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid == lastPid) return lastMatch;
        lastPid = pid; lastMatch = false;
        try { lastMatch = targets.Contains(Process.GetProcessById((int)pid).ProcessName); } catch { }
        return lastMatch;
    }

    // 定时更新：游戏是否在前台、窗口边界在哪
    static void Refresh()
    {
        IntPtr fg = GetForegroundWindow();
        RECT w;
        if (enabled && fg != IntPtr.Zero && IsTarget(fg) && !IsIconic(fg) && GetWindowRect(fg, out w)
            && w.R - w.L > 2 * INSET && w.B - w.T > 2 * INSET)
        {
            maxX = w.R - 1 - INSET;
            maxY = w.B - 1 - INSET;
            active = true;
        }
        else active = false;
    }

    // 鼠标移动生效前被调用：会越界就拦下，改成停在边界上
    static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && active && (int)wParam == WM_MOUSEMOVE)
        {
            var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
            if (m.pt.X > maxX || m.pt.Y > maxY)
            {
                SetCursorPos(Math.Min(m.pt.X, maxX), Math.Min(m.pt.Y, maxY));
                return new IntPtr(1); // 吞掉原始移动，光标不会先越界
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }
}
