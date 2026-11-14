using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MagniReader.Infrastructure;

namespace MagniReader.UI;

/// <summary>
/// Shortcuts tab: click a shortcut, press the new keys, and it is checked and kept until Save.
///
/// Shortcuts are stored as text like "Ctrl+Alt+S": modifiers in the order Ctrl, Alt, Shift, Win,
/// then one key (A–Z, 0–9 or F1–F12). The read-at-cursor shortcut is modifiers only
/// ("Ctrl+Alt"), because it is held while clicking.
/// </summary>
public partial class SettingsWindow
{
    private const string ReadId = "Read";
    private const string SelectionId = "Selection";
    private const string SnipId = "Snip";

    private static readonly Dictionary<string, string> DefaultHotkeys = new()
    {
        [ReadId] = "Ctrl+Alt",
        [SelectionId] = "Ctrl+Alt+R",
        [SnipId] = "Ctrl+Alt+S",
    };

    private static readonly Dictionary<string, string> HotkeyNames = new()
    {
        [ReadId] = "Read text under the cursor",
        [SelectionId] = "Read selected text",
        [SnipId] = "Describe a screen area",
    };

    /// <summary>
    /// True while the user is typing a new shortcut, so the app doesn't act on the
    /// current shortcut (e.g. open the snipping tool) while it is being replaced.
    /// Read from the keyboard hook thread, hence volatile.
    /// </summary>
    public static bool IsRecordingShortcut
    {
        get => _isRecordingShortcut;
        private set => _isRecordingShortcut = value;
    }
    private static volatile bool _isRecordingShortcut;

    private readonly Dictionary<string, string> _hotkeys = new();
    private Button? _recordingButton;
    private ModifierKeys _peakModifiers;

    private void LoadHotkeys(AppSettings s)
    {
        _hotkeys[ReadId] = Normalize(s.ReadHotkey, ReadId);
        _hotkeys[SelectionId] = Normalize(s.ReadSelectionHotkey, SelectionId);
        _hotkeys[SnipId] = Normalize(s.SnipHotkey, SnipId);
        RefreshHotkeyButtons();
    }

    /// <summary>A saved shortcut in canonical form, or the default if it is missing or invalid.</summary>
    private static string Normalize(string? text, string id)
    {
        if (TryParseHotkey(text, out var mods, out var key) && Validate(id, mods, key, null) == null)
            return FormatHotkey(mods, key);
        return DefaultHotkeys[id];
    }

    private Button ButtonFor(string id) => id switch
    {
        ReadId => ReadHotkeyBtn,
        SelectionId => SelectionHotkeyBtn,
        _ => SnipHotkeyBtn,
    };

    private void RefreshHotkeyButtons()
    {
        foreach (var id in DefaultHotkeys.Keys)
        {
            var btn = ButtonFor(id);
            btn.Content = KeyCaps(_hotkeys[id], withClick: id == ReadId);
            btn.ToolTip = "Click to change";
        }
        SnipHowToKeys.Text = _hotkeys[SnipId].Replace("+", " + ");
    }

    // ─── Recording ───────────────────────────────────────────

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn == _recordingButton) return;
        StopRecording();
        _recordingButton = btn;
        _peakModifiers = ModifierKeys.None;
        IsRecordingShortcut = true;
        btn.BorderBrush = (Brush)FindResource("AccentBrush");
        btn.Content = Prompt(btn.Tag as string == ReadId ? "Hold the keys, then let go…" : "Press the new keys…");
        HideHotkeyStatus();
        btn.Focus();
    }

    private void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == _recordingButton) StopRecording();
    }

    private void StopRecording()
    {
        if (_recordingButton == null) return;
        _recordingButton.ClearValue(BorderBrushProperty);
        _recordingButton = null;
        IsRecordingShortcut = false;
        RefreshHotkeyButtons();
    }

    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        StopRecording();
        var def = DefaultHotkeys[id];
        TryParseHotkey(def, out var mods, out var key);
        var error = Validate(id, mods, key, _hotkeys);
        if (error != null) { ShowHotkeyStatus(error, isError: true); return; }
        _hotkeys[id] = def;
        HideHotkeyStatus();
        RefreshHotkeyButtons();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingButton?.Tag is not string id) return;
        e.Handled = true; // keys typed here are the shortcut, not commands for the window

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key == Key.Escape && mods == ModifierKeys.None) { StopRecording(); return; }

        if (ModifierOf(key) is ModifierKeys m)
        {
            _peakModifiers |= mods | m;
            var shown = id == ReadId ? _peakModifiers : mods | m;
            _recordingButton.Content = KeyCaps(FormatHotkey(shown, Key.None), withClick: false, trailing: "…");
            return;
        }

        if (id == ReadId)
        {
            ShowHotkeyStatus("This shortcut is held while you click, so use only Ctrl, Alt, Shift or Win.", isError: true);
            return;
        }

        if (KeyName(key) == null)
        {
            ShowHotkeyStatus("That key can't be used. Pick a letter, a number or F1–F12.", isError: true);
            return;
        }

        Commit(id, mods, key);
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_recordingButton?.Tag is not string id) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ModifierOf(key) == null) return;

        var stillHeld = HeldModifiers(except: key);
        if (id == ReadId)
        {
            // Done once every key is let go: the shortcut is everything that was held together.
            if (stillHeld == ModifierKeys.None && _peakModifiers != ModifierKeys.None)
            {
                var peak = _peakModifiers;
                _peakModifiers = ModifierKeys.None;
                Commit(id, peak, Key.None);
            }
        }
        else
        {
            _recordingButton.Content = stillHeld == ModifierKeys.None
                ? Prompt("Press the new keys…")
                : KeyCaps(FormatHotkey(stillHeld, Key.None), withClick: false, trailing: "…");
        }
    }

    private void Commit(string id, ModifierKeys mods, Key key)
    {
        var error = Validate(id, mods, key, _hotkeys);
        if (error != null)
        {
            ShowHotkeyStatus(error, isError: true);
            if (_recordingButton != null)
                _recordingButton.Content = Prompt(id == ReadId ? "Hold the keys, then let go…" : "Press the new keys…");
            return;
        }

        _hotkeys[id] = FormatHotkey(mods, key);
        StopRecording();

        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key != Key.None)
            ShowHotkeyStatus("Heads-up: many apps use Ctrl + Shift shortcuts too (browsers, Office). " +
                             "If this one clashes, Ctrl + Alt is usually free.", isError: false);
        else
            HideHotkeyStatus();
    }

    private static ModifierKeys HeldModifiers(Key except)
    {
        var held = ModifierKeys.None;
        void Check(Key k, ModifierKeys m) { if (k != except && Keyboard.IsKeyDown(k)) held |= m; }
        Check(Key.LeftCtrl, ModifierKeys.Control); Check(Key.RightCtrl, ModifierKeys.Control);
        Check(Key.LeftAlt, ModifierKeys.Alt); Check(Key.RightAlt, ModifierKeys.Alt);
        Check(Key.LeftShift, ModifierKeys.Shift); Check(Key.RightShift, ModifierKeys.Shift);
        Check(Key.LWin, ModifierKeys.Windows); Check(Key.RWin, ModifierKeys.Windows);
        return held;
    }

    private void ShowHotkeyStatus(string text, bool isError)
    {
        HotkeyStatusText.Text = (isError ? "⚠ " : "💡 ") + text;
        HotkeyStatusText.Foreground = (Brush)FindResource(isError ? "ErrorBrush" : "SubtextBrush");
        HotkeyStatusText.Visibility = Visibility.Visible;
    }

    private void HideHotkeyStatus() => HotkeyStatusText.Visibility = Visibility.Collapsed;

    // ─── Rules ───────────────────────────────────────────────

    /// <summary>Returns why a shortcut can't be used, or null if it is fine.</summary>
    private static string? Validate(string id, ModifierKeys mods, Key key, IReadOnlyDictionary<string, string>? current)
    {
        int count = CountModifiers(mods);

        if (id == ReadId)
        {
            if (key != Key.None) return "This shortcut is held while you click, so use only Ctrl, Alt, Shift or Win.";
            if (count < 2) return "Hold at least two keys (like Ctrl + Alt), so normal clicks keep working.";
        }
        else
        {
            if (key == Key.None || KeyName(key) == null) return "Add a letter, a number or F1–F12 after the modifier keys.";
            if (count < 2) return "Use at least two of Ctrl, Alt, Shift and Win (like Ctrl + Alt + S), so it doesn't fire while you type.";
            if (mods.HasFlag(ModifierKeys.Windows)) return "Windows keeps most Win shortcuts for itself (Win + Shift + S is its own snipping tool). Use Ctrl, Alt and Shift instead.";
        }

        if (current != null)
        {
            var text = FormatHotkey(mods, key);
            foreach (var (otherId, otherText) in current)
            {
                if (otherId != id && otherText == text)
                    return $"{text.Replace("+", " + ")} is already used for \"{HotkeyNames[otherId]}\".";
            }
        }
        return null;
    }

    private static int CountModifiers(ModifierKeys m) =>
        (m.HasFlag(ModifierKeys.Control) ? 1 : 0) + (m.HasFlag(ModifierKeys.Alt) ? 1 : 0) +
        (m.HasFlag(ModifierKeys.Shift) ? 1 : 0) + (m.HasFlag(ModifierKeys.Windows) ? 1 : 0);

    private static ModifierKeys? ModifierOf(Key k) => k switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LWin or Key.RWin => ModifierKeys.Windows,
        _ => null,
    };

    // ─── Text form: "Ctrl+Alt+S" ─────────────────────────────

    private static string FormatHotkey(ModifierKeys mods, Key key)
    {
        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (key != Key.None && KeyName(key) is string name) parts.Add(name);
        return string.Join("+", parts);
    }

    private static bool TryParseHotkey(string? text, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": case "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None) return false;
                    key = KeyFromName(part);
                    if (key == Key.None) return false;
                    break;
            }
        }
        return mods != ModifierKeys.None;
    }

    /// <summary>Name of a key that can end a shortcut, or null if it can't be used.</summary>
    private static string? KeyName(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return ((char)('A' + (k - Key.A))).ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.F1 && k <= Key.F12) return "F" + (k - Key.F1 + 1);
        return null;
    }

    private static Key KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') return Key.A + (c - 'A');
            if (c >= '0' && c <= '9') return Key.D0 + (c - '0');
        }
        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f') &&
            int.TryParse(name.AsSpan(1), out int f) && f >= 1 && f <= 12)
            return Key.F1 + (f - 1);
        return Key.None;
    }

    // ─── Key caps ────────────────────────────────────────────

    private UIElement KeyCaps(string text, bool withClick, string? trailing = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (withClick) parts.Add("Click");

        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = "+", FontSize = 11, Margin = new Thickness(4, 0, 4, 0),
                    Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center
                });
            bool isMouse = withClick && i == parts.Count - 1;
            panel.Children.Add(new Border
            {
                Style = (Style)FindResource("KeyCap"),
                Child = new TextBlock
                {
                    Text = isMouse ? "🖱 Click" : parts[i], FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource(isMouse ? "SubtextBrush" : "AccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            });
        }

        if (trailing != null)
            panel.Children.Add(new TextBlock
            {
                Text = trailing, Margin = new Thickness(4, 0, 0, 0),
                Foreground = (Brush)FindResource("SubtextBrush"), VerticalAlignment = VerticalAlignment.Center
            });
        return panel;
    }

    private UIElement Prompt(string text) => new TextBlock
    {
        Text = text, FontSize = 12, FontStyle = FontStyles.Italic,
        Foreground = (Brush)FindResource("SubtextBrush"), VerticalAlignment = VerticalAlignment.Center
    };
}
