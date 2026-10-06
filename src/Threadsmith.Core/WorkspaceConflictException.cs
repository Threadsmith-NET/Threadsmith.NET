namespace Threadsmith.Core;

/// <summary>Signals that on-disk state no longer matches the immutable mutation baseline.</summary>
public sealed class WorkspaceConflictException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException()
        : this(new ConflictReport(default, []))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException(string message)
        : base(message)
    {
        Report = new ConflictReport(default, []);
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
        Report = new ConflictReport(default, []);
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException(ConflictReport report)
        : base("The mutation set conflicts with current workspace files.")
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
    }

    /// <summary>Detailed conflicts that blocked application.</summary>
    public ConflictReport Report { get; }
}
