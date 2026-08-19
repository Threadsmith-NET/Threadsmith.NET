# Tool Contracts

Threadsmith tools expose host-owned typed inputs and outputs through `ITool`. Built-ins normally derive from `Tool<TInput,TOutput>` so JSON binding, unknown-member rejection, and typed execution remain centralized.

A definition must provide a stable snake-case id and version, input/output schema references, category, minimum repository trust, required approval, side-effect and idempotency classifications, cancellation support, timeout, maximum serialized output size, and redaction metadata.

Implementations must:

- validate type-specific bounds in `ValidateInput`;
- declare every path, executable, network host, and logical secret reference consumed by the operation;
- return host-owned DTOs plus `ToolProvenanceSource` entries;
- propagate the supplied `CancellationToken`;
- bound work before allocating or retaining large results;
- avoid logging arguments, file contents, command output, or resolved secrets.

Tools are invoked only through `IToolInvocationPipeline`. The pipeline owns policy, approval, budgets, cancellation/timeouts, sanitization, durable activity, and dynamic result serialization. Extension registration and leases arrive in plans 14–16; those capabilities must preserve this same boundary and must not expose extension implementation types in durable state.
