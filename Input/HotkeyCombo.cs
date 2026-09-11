using System.Windows.Forms;

namespace PushToTalkDictation.Input;

[Flags]
public enum HotModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Win = 8
}

/// <summary>
/// A parsed hold-to-talk combination: zero or more modifiers plus an optional
/// trigger key. "Ctrl+Shift+Space" -> Control|Shift + Space.
/// "Ctrl+Shift" -> Control|Shift with no trigger (modifiers-only mode).
/// </summary>
public sealed record HotkeyCombo(HotModifiers Modifiers, Keys TriggerKey)
{
    public bool IsModifiersOnly => TriggerKey == Keys.None;

    public static HotkeyCombo Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Hotkey combo is empty.");

        var modifiers = HotModifiers.None;
        var trigger = Keys.None;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotModifiers.Control;
                    break;
                case "shift":
                    modifiers |= HotModifiers.Shift;
                    break;
                case "alt" or "menu":
                    modifiers |= HotModifiers.Alt;
                    break;
                case "win" or "windows" or "meta" or "super":
                    modifiers |= HotModifiers.Win;
                    break;
                default:
                    if (trigger != Keys.None)
                        throw new FormatException($"'{text}' has more than one non-modifier key.");
                    trigger = ParseKey(rawPart);
                    break;
            }
        }

        if (modifiers == HotModifiers.None && trigger == Keys.None)
            throw new FormatException($"'{text}' does not describe any key.");

        return new HotkeyCombo(modifiers, trigger);
    }

    private static Keys ParseKey(string name)
    {
        // "Space", "F9", "D", "Oem3", "NumPad0" ...
        if (Enum.TryParse<Keys>(name, ignoreCase: true, out var key) && key != Keys.None)
            return key;

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') return (Keys)c;
            if (c is >= '0' and <= '9') return (Keys)c;
        }

        throw new FormatException($"Unrecognised key '{name}'.");
    }

    /// <summary>True when every modifier in this combo is physically down and no extra modifier is.</summary>
    public bool ModifiersSatisfied(HotModifiers current) => current == Modifiers;

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotModifiers.Win)) parts.Add("Win");
        if (TriggerKey != Keys.None) parts.Add(TriggerKey.ToString());
        return string.Join("+", parts);
    }
}
