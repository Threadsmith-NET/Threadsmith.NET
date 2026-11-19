namespace Threadsmith.SemanticFixtures.Roslyn59;

using System.IO.Pipes;

/// <summary>Provides a pipe barrier for cancellation and preparation tests.</summary>
internal static class SemanticFixtureConstruction
{
    /// <summary>Blocks only when the test supplies a pipe marker.</summary>
#pragma warning disable RS1035 // Test-only I/O makes non-cooperative work observable and releasable.
    internal static void WaitForRelease(string markerPath)
    {
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
