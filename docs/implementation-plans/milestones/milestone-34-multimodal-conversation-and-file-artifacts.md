# Milestone 34 — Multimodal conversation and file artifacts

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Supply images to capable models through ordinary file exploration and composer clipboard input, and save model-produced image artifacts through governed file tools.

## Deliverables

- Per-model composable input content capabilities and independent output capabilities.
- Host-owned image parts, bounded binary artifacts, governed context and durable reference lifecycle.
- Image input through all three compiled providers where their actual endpoints support it.
- Image discovery and loading through existing exploratory tools and `read_file`.
- Clipboard attachments through the existing composer and interaction path.
- Generated image normalization and exact-byte export through `write_file`, with explicit provider limitations.

## Capability prerequisites

M3 model/tool contracts; M7.3 compiled provider configuration; M7.4 conversation context; M8 artifact persistence; M14 file tools; M18 Codex provider and operation visibility; M19 canonical requests/cache; M20 resume/clone; M30 native Anthropic provider. Preserve frontend-neutral coordination, sole TUIKit frontend, and deployed prompt ownership contracts.

## Exit criteria

Users can inspect repository and pasted PNG/JPEG images with each capable provider through ordinary execution. Capability failures, budgets, cancellation, privacy, provenance, replay and retention remain governed. A model-produced artifact can be exported through a normal model-issued `write_file` invocation without corrupting bytes or widening destination authority. Deterministic and available live-provider/platform evidence establish the supported matrix, with unsupported output capabilities clearly identified.

## Boundaries

Text and raster images are the initial supported kinds. This capability does not imply universal image generation, introduce another conversation engine or artifact store, add a generation service, or execute/fetch embedded content. Existing policy and dependency direction remain authoritative.

[Dependency DAG](dependency-dag.md)

## Implementation evidence

See [Plan 120 §18](../plan-120-multimodal-input-and-file-output.md#18-implementation-and-verification-record--2026-10-16) for implementation decisions, provider/platform verification and remaining work.
