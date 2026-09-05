namespace Threadsmith.Tui.TuiKit;

using TUIKit.Input;

/// <summary>Normalizes public decoded keys and preserves the existing submit gestures.</summary>
internal static class TuiKitInput
{
    /// <summary>Normalizes protocol-specific control runes to named keys.</summary>
    internal static KeyEvent Normalize(KeyEvent key) => key.Code == KeyCode.Character ? key.Rune switch
    {
        9 => KeyEvent.Special(KeyCode.Tab, key.Modifiers),
        13 => KeyEvent.Special(KeyCode.Enter, key.Modifiers),
        27 => KeyEvent.Special(KeyCode.Escape, key.Modifiers),
        127 => KeyEvent.Special(KeyCode.Backspace, key.Modifiers),
        _ => key,
    } : key;

    /// <summary>Distinguishes submission from an explicit multiline insertion.</summary>
    internal static SubmitDecision ResolveSubmit(KeyEvent key)
    {
        key = Normalize(key);
        if (key.Code == KeyCode.Enter)
        {
            return key.Modifiers switch
            {
                KeyModifiers.Shift or KeyModifiers.Alt or KeyModifiers.Ctrl => SubmitDecision.InsertNewline,
                KeyModifiers.None or KeyModifiers.Ctrl | KeyModifiers.Alt => SubmitDecision.Submit,
                _ => SubmitDecision.Ignore,
            };
        }

        // Windows Terminal can report Ctrl+Enter as the line-feed control key that TUIKit names Ctrl+J.
        return key.Code == KeyCode.Character && key.Rune == 'j' && key.Modifiers == KeyModifiers.Ctrl
            ? SubmitDecision.InsertNewline : SubmitDecision.Ignore;
    }
}
