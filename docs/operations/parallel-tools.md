# Parallel tool execution

Threadsmith can execute independent sibling tool calls from one completed model response concurrently. This is host scheduling, not model authority.

## How a batch runs

1. The provider finishes streaming the complete sibling call set. No tool starts while the set is still arriving.
2. The host validates each registration and typed argument object, then derives scheduling claims from confined host state.
3. `ToolConflictPlanner` considers calls in original ordinal order and places each call in the earliest wave that has capacity and no conflict.
4. `ToolInvocationPipeline.InvokeBatchAsync` creates every task in a wave before awaiting `Task.WhenAll`.
5. Every invocation still passes the ordinary policy, hook, approval, budget, timeout, sanitation, activity, and generation-fence boundaries.
6. Joined results are sorted by original ordinal before evidence and model-continuation messages are appended. Completion timing cannot reorder model context.

## Selection rules

Parallel execution requires all of the following:

- `tools:parallel:enabled` is true and the wave is below `maximumConcurrency`;
- both definitions use scheduling schema version 1 and `ParallelSafe`;
- both have a source concurrency maximum greater than one;
- both are compiled built-ins (dynamic MCP and extension registrations remain conservative by default);
- neither call requires approval;
- overlapping claims are read/read only.

A write, execute, external-effect, or exclusive access claim conflicts with another claim for the same resource. Path claims also conflict across ancestor/descendant identities. Unsupported metadata, code/process execution, semantic-workspace serialization, workflows, approval interaction, MCP, and extensions serialize.

## Tool metadata and where to edit it

The closed contracts are in `src/Threadsmith.Tools/ToolContracts.cs`:

- `ToolAccessMode` describes read/write/execute/external/exclusive access;
- `ToolResourceKind` identifies repository, path, Git, solution, semantic workspace, process, network, MCP, extension, session, or global resources;
- `ToolConcurrencyMode` selects parallel-safe or a conservative serialization scope;
- `ToolSchedulingDescriptor` carries schema version, resolver identity, and source maximum;
- `ToolResourceClaim` is the invocation-specific normalized claim.

Built-in definitions are created by `ToolDefinitionFactory` in `src/Threadsmith.Tools/BuiltInTools.cs`. Edit `CreateSchedulingDescriptor` when a category's reviewed concurrency behavior changes. A specialized tool can set `ToolDefinition.Scheduling` explicitly. Invocation-specific claims are produced by `Tool<TInput,TOutput>.GetSchedulingClaims`; override that boundary only when a tool needs more precise implicit resources than its declared paths, workspace, or executable.

Implementations that directly implement `ITool` inherit an exclusive compatibility claim and serialized descriptor unless they explicitly adopt a reviewed host contract. This is intentional for extension and MCP compatibility.

## Context and model-request state

Scheduling uses the immutable state captured for the current model request:

- the complete sibling call list and original ordinals/correlation IDs;
- current session and run IDs and authoritative `RunPhase`;
- repository root, approved/prohibited roots, trust, executable/network/secret policy, and requester identity from `ToolInvocationContext`;
- current workspace ID for semantic-workspace claims;
- the exact enabled registry instance and host-owned activity source;
- the batch-bound `ToolParallelOptions` snapshot.

The model supplies tool names and JSON arguments only. It cannot provide claims, concurrency modes, wave placement, limits, or overrides. A later model request is not dispatched until the batch joins.

## Configuration

```json
{
  "tools": {
    "parallel": {
      "enabled": true,
      "maximumConcurrency": 4,
      "failureMode": "CompleteStarted"
    }
  }
}
```

The host clamps concurrency to 1–16. `CompleteStarted` lets admitted siblings finish independently. `CancelBatchOnFailure` links sibling cancellation after the first terminal failure. Set `enabled` to false to reproduce sequential execution for diagnosis.
