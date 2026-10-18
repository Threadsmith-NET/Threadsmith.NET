# Plan 120 — Multimodal input and file output

**Status:** Complete — implementation and release contracts verified; unavailable live/platform coverage recorded below

**Delivery track:** M34 — Multimodal conversation and file artifacts

**Prerequisites:** Existing polymorphic model configuration, all three compiled providers, chronological requests and cache identity, governed context and conversation persistence, ordinary tool execution, `read_file` and `write_file`, frontend-neutral interaction and sole TUIKit composer, session resume/clone, deployed prompt catalog. Integrate with current context admission, steering, and delegation contracts without waiting for unrelated work.

**Related contracts:** [planning governance](planning-governance.md), [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), [M34](milestones/milestone-34-multimodal-conversation-and-file-artifacts.md), [portable C# guardrails](../guardrails/portable-csharp-guardrails.md), [conversation loop](../operations/conversation-loop.md).

## 1. Objective

Let users supply images to capable models through ordinary conversation: describe or name a repository image that the agent discovers and opens with `read_file`, or paste an image into the composer. Let the agent save a model-produced image through the existing `write_file` tool. Preserve normal tools, policy, progress, cancellation, context budgeting, and session continuity.

Declare supported input content types on every model configuration using a flags enum. Treat output capabilities separately: accepting an image does not imply generating one. Deliver text and raster images first; provide an extensible contract without advertising unimplemented audio, video, or document support.

## 2. Architectural Context

Extend the existing conversation, request preparation, provider, artifact, and tool paths. Do not create a separate vision agent, image conversation loop, attachment database, file reader, file writer, or clipboard submission route. Host-owned immutable descriptors cross boundaries; SDK, terminal, decoder, and clipboard types remain internal adapters. UI renders attachment metadata and submits ordinary commands; Execution retains authority.

Binary storage is a focused extension of existing artifact ownership. Its current text sanitizer cannot sanitize image bytes or detect secrets depicted in an image. Explicit binary admission, confidentiality policy, bounded validation, and accurate documentation are required; do not pretend text redaction protects visual content.

## 3. Scope

- Per-model flags for supported input types, independent output flags, validation, configuration layering, discovery, selection, and profile propagation.
- Ordered text/image parts through submission, tool evidence, context assembly, provider requests, outputs, archives, resume/clone, and delegated context.
- Image input for OpenAI-compatible, OpenAI Codex Responses/OAuth, and Anthropic providers where the selected model and actual endpoint support it.
- Existing exploratory tools locate candidate paths; `read_file` loads an authorized image as image evidence.
- Clipboard image capture, attachment inspection/removal, image-only submission, and draft preservation in TUIKit.
- Model-produced image admission and exact-byte export through `write_file`, with honest unsupported-provider behavior.
- Byte, pixel, attachment-count, context, storage, and transport limits; lifecycle events and meaningful tests.

## 4. Non-Scope

Audio/video/PDF delivery, arbitrary binary execution, remote image URL ingestion, OCR substitution for image input, a separate image-generation service, new hosted image tools, image editing UI, drag-and-drop, and terminal inline bitmap rendering. Future content kinds need their own validators and protocol support before enablement. This work does not make every provider an image generator or broaden `write_file` destination authority.

## 5. Current State

Source observations below are from the active checkout; re-read before implementation.

| Existing owner | Observation and implication |
|---|---|
| `Models/ModelProviderConfiguration.cs` | Shared `ModelConfiguration` carries capabilities but no content-type flags. Add the common property here and propagate through all provider registrations and profiles. |
| `Models/RequestOptimizationContracts.cs` | `ModelContentPartKind` has Text/Json; parts require string content. `GetModelVisibleContent()` concatenates strings. Flattening must not silently discard images. |
| `Models.OpenAiCompatible/OpenAiCompatibleModelProvider.cs` | `BuildMessages` currently flattens user and tool messages into text. Preserve its existing role and tool-call handling while adding typed parts. |
| `Models.OpenAiCodex/OpenAiCodexModelProvider.cs` | Structured requests currently emit text content blocks and textual tool results. Extend the actual OAuth endpoint adapter, not a general API-key Responses substitute. |
| `Models.Anthropic/AnthropicRequestMapper.cs` | Flattens content before mapping text and correlated `tool_result` blocks. Preserve replay and tool identity while adding image blocks. |
| `Tools/BuiltInTools.cs` | `ReadFileTool` reads text ranges and UTF-8 snapshots with source receipts. Image reads need bounded classification before text decoding; existing snapshot/line semantics remain text-only. |
| `Tools/WriteFileTool.cs` | Writes UTF-8 content or the last archived response, has a text extension allowlist, validates destination confinement, and publishes via a temporary file. Extend this implementation. |
| `Persistence/ArtifactStore.cs` | Content-addressed artifact storage accepts sanitized strings and owns SQLite metadata. Add binary operations and reference ownership within this lifecycle. |
| `Interaction/Contracts/InteractionInput.cs` | Submission carries text and cancellation. Extend its ordinary submission path with attachment references. |
| `Tui.TuiKit/ClipboardReader.cs`, `TuiKitSurface.cs`, `TuiKitComposer.cs` | Clipboard reads return text; paste has input-epoch checks. Retain those race protections for asynchronous image capture. |

Paths in this table are under `src/Threadsmith.*`. Provider protocol details, clipboard platform behavior, and native image output support have not been verified live by this planning change. Their verification is an explicit step deliverable, not an assumed capability.

## 6. Proposed Design

### 6.1 Model content capabilities

Introduce host-owned `[Flags] ModelContentTypes` with stable values `None = 0`, `Text = 1`, `Image = 2`. Use repository C# naming conventions rather than uppercase enum members. Reserve extensibility without declaring unsupported values usable. Add `SupportedInputContentTypes` to base `ModelConfiguration` and effective `ModelProfile`, defaulting to `Text`; add independent `SupportedOutputContentTypes`, also defaulting to `Text`.

Code can combine `Text | Image`; configuration uses a documented flags string such as `"Text, Image"`. Reuse the existing JSON/configuration pipeline, validating unknown bits, invalid names, empty flags, and configurations lacking Text for this conversational harness. Decide and test numeric handling explicitly; prefer rejecting numeric JSON flags rather than accepting undocumented values. Keep the flags separate from existing tool/reasoning capabilities.

Effective support is the intersection of configured model support and the adapter's verified endpoint support, further constrained by host policy and format/size limits. Discovery may populate verified metadata but must not infer vision or generation from a model name or overwrite explicit configuration. Preserve omission versus explicit override in stable-ID catalog merging. Revalidate on model selection, role-model assignment, fallback/retry, resume, and every submission. Never silently drop an image, convert it into a filename, or switch providers to make a request succeed. An incompatible queued draft remains recoverable with an actionable error.

### 6.2 Content and artifact contracts

Extend existing ordered content parts with an image variant that references an immutable host artifact descriptor: opaque ID, content digest, validated MIME type, byte length, dimensions, and source provenance. Text retains existing meaning; images do not overload the text field with base64. Put shared descriptors below Models/Interaction/Tools in the existing dependency graph, with no persistence implementation reference from those consumers.

Extend `IArtifactStore` and its implementation for bounded binary streams and metadata. Reuse content-addressed bytes, atomic publication, retention, and disposal ownership; store session/run reference grants separately from digest deduplication. Knowing a digest must not authorize access to another session's bytes. Keep clipboard draft references alive until transfer to a submitted message, removal, cancellation, or expiry. Archive references and ownership atomically enough to prevent accepted messages pointing to uncommitted artifacts. Resume/clone maintain authorized references; garbage collection cannot delete bytes referenced by live drafts, conversations, or active requests. Missing/corrupt content yields an explicit recoverable result.

Initial required raster formats are PNG and JPEG. Other formats require deliberate validated allowlisting and endpoint coverage. Classify by signature and validate dimensions/structure, not just extension or claimed MIME. Bound encoded bytes, decoded pixels, width/height, frame count, attachment count, per-request bytes, and total retained bytes before expensive decoding or allocation. Reject unsupported animated, vector, malformed, or decompression-bomb inputs. Clipboard bitmap conversion may normalize to PNG once; record the resulting digest. Do not silently resize repository or generated image bytes.

### 6.3 Context, execution, and provider mapping

Carry attachments through ordinary input commands and chronological messages. Extend tool output/evidence with typed artifact references alongside bounded textual metadata; JSON descriptions alone are not image input. Resolve bytes only after policy and capacity admission, at the normal provider boundary. Preserve role, ordering, tool invocation/source identity, and the distinction between user input and untrusted tool evidence.

Provider adapters map images into their supported native request structures. If an endpoint cannot put images inside a tool result, keep the correlated textual tool result and emit an immediately adjacent image-bearing message in a supported role. This adapter-local representation must retain host provenance, avoid a fabricated user instruction, and be tested against actual protocol constraints. Do not add a second engine path. Required fixture cases cover images loaded by tools, not only composer images.

Preparation counts image costs using verified endpoint rules or a documented conservative bound; unknown image cost cannot be zero. Include media digest, type, dimensions, detail/transform options, and order in canonical request/cache/replay identities. Account for base64 expansion and JSON envelopes independently from text tokens. Do not tokenize base64 as natural language. Preserve existing output reserves, retry limits, sensitive-data rules, and context inspection receipts.

Compaction may replace historical images with explicit metadata summaries under existing context policy; it must not report omitted pixels as visible. Required current attachments fail admission rather than vanish. Explicit rereads recover historical content through ordinary tools. Child contexts inherit only admitted references within their immutable assignment baseline and selected model capability. Do not copy all parent attachments or expose sibling artifacts. No new raw bytes in logs, transcript JSON, search indexes, embeddings, or hidden provider replay dumps.

### 6.4 File discovery and `read_file`

Reuse existing filename/path discovery. Searching by description can locate plausible filenames and metadata, then inspect bounded candidates with `read_file`; do not promise content-based image search or decode every repository image. Ensure discovery does not filter image paths merely because text search ignores binary files.

Add a backwards-compatible read mode (`auto`, `text`, `image`, with `auto` the default) or equivalent closed discriminator to `read_file`. In auto mode, inspect a bounded prefix before choosing the existing text reader or image path. Images return metadata plus an admitted image artifact reference consumed by the normal evidence path. Text range/snapshot flags on image reads receive a clear argument error; no fake line receipts. Unsupported binary files fail clearly. Preserve repository/scratchpad confinement, prohibited paths, trust, source digest and change detection, activity, cancellation, and resource budgets. Recheck size while streaming to handle files that grow after stat.

### 6.5 Clipboard and composer

Extend the existing clipboard adapter result to distinguish bounded text and image data. Keep OS clipboard access inside the frontend adapter. Support maintained Windows, macOS, and Linux clipboard environments where an available platform mechanism permits image capture; bound helper process output and time, propagate cancellation, and report missing helpers or remote/SSH clipboard limitations honestly. Terminal bracketed paste remains text; it is not a binary-image transport.

Explicit paste captures the clipboard once, preferring its validated image representation when present and otherwise preserving text behavior. Display a removable attachment entry with name/type/dimensions/size. Permit text-plus-images and image-only submissions. Capture freezes bytes before the clipboard changes. Preserve the text draft and existing attachments on error, limit failure, unsupported model, or cancelled submission. Reject stale capture results after input epoch, composer purpose, session, or ownership changes; release their temporary references. Images must not enter command palettes, approval prompts, or other text-only secondary prompts. Steering uses the same attachment admission at the ordinary steering boundary, or explicitly refuses attachment paste if that boundary cannot yet carry it; completion requires steering behavior to be documented and tested. Respect a read-only automation-owned composer if that capability is present.

### 6.6 Generated output and `write_file`

Extend normalized provider output with typed image artifacts only where the actual endpoint can return image bytes. Validate and publish a complete artifact once, after successful bounded decoding/stream completion. Partial, cancelled, duplicate/retried, or malformed output must never become a successful file. Keep a response-local stable artifact identity so completed content can coexist with text and tool calls without double publication. Do not interpret arbitrary prose, Markdown image links, or a model's claim of generation as binary output.

Extend `write_file` with an `artifactId` source mutually exclusive with existing `content` and `useLastResponse`. Resolve only artifacts authorized in the current session/run context, validate MIME and destination extension, stream exact bytes, and reuse its existing destination allowlist, prohibited paths, link rejection, overwrite semantics, temporary-file publication, activity, and cancellation. Add PNG/JPEG only to the binary branch; do not weaken text validation or turn `useLastResponse` into an ambiguous binary selector. Return artifact digest, MIME, source response identity, path, and byte count without echoing bytes.

Make completed generated artifact IDs available to the model through the ordinary response/evidence continuation mechanism before it calls `write_file`. If generation ended a response, a later ordinary user request can ask to save it; a previously requested save may continue through the existing tool loop under normal budgets, with no fabricated tool call, silent automatic export, or extra generation request. Specify and test response finality so the host does not announce task completion before a requested save finishes.

Some configured endpoints may have no native image output. Their output flags remain Text, with a clear unsupported-generation result. The binary export feature must still be proven end to end with a deterministic provider fixture producing real PNG/JPEG bytes, including a subsequent model-issued `write_file` invocation. Validate every available real output-capable endpoint; record unavailable live evidence. Supporting a new generation endpoint or hosted tool requires a separately scoped extension, not a hidden provider fallback.

## 7. Public Contracts

Proposed additive contracts: model input/output flags; image content variant and shared artifact descriptor; bounded binary artifact store operations; attachment-bearing ordinary submission and steering DTOs; typed tool evidence references; normalized generated-image output; `read_file` mode; `write_file.artifactId`. Final names follow existing code conventions. Preserve existing serialized text contracts and defaults; version durable records where needed. Tool schemas use closed alternatives with runtime validation. SDK, OS, decoder, and terminal types do not cross these contracts.

## 8. Project/File Changes

| Owner | Expected work |
|---|---|
| `Threadsmith.Core`, `Threadsmith.Models` | Shared descriptors, configuration/profile flags, content/output contracts, admission and canonical identity. |
| Three `Threadsmith.Models.*` providers | Registration/discovery propagation, preparation, native mapping, stream normalization, replay/cache accounting. |
| `Threadsmith.Persistence`, `Threadsmith.Context` | Binary artifact extension, reference lifecycle, archive compatibility, selection/compaction/inspection. |
| `Threadsmith.Execution`, `Threadsmith.App` | Ordinary conversation/tool integration, output publication/continuation, configuration composition and resource limits. |
| `Threadsmith.Tools` | Existing discovery as needed, `read_file`, `write_file`, schemas and deployed descriptions. |
| `Threadsmith.Interaction`, `Threadsmith.Tui.TuiKit` | Attachment submission/projection, clipboard adapters, composer attachment lifecycle. |
| Corresponding tests and documentation | Behavior fixtures, integration, migrations, platform procedures, configuration/prompt references. |

Do not add projects or dependencies until an inspected implementation need justifies them. Pin any required decoder/platform package centrally and include license/release closure.

## 9. Ordered Tasks

Each step is a bounded implementation assignment with its own tests and completion gate. Record completion here. A step may land while later surfaces remain unavailable; production must reject unsupported content until its dependencies are present. Tests use the common contracts, not a temporary competing implementation.

| Step | Depends on | Independently completed result and verification gate |
|---|---|---|
| S1 — Capability and content contracts | Existing prerequisites | Implement flags/defaults/merge validation and profile propagation; image descriptors and request/output/tool contract shapes. Establish official protocol evidence for the three actual endpoints, required formats/limits, and output support. Complete serialization, unknown-value, default compatibility, and dependency tests. No image is sent yet. |
| S2 — Binary artifact lifecycle | S1 | Extend existing artifact storage with bounded validation, atomic bytes, scoped grants, draft/archive references, retention and migrations. Prove corrupt/oversized input rejection, cross-session denial, cancellation cleanup, digest integrity and reference-safe deletion using real small images. |
| S3 — Governed multimodal conversation | S1, S2 | Thread ordered references through ordinary submission, tool evidence, context preparation, archives, resume/clone, cache/replay, output publication and child admission. Complete the normalized output/artifact handoff for later export. Prove full request and output paths with the deterministic provider, including capacity, fallback and finality. |
| S4 — OpenAI-compatible adapter | S3 | Complete input mapping for the configured chat endpoint, including tool-loaded images and text compatibility; output normalization only for verified supported response shapes. Pass sanitized wire fixtures, accounting, retry/cancel and unsupported-capability tests. Record endpoint-specific live limitations. |
| S5 — Codex Responses/OAuth adapter | S3 | Complete input mapping against the actual Codex endpoint and image-bearing tool evidence representation; preserve OAuth, continuation and replay. Verify output capability honestly. Pass wire fixtures, cache invalidation and retry/cancel tests. |
| S6 — Anthropic adapter | S3 | Complete image and tool-result mapping through the existing SDK adapter; preserve thinking/replay/tool correlation and cache controls. Verify output capability honestly. Pass wire fixtures, count/admission and retry/cancel tests. |
| S7 — Image discovery and `read_file` | S3 | Extend existing tools to discover paths and read images via the ordinary invocation/evidence pipeline. Complete filename-description discovery, auto/text/image behavior, confinement, changed-file, size/race and cancellation tests. Prove a deterministic conversation reads and reasons over actual image parts. |
| S8 — Clipboard composer attachments | S3 | Implement capture adapters and ordinary composer/steering submission with removable entries and image-only input. Complete draft/epoch/session race tests and supported-platform clipboard smoke checks. A fake provider proves submission independently of live providers. |
| S9 — Generated image export | S3 | Extend existing `write_file` to consume authorized artifact IDs. Complete output-to-artifact-to-model-tool-call-to-exact-file integration, overwrite/path denial, partial output, duplicate retry, cancellation, and byte-integrity tests. No dependency on clipboard or repository reading. |
| S10 — Integrated acceptance and documentation | S4–S9 | Exercise all three real input adapters with composer and tool input, output-capable endpoints where available, resume/model changes, child visibility, and the complete manual matrix. Finish documentation and required architecture/build/release checks; record every unassessed environment. |

S1 → S2 → S3 forms the shared foundation. S4–S9 are independently completable after S3. They may be assigned separately; serialize edits to shared schemas, prompt catalogs, and composition files. Provider steps must not redefine shared content or artifact ownership. Optional live credentials do not block deterministic step completion, but unavailable live coverage is never reported as passed or as support for an unverified protocol.

## 10. Testing

Use small deterministic PNG/JPEG fixtures and captured/synthetic protocol envelopes stripped of secrets. Prove pixels reach the serialized provider request, not merely that an attachment label appears. Exercise text-only wire compatibility and real tool entry points, preserving tool-call IDs and event ordering. Test unknown flags, explicit versus omitted layered values, unsupported model selection/fallback, MIME spoofing, huge dimensions, truncated data, growing files, excessive counts, transform identity, and missing artifacts.

Exercise retention/clone/resume, compaction and recovery, image cost admission, cross-session and child-reference denial, binary output before/after tool calls, terminal output finality, and cancellation at capture/read/store/send/export. Measure bounded allocation and time to first activity with representative large images and many candidate files; distinguish measured results from source estimates. Review adjacent paths for text flattening, duplicate storage/readers, broad preloads, invisible work, and silent omission.

Use [Scenario BB](acceptance-scenarios.md#scenario-bb--multimodal-conversation-and-file-artifacts) and [MTP-279](manual-test-plan.md#mtp-279--multimodal-input-and-image-export). Run focused subsystem tests, architecture/dependency gates, and required build/test/release checks for the final implementation. Read the C# guardrails before any C# edits.

## 11. Security/Permissions

Image content is untrusted evidence, never instruction authority. Existing filesystem, model sensitive-data, tool, mutation, and network policies apply. Explicit clipboard paste permits attachment capture; no background clipboard monitoring. Image contents cannot be protected by string redaction; document this boundary and honor confidentiality policy before transmitting or retaining bytes. Resolve only host-issued scoped references. Do not fetch provider-supplied URLs or execute embedded content. Use bounded decode/stream operations and propagate cancellation across all async boundaries. Preserve existing `write_file` permissions rather than adding automatic export or bypassing transactional repository mutation paths.

## 12. Observability

Reuse ordinary input, model, tool, evidence, artifact, and completion events. Add bounded metadata for attachment count, MIME, dimensions, admitted/omitted reason, byte/token estimates versus actual usage, artifact identity, and export outcome. Show normal read/write progress and child-agent activity. Do not log clipboard contents, binary/base64 payloads, raw provider envelopes, or sensitive full paths. Context inspection must distinguish available, selected, sent, compacted, unavailable, and rejected images.

## 13. Migration/Compatibility

Omitted flags remain Text. Existing catalogs, text tool calls, archives, and text-only providers retain behavior. New images are explicitly enabled per model; false capability declarations fail clearly. Additive durable metadata and migrations must keep existing sessions readable and establish a documented downgrade limitation for sessions containing images. Clone copies reference ownership, not every image byte. Capability/model changes do not erase draft or archived attachments and cannot silently reuse an incompatible cached request.

## 14. Acceptance Criteria

- [x] Input flags are OR-composable, configured per model, validated and propagated across all providers; output flags are independent.
- [x] Every provider can deliver PNG/JPEG user and tool-read image input on a verified capable endpoint, preserving ordinary execution and text behavior.
- [x] The agent can discover an image from a description/path and inspect its pixels using `read_file` without a separate image tool.
- [x] Clipboard images appear as removable attachments, support image-only submissions, and survive recoverable failures without corrupting the draft.
- [x] Required images cannot be silently omitted; capacity, unsupported capability, malformed content and missing artifacts produce actionable outcomes.
- [x] Image ordering, identity and provenance survive ordinary continuation, supported resume/clone, context accounting and scoped delegation.
- [x] A generated image can be saved by a real `write_file` tool invocation with exact validated bytes and existing path/overwrite policy. Deterministic output coverage is mandatory; real endpoint support and evidence limits are explicit.
- [x] No new alternate conversation/tool/storage path, unbounded buffering, raw binary logging, or terminal/SDK leakage is introduced.
- [x] Each step's verification gate, Scenario BB, MTP-279, and relevant documentation are satisfied or explicitly recorded as unavailable before completion is claimed.
- [x] No unnecessary duplicated code. Shared, single-implementation functionality is implemented with new shared components, existing as-is, or by extending existing components.

## 15. Risks

Endpoint-specific image roles and output support can differ despite similar API names. Text helpers can silently discard images. Binary payloads can multiply memory through decoding/base64/JSON copies. Retention bugs can leak bytes or break replay. Terminal paste cannot universally access a remote machine's image clipboard. Text sanitizers do not remove visual secrets. Mitigate through endpoint evidence, shared typed admission, bounded streaming, scoped references, platform capability reporting, and adversarial tests rather than broad fallback logic.

## 16. Documentation

During implementation update model configuration/reference examples, discovery and capability behavior, user guide image discovery/paste/save workflows, platform clipboard prerequisites, resource limits, privacy/retention/resume behavior, and output-provider limitations. Update architecture contracts only where their durable boundary changes. Update deployed tool/prompt assets and both `docs/operations/prompts.md` and `docs/prompt-file-reference.md` for any changed filename, purpose, token contract, or call-site token meaning; validate release payload synchronization. Planning navigation, milestone ownership, Scenario BB, and MTP-279 accompany this plan without rewriting completed provider milestones.

## 17. Open Decisions

Resolve in the owning step and record evidence here: S1 exact endpoint-supported image roles, accounting rules and native output support; S2 decoder/package choice, quantitative defaults, reference schema and retention quotas; S8 platform capture mechanism and maintained terminal matrix. These choices must not defer the required Text/Image flags, tool-based image input, clipboard input, or generated-artifact export contracts. If no current endpoint produces images, report that limit while delivering the tested host/export path; do not claim image generation is available or add an unrelated service implicitly.


## 18. Implementation and verification record — 2026-10-16

Implementation branch: `feature/m34-multimodal-file-artifacts`. S1–S9 are implemented through the existing provider, context, conversation, tool, artifact and interaction paths. S10 release closure is verified; unavailable live-provider and platform coverage is explicitly recorded in the evidence matrix below.

### Resolved implementation decisions

- Input/output flags use named `Text, Image` values; omitted fields remain `Text`. Unknown/numeric flags and output Image on the three current endpoints are rejected. Trusted exact-model overrides may establish input support; repository overrides cannot broaden discovered Anthropic support. Legacy Codex catalogs stay readable and receive a bounded capability refresh.
- PNG/JPEG are stored unchanged in the existing content-addressed artifact store. Migration 16 adds descriptor references and conversation image metadata. Grants are scoped to sessions; clone copies archived ownership, child export requires admitted evidence, and retention preserves retained conversation bodies and active session pins. Standalone artifact initialization still works without conversation migrations.
- Bounds are 5 MiB encoded per image, 8,192 per dimension, 16,777,216 pixels, 8 images and 20 MiB encoded per model request, and 512 MiB image storage. `read_file` additionally applies its configured byte ceiling during streaming. PNG checks include chunk CRC, bounded inflation and animation rejection; complete PNG/JPEG decoding uses centrally pinned StbImageSharp 2.30.15. Validation never publishes partially admitted content.
- Image capacity uses a conservative tile/patch allowance, including the documented 4o-mini high-detail schedule before downscaling. Base64 is deferred until transport and is not natural-language tokenized or serialized in ordinary request metadata. Visual secrets cannot be text-redacted; the selected profile must permit sensitive content.
- OpenAI-compatible Chat Completions carries user `image_url` parts. Tool images retain the original correlated tool result followed by an adjacent user evidence message. Codex OAuth Responses carries user `input_image` and image-bearing `function_call_output.output` arrays. Anthropic uses native base64 image blocks, including inside `tool_result`. Existing SDK/terminal/decoder types remain inside their adapters.
- Clipboard capture uses a bounded Windows STA helper, macOS AppKit, or Linux `wl-paste`/`xclip`. Explicit paste freezes one capture; F9 inspects/removes attachments and recovers failed submissions. Ordinary image-only input is supported. Steering and secondary prompts explicitly refuse images and retain text behavior.
- No compiled endpoint currently advertises native image output. Complete generated-byte chunks are a tested host contract, not a claim of provider generation support. Deterministic fixtures exercise image/text/tool ordering, duplicate chunks, successful completion, incomplete failure, subsequent model-issued `write_file`, and exact bytes. Terminal Stop responses remain terminal.

Protocol references consulted: [OpenAI image input guide](https://developers.openai.com/api/docs/guides/images-vision), [Chat Completions reference](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create), [Responses reference](https://developers.openai.com/api/reference/resources/responses/methods/create), and [Anthropic tool-result content](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls). Codex's actual OAuth endpoint was additionally verified by live requests; the public Responses reference alone was not treated as proof of that endpoint's protocol.

### Evidence matrix

| Area | Evidence and limits |
|---|---|
| Shared storage/tools/context | `MultimodalTests` exercises real SQLite migrations, checksums, malformed/truncated PNG, scoped grants/deletion, real session clone ownership, pre-resolution capability/privacy/count/dimension rejection, streaming caller bounds, standalone cleanup, archive/pin/retention, image-only ContextAssembler, UTF-16 compatibility, read/write policy, child export and generated-output ordering/finality. |
| OpenAI-compatible | Synthetic HTTP wire tests prove actual PNG base64 and retained tool-call identity for user/tool paths; existing provider tests cover text behavior. No credentialed endpoint live test was performed. |
| Codex OAuth | Synthetic user/tool wire tests plus live `gpt-6.1-sol`, `ReasoningLevel.Medium`, PNG and JPEG in both user and tool-result positions: all four live cases passed. Each identified the fixture's K7Q2 marker and red/blue/green shapes. |
| Full application | Built application, explicit active repository, GPT 6.1 Sol Medium, ordinary model-issued `read_file` followed by `write_file.artifactId`: correct spatial/color/marker description and byte-identical PNG export. SHA-256 source/export: `BFD036B72EE488842857137F6FCA577678A67209FB6CF09990B84967490C194D`. Fixture 420×320, 3,745 bytes. |
| Anthropic | Synthetic SDK mapping tests prove real PNG bytes for user and nested tool-result parts. No credentialed live request was performed. |
| Composer | Headless real TUIKit/parser tests cover image-only submission, capture validation/timeout preserving text, text-only secondary/steering boundaries and release on close. Native Windows bitmap capture passed with the production 2-second capture default; the test restored the previous clipboard. macOS/Linux native clipboard and remote/SSH environments were unavailable. |
| Native output | No current output-capable endpoint is available. Deterministic provider produces actual PNG and invokes the real tool pipeline to export it, including images before/after tool calls. No native generation live result is claimed. |
| Large-image measurement | One 4096×4096 PNG (100,837 encoded bytes, the maximum pixel count) was admitted in 152 ms with a 67,934,296-byte process allocation delta on this Windows host. This is one diagnostic sample, not a portable timing gate or peak-memory measurement. Decode is serialized; source inspection confirms bounded streaming and no repository preload. A separate large-candidate discovery benchmark and adversarial decoder corpus were not run. |
| Reviews | Three fresh-context scoped adversarial reviews plus a separate fresh holistic review, with follow-up source reviews after fixes. Final source reviews reported no remaining concrete findings. Fixes reused existing paths: archive ownership/retention, child scope and recovery, generated output ordering/finality/visibility, UTF text compatibility, conservative accounting, clipboard recovery/epochs, cache refresh recovery and streamed read limits. Cross-process retention concurrency was not independently stress-tested. |

Live full-app semantic readiness needed the existing configurable 180-second startup allowance on this checkout; the default 30-second attempt stopped before a model request. This is recorded as environment/startup evidence, not a multimodal latency measurement. The successful request's logical estimate was 9,423 tokens and wire admission 22,834 of 239,232 tokens.

### Release closure

StbImageSharp has no native binaries or transitive package dependencies. The exact-package disposition is in `eng/release/stbimagesharp-license-review.json`; exact port public-domain wording and upstream dual MIT/public-domain text are preserved in `eng/release/legal/licenses/StbImageSharp-2.30.15.txt`. The designated release owner explicitly approved this disposition on 2026-10-16 after reviewing the port and upstream license declarations. The disposition is now marked approved and included in `eng/release/release-license-evidence.json`, as required by ADR-49 and `docs/operations/release-packaging.md`.

The release owner subsequently authorized completing the LibGit2Sharp dispositions on 2026-10-16 after review of the MIT wrapper and native GPLv2 linking exception. The canonical register now includes `LibGit2Sharp/0.32.0` and `LibGit2Sharp.NativeBinaries/2.0.324`, with exact restore hashes, source revisions, the complete native license bundle and source/build provenance. The native library is recorded as `GPL-2.0-only WITH GCC-exception-2.0`; its mixed third-party bundle is preserved in full and not relabeled MIT. Approval covers the unmodified library/application combination.

`Test-ReleaseLicenseEvidence.ps1` and the full `Test-ReleaseContracts.ps1` suite now pass, including deterministic notice/SBOM generation for the exact restore closure, runtime legal staging, source documentation, prompt payload and fail-closed release checks. The prior LibGit2Sharp evidence gap is resolved. StbImageSharp's prepared archive SHA-512 was corrected to NuGet's restore content hash from `.nupkg.metadata` and `project.assets.json`, which is the value the existing release gate compares. No publishing was attempted; these checks do not constitute a six-platform installer rehearsal or additional live-provider validation.


### Final automated checks

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`: passed, zero warnings/errors.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4 --no-progress`: 3,959 total; 3,911 passed, zero failed, 48 explicitly skipped for opt-in or unavailable-platform prerequisites. Includes architecture/dependency direction, prompt documentation/catalog bijection and existing subsystem regressions.
- Changed C# files formatted with the repository's whitespace rules; `git diff --check` passed.
- `eng/release/Test-SourceDocumentation.ps1`: passed (113 packaged documentation files). Prompt payload and all release-contract checks passed after the license closure recorded above.
- Separate opt-in runs passed native Windows clipboard capture, maximum-pixel admission, and the four real Codex PNG/JPEG user/tool cases. Native image generation, Anthropic/OpenAI-compatible live services and non-Windows clipboard smoke tests remain explicitly unassessed.

A final review of rejected attachment recovery confirmed that existing incomplete-turn history exclusion prevents an archived failed user-only image turn from reappearing on a text retry. No alternate preflight or history path was added.


### Review correction — pasted-image export metadata

Current and selected historical user-message image parts now share one context assembly helper that includes ordered `attachments` JSON (`artifactId`, `mediaType`, `width`, `height`, `byteLength`) beside the pixels. The model can use those IDs with the existing `write_file.artifactId` branch. No lookup tool, adapter-specific metadata path, authority change or pixel encoding in text was added. The deployed writer description and both prompt references document the metadata source.

Both current-turn and historical-turn export regressions reproduced the missing metadata before the fix and passed afterward. Each test attaches two images, obtains the second ID solely from model-visible JSON, invokes the real writer through the ordinary model/tool loop, and verifies exact output bytes and metadata/image ordering. Synthetic wire tests confirm all three adapters preserve the IDs and pixels. Final solution build: zero warnings/errors. Targeted multimodal, Anthropic mapping, Codex image wire, conversation-context, context-caching and prompt-architecture tests: 346 passed, zero failed, four opt-in/platform skips. Packaged documentation validation and `git diff --check` passed. A fresh-context adversarial review found no actionable issue in this correction. Native macOS/Linux clipboard behavior remains unvalidated; no new live-provider or native clipboard run was needed for this shared metadata correction.


### Clipboard shortcut correction — 2026-10-18

The user specified the final shortcut contract: Ctrl+V retains its original text-paste behavior; Ctrl+Alt+V explicitly pastes an image, independently of terminal-specific paste behavior. Direct Ctrl+V and Shift+Insert use the existing text clipboard reader. Bracketed paste remains text-only, including empty events. Ctrl+Alt+V uses the existing image capture/admission pipeline with the same input ownership, cancellation, session and epoch checks. An image request with no image reports that fact and preserves the draft without inserting clipboard text. F9 offers optional image paste and attachment management.

Both enhanced CSI-u Ctrl+Alt+V and legacy ESC + Ctrl+V encodings are supported. The frontend's existing key normalization maps the legacy encoding to Ctrl+Alt+V. No terminal settings changes, polling, alternate input backend or keyboard hooks were introduced. The earlier claim that Windows Terminal required a binding change was incorrect; its empty bracketed-paste signal is deliberately treated as text under the user's final shortcut contract.

The opt-in native clipboard check injects Ctrl+Alt+V through the real TUIKit parser and uses the production Windows clipboard reader and ArtifactStore. This verifies the input-event-to-capture boundary with a headless backend, not a physical keyboard press in Windows Terminal. Native macOS/Linux clipboard behavior remains unvalidated.

Validation: the full CoreRuntime module passed 700 tests with zero failures and three explicit opt-in skips, including production Windows clipboard capture of a 626×626 PNG (813,403 bytes) through the legacy Ctrl+Alt+V input sequence. Regression coverage includes both image key encodings, text-only Ctrl+V/Shift+Insert, nonempty/whitespace/empty terminal paste, missing-image draft preservation and secondary/steering rejection. A fresh-context adversarial review of the final shortcut contract reported no concrete findings. Packaged source documentation validation (113 files) and `git diff --check` passed.
