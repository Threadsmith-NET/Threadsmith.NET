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
    /// <summary>Redacts complete token, authorization, and API-key values.</summary>
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = CredentialPattern().Replace(value, "$1[REDACTED]");
        redacted = ConnectionStringPasswordPattern().Replace(redacted, "$1[REDACTED]");
        redacted = UriUserInfoPattern().Replace(redacted, "$1[REDACTED]@");
        return StandaloneCredentialPattern().Replace(redacted, "[REDACTED]");
    }

    [GeneratedRegex(
        "(?i)([\\\"']?(?:api[_-]?key|authorization|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|pwd)[\\\"']?\\s*[:=]\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|(?:(?:Bearer|Basic)\\s+)?[^\\s,;}&]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

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
        using Activity? activity = ThreadsmithActivity.Source.StartActivity("domain-event");
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
