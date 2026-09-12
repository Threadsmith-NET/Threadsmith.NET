namespace Threadsmith.Mcp;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>Translates SDK client values into bounded host-owned MCP transport contracts.</summary>
internal sealed class McpTransportMapping
{
    private readonly McpResourceLimits _limits;

    /// <summary>Initializes a new instance of the <see cref="McpTransportMapping"/> class.</summary>
    internal McpTransportMapping(McpResourceLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <summary>Maps discovered SDK tools into host-owned imported capability records.</summary>
    internal IReadOnlyList<McpImportedCapability> MapTools(
        McpConnectionProfile profile,
        IEnumerable<McpClientTool> tools)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tools);
        return Bound(tools, "tools")
            .Select(tool =>
            {
                var name = NormalizeRequired(tool.ProtocolTool.Name, 128, "tool name");
                var description = NormalizeOptional(tool.Description, _limits.MaximumDescriptionCharacters);
                var schema = tool.JsonSchema.GetRawText();
                if (schema.Length > _limits.MaximumSchemaCharacters)
                {
                    throw new InvalidOperationException("An MCP tool input schema exceeds the host metadata bound.");
                }

                return new McpImportedCapability
                {
                    Id = $"{profile.Id}:{name}",
                    Kind = McpCapabilityKind.Tool,
                    ServerName = name,
                    Description = description,
                    InputSchemaJson = schema,
                    Digest = ComputeDigest("tool", name, description, schema),
                };
            })
            .ToArray();
    }

    /// <summary>Maps discovered SDK resources into host-owned imported capability records.</summary>
    internal IReadOnlyList<McpImportedCapability> MapResources(
        McpConnectionProfile profile,
        IEnumerable<McpClientResource> resources)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(resources);
        return Bound(resources, "resources")
            .Select(resource =>
            {
                var uri = NormalizeRequired(resource.Uri, _limits.MaximumIdentityCharacters, "resource URI");
                _ = new Uri(uri, UriKind.Absolute);
                var name = NormalizeRequired(resource.Name, _limits.MaximumNameCharacters, "resource name");
                var description = NormalizeOptional(resource.Description, _limits.MaximumDescriptionCharacters);
                var mimeType = NormalizeNullable(resource.MimeType, _limits.MaximumNameCharacters);
                return new McpImportedCapability
                {
                    Id = $"{profile.Id}:resource:{ShortDigest(uri)}",
                    Kind = McpCapabilityKind.Resource,
                    ServerName = name,
                    Description = description,
                    Digest = ComputeDigest("resource", name, description, uri, mimeType),
                    ResourceIdentity = uri,
                    MimeType = mimeType,
                };
            })
            .ToArray();
    }

    /// <summary>Maps discovered SDK resource templates into host-owned imported capability records.</summary>
    internal IReadOnlyList<McpImportedCapability> MapResourceTemplates(
        McpConnectionProfile profile,
        IEnumerable<McpClientResourceTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(templates);
        return Bound(templates, "resource templates")
            .Select(template =>
            {
                var uriTemplate = NormalizeRequired(
                    template.UriTemplate,
                    _limits.MaximumIdentityCharacters,
                    "resource URI template");
                var expandedIdentity = uriTemplate
                    .Replace("{", string.Empty, StringComparison.Ordinal)
                    .Replace("}", string.Empty, StringComparison.Ordinal);
                if (!Uri.TryCreate(expandedIdentity, UriKind.Absolute, out _))
                {
                    throw new InvalidOperationException("An MCP resource template is not an absolute URI template.");
                }

                var name = NormalizeRequired(template.Name, _limits.MaximumNameCharacters, "resource-template name");
                var description = NormalizeOptional(template.Description, _limits.MaximumDescriptionCharacters);
                var mimeType = NormalizeNullable(template.MimeType, _limits.MaximumNameCharacters);
                return new McpImportedCapability
                {
                    Id = $"{profile.Id}:resource-template:{ShortDigest(uriTemplate)}",
                    Kind = McpCapabilityKind.ResourceTemplate,
                    ServerName = name,
                    Description = description,
                    Digest = ComputeDigest("resource-template", name, description, uriTemplate, mimeType),
                    ResourceIdentity = uriTemplate,
                    MimeType = mimeType,
                };
            })
            .ToArray();
    }

    /// <summary>Maps discovered SDK prompts into host-owned imported capability records.</summary>
    internal IReadOnlyList<McpImportedCapability> MapPrompts(
        McpConnectionProfile profile,
        IEnumerable<McpClientPrompt> prompts)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(prompts);
        return Bound(prompts, "prompts")
            .Select(prompt =>
            {
                var name = NormalizeRequired(prompt.Name, 128, "prompt name");
                var description = NormalizeOptional(prompt.Description, _limits.MaximumDescriptionCharacters);
                var protocolArguments = prompt.ProtocolPrompt.Arguments ?? [];
                if (protocolArguments.Count > Math.Min(_limits.MaximumPromptArguments, _limits.MaximumArguments))
                {
                    throw new InvalidOperationException("An MCP prompt declares too many arguments.");
                }

                if (protocolArguments.Any(argument => string.IsNullOrWhiteSpace(argument.Name)
                    || argument.Name.Length > _limits.MaximumArgumentNameCharacters
                    || argument.Name.Any(char.IsControl))
                    || protocolArguments.Select(argument => argument.Name).Distinct(StringComparer.Ordinal).Count() != protocolArguments.Count)
                {
                    throw new InvalidOperationException("An MCP prompt declares invalid, duplicate, or oversized argument names.");
                }

                McpImportedPromptArgument[] arguments =
                [
                    .. protocolArguments.Select(argument => new McpImportedPromptArgument
                    {
                        Name = argument.Name,
                        Description = NormalizeOptional(argument.Description, _limits.MaximumArgumentDescriptionCharacters),
                        Required = argument.Required is true,
                    }),
                ];
                var argumentIdentity = JsonSerializer.Serialize(arguments);
                return new McpImportedCapability
                {
                    Id = $"{profile.Id}:prompt:{name}",
                    Kind = McpCapabilityKind.Prompt,
                    ServerName = name,
                    Description = description,
                    Digest = ComputeDigest("prompt", name, description, argumentIdentity),
                    PromptArguments = arguments,
                };
            })
            .ToArray();
    }

    /// <summary>Maps a resource result into bounded textual untrusted content.</summary>
    internal McpTransportContentResult MapResourceContent(ReadResourceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var content = new List<McpTransportContentItem>();
        var retainedCharacters = 0;
        var truncated = false;
        foreach (var item in result.Contents.Take(_limits.MaximumContentItems))
        {
            var text = item switch
            {
                TextResourceContents textResource => textResource.Text,
                BlobResourceContents blobResource
                    => $"[binary MCP resource withheld; encoded length {blobResource.Blob.Length}]",
                _ => "[unsupported MCP resource content withheld]",
            };
            var remaining = _limits.MaximumContentCharacters - retainedCharacters;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var itemTruncated = text.Length > remaining;
            text = NormalizeOptional(text, remaining);
            truncated |= itemTruncated;
            retainedCharacters += text.Length;
            content.Add(new McpTransportContentItem
            {
                Label = NormalizeOptional(item.Uri, _limits.MaximumResourceLabelCharacters),
                Text = text,
                MimeType = NormalizeNullable(item.MimeType, _limits.MaximumNameCharacters),
                IsTruncated = itemTruncated,
            });
        }

        truncated |= result.Contents.Count > content.Count;
        return new McpTransportContentResult { Content = content, IsTruncated = truncated };
    }

    /// <summary>Maps a prompt result into bounded textual untrusted content.</summary>
    internal McpTransportContentResult MapPromptContent(GetPromptResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var content = new List<McpTransportContentItem>();
        var retainedCharacters = 0;
        var truncated = false;
        foreach (var message in result.Messages.Take(_limits.MaximumContentItems))
        {
            var text = message.Content switch
            {
                TextContentBlock textBlock => textBlock.Text,
                EmbeddedResourceBlock { Resource: TextResourceContents resource } => resource.Text,
                EmbeddedResourceBlock { Resource: BlobResourceContents resource }
                    => $"[binary embedded MCP resource withheld; encoded length {resource.Blob.Length}]",
                ImageContentBlock image => $"[MCP image withheld; media type {NormalizeOptional(image.MimeType, _limits.MaximumNameCharacters)}]",
                AudioContentBlock audio => $"[MCP audio withheld; media type {NormalizeOptional(audio.MimeType, _limits.MaximumNameCharacters)}]",
                _ => "[unsupported MCP prompt content withheld]",
            };
            var remaining = _limits.MaximumContentCharacters - retainedCharacters;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var itemTruncated = text.Length > remaining;
            text = NormalizeOptional(text, remaining);
            retainedCharacters += text.Length;
            truncated |= itemTruncated;
            content.Add(new McpTransportContentItem
            {
                Label = message.Role.ToString(),
                Text = text,
                IsTruncated = itemTruncated,
            });
        }

        truncated |= result.Messages.Count > content.Count;
        return new McpTransportContentResult { Content = content, IsTruncated = truncated };
    }

    /// <summary>Maps an SDK call result into the bounded host transport invocation contract.</summary>
    internal McpTransportInvocation MapInvocation(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new List<string>();
        var retainedCharacters = 0;
        var truncated = false;
        ContentBlock[] contentBlocks = [.. result.Content.Take(_limits.MaximumContentItems == int.MaxValue ? int.MaxValue : _limits.MaximumContentItems + 1)];
        foreach (var block in contentBlocks.Take(_limits.MaximumContentItems))
        {
            var remaining = _limits.MaximumContentCharacters - retainedCharacters;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var value = block switch
            {
                TextContentBlock textBlock => textBlock.Text ?? string.Empty,
                ImageContentBlock image
                    => $"[MCP image withheld; media type {NormalizeOptional(image.MimeType, _limits.MaximumNameCharacters)}]",
                AudioContentBlock audio
                    => $"[MCP audio withheld; media type {NormalizeOptional(audio.MimeType, _limits.MaximumNameCharacters)}]",
                EmbeddedResourceBlock { Resource: TextResourceContents resource }
                    => $"[embedded MCP text resource withheld; URI {NormalizeOptional(resource.Uri, _limits.MaximumResourceLabelCharacters)}]",
                EmbeddedResourceBlock { Resource: BlobResourceContents resource }
                    => $"[embedded MCP binary resource withheld; URI {NormalizeOptional(resource.Uri, _limits.MaximumResourceLabelCharacters)}]",
                _ => "[unsupported MCP tool content withheld]",
            };
            var itemTruncated = value.Length > remaining;
            value = NormalizeOptional(value, remaining);
            retainedCharacters += value.Length;
            truncated |= itemTruncated || block is not TextContentBlock;
            text.Add(value);
        }

        truncated |= contentBlocks.Length > text.Count;
        return new McpTransportInvocation
        {
            Succeeded = result.IsError is not true,
            ResultJson = JsonSerializer.Serialize(text),
            IsTruncated = truncated,
            Error = result.IsError is true ? string.Join(Environment.NewLine, text) : null,
        };
    }

    /// <summary>Deserializes a host JSON argument object for an SDK tool call.</summary>
    internal static IReadOnlyDictionary<string, object?> DeserializeArguments(string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        var values =
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson)
            ?? throw new JsonException("MCP tool arguments must be a JSON object.");
        return values.ToDictionary(
            pair => pair.Key,
            object? (pair) => pair.Value,
            StringComparer.Ordinal);
    }

    /// <summary>Converts bounded user string arguments to the SDK argument shape.</summary>
    internal IReadOnlyDictionary<string, object?> MapArguments(
        IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > Math.Min(_limits.MaximumPromptArguments, _limits.MaximumArguments)
            || arguments.Any(pair => (pair.Key.Length == 0 || pair.Key.Length > _limits.MaximumArgumentNameCharacters) || pair.Value.Length > _limits.MaximumArgumentCharacters))
        {
            throw new InvalidOperationException("MCP resource or prompt arguments exceed host bounds.");
        }

        return arguments.ToDictionary(
            pair => pair.Key,
            object? (pair) => pair.Value,
            StringComparer.Ordinal);
    }

    private IEnumerable<T> Bound<T>(IEnumerable<T> values, string kind)
    {
        T[] bounded = [.. values.Take((int)Math.Min((long)_limits.MaximumCapabilitiesPerKind + 1, int.MaxValue))];
        if (bounded.Length > _limits.MaximumCapabilitiesPerKind)
        {
            throw new InvalidOperationException($"The MCP server advertises too many {kind}.");
        }

        return bounded;
    }

    private static string ComputeDigest(params string?[] parts)
    {
        var joined = string.Join('\u001f', parts.Select(part => part ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private static string ShortDigest(string value)
    {
        return ComputeDigest(value)[..16];
    }

    private static string NormalizeRequired(string value, int maximumLength, string field)
    {
        var normalized = NormalizeOptional(value, maximumLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"An MCP {field} is empty or invalid.");
        }

        return normalized;
    }

    private static string NormalizeOptional(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = string.Concat(value
            .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .Take(maximumLength));
        return normalized;
    }

    private static string? NormalizeNullable(string? value, int maximumLength)
    {
        return value is null ? null : NormalizeOptional(value, maximumLength);
    }
}
