namespace Threadsmith.App;

/// <summary>Identifies an explicitly selected interactive projection.</summary>
internal enum InteractiveFrontendKind
{
    /// <summary>No interactive frontend is selected.</summary>
    None,

    /// <summary>The original native-scrollback PrettyPrompt frontend.</summary>
    Original,

    /// <summary>The default retained TUIKit frontend.</summary>
    TuiKit,
}
