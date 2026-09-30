# Milestone 32 — In-process TUI automation

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Allow a .NET application to host an ordinary Threadsmith.NET TUI in-process, drive its permanently read-only composer through a public API, receive only final main-model answer text, and shut down all owned work safely.

## Deliverables

- Reusable public host library and shared CLI/API composition and lifetime.
- Required absolute solution/project binding through ordinary repository and semantic startup.
- Optional config/providers JSON independently replacing the corresponding user layer under existing hierarchy/validation, with ordinary calling-account file fallback when omitted.
- Immutable external composer ownership, common input commit, and safe programmatic secondary/steering replies.
- One continuous bounded instance-level final-answer async stream emitting each new response once without conversation-history replay; independent per-submission completion, explicit failure/cancellation semantics, and unchanged full TUI output.
- Model-facing equivalence with ordinary input and no automation-origin annotations.
- Shared quit/stop/dispose authority covering active parent/descendants, terminal restoration, and owned resources.
- Consuming sample, transitive runtime asset validation, deterministic integration tests, and maintained terminal procedures.

## Capability prerequisites

- M2: repository and solution/project lifecycle.
- M7.4: governed conversation continuity.
- M11: ordinary execution and approval orchestration.
- M11.1: hierarchical in-process delegation and cancellation.
- M15: deployment asset closure and platform requirements.
- M20: interactive session lifecycle.
- M29: deployed prompt cache and immutable model-facing assets.
- Frontend-neutral coordination and the sole TUIKit frontend architecture contracts.

## Exit criteria

A separate .NET consumer can launch the ordinary TUI with an explicit target, render and submit API messages through common input coordination, enumerate exclusively final main-model answers under bounded delivery, and dispose during active delegated work without leaving tracked work or terminal ownership behind. Manual/API model requests remain equivalent, ordinary CLI/headless behavior remains compatible, and required deterministic and real-terminal evidence is recorded.

## Boundaries

This is interactive TUI automation, not headless embedding or a new execution surface. It does not add trust, bypass approvals, expose private reasoning/tool results, or support concurrent TUIs sharing a terminal. Non-composer selection controls retain ordinary human interaction unless a separately agreed decision contract changes that behavior.

[Dependency DAG](dependency-dag.md)
