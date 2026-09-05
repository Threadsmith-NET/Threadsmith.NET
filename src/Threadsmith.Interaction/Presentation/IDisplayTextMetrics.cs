namespace Threadsmith.Interaction.Presentation;

/// <summary>Supplies backend-specific display-cell measurements to shared text layout.</summary>
/// <typeparam name="TSelf">The concrete, allocation-free metrics adapter.</typeparam>
internal interface IDisplayTextMetrics<TSelf>
    where TSelf : IDisplayTextMetrics<TSelf>
{
    /// <summary>Measures the number of display cells occupied by complete graphemes.</summary>
    static abstract int GetWidth(string text);

    /// <summary>Returns the UTF-16 length that fits without splitting a grapheme.</summary>
    static abstract int GetLengthThatFits(string text, int width);
}
