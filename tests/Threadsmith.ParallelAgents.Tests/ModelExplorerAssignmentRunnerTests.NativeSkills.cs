namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Tools.Jira;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    private sealed class NativeSkillSnapshotProbe : IConversationToolSnapshotStore
    {
        private readonly ConversationToolSnapshotStore _inner = new();

        public ConcurrentQueue<Guid> Captured { get; } = new();

        public ConcurrentQueue<Guid> Resolved { get; } = new();

        public ConcurrentQueue<Guid> Released { get; } = new();

        public Guid Capture(SessionId sessionId, RunId runId, IReadOnlyList<ToolRegistration> registrations, ToolInvocationContext? invocationContext = null)
        {
            var identity = _inner.Capture(sessionId, runId, registrations, invocationContext);
            Captured.Enqueue(identity);
            return identity;
        }

        public IReadOnlyList<ToolRegistration> Resolve(Guid snapshotId, SessionId sessionId, RunId runId)
        {
            var registrations = _inner.Resolve(snapshotId, sessionId, runId);
            Resolved.Enqueue(snapshotId);
            return registrations;
        }

        public ToolInvocationContext? ResolveContext(Guid snapshotId, SessionId sessionId, RunId runId)
        {
            return _inner.ResolveContext(snapshotId, sessionId, runId);
        }

        public void Release(Guid snapshotId)
        {
            _inner.Release(snapshotId);
            Released.Enqueue(snapshotId);
        }
    }
}
