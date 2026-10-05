namespace Threadsmith.Tools;

/// <summary>Opt-in metadata on concrete native tool inputs; never grants execution authority.</summary>
public interface IConceptToolInput
{
    /// <summary>Optional bounded applicability hints supplied by the model.</summary>
    IReadOnlyList<string>? Concepts { get; }
}
