namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Closed model-provider authentication operation.</summary>
public enum ModelProviderAuthenticationAction
{
    /// <summary>Authenticate and refresh provider model metadata.</summary>
    Login,

    /// <summary>Inspect secret-free authentication state.</summary>
    Status,

    /// <summary>Remove the host-owned provider grant and cached metadata.</summary>
    Logout,
}

/// <summary>Runs one model-provider authentication operation through the shared host boundary.</summary>
/// <param name="ProviderId">Stable compiled provider identifier.</param>
/// <param name="Action">Requested authentication action.</param>
public sealed record ManageModelProviderAuthenticationCommand(
    string ProviderId,
    ModelProviderAuthenticationAction Action) : ICommand<ModelProviderAuthenticationResult>;

/// <summary>Secret-free model-provider authentication result.</summary>
/// <param name="ProviderId">Stable compiled provider identifier.</param>
/// <param name="IsAuthenticated">Whether a usable grant exists.</param>
/// <param name="DiscoveredModelCount">Number of account models discovered during login.</param>
/// <param name="RequiresRestart">Whether the immutable session catalog must be rebuilt.</param>
/// <param name="Message">Bounded user guidance.</param>
public sealed record ModelProviderAuthenticationResult(
    string ProviderId,
    bool IsAuthenticated,
    int DiscoveredModelCount,
    bool RequiresRestart,
    string Message);
