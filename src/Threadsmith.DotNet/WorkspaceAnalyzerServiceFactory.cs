namespace Threadsmith.DotNet;

using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

/// <summary>Shares reusable analyzer images across the workspaces of this Roslyn host.</summary>
[ExportWorkspaceServiceFactory(typeof(IAnalyzerService), ServiceLayer.Host)]
[Shared]
internal sealed class WorkspaceAnalyzerServiceFactory : IWorkspaceServiceFactory
{
    private readonly WorkspaceAnalyzerLoader.ContextCache _cache = new();

    /// <inheritdoc />
    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices) => new WorkspaceAnalyzerLoader(_cache);
}
