namespace Threadsmith.App;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Removes only the retired planning properties from existing configuration.</summary>
internal static class RetiredPlanningConfiguration
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "planning:approvalPolicy",
        "planning:approvalRepositoryIdentity",
        "planning:incrementalPlans:enabled",
        "planning:incrementalPlans:targetSteps",
        "planning:incrementalPlans:targetFiles",
        "execution:mutationBatching:targetMutations",
        "execution:mutationBatching:targetFiles",
        "execution:mutationBatching:targetMutationCharacters",
        "execution:maxPlanningToolRounds",
        "execution:maxPlanSanityIssues",
        "limits:plan:maximumSteps",
        "limits:plan:maximumMetadataItems",
        "limits:plan:maximumSummaryCharacters",
        "limits:plan:maximumTitleCharacters",
        "limits:plan:maximumDescriptionCharacters",
        "limits:plan:maximumPathCharacters",
    };

    /// <summary>Identifies retired properties and their obsolete value subtrees.</summary>
    internal static bool IsRetired(string key) => Keys.Contains(key)
        || Keys.Any(retired => key.StartsWith(retired + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reports external or read-only retired settings once without exposing their values.</summary>
    internal static string? GetWarning(IConfiguration configuration)
    {
        return Keys.Any(key => configuration.GetSection(key).Exists())
            ? "Retired planning settings are ignored. Remove planning approval, incremental-plan, mutation-batching and plan-only limit properties from external or read-only configuration. Mutation approval is unchanged."
            : null;
    }

    /// <summary>Migrates identified ordinary JSON layers through the existing settings write owner.</summary>
    internal static async Task MigrateAsync(ConfigurationPaths paths, CancellationToken cancellationToken = default)
    {
        foreach (var path in new[]
        {
            paths.MachineConfiguration,
            paths.UserConfiguration,
            paths.RepositoryConfiguration,
            paths.SessionConfiguration,
        }.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            await MigrateFileAsync(path, cancellationToken);
        }
    }

    /// <summary>Preserves unrelated JSON bytes and replaces an owned writable file atomically.</summary>
    internal static async Task MigrateFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await RepositorySettingsCoordinator.ExecuteWriteAsync(
                path,
                async token =>
                {
                    if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)
                        || new FileInfo(path).Length > MaximumBytes)
                    {
                        return;
                    }

                    var original = await File.ReadAllBytesAsync(path, token);
                    if (original.Length > MaximumBytes)
                    {
                        return;
                    }

                    var updated = RemoveProperties(original);
                    if (updated is null)
                    {
                        return;
                    }

                    var temporary = path + $".{Guid.NewGuid():N}.tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, updated, token);
                        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(path);

                        // Do not overwrite an external writer that changed this file during migration.
                        var current = await File.ReadAllBytesAsync(path, token);
                        if (!original.AsSpan().SequenceEqual(current))
                        {
                            return;
                        }

                        token.ThrowIfCancellationRequested();
                        File.Replace(temporary, path, destinationBackupFileName: null);
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                        {
                            File.Delete(temporary);
                        }
                    }
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unsupported/read-only inputs retain their bytes. Normal configuration loading still
            // owns parse errors; GetWarning reports ignored old properties without their values.
        }
    }

    private static byte[]? RemoveProperties(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        var removals = new List<(int Start, int End)>();
        ReadObject(ref reader, string.Empty, offset, bytes, removals);
        if (removals.Count == 0)
        {
            return null;
        }

        using var output = new MemoryStream(bytes.Length);
        var cursor = 0;
        foreach (var (start, end) in removals.OrderBy(range => range.Start))
        {
            if (start > cursor)
            {
                output.Write(bytes.AsSpan(cursor, start - cursor));
            }

            cursor = Math.Max(cursor, end);
        }

        output.Write(bytes.AsSpan(cursor));
        var result = output.ToArray();
        using var verified = JsonDocument.Parse(result.AsMemory(offset), RepositorySettingsCoordinator.DocumentOptions);
        return result;
    }

    private static void ReadObject(
        ref Utf8JsonReader reader,
        string prefix,
        int offset,
        byte[] bytes,
        List<(int Start, int End)> removals)
    {
        var properties = new List<(int Start, int End, bool Remove)>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Configuration object requires a property name.");
            }

            var start = checked((int)reader.TokenStartIndex + offset);
            var key = string.IsNullOrEmpty(prefix) ? reader.GetString() ?? string.Empty : prefix + ":" + reader.GetString();
            var remove = Keys.Contains(key);
            if (!reader.Read())
            {
                throw new JsonException("Configuration property requires a value.");
            }

            if (!remove && reader.TokenType == JsonTokenType.StartObject)
            {
                ReadObject(ref reader, key, offset, bytes, removals);
            }
            else
            {
                reader.Skip();
            }

            properties.Add((start, checked((int)reader.BytesConsumed + offset), remove));
        }

        for (var index = 0; index < properties.Count; index++)
        {
            if (!properties[index].Remove)
            {
                continue;
            }

            var first = index;
            while (index + 1 < properties.Count && properties[index + 1].Remove)
            {
                index++;
            }

            for (var removed = first; removed <= index; removed++)
            {
                removals.Add((properties[removed].Start, properties[removed].End));
                RemoveSeparators(
                    bytes,
                    properties[removed].End,
                    removed + 1 < properties.Count ? properties[removed + 1].Start : checked((int)reader.TokenStartIndex + offset),
                    removals);
            }

            if (index + 1 == properties.Count && first > 0)
            {
                RemoveSeparators(bytes, properties[first - 1].End, properties[first].Start, removals);
            }
        }
    }

    private static void RemoveSeparators(byte[] bytes, int start, int end, List<(int Start, int End)> removals)
    {
        // Between complete JSON properties only separators, whitespace and comments are legal.
        for (var index = start; index < end; index++)
        {
            if (bytes[index] == '/' && index + 1 < end)
            {
                if (bytes[index + 1] == '/')
                {
                    while (index < end && bytes[index] is not (byte)'\r' and not (byte)'\n')
                    {
                        index++;
                    }
                }
                else if (bytes[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < end && !(bytes[index] == '*' && bytes[index + 1] == '/'))
                    {
                        index++;
                    }

                    index++;
                }
            }
            else if (bytes[index] == ',')
            {
                removals.Add((index, index + 1));
            }
        }
    }
}
