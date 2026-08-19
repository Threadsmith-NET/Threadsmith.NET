## Milestone 1 — Core Host, Events, and TUI Shell  *(plans 02, 03, 04)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Establish the application lifecycle and observable command/event architecture.

**Deliverables:**
- Core identifiers and result types.
- Application command bus/dispatcher.
- Domain event stream.
- In-memory projections.
- Conversation-first inline terminal with a multiline composer, native selectable scrollback, batched streaming output, and fail-closed review prompts.
- Headless CLI shell.
- Configuration bootstrap.
- Structured logging and tracing baseline.
- Deterministic fake model provider.

**Exit criteria:**
- A user can open the interactive terminal, create a session, submit a multiline request, and observe scripted activity in native scrollback.
- Native terminal selection can copy prior inputs and responses, and bulk paste remains responsive.
- The same scripted session can run headlessly.
- All UI actions map to application commands.
- Cancellation works through the application boundary.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
