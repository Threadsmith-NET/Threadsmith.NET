namespace Threadsmith.Extensions.Neo4j;

using global::Neo4j.Driver;

/// <summary>Translates driver exceptions into safe, classified failures at the adapter boundary.</summary>
/// <remarks>
/// Raw driver messages can echo the submitted statement, parameter values, or connection details, so
/// only an allow-listed server error code and a fixed classification cross this boundary.
/// </remarks>
internal static class Neo4jFailureTranslator
{
    private const int _maximumCodeLength = 128;

    /// <summary>Classifies one failure without retaining any driver message text.</summary>
    /// <param name="exception">The failure observed by the adapter.</param>
    /// <returns>The classified failure.</returns>
    internal static GraphFailure Translate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is Neo4jException neo4j)
        {
            var code = Sanitize(neo4j.Code);
            return new GraphFailure(Classify(neo4j, code), code);
        }

        return exception switch
        {
            TimeoutException => new GraphFailure(GraphFailureKind.Timeout, null, "The client deadline expired."),
            IOException => new GraphFailure(GraphFailureKind.Unavailable, null, "The connection failed."),
            _ => new GraphFailure(GraphFailureKind.Database, null, "The driver reported an unexpected failure."),
        };
    }

    private static string Classify(Neo4jException exception, string? code)
    {
        if (exception is ServiceUnavailableException or SessionExpiredException)
        {
            return GraphFailureKind.Unavailable;
        }

        if (exception is TransientException)
        {
            return GraphFailureKind.Transient;
        }

        if (code is null)
        {
            return GraphFailureKind.Database;
        }

        if (code.Contains(".Security.Unauthorized", StringComparison.Ordinal)
            || code.Contains(".Security.AuthenticationRateLimit", StringComparison.Ordinal)
            || code.Contains(".Security.CredentialsExpired", StringComparison.Ordinal))
        {
            return GraphFailureKind.Authentication;
        }

        if (code.Contains(".Security.", StringComparison.Ordinal))
        {
            return GraphFailureKind.Authorization;
        }

        if (code.Contains("TransactionTimedOut", StringComparison.Ordinal)
            || code.Contains("Timeout", StringComparison.Ordinal))
        {
            return GraphFailureKind.Timeout;
        }

        if (code.Contains(".Statement.", StringComparison.Ordinal))
        {
            return GraphFailureKind.InvalidStatement;
        }

        return GraphFailureKind.Database;
    }

    private static string? Sanitize(string? code)
    {
        // Server codes are dotted identifiers. Anything else could carry statement text.
        if (string.IsNullOrWhiteSpace(code)
            || code.Length > _maximumCodeLength
            || !code.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.'))
        {
            return null;
        }

        return code;
    }
}
