using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagniReader.Helpers;

/// <summary>
/// Copies the selection of the app in front by sending it Ctrl+C, for apps that
/// don't show their selection to UI Automation. Google Docs draws its text on a
/// canvas, so the read-selection shortcut used to read whatever was on the
/// clipboard before instead of what the user had just selected.
///
/// Only sent to apps known to treat Ctrl+C as Copy: in a terminal Ctrl+C stops
/// the running program.
/// </summary>
public static class SelectionCopier
{
    private static readonly HashSet<string> CopySafeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers (Google Docs, Office online, web pages)
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "iexplore",
        // Office and other word processors / readers
        "WINWORD", "POWERPNT", "EXCEL", "ONENOTE", "OUTLOOK", "olk", "soffice", "soffice.bin",
        "wps", "wpp", "et", "AcroRd32", "Acrobat", "FoxitPDFReader", "SumatraPDF",
        "notepad", "wordpad", "Notion", "Obsidian", "Kindle", "thunderbird",
    };

    /// <summary>
    /// Sends Ctrl+C to the foreground app and waits for the clipboard to change.
    /// Returns true when the app put something new on the clipboard.
    /// Call on the UI thread (it waits asynchronously).
    /// </summary>
    public static async Task<bool> TryCopyFromForegroundAsync()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;

        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch { return false; }
        if (!CopySafeApps.Contains(name)) return false;

        // The shortcut's own keys must be up first: Ctrl+Alt+C in Word types ©
        // over the selection.
        var wait = Stopwatch.StartNew();
        while (AnyModifierDown())
        {
            if (wait.ElapsedMilliseconds > 1500) return false;
            await Task.Delay(20);
        }

        uint before = GetClipboardSequenceNumber();
        if (!SendCtrlC()) return false;

        wait.Restart();
        while (wait.ElapsedMilliseconds < 700)
        {
            await Task.Delay(25);
            if (GetClipboardSequenceNumber() != before)
            {
                await Task.Delay(30); // let the app finish writing every format
                return true;
            }
        }
        return false;
    }

    private static bool AnyModifierDown()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        return Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C); // Shift, Ctrl, Alt, Win
    }

    private static bool SendCtrlC()
    {
        const ushort VK_CONTROL = 0x11, VK_C = 0x43;
        var inputs = new[]
        {
            Key(VK_CONTROL, false), Key(VK_C, false), Key(VK_C, true), Key(VK_CONTROL, true),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x0002u : 0u } },
    };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // Sized for the largest member (MOUSEINPUT) so cbSize matches what Windows expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }
}
