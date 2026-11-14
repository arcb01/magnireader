using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MagniReader.Helpers;
using MagniReader.Infrastructure;

namespace MagniReader.Input;

/// <summary>
/// Manages input detection for MagniReader.
///
/// Interaction model (shortcuts are configurable, these are the defaults):
///   - Ctrl+Alt + Left Click → start reading at the clicked position
///   - Ctrl+Alt+R → read the selected text (or the clipboard)
///   - Ctrl+Alt+S → snip an area for the vision feature
///   - Plain Left Click (while reading) → cancel/stop reading
///   - Escape (while reading) → cancel/stop reading
///
/// The low-level mouse and keyboard hooks run on their own thread with its own
/// message loop. Windows calls a low-level hook on the thread that installed it,
/// and waits for it: when the hooks lived on the WPF UI thread, any slow work there
/// (UI Automation calls into a busy app, screenshots) made the mouse and keyboard
/// lag for the whole PC, and Windows silently removed hooks that timed out.
/// The callbacks here only read a few fields and post events to the UI thread.
/// </summary>
public class InputManager : IDisposable
{
    private IntPtr _mouseHookId = IntPtr.Zero;
    private NativeInterop.LowLevelMouseProc? _mouseProc;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private NativeInterop.LowLevelKeyboardProc? _keyboardProc;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private Dispatcher? _dispatcher;

    // Read on the hook thread, written on the UI thread
    private volatile bool _isReading;
    private volatile bool _pickArmed;
    private volatile bool _isHovering;
    private volatile HotkeySet _hotkeys = new(Hotkey.DefaultRead, Hotkey.DefaultSnip, Hotkey.DefaultReadSelection);

    // Hover preview throttle (hook thread only)
    private long _lastHoverTicks;
    private const int HoverThrottleMs = 150;

    // Modifier state as seen by the keyboard hook (hook thread only).
    // Tracked per side so AltGr (sent by Windows as a fake Left Ctrl + Right Alt)
    // isn't mistaken for Ctrl+Alt.
    private uint _lCtrlDownTime;
    private bool _lCtrl, _rCtrl, _fakeCtrl, _lAlt, _rAlt, _lShift, _rShift, _lWin, _rWin;

    private sealed record HotkeySet(Hotkey Read, Hotkey Snip, Hotkey ReadSelection);

    /// <summary>Fired when the read shortcut + Left Click is detected, with screen coordinates.</summary>
    public event Action<int, int>? ReadTriggered;

    /// <summary>Fired when the user left-clicks during reading, or presses Escape.</summary>
    public event Action? CancelTriggered;

    /// <summary>Fired when the snip shortcut is pressed.</summary>
    public event Action? SnipTriggered;

    /// <summary>Fired when the read-selection shortcut is pressed.</summary>
    public event Action? ReadSelectionTriggered;

    /// <summary>
    /// Fired when the read modifiers are held (or pick mode is armed) and the mouse moves.
    /// Throttled to ~150ms. Provides screen coordinates (X, Y).
    /// </summary>
    public event Action<int, int>? HoverPreview;

    /// <summary>Fired when the read modifiers are released after hovering.</summary>
    public event Action? HoverEnd;

    /// <summary>Fired when pick mode turns on or off (a pick, Esc, or ArmPickMode).</summary>
    public event Action<bool>? PickModeChanged;

    /// <summary>
    /// Returns true when a screen point (physical pixels) is on MagniReader's own UI.
    /// Called on the hook thread, so it must be thread-safe and fast (Win32 only).
    /// </summary>
    public Func<int, int, bool>? IsOwnUiAt { get; set; }

    /// <summary>The click shortcut currently in use (for tooltips).</summary>
    public Hotkey ReadHotkey => _hotkeys.Read;
    public Hotkey SnipHotkey => _hotkeys.Snip;
    public Hotkey ReadSelectionHotkey => _hotkeys.ReadSelection;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3,
                      VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                      VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    /// <summary>Scan code Windows gives the fake Left Ctrl it sends with AltGr.</summary>
    private const uint AltGrFakeCtrlScanCode = 0x21D;

    /// <summary>
    /// Applies the shortcuts from settings. Invalid text falls back to the default.
    /// </summary>
    public void ApplyHotkeys(string? read, string? snip, string? readSelection)
    {
        _hotkeys = new HotkeySet(
            Hotkey.ParseOr(read, requireKey: false, Hotkey.DefaultRead),
            Hotkey.ParseOr(snip, requireKey: true, Hotkey.DefaultSnip),
            Hotkey.ParseOr(readSelection, requireKey: true, Hotkey.DefaultReadSelection));
    }

    /// <summary>
    /// Starts the hook thread and installs the global mouse and keyboard hooks.
    /// Call once at app startup, on the UI thread.
    /// </summary>
    public void Install()
    {
        if (_hookThread != null) return;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() => HookThreadMain(ready))
        {
            IsBackground = true,
            Name = "MagniReader input hooks",
            Priority = ThreadPriority.AboveNormal,
        };
        _hookThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            CrashLogger.LogWarning("[Input] Hook thread did not start in time");
    }

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        try
        {
            _hookThreadId = NativeInterop.GetCurrentThreadId();
            _mouseProc = MouseHookCallback;
            _keyboardProc = KeyboardHookCallback;

            using (var process = Process.GetCurrentProcess())
            using (var module = process.MainModule!)
            {
                var moduleHandle = NativeInterop.GetModuleHandle(module.ModuleName);
                _mouseHookId = NativeInterop.SetWindowsHookEx(NativeInterop.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
                _keyboardHookId = NativeInterop.SetWindowsHookEx(NativeInterop.WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
            }

            if (_mouseHookId == IntPtr.Zero || _keyboardHookId == IntPtr.Zero)
                CrashLogger.LogError($"[Input] Hook install failed (mouse={_mouseHookId != IntPtr.Zero}, " +
                                     $"keyboard={_keyboardHookId != IntPtr.Zero}, error={Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("[Input] Hook thread failed to start", ex);
        }
        finally
        {
            ready.Set();
        }

        // Low-level hooks are called through this thread's message loop.
        while (NativeInterop.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeInterop.TranslateMessage(ref msg);
            NativeInterop.DispatchMessage(ref msg);
        }

        if (_mouseHookId != IntPtr.Zero) NativeInterop.UnhookWindowsHookEx(_mouseHookId);
        if (_keyboardHookId != IntPtr.Zero) NativeInterop.UnhookWindowsHookEx(_keyboardHookId);
        _mouseHookId = _keyboardHookId = IntPtr.Zero;
    }

    /// <summary>Tells the InputManager whether we are reading, so it knows how to treat left clicks.</summary>
    public void SetReadingState(bool isReading) => _isReading = isReading;

    /// <summary>
    /// Arms (or disarms) pick mode: the next left click outside MagniReader's own UI
    /// reads the text under it, as if the read shortcut were held. Call on the UI thread.
    /// </summary>
    public void ArmPickMode(bool armed)
    {
        if (_pickArmed == armed) return;
        _pickArmed = armed;
        if (!armed && _isHovering)
        {
            _isHovering = false;
            HoverEnd?.Invoke();
        }
        PickModeChanged?.Invoke(armed);
    }

    public bool IsPickArmed => _pickArmed;

    private void Post(Action action) => _dispatcher?.BeginInvoke(action);

    // ─── Modifier tracking ───────────────────────────────────────

    private HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (_lCtrl || _rCtrl) m |= HotkeyModifiers.Ctrl;
        // Right Alt only counts as Alt on layouts without AltGr (no fake Ctrl with it).
        if (_lAlt || (_rAlt && !_fakeCtrl)) m |= HotkeyModifiers.Alt;
        if (_lShift || _rShift) m |= HotkeyModifiers.Shift;
        if (_lWin || _rWin) m |= HotkeyModifiers.Win;
        return m;
    }

    /// <summary>
    /// Clears modifiers Windows says are no longer down. A key-up can be missed
    /// (secure desktop, Win+L, another hook swallowing it), which would otherwise
    /// leave a modifier "stuck" and make every click read.
    /// </summary>
    private void ResyncModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (_lCtrl && !Down(VK_LCONTROL)) _lCtrl = false;
        if (_fakeCtrl && !Down(VK_LCONTROL)) _fakeCtrl = false;
        if (_rCtrl && !Down(VK_RCONTROL)) _rCtrl = false;
        if (_lAlt && !Down(VK_LMENU)) _lAlt = false;
        if (_rAlt && !Down(VK_RMENU)) _rAlt = false;
        if (_lShift && !Down(VK_LSHIFT)) _lShift = false;
        if (_rShift && !Down(VK_RSHIFT)) _rShift = false;
        if (_lWin && !Down(VK_LWIN)) _lWin = false;
        if (_rWin && !Down(VK_RWIN)) _rWin = false;
    }

    /// <summary>Updates modifier state. Returns true if the key was a modifier.</summary>
    private bool TrackModifier(int vk, uint scanCode, uint time, bool down)
    {
        switch (vk)
        {
            case VK_LCONTROL:
            case VK_CONTROL:
                if (down)
                {
                    if (scanCode == AltGrFakeCtrlScanCode) _fakeCtrl = true;
                    else { _lCtrl = true; _lCtrlDownTime = time; }
                }
                else
                {
                    if (scanCode == AltGrFakeCtrlScanCode) _fakeCtrl = false;
                    else { _lCtrl = false; }
                }
                return true;
            case VK_RCONTROL: _rCtrl = down; return true;
            case VK_LMENU:
            case VK_MENU: _lAlt = down; return true;
            case VK_RMENU:
                // Some drivers don't mark AltGr's fake Ctrl: it arrives with the exact
                // same timestamp as the Right Alt, which a person can't type.
                if (down && _lCtrl && time == _lCtrlDownTime) { _lCtrl = false; _fakeCtrl = true; }
                _rAlt = down;
                if (!down) _fakeCtrl = false; // AltGr released: its fake Ctrl goes with it
                return true;
            case VK_LSHIFT:
            case VK_SHIFT: _lShift = down; return true;
            case VK_RSHIFT: _rShift = down; return true;
            case VK_LWIN: _lWin = down; return true;
            case VK_RWIN: _rWin = down; return true;
        }
        return false;
    }

    // ─── Hooks (run on the hook thread) ──────────────────────────

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeInterop.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

        try
        {
            if (wParam == (IntPtr)NativeInterop.WM_LBUTTONDOWN)
            {
                ResyncModifiers();
                var click = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int x = click.pt.X, y = click.pt.Y;
                bool readCombo = CurrentModifiers() == _hotkeys.Read.Modifiers;
                bool onOwnUi = IsOwnUiAt?.Invoke(x, y) == true;

                if (onOwnUi)
                {
                    // Clicks on the control bar go to its buttons, never cancel or read.
                }
                else if (_pickArmed)
                {
                    Post(() =>
                    {
                        ArmPickMode(false);
                        ReadTriggered?.Invoke(x, y);
                    });
                    return (IntPtr)1;
                }
                else if (readCombo)
                {
                    Post(() => ReadTriggered?.Invoke(x, y));
                    return (IntPtr)1; // swallow so the app underneath doesn't get the click
                }
                else if (_isReading)
                {
                    Post(() => CancelTriggered?.Invoke());
                    return (IntPtr)1;
                }
            }
            else if (wParam == (IntPtr)NativeInterop.WM_MOUSEMOVE)
            {
                bool readCombo = CurrentModifiers() == _hotkeys.Read.Modifiers;
                if ((readCombo || _pickArmed) && !_isReading)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastHoverTicks >= HoverThrottleMs)
                    {
                        _lastHoverTicks = now;
                        _isHovering = true;
                        var move = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        int x = move.pt.X, y = move.pt.Y;
                        Post(() => HoverPreview?.Invoke(x, y));
                    }
                }
                else if (_isHovering && !_pickArmed)
                {
                    _isHovering = false;
                    Post(() => HoverEnd?.Invoke());
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Input] Mouse hook error: {ex.Message}");
        }

        return NativeInterop.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeInterop.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        try
        {
            int msg = (int)wParam;
            bool down = msg == NativeInterop.WM_KEYDOWN || msg == NativeInterop.WM_SYSKEYDOWN;
            bool up = msg == NativeInterop.WM_KEYUP || msg == NativeInterop.WM_SYSKEYUP;
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)kb.vkCode;

            if ((down || up) && TrackModifier(vk, kb.scanCode, kb.time, down))
            {
                // Releasing Ctrl, Alt (WM_SYSKEYUP), etc. ends the hover preview
                if (up && _isHovering && !_pickArmed && CurrentModifiers() != _hotkeys.Read.Modifiers)
                {
                    _isHovering = false;
                    Post(() => HoverEnd?.Invoke());
                }
            }
            else if (down)
            {
                if (vk == NativeInterop.VK_ESCAPE)
                {
                    if (_pickArmed) Post(() => ArmPickMode(false));
                    if (_isReading) Post(() => CancelTriggered?.Invoke());
                }
                else
                {
                    var mods = CurrentModifiers();
                    var hk = _hotkeys;
                    if (vk == hk.Snip.VirtualKey && mods == hk.Snip.Modifiers && !_isReading)
                    {
                        Post(() => SnipTriggered?.Invoke());
                        return (IntPtr)1; // swallow
                    }
                    if (vk == hk.ReadSelection.VirtualKey && mods == hk.ReadSelection.Modifiers)
                    {
                        Post(() => ReadSelectionTriggered?.Invoke());
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Input] Keyboard hook error: {ex.Message}");
        }

        return NativeInterop.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        var thread = _hookThread;
        if (thread == null) return;
        _hookThread = null;
        if (_hookThreadId != 0)
            NativeInterop.PostThreadMessage(_hookThreadId, NativeInterop.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(TimeSpan.FromSeconds(2)))
            CrashLogger.LogWarning("[Input] Hook thread did not stop in time");
    }

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
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
