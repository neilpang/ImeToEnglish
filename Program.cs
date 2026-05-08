using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ImeToEnglish;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new System.Threading.Mutex(true, "ImeToEnglish.SingleInstance", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly WinEventDelegate _proc;
    private readonly IntPtr _hook;
    private readonly IntPtr _enLayout;
    private readonly System.Windows.Forms.Timer _deferTimer;
    private readonly AutomationFocusChangedEventHandler _uiaFocusHandler;
    private readonly CursorHider _cursorHider = new();
    private IntPtr _pendingHwnd;
    private IntPtr _iconHandle;
    private bool _enabled = true;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WM_IME_CONTROL = 0x0283;
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const int IMC_SETCONVERSIONMODE = 0x0002;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const int IME_CMODE_ALPHANUMERIC = 0x0000;
    private const int IME_CMODE_NATIVE = 0x0001;
    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_SHIFT = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KLF_ACTIVATE = 0x00000001;
    private const int OBJID_WINDOW = 0;
    private const int LANG_ENGLISH = 0x09;
    private const int LANG_CHINESE = 0x04;

    public TrayApp()
    {
        _enLayout = LoadKeyboardLayout("00000409", KLF_ACTIVATE);

        _proc = OnForeground;
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);

        _deferTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _deferTimer.Tick += (_, _) =>
        {
            _deferTimer.Stop();
            IntPtr hwnd = _pendingHwnd;
            if (hwnd != IntPtr.Zero && hwnd == GetForegroundWindow())
                SwitchToEnglish(hwnd);
        };

        _uiaFocusHandler = OnUiaFocusChanged;
        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_uiaFocusHandler);
        }
        catch { /* UIA may be unavailable in some environments */ }

        var menu = new ContextMenuStrip();
        var toggle = new ToolStripMenuItem("切焦点时切英文") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += (_, _) => _enabled = toggle.Checked;
        menu.Items.Add(toggle);

        var hideCursor = new ToolStripMenuItem("敲键盘时隐藏鼠标")
        {
            Checked = true,
            CheckOnClick = true,
        };
        hideCursor.CheckedChanged += (_, _) => _cursorHider.Enabled = hideCursor.Checked;
        menu.Items.Add(hideCursor);

        var autoStart = new ToolStripMenuItem("开机自启动")
        {
            Checked = IsAutoStartEnabled(),
            CheckOnClick = true,
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            try { SetAutoStart(autoStart.Checked); }
            catch (Exception ex)
            {
                MessageBox.Show("无法修改自启动设置: " + ex.Message,
                    "ImeToEnglish", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                autoStart.Checked = IsAutoStartEnabled();
            }
        };
        menu.Items.Add(autoStart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = CreateTrayIcon(out _iconHandle),
            Visible = true,
            Text = "Force English IME on focus change",
            ContextMenuStrip = menu,
        };

        SwitchToEnglish(GetForegroundWindow());

        _cursorHider.Enabled = true;

        // Safety net: restore cursors no matter how we exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _cursorHider.Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _cursorHider.Restore();
        Application.ApplicationExit += (_, _) => _cursorHider.Restore();
    }

    private void OnForeground(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_enabled || hwnd == IntPtr.Zero) return;
        if (idObject != OBJID_WINDOW) return;
        _pendingHwnd = hwnd;
        _deferTimer.Stop();
        _deferTimer.Start();
    }

    private void OnUiaFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        if (!_enabled) return;
        if (sender is not AutomationElement element) return;

        try
        {
            if (!IsChromiumAddressBar(element)) return;
        }
        catch { return; }

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero) SwitchToEnglish(hwnd);
    }

    private static bool IsChromiumAddressBar(AutomationElement element)
    {
        if (element.Current.ControlType != ControlType.Edit) return false;

        int pid = element.Current.ProcessId;
        string procName;
        try
        {
            using var proc = Process.GetProcessById(pid);
            procName = proc.ProcessName.ToLowerInvariant();
        }
        catch { return false; }

        if (procName != "chrome" && procName != "msedge" &&
            procName != "brave" && procName != "vivaldi" &&
            procName != "opera" && procName != "thorium")
            return false;

        // The omnibox lives in the browser chrome (Toolbar/Pane → Window).
        // Web-page <input> elements are inside a Document. Walking up,
        // if we hit a Document before a Window, it's a page field — skip it.
        var walker = TreeWalker.ControlViewWalker;
        var node = walker.GetParent(element);
        for (int i = 0; i < 30 && node != null; i++)
        {
            var ct = node.Current.ControlType;
            if (ct == ControlType.Document) return false;
            if (ct == ControlType.Window) return true;
            node = walker.GetParent(node);
        }
        return false;
    }

    private void SwitchToEnglish(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        uint threadId = GetWindowThreadProcessId(hwnd, out _);
        IntPtr currentLayout = GetKeyboardLayout(threadId);
        int langId = (int)((long)currentLayout & 0xFFFF);
        int primaryLang = langId & 0x3FF;

        if (primaryLang == LANG_ENGLISH) return;

        if (primaryLang == LANG_CHINESE)
        {
            IntPtr imeWnd = ImmGetDefaultIMEWnd(hwnd);
            if (imeWnd == IntPtr.Zero) return;

            SendMessage(imeWnd, WM_IME_CONTROL,
                new IntPtr(IMC_SETCONVERSIONMODE),
                new IntPtr(IME_CMODE_ALPHANUMERIC));

            IntPtr modeResult = SendMessage(imeWnd, WM_IME_CONTROL,
                new IntPtr(IMC_GETCONVERSIONMODE), IntPtr.Zero);
            int mode = modeResult.ToInt32();
            if ((mode & IME_CMODE_NATIVE) != 0)
            {
                SendShiftTap();
            }
            return;
        }

        if (_enLayout != IntPtr.Zero)
        {
            PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, _enLayout);
        }
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ImeToEnglish";

    private static string ExePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    private static string ExpectedRunValue => "\"" + ExePath + "\"";

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key?.GetValue(RunValueName) is not string value) return false;
        return string.Equals(value, ExpectedRunValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, ExePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开注册表 Run 键");
        if (enabled)
            key.SetValue(RunValueName, ExpectedRunValue, RegistryValueKind.String);
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static Icon CreateTrayIcon(out IntPtr handle)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var rect = new Rectangle(1, 1, size - 2, size - 2);
            using var path = RoundedRect(rect, 7);
            using var bgBrush = new LinearGradientBrush(
                rect,
                Color.FromArgb(255, 56, 140, 230),
                Color.FromArgb(255, 24, 82, 178),
                LinearGradientMode.Vertical);
            g.FillPath(bgBrush, path);

            using var borderPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);
            g.DrawPath(borderPen, path);

            using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            var textRect = new RectangleF(0, 0, size, size);
            using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            var shadowRect = new RectangleF(0, 1, size, size);
            g.DrawString("EN", font, shadow, shadowRect, fmt);
            g.DrawString("EN", font, Brushes.White, textRect, fmt);
        }

        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static void SendShiftTap()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_SHIFT;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_SHIFT;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            try { Automation.RemoveAutomationFocusChangedEventHandler(_uiaFocusHandler); }
            catch { }
            _cursorHider.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        }
        base.Dispose(disposing);
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
}

internal sealed class CursorHider : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint SPI_SETCURSORS = 0x0057;

    // System cursor IDs (OCR_*) — covers the standard set on Win10/11.
    private static readonly uint[] CursorIds =
    {
        32512, 32513, 32514, 32515, 32516,
        32642, 32643, 32644, 32645, 32646,
        32648, 32649, 32650, 32651,
    };

    private readonly System.Windows.Forms.Timer _hideExpiry;
    private readonly HookProc _kbProc;
    private readonly HookProc _msProc;
    private IntPtr _kbHook;
    private IntPtr _msHook;
    private bool _hidden;
    private bool _enabled;
    private long _lastMouseMoveTicks;

    public CursorHider()
    {
        _hideExpiry = new System.Windows.Forms.Timer { Interval = 2000 };
        _hideExpiry.Tick += (_, _) => { _hideExpiry.Stop(); Restore(); };
        _kbProc = KeyboardHookProc;
        _msProc = MouseHookProc;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            if (value) Install(); else Uninstall();
        }
    }

    private void Install()
    {
        IntPtr hMod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, hMod, 0);
    }

    private void Uninstall()
    {
        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
        _hideExpiry.Stop();
        Restore();
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0)
        {
            // Don't hide if mouse moved within last 500ms — prevents flicker
            // when user types while moving the mouse.
            long now = Environment.TickCount64;
            if (now - _lastMouseMoveTicks > 500)
            {
                Hide();
                _hideExpiry.Stop();
                _hideExpiry.Start();
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0 && wParam.ToInt32() == WM_MOUSEMOVE)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((info.flags & LLMHF_INJECTED) == 0)
                {
                    _lastMouseMoveTicks = Environment.TickCount64;
                    if (_hidden)
                    {
                        _hideExpiry.Stop();
                        Restore();
                    }
                }
            }
            catch { /* ignore */ }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Hide()
    {
        if (_hidden) return;
        foreach (uint id in CursorIds)
        {
            IntPtr blank = CreateBlankCursor();
            if (blank != IntPtr.Zero)
                SetSystemCursor(blank, id);
        }
        _hidden = true;
    }

    public void Restore()
    {
        if (!_hidden) return;
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        _hidden = false;
    }

    private static IntPtr CreateBlankCursor()
    {
        const int W = 32, H = 32;
        int sz = W * H / 8;
        byte[] and = new byte[sz];
        byte[] xor = new byte[sz];
        for (int i = 0; i < sz; i++) { and[i] = 0xFF; xor[i] = 0x00; }
        return CreateCursor(IntPtr.Zero, 0, 0, W, H, and, xor);
    }

    public void Dispose() => Uninstall();

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot,
        int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
