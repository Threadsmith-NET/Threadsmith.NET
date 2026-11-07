namespace Threadsmith.Interaction.Presentation;

/// <summary>Shares the original product banner across early and session startup.</summary>
internal static class StartupBanner
{
    /// <summary>Gets the unchanged startup logo and tagline.</summary>
    internal const string Text = """
         _____ _                        _               _ _   _
        |_   _| |__  _ __ ___  __ _  __| |___ _ __ ___ (_) |_| |__
          | | | '_ \| '__/ _ \/ _` |/ _` / __| '_ ` _ \| | __| '_ \
          | | | | | | | |  __/ (_| | (_| \__ \ | | | | | | |_| | | |
          |_| |_| |_|_|  \___|\__,_|\__,_|___/_| |_| |_|_|\__|_| |_|
                                                              .NET
        Forge better code, not slop.
        """;
}
