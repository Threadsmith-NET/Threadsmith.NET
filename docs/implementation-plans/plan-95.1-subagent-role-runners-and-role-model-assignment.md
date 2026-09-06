# Implementation Plan 95.1: Subagent Roles and Role-Based Model Assignment

**Status:** Active - prompt-driven roles are implemented and tested; operational-limit configuration and the remaining live utility issues below are still open.
**Delivery track:** Product capability - all defined subagent roles and trusted per-role model selection.
**Prerequisites:** Existing delegation, model catalogs, tool policy, checkpoints, and approved mutation workflows from Plans 38, 80, 90, 91, 94, and the relevant foundations in Plan 95.
**Split from:** [Plan 95](plan-95-subagent-efficiency-code-explore-role-model-routing.md).

## 1 Objective

A role is a system-prompt amendment that steers a subagent's activity, together with its model selection and available tools. Give the model its task and let it respond in whatever form it chooses.

Support Explorer, Implementer, SecurityReviewer, TestReviewer, PerformanceReviewer, and ArchitectureReviewer. Let trusted configuration assign an existing provider, profile, and reasoning level to each role. Keep identity, permissions, cancellation, and usage visible without making the model fill out a report.

This clarified design replaces the earlier proposal for mandatory role-specific JSON, citation GUIDs, required report fields, and answer-format correction loops. Those are not requirements for ordinary subagents.

## 2 Architectural Context

The existing `delegate_agents` tool creates one layer of in-process child runs from the parent's available tool registrations. The scheduler owns lifecycle and cancellation. Model selection uses the existing provider catalog. Tool calls pass through normal permission and path checks.

The approved edit workflow is separate. A child preparing executable mutations must still use the existing mutation protocol, followed by parent approval, staging, transactional application, and validation. That protocol must not constrain ordinary responses.

## 3 Scope

| Role | Prompt amendment steers toward |
|---|---|
| `explorer` | Understanding relevant code, behavior, files, and relationships. |
| `implementer` | Working out a requested implementation or change. |
| `securityReviewer` | Security risks and useful fixes in the assigned work. |
| `testReviewer` | Missing or weak tests and useful assertions. |
| `performanceReviewer` | Unnecessary work, allocation, latency, concurrency, and measurement needs. |
| `architectureReviewer` | Dependencies and consistency with documented architecture. |

All ordinary roles share the child loop. Available tools come from the permitted parent surface and the assignment's policy. A role never adds permissions the parent lacks. Ordinary Implementer assignments are read-only; approved implementation retains separate mutation authority.

## 4 Non-Scope

- Mandatory answer schemas, fixed reviewer fields, citation formats, or content grading inside the runtime.
- Rewriting a child's answer until it fits a template.
- Automatically verifying the truth of model-authored claims.
- Recursive delegation, automatic merges, or publishing reviews.
- A new provider, credential source, configuration editor, or automatic child-loop resume API.

## 5 Current State

Work is on `plan-95.1-subagent-roles`. Model routing, all six role names, approved child preparation, persistence metadata, and the shared tool loop have been added. The first implementation imposed structured role outputs. The user explicitly replaced that approach with prompt-driven roles and unrestricted responses.

The current change removes role-output parsing from ordinary execution, deletes the unused new role schemas and parsers, adds the final response to the outcome, and separates role amendments from common system policy. Existing structured checkpoint data remains readable through its Core records for compatibility and approved workflows.

## 6 Proposed Design

### Instructions

Send common child system instructions followed by the selected role amendment. Supply the task, relevant repository instructions, evidence, and tools. Role wording gives direction, not a checklist the answer must satisfy.

Do not send an output schema, require JSON mode, require finding objects, or demand special citation identifiers. Tool-call arguments keep their normal schemas because they invoke actual operations. Invalid tool calls may receive technical feedback; ordinary answers do not receive format-repair feedback.

### Models

Read `agents:roleModels` only from trusted machine/user configuration. Each entry identifies an existing `providerId`, `profileId`, and optional `reasoningLevel`. Reject invalid roles, missing profiles, provider/profile mismatches, and unsupported reasoning before dispatch.

Precedence is an application assignment pin, role preference, inherited preference, then a compatible default. Capture the selection at acceptance. Check each actual request before provider I/O. Fallback preserves the complete context and rebuilds with the receiving model's instructions and output reserve; record the reason.

Ordinary responses require streaming and, when offered, tool-call support. They do not require structured-output capability. Requests explicitly using the approved mutation protocol retain their own capability requirements.

### Tools

Freeze the permitted registrations and model choice before execution. Role instructions cannot create tools or widen permissions. `readOnly` uses eligible local inspection tools; `inherit` can additionally retain eligible network-backed reads. Neither ordinary mode exposes mutation, processes, approval, workflow, or recursive delegation.

### Responses

Treat the final response as opaque model-authored text. Preserve its content and format after existing secret sanitization. Do not interpret JSON-looking text as a special result, invent findings from prose, or require particular fields, citations, or issue counts.

Store the response alongside separately collected role, identity, model selection, status, usage, and tool-evidence metadata. Return it through the parent tool result after the durable join. A final answer is not a transcript or hidden reasoning. The parent decides how useful it is.

Transport errors, cancellation, unauthorized tools, invalid assignment identity, and failed joins still affect run status. Answer style or missing report fields do not.

### Operational Controls

Keep operational limits separate from answer-format rules. Audit delegation, provider, shared-budget, tool, and projection limits. The requested end state is configurable limits with explicit disable settings, conservative defaults, and cancellation that always remains available. Actual model capacity, permissions, and path restrictions are not optional operational caps.

Broader opt-in unlimited-resource changes were blocked by safety review. The user was informed about memory exhaustion, provider load, and cost risks and asked for approval. Do not claim all disable settings work until their consumers and tests are complete.

## 7 Public Contracts

- `agents[].role` accepts the six exact public names; omission retains Explorer.
- `agents:roleModels` selects trusted existing bindings. Obsolete `agents:roleProfiles` fails with migration guidance.
- `AgentRunOutcome.Response` is optional for old stored data and holds the ordinary response for new runs, including an empty response if the provider completes that way.
- `agent-response/1` identifies the host envelope, not a schema the model must follow.
- Existing typed findings, reviews, handoffs, and change sets remain compatible with stored records and separate application workflows.
- `/agents <id>` shows role, status, effective model, reasoning, selection source, fallback, and usage.

## 8 Project Changes

- Core: role names, response metadata, model provenance, compatible checkpoint fields.
- Execution: shared loop, prompt amendments, tool selection, response joins, approved preparation.
- Models: trusted role preferences, request compatibility, matching provider dispatch.
- App: trusted configuration and composition.
- Context: relevant evidence/instructions without parent or sibling transcripts.
- Tests: unrestricted responses, routing, permissions, lifecycle, compatibility, opt-in live runs.

## 9 Ordered Tasks

1. Preserve all six role names and the omitted-role default.
2. Compose common system instructions and one role amendment.
3. Apply trusted model preferences and permitted tool selection.
4. Remove role-output schemas and answer-format repair from ordinary execution.
5. Store and join the response without interpreting its contents.
6. Preserve cancellation, failed joins, identity, and successful sibling responses.
7. Keep executable mutation preparation on its separate approved validation path.
8. Update tests without weakening permission, capacity, or lifecycle checks.
9. Run every role against real models and inspect usefulness and efficiency.
10. Finish approved operational-limit changes and downstream tests.
11. Update documentation and run the final build and regression suites.

## 10 Testing

Accept prose, Markdown, JSON-looking text, questions, empty answers, and other ordinary responses without format corrections. Verify hidden reasoning is excluded, secrets remain sanitized, and answer text cannot authorize an action or automatically become a verified finding.

Retain exact tool-registration, denied-tool/path, cancellation, tool-error, usage, model precedence, trusted dispatch, fallback, legacy persistence, durable join, sibling failure, and approved mutation coverage.

The opt-in live suite uses synthetic repositories, real file tools, and a trusted configured provider. Explorer gets a cross-file trace; Implementer gets cancellation changes; reviewers get issue cases and clean controls. Record responses, inspected evidence, requests, calls, repetition, corrections, tokens, and time. Assess usefulness manually rather than making runtime behavior depend on keywords or a template.

## 11 Security And Permissions

Repository content and model output cannot change model routing, tools, credentials, approval, or path authority. Ordinary roles stay read-only. Only approved workflows can prepare executable changes, and the parent retains application authority.

Resolve secrets only at the provider boundary. Do not store credentials, provider payloads, raw transcripts, or hidden reasoning in parent results. Natural responses are advisory output, not proof that files changed or tests ran.

## 12 Observability

Retain identity, role, configured/effective model and reasoning, fallback, status, usage, cancellation reason, and response. Keep operational errors separate from model conclusions. Do not label an answer correct merely because execution completed.

## 13 Compatibility

Missing roles still mean Explorer. Old typed outcomes remain inspectable. New ordinary outcomes use the response field without reinterpreting old schemas. Interrupted child loops are not automatically resumed; new work receives a new attempt/generation. Approved-plan recovery keeps its existing lifecycle.

## 14 Acceptance Criteria

- Every role runs as a system-prompt amendment with the selected model and permitted tools.
- Any response format is accepted without a role-output parser or answer-format repair.
- Routing, permissions, cancellation, identity, and durable joins still work.
- Ordinary responses are not automatically promoted to verified findings.
- Approved mutations retain validation, approval, transactions, and recovery.
- All six roles have live results reviewed for usefulness and efficiency; failures are reported honestly.
- Operational-limit controls are tested through actual consumers before claiming that requirement complete.
- Affected tests, solution build, documentation checks, and independent review pass.

## 15 Verification Record

An earlier automated pass, before this clarification and the additional live requirements, passed 185 ParallelAgents, 382 ModelTooling, 33 ContextCaching, 23 ExecutionOrchestration, 59 Mutations, 291 CoreRuntime, and 183 Architecture tests. Four platform-dependent tests were skipped. That solution build had zero warnings/errors. These historical results do not establish completion of current changes.

The first remote attempt failed because `promaxgb10-f350` could not resolve. Local Qwen2.5 Coder 7B returned tool calls as text, including in a minimal request outside Threadsmith, so its twelve ordinary responses were not useful inspection results. Ordinary roles no longer request JSON; the OpenAI-compatible adapter also avoids JSON-only mode while native tools are advertised.

On September 6, 2026, twelve synthetic cases ran through the existing authenticated native Codex provider with GPT-5.6-Sol at Low reasoning, selected through trusted role configuration. No user provider configuration changed. The first successful native set inspected the intended sources and returned useful answers for all six roles, including clean reviewer controls. It took 69 model requests, 77 attempted tool calls, and 262.3 seconds summed across sequential cases. These are small fixture observations, not production throughput claims.

The next set, after retaining rejected tool-call history, used 37 requests, 42 attempted calls, and 171.6 seconds. One TestReviewer control stopped without inspecting files: a single bad directory-listing argument caused the whole batch to be rejected, but the error did not identify the bad call. Two Implementer code examples also exposed existing secret-sanitizer false positives for `token =>` and C# fences. The mechanics-only test passed; manual evaluation did not treat those issues as a success. Follow-up fixes identify the failed batch member, preserve cancelled terminal status, and retain harmless source syntax without bypassing credential redaction.

The final source build completed with zero warnings and errors. The affected regression run passed 1,410 tests: ParallelAgents 191, CoreRuntime 317, ModelTooling 401, CodexProvider 25, ExecutionOrchestration 23, Mutations 59, ContextCaching 33, Architecture 183, Planning 98, SecretResolution 25, and PersistenceMcpHardening 55. Four platform-dependent tests were skipped. The default Architecture run also skipped the opt-in live test, which was then executed separately. Focused tests cover arbitrary and empty responses, unchanged parent response bodies beyond the old summary limit, role availability without an Explorer-compatible model, rejected-tool history, child-only terminal cancellation, and source-syntax redaction regressions. Sanitizer allocation measurements remained unchanged.

The last twelve-case native run used 37 requests, 44 attempted tool calls, and 141.9 seconds summed across cases. Manual review found useful results for both Explorer variants, both Implementer variants, both SecurityReviewer cases, the TestReviewer issue case, both PerformanceReviewer cases, and both ArchitectureReviewer cases. The clean TestReviewer control again stopped after one invalid `list_files` call prevented its sibling reads from executing. It honestly reported that it had not inspected the files, but did not complete the requested review. This is **11 useful results and one inconclusive result**, not an all-case utility pass. An Implementer reply also retained a cosmetic sanitizer false positive: the ordered-list marker after prose ending in `token:` became `[REDACTED]`. Code fences and lambda syntax passed the new regression tests and survived the observed code examples.

The lower request counts must not be presented as an equal-quality benchmark win because the last run left one review undone. The runtime does not force another answer or reject a response on that basis. Reports are retained locally under `C:\Users\mwrig\Documents\Codex\2026-09-05\loo\live-subagent-reports`, with `run-5-native`, `run-6-history`, and `run-7-final` containing the relevant comparisons. No files have been staged, committed, or pushed.

A later three-role manual audit produced useful Explorer, SecurityReviewer, and ArchitectureReviewer answers, but required 43 model requests and 3,229,050 provider-reported input tokens, including 1,990,144 cache-read tokens. Growing tool histories dominated the two reviewer runs. The audit also showed that JSON tool output was serialized as a JSON string inside the child continuation envelope. The continuation now embeds valid JSON directly while preserving non-JSON text as a string and retaining the original sanitized evidence content. The focused ParallelAgents suite passes 192 tests, including structured and plain-text continuation coverage. Retrieval breadth and repeated growing-history cost remain separate efficiency work.

## 16 Documentation

Update the guide, delegation/model operations, architecture, prompt catalogs, configuration example, acceptance/manual procedures, and relevant DOX. Remove ordinary-role requirements for JSON, citation GUIDs, report fields, or structured finding admission. Do not copy work-item status into milestone documents or navigation indexes.

## 17 Open Items

- Improve recovery from invalid directory-listing arguments without imposing answer requirements. A rejected read-only batch can still lead the model to stop before inspecting otherwise available files.
- Correct the remaining secret-sanitizer false positive for a Markdown ordered-list marker following prose ending in `token:`, while preserving actual credential redaction.
- Resolve the explicit approval request for disableable operational limits, then finish and verify approved changes. Outstanding consumers include the advertised delegation input schema, child output/tool-argument/batch caps, result projection and rendering, shared tool budgets and file-tool limits, and remaining provider retry/transport settings. Option-binding tests alone do not verify those consumers.
