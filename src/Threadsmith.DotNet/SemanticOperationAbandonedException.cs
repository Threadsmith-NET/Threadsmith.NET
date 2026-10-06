namespace Threadsmith.DotNet;

using System.Diagnostics.CodeAnalysis;

/// <summary>Carries actual completion so interrupted warming cannot restart while its abandoned work still runs.</summary>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Abandonment requires the actual operation task; constructors without that identity would be invalid.")]
internal sealed class SemanticOperationAbandonedException : OperationCanceledException
{
    /// <summary>Initializes a new instance of the <see cref="SemanticOperationAbandonedException"/> class.</summary>
    public SemanticOperationAbandonedException(Task completion, CancellationToken cancellationToken)
        : base("Compiler work remains active after bounded cancellation.", cancellationToken)
    {
        Completion = completion;
    }

    /// <summary>Actual operation completion, independent of the caller's bounded wait.</summary>
    public Task Completion { get; }
}
