namespace Threadsmith.Tools;

/// <summary>Host-owned expected integrity for the raw bytes of one script source file.</summary>
/// <param name="Bytes">Verified byte length, including an optional UTF-8 BOM.</param>
/// <param name="Sha256">Lowercase SHA-256 of the verified file bytes.</param>
public sealed record ScriptSourceIntegrity(long Bytes, string Sha256);
