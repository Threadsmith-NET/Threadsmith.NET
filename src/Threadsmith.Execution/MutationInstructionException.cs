namespace Threadsmith.Execution;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Invalid mutation instructions, distinct from advisory compiler diagnostics.</summary>
internal sealed class MutationInstructionException : MalformedModelOutputException
{
    /// <summary>Initializes a new instance of the <see cref="MutationInstructionException"/> class.</summary>
    public MutationInstructionException()
        : this(
            CreateRepairableMutationDiagnostic(
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                "The mutation proposal was rejected before staging."))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MutationInstructionException"/> class.</summary>
    public MutationInstructionException(string message)
        : this(
            CreateRepairableMutationDiagnostic(
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                message))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MutationInstructionException"/> class.</summary>
    public MutationInstructionException(string message, Exception innerException)
        : this(
            CreateRepairableMutationDiagnostic(
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                message),
            innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MutationInstructionException"/> class.</summary>
    public MutationInstructionException(
        MalformedInvocationDiagnostic diagnostic)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).SafeMessage)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Initializes a new instance of the <see cref="MutationInstructionException"/> class.</summary>
    public MutationInstructionException(
        MalformedInvocationDiagnostic diagnostic,
        Exception innerException)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).SafeMessage, innerException)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Bounded safe failure details.</summary>
    public MalformedInvocationDiagnostic Diagnostic { get; }

    /// <summary>Creates bounded correction evidence for invalid mutation instructions.</summary>
    internal static MutationInstructionException CreateRepairableMutationFailure(
        MalformedInvocationFailureKind kind,
        string safeMessage,
        Exception? innerException = null,
        string toolName = "edit_source")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        var diagnostic = CreateRepairableMutationDiagnostic(kind, safeMessage) with { ToolName = toolName };
        return innerException is null
            ? new MutationInstructionException(diagnostic)
            : new MutationInstructionException(diagnostic, innerException);
    }

    /// <summary>Creates bounded correction evidence for invalid mutation instructions.</summary>
    internal static MalformedInvocationDiagnostic CreateRepairableMutationDiagnostic(
        MalformedInvocationFailureKind kind,
        string safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return new MalformedInvocationDiagnostic
        {
            Kind = kind,
            SafeMessage = BoundCorrectionReason(safeMessage),
            ToolName = "edit_source",
            ToolOrdinal = 0,
            ToolCallCount = 1,
        };
    }

    /// <summary>Bounds one correction without retaining control characters.</summary>
    internal static string BoundCorrectionReason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var sanitized = value.ReplaceLineEndings(" ");
        var builder = new StringBuilder(Math.Min(sanitized.Length, 512));
        foreach (var character in sanitized)
        {
            if (builder.Length == 512)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
