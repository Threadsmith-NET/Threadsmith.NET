namespace Threadsmith.Execution;

/// <summary>Applies deterministic UTF-16-safe text bounds at model-facing boundaries.</summary>
internal static class BoundedText
{
    /// <summary>Truncates without splitting a surrogate pair.</summary>
    public static string Truncate(string value, int maximumCharacters, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        truncated = value.Length > maximumCharacters;
        if (!truncated)
        {
            return value;
        }

        var length = maximumCharacters;
        if (char.IsHighSurrogate(value[length - 1])
            && length < value.Length
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }
}
