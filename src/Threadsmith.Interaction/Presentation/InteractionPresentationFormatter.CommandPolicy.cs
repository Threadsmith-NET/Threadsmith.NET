namespace Threadsmith.Interaction.Presentation;

using System.Text;
using Threadsmith.Policies.Contracts;

/// <summary>Formats bounded manual policy summaries without serializing matcher payloads.</summary>
internal static partial class InteractionPresentationFormatter
{
    /// <summary>Shows counts and bounded identity previews; full persistent definitions remain in trusted configuration.</summary>
    internal static string FormatCommandPolicyInspection(PolicySnapshot snapshot, long epoch, IReadOnlyList<string> liveGrants)
    {
        const int previewEntries = 8;
        var text = new StringBuilder();
        text.Append("Command policy: ").Append(Bounded(snapshot.Platform)).Append('/').AppendLine(Bounded(snapshot.Shell));
        text.Append("Fingerprint: ").AppendLine(Bounded(snapshot.Fingerprint));
        text.Append("Configuration revision: ").AppendLine(Bounded(snapshot.ConfigurationRevision));
        text.Append("Require enforced match: ").AppendLine(snapshot.RequireEnforcedMatch.ToString());
        text.Append("Live session epoch: ").Append(epoch).AppendLine();
        AppendPreview("Rules", snapshot.Rules.Select(rule => rule.Id), snapshot.Rules.Length);
        AppendPreview("Enforced rules", snapshot.Enforced.Select(rule => rule.Id), snapshot.Enforced.Length);
        AppendPreview("Sources", snapshot.Sources.Select(pair => Bounded(pair.Key) + "=" + Bounded(pair.Value)), snapshot.Sources.Count);
        AppendPreview("Persistent grants", snapshot.AlwaysAllow.Select(grant => grant.Id), snapshot.AlwaysAllow.Length);
        AppendPreview("Live grants", liveGrants, liveGrants.Count);
        text.AppendLine("Identity previews only (at most 8 entries per category, 120 characters per entry). Full persistent definitions are in machine/user configuration files.");
        return text.ToString();

        void AppendPreview(string label, IEnumerable<string> identities, int count)
        {
            text.Append(label).Append(": ").Append(count).AppendLine();
            foreach (var identity in identities.Take(previewEntries))
            {
                text.Append("  ").AppendLine(Bounded(identity));
            }

            if (count > previewEntries)
            {
                text.Append("  ... ").Append(count - previewEntries).AppendLine(" omitted");
            }
        }
    }

    private static string Bounded(string value)
    {
        const int maximumIdentityCharacters = 120;
        var length = Math.Min(value.Length, maximumIdentityCharacters);
        var text = new StringBuilder(length + 1);
        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            text.Append(char.IsControl(character) ? '?' : character);
        }

        if (value.Length > length)
        {
            text.Append('…');
        }

        return text.ToString();
    }
}
