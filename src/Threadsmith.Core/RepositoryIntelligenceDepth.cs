namespace Threadsmith.Core;

using System.Globalization;

/// <summary>Shared parsing of the optional user-selected onboarding history depth.</summary>
public static class RepositoryIntelligenceDepth
{
    /// <summary>Returns full ancestry when omitted, otherwise a positive maximum count of newest commits.</summary>
    public static int? Parse(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var depth) || depth <= 0)
        {
            throw new ArgumentException("Use /intelligence onboard [depth], where depth is a positive number of newest commits. Omit depth for full history.", nameof(value));
        }

        return depth;
    }
}
