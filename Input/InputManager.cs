using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using MagniReader.Helpers;

namespace MagniReader.Input;

/// <summary>
/// Manages input detection for MagniReader.
/// 
/// Interaction model:
///   - Ctrl+Alt + Left Click → start reading at the clicked position
///   - Plain Left Click (while reading) → cancel/stop reading
///   - Escape (while reading) → cancel/stop reading
/// 
/// Uses a low-level mouse hook that is always active to detect
/// Ctrl+Alt+Click combos and plain clicks for cancellation.
/// </summary>
public class InputManager : IDisposable
{
    // Mouse hook
    private IntPtr _mouseHookId = IntPtr.Zero;
    private NativeInterop.LowLevelMouseProc? _mouseProc;

    // Keyboard hook (for Escape cancel)
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private NativeInterop.LowLevelKeyboardProc? _keyboardProc;

    private bool _isReading;

    /// <summary>
    /// Fired when Ctrl+Alt + Left Click is detected. 
    /// Provides the click screen coordinates (X, Y).
    /// </summary>
    public event Action<int, int>? ReadTriggered;

    /// <summary>
    /// Fired when the user left-clicks (without Ctrl+Alt) during reading, 
    /// or presses Escape.
    /// </summary>
    public event Action? CancelTriggered;

    /// <summary>
    /// Fired when Ctrl+S is pressed to trigger screen snip analysis.
    /// </summary>
    public event Action? SnipTriggered;

    // ─── Win32 for checking modifier key state ──────────────────
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt key

    /// <summary>
    /// Installs the global mouse and keyboard hooks. 
    /// Call once at app startup.
    /// </summary>
    public void Install()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        var moduleHandle = NativeInterop.GetModuleHandle(module.ModuleName);

        _mouseHookId = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_MOUSE_LL,
            _mouseProc,
            moduleHandle,
            0
        );

        _keyboardHookId = NativeInterop.SetWindowsHookEx(
            NativeInterop.WH_KEYBOARD_LL,
            _keyboardProc,
            moduleHandle,
            0
        );

        System.Diagnostics.Debug.WriteLine(
            $"InputManager installed. Mouse hook: {_mouseHookId != IntPtr.Zero}, " +
            $"Keyboard hook: {_keyboardHookId != IntPtr.Zero}");
    }

    /// <summary>
    /// Tell the InputManager whether we are currently reading, 
    /// so it knows how to interpret left clicks.
    /// </summary>
    public void SetReadingState(bool isReading)
    {
        _isReading = isReading;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)NativeInterop.WM_LBUTTONDOWN)
        {
            bool ctrlHeld = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool altHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

            if (ctrlHeld && altHeld)
            {
                // Ctrl+Alt+Click → trigger reading at click position
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int x = hookStruct.pt.X;
                int y = hookStruct.pt.Y;

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ReadTriggered?.Invoke(x, y);
                });

                // Swallow the click so the underlying app doesn't receive it
                return (IntPtr)1;
            }
            else if (_isReading)
            {
                // Plain click while reading → cancel
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    CancelTriggered?.Invoke();
                });

                // Swallow this click too so it doesn't interact with the app behind
                return (IntPtr)1;
            }
        }

        return NativeInterop.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private const int VK_S = 0x53;

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)NativeInterop.WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);

            if (vkCode == NativeInterop.VK_ESCAPE && _isReading)
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    CancelTriggered?.Invoke();
                });
            }
            else if (vkCode == VK_S && !_isReading)
            {
                bool ctrlHeld = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool altHeld = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

                if (ctrlHeld && altHeld)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        SnipTriggered?.Invoke();
                    });
                    return (IntPtr)1; // swallow
                }
            }
        }

        return NativeInterop.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHookId != IntPtr.Zero)
        {
            NativeInterop.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }

        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeInterop.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
    }

    // ─── MSLLHOOKSTRUCT for reading click coordinates ───────────
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
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
