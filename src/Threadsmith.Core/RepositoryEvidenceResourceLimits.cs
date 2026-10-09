namespace Threadsmith.Core;

/// <summary>Trusted configurable ceilings for invocation-only repository evidence collection.</summary>
public sealed record RepositoryEvidenceResourceLimits
{
    /// <summary>Maximum source and metadata bytes across an operation.</summary>
    public int MaximumInputBytes { get; init; } = 262144;

    /// <summary>Maximum serialized bytes in one evidence packet.</summary>
    public int MaximumPacketBytes { get; init; } = 131072;

    /// <summary>Maximum serialized bytes across all packets and inspections.</summary>
    public int MaximumOutputBytes { get; init; } = 1048576;

    /// <summary>Maximum prerequisite metadata acquisition, separate from profile body bytes.</summary>
    public int MaximumProfileMetadataBytes { get; init; } = 65536;

    /// <summary>Maximum metadata acquisition in one governed read.</summary>
    public int MaximumMetadataReadBytes { get; init; } = 16384;

    /// <summary>Maximum source bytes acquired for one file.</summary>
    public int MaximumFileBytes { get; init; } = 16384;

    /// <summary>Maximum files requested in an evidence batch.</summary>
    public int FileBatchSize { get; init; } = 8;

    /// <summary>Maximum commits examined in one frontier page.</summary>
    public int HistoryPageSize { get; init; } = 4;

    /// <summary>Maximum selected changed paths per commit.</summary>
    public int MaximumChangesPerCommit { get; init; } = 8;

    /// <summary>Maximum question characters admitted before normalization.</summary>
    public int MaximumQuestionCharacters { get; init; } = 2048;

    /// <summary>Maximum literal scope paths.</summary>
    public int MaximumScopePaths { get; init; } = 8;

    /// <summary>Maximum requested symbol anchors.</summary>
    public int MaximumSymbols { get; init; } = 8;

    /// <summary>Maximum characters in a symbol anchor.</summary>
    public int MaximumSymbolCharacters { get; init; } = 256;

    /// <summary>Maximum characters in a source locator.</summary>
    public int MaximumLocatorCharacters { get; init; } = 2048;

    /// <summary>Maximum lines returned by one cached-source inspection.</summary>
    public int MaximumInspectionLines { get; init; } = 201;

    /// <summary>Maximum evidence identifiers retained during the live operation.</summary>
    public int MaximumEvidenceIdentifiers { get; init; } = 512;

    /// <summary>Serialized space reserved for episode and omission metadata before acquiring content.</summary>
    public int PacketReserveBytes { get; init; } = 8192;

    /// <summary>Maximum characters retained in an optional omission locator.</summary>
    public int MaximumDiagnosticLocatorCharacters { get; init; } = 256;

    /// <summary>Rejects invalid configured ceilings before collection starts.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPacketBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProfileMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMetadataReadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FileBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HistoryPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumChangesPerCommit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumQuestionCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumScopePaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSymbols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSymbolCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLocatorCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInspectionLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEvidenceIdentifiers);
        ArgumentOutOfRangeException.ThrowIfNegative(PacketReserveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiagnosticLocatorCharacters);
    }
}
