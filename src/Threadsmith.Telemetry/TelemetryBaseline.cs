namespace Threadsmith.Telemetry;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Shared tracing source for host operations.</summary>
public static class ThreadsmithActivity
{
    /// <summary>Activity source name.</summary>
    public const string SourceName = "Threadsmith.NET";

    /// <summary>Activity source.</summary>
    public static ActivitySource Source { get; } = new(SourceName);
}

/// <summary>Redacts common credentials from text.</summary>
public static partial class SecretRedactor
{
    private static readonly string[] SourceCredentialArgumentNames =
    [
        "apiKey",
        "api_key",
        "api-key",
        "authorization",
        "token",
        "accessToken",
        "access_token",
        "access-token",
        "refreshToken",
        "refresh_token",
        "refresh-token",
        "clientSecret",
        "client_secret",
        "client-secret",
        "password",
        "passwd",
        "pwd",
    ];

    /// <summary>Redacts complete token, authorization, and API-key values.</summary>
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = QualifiedEnvironmentCredentialPattern().Replace(value, "$1[REDACTED]");
        redacted = QualifiedQuotedCredentialPattern().Replace(redacted, "$1[REDACTED]");
        redacted = ContainsSourceCredentialArgumentCandidate(redacted)
            ? CredentialPattern().Replace(redacted, RedactCredentialMatch)
            : CredentialPattern().Replace(redacted, "${lead}${prefix}[REDACTED]");
        redacted = ConnectionStringPasswordPattern().Replace(redacted, "$1[REDACTED]");
        redacted = UriUserInfoPattern().Replace(redacted, "$1[REDACTED]@");
        return StandaloneCredentialPattern().Replace(redacted, "[REDACTED]");
    }

    [GeneratedRegex(
        "(?i)(?<lead>[(,]\\s*)?(?<prefix>(?<![A-Za-z0-9_])[\\\"']?(?<key>api[_-]?key|authorization|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|pwd)[\\\"']?\\s*[:=]\\s*)(?<value>\\\"[^\\\"]*\\\"|'[^']*'|(?:(?:Bearer|Basic)\\s+)?[^\\s,;})\\]&]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(
        "(?i)((?<![A-Za-z0-9])(?:[A-Za-z0-9]+[_-]+)+(?:api[_-]?key|authorization|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|pwd)\\s*=\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|(?:(?:Bearer|Basic)\\s+)?[^\\s,;})\\]&]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedEnvironmentCredentialPattern();

    [GeneratedRegex(
        "(?i)((?<![A-Za-z0-9_])[\\\"'](?:[A-Za-z0-9]+[_-]+)+(?:api[_-]?key|authorization|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|pwd)[\\\"']\\s*:\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|(?:(?:Bearer|Basic)\\s+)?[^\\s,;})\\]&]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedQuotedCredentialPattern();

    private static bool ContainsSourceCredentialArgumentCandidate(string value)
    {
        foreach (var name in SourceCredentialArgumentNames)
        {
            if (ContainsSourceCredentialArgumentCandidate(value, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSourceCredentialArgumentCandidate(string value, string key)
    {
        var startIndex = 0;
        while (startIndex < value.Length)
        {
            var index = value.IndexOf(key, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            startIndex = index + key.Length;
            if (index > 0 && IsIdentifierCharacter(value[index - 1]))
            {
                continue;
            }

            if (!HasSourceNamedArgumentPrefix(value, index))
            {
                continue;
            }

            var cursor = startIndex;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length || value[cursor] != ':')
            {
                continue;
            }

            cursor++;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor < value.Length && value[cursor] == '@')
            {
                cursor++;
            }

            var identifierLength = ReadIdentifierLength(value.AsSpan(cursor));
            if (identifierLength == 0)
            {
                continue;
            }

            var endIndex = cursor + identifierLength;
            if (endIndex >= value.Length || value[endIndex] is ',' or ')' or ']' or ';' or '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static string RedactCredentialMatch(Match match)
    {
        return IsIdentifierEcho(match)
            ? match.Value
            : match.Groups["lead"].Value + match.Groups["prefix"].Value + "[REDACTED]";
    }

    private static bool IsIdentifierEcho(Match match)
    {
        if (!IsSourceNamedArgumentMatch(match))
        {
            return false;
        }

        var value = match.Groups["value"].Value;
        if (IsQuotedCredentialLiteral(value))
        {
            return false;
        }

        var identifier = TrimCredentialLiteral(value);
        if (!identifier.IsEmpty && identifier[0] == '@')
        {
            identifier = identifier[1..];
        }

        return IsIdentifierLike(identifier);
    }

    private static bool HasSourceNamedArgumentPrefix(string value, int keyIndex)
    {
        var cursor = keyIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(value[cursor]))
        {
            cursor--;
        }

        return cursor >= 0 && value[cursor] is '(' or ',';
    }

    private static bool IsSourceNamedArgumentMatch(Match match)
    {
        return !string.IsNullOrEmpty(match.Groups["lead"].Value)
            && match.Groups["prefix"].Value.Contains(':', StringComparison.Ordinal);
    }

    private static bool IsQuotedCredentialLiteral(string value)
    {
        var trimmed = value.AsSpan().Trim();
        return trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\''));
    }

    private static ReadOnlySpan<char> TrimCredentialLiteral(string value)
    {
        var trimmed = value.AsSpan().Trim();
        return trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\''))
                ? trimmed[1..^1]
                : trimmed;
    }

    private static bool IsIdentifierLike(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || (!char.IsLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsIdentifierCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadIdentifierLength(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || (!char.IsLetter(value[0]) && value[0] != '_'))
        {
            return 0;
        }

        var index = 1;
        while (index < value.Length && IsIdentifierCharacter(value[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsIdentifierCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_';
    }

    [GeneratedRegex(
        "(?i)((?:^|;)\\s*(?:Password|Pwd)\\s*=\\s*)[^;]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPasswordPattern();

    [GeneratedRegex("(://)[^/@\\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfoPattern();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])(?:-----BEGIN(?: [A-Z]+)* PRIVATE KEY-----|sk-[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9]{12,}|AIza[A-Za-z0-9_-]{20,}|eyJ[A-Za-z0-9_-]{8,}\\.eyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneCredentialPattern();
}

/// <summary>Sanitizes untrusted output before persistence, logging, or rendering.</summary>
public sealed partial class SecretOutputSanitizer : IOutputSanitizer
{
    /// <inheritdoc />
    public string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = SecretRedactor.Redact(value);
        return UnsafeControlPattern().Replace(redacted, string.Empty);
    }

    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeControlPattern();
}

/// <summary>Records structured logs and traces for every domain event.</summary>
public sealed class DomainEventTelemetry
{
    private readonly ILogger<DomainEventTelemetry> _logger;

    /// <summary>Initializes a new instance of the <see cref="DomainEventTelemetry"/> class.</summary>
    public DomainEventTelemetry(ILogger<DomainEventTelemetry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Observes a domain event without logging its potentially sensitive payload.</summary>
    public Task ObserveAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();
        var eventName = DomainEventJson.GetDiscriminator(domainEvent);
        using var activity = ThreadsmithActivity.Source.StartActivity("domain-event");
        activity?.SetTag("threadsmith.event.name", eventName);
        activity?.SetTag("threadsmith.event.schema_version", domainEvent.SchemaVersion);
        activity?.SetTag("threadsmith.session.id", domainEvent.SessionId.Value.ToString("D"));
        _logger.LogInformation(
            "Observed domain event {EventName} schema {SchemaVersion} for session {SessionId}",
            eventName,
            domainEvent.SchemaVersion,
            domainEvent.SessionId.Value);
        return Task.CompletedTask;
    }
}
