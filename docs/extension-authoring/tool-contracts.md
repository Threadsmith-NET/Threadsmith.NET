# Tool Contracts

Threadsmith tools expose host-owned typed inputs and outputs through `ITool`. Built-ins normally derive from `Tool<TInput,TOutput>` so JSON binding, unknown-member rejection, and typed execution remain centralized.

A definition must provide a stable snake-case id and version, input/output schema references, category, minimum repository trust, required approval, side-effect and idempotency classifications, cancellation support, timeout, maximum serialized output size, and redaction metadata.

Implementations must:

- validate type-specific bounds in `ValidateInput`;
- declare executables and logical secret references for policy evaluation;
- for host-managed tools, declare network hosts and disk paths for policy evaluation;
- for extensions, disclose disk and network use in the package manifest so the operator can review it before enabling the package;
- return host-owned DTOs plus `ToolProvenanceSource` entries;
- propagate the supplied `CancellationToken`;
- bound work before allocating or retaining large results;
- avoid logging arguments, file contents, command output, or resolved secrets.

Tools are invoked only through `IToolInvocationPipeline`. The pipeline owns policy, approval, budgets, cancellation/timeouts, sanitization, durable activity, and dynamic result serialization. Extension tools share the same invocation path and hold a generation lease while executing.

## External services and optional operator review

Set `Category = ExtensionToolCategory.ExternalService` for tools that operate on an external service. For a database extension, use this category for both schema inspection and query execution. Set `SideEffect` to `ReadOnly` for schema inspection and forced-read-only execution. Category and side effect do not request approval or limit extension I/O.

Use `ExtensionToolSideEffect.WritesExternalData` for a tool that may change a remote system. Set its idempotency according to its actual replay behavior. Enabling an extension already gives it direct I/O. The side-effect label tells the host how to report an interruption. To request a review for each model tool call, also set `RequiredApproval = ExtensionApprovalRequirement.User`.

A statement tool can declare its review when it registers its definition. For example, set `StatementReview = new ExtensionStatementReview(destination, database, "cypher", "parameters")`. Supply the target from generation-owned settings. Execute the same statement at that target. The host reads the complete statement from the invocation JSON and lists the parameter names and JSON types. Parameter values stay out of the preview. Separate unsupported options into another tool contract. Unexpected top-level fields, ambiguous JSON, an unsafe display value or an oversized review prevent approval.

The terminal opens the complete scrollable review. F2 returns to the choices. Approval applies once to that invocation and registration. The durable approval event identifies the tool. If a write is denied before dispatch, its host failure receipt reports `notStarted`. If the host interrupts a dispatched call before it has a result, the receipt reports `unknown`. Verify external state before retrying. The extension can return a confirmed commit or rollback in its own structured result. The host cannot observe writes an extension performs outside a tool invocation.

## Extension secret access

Use a host with extension contract 1.1 or later. Add `secret-access` to the package permissions and set `RequiredTrust` to `FullyTrustedAutomation`.
Load the package in a repository with that level of trust.
Return the logical reference from `IToolCapability.GetSecretReferences` for each call that needs it.
Inside `ExecuteAsync`, use the reference captured from the generation's settings.

```csharp
var access = context.Secrets ?? throw new UnauthorizedAccessException("Secret access unavailable.");
var credential = await access.ResolveAsync(passwordReference, cancellationToken);
await client.AuthenticateAsync(credential.Reveal(), cancellationToken);
```

The context supplies access only after admission for the exact tool and arguments.
The shared resolver selects an eligible user-owned or higher-trust provider.
Keep the revealed value local to the authenticated operation.
Completion, cancellation, deadline expiry, and generation retirement end access.
An ordinary drain lets an admitted operation finish. Use the wrapper's explicit reveal only at the transport boundary.
The host checks tool output and provenance and protects extension logs. After a generation resolves a credential,
its host logger also withholds messages containing backslashes because free-form text can contain escaped credentials.
Extension code remains responsible for any copies it makes and for third-party logging inside its own process.
