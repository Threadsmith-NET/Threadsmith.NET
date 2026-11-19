namespace Threadsmith.DotNet;

using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

/// <summary>Provides a fresh analyzer loader to Roslyn for each evaluated workspace.</summary>
[ExportWorkspaceServiceFactory(typeof(IAnalyzerService), ServiceLayer.Host)]
[Shared]
internal sealed class WorkspaceAnalyzerServiceFactory : IWorkspaceServiceFactory
{
    /// <inheritdoc />
    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices) => new WorkspaceAnalyzerLoader();
}
