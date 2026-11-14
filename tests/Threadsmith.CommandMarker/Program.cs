namespace Threadsmith.CommandMarker;

using System.Text.Json;

/// <summary>Harmless native apphost used to distinguish authorized and cwd-shadow launches.</summary>
internal static class Program
{
    /// <summary>Writes its actual launch location and exact received arguments.</summary>
    public static void Main(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(new Marker(
            Path.GetFileName(Path.GetDirectoryName(Environment.ProcessPath)),
            args,
            Environment.GetEnvironmentVariable("GIT_AUTHOR_DATE"),
            Environment.GetEnvironmentVariable("GIT_COMMITTER_DATE"))));
    }

    private sealed record Marker(string? Identity, string[] Arguments, string? GitAuthorDate, string? GitCommitterDate);
}
