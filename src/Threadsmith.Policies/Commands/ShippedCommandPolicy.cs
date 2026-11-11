namespace Threadsmith.Policies.Commands;

using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;

/// <summary>Loads the immutable shipped policy document; program semantics remain in rule data.</summary>
public static class ShippedCommandPolicy
{
    private static readonly Lazy<PolicyDocument> Document = new(Load);

    /// <summary>Gets the baseline evaluated and replaced through the same schema as machine and user rules.</summary>
    public static PolicyDocument CreateDefaults() => Document.Value;

    private static PolicyDocument Load()
    {
        using var stream = typeof(ShippedCommandPolicy).Assembly.GetManifestResourceStream("Threadsmith.Policies.command-policy.json")
            ?? throw new InvalidDataException("PolicyDefaultsMissing");
        using var reader = new StreamReader(stream);
        return PolicyJson.Parse(reader.ReadToEnd());
    }
}
