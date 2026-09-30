namespace Threadsmith.SemanticFixtures.Roslyn59;

using System.IO.Pipes;

/// <summary>Provides an explicit construction barrier for cancellation regression tests.</summary>
internal static class SemanticFixtureConstruction
{
    /// <summary>Blocks construction only when the test supplies a pipe marker beside this assembly.</summary>
#pragma warning disable RS1035 // Test-only I/O makes non-cooperative construction observable and releasable.
    internal static void WaitForRelease(string assemblyPath, string component)
    {
        var markerPath = assemblyPath + ".block-" + component;
        if (!File.Exists(markerPath))
        {
            return;
        }

        using var pipe = new NamedPipeClientStream(".", File.ReadAllText(markerPath), PipeDirection.InOut);
        pipe.Connect();
        _ = pipe.ReadByte();
        pipe.WriteByte(1);
    }
#pragma warning restore RS1035
}
