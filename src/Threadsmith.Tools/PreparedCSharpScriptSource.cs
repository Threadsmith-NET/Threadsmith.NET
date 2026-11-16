namespace Threadsmith.Tools;

/// <summary>One host-owned source snapshot shared by evidence and execution.</summary>
internal sealed record PreparedCSharpScriptSource(string Code, string? SourcePath)
{
    /// <summary>Describes provenance without disclosing executable source in diagnostic strings.</summary>
    public override string ToString() => $"PreparedCSharpScriptSource {{ {SourcePath} }}";
}
