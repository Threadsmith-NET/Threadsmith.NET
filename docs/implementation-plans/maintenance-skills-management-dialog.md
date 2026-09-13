# Skills management dialog

Status: Complete

Delivery track: Maintenance

Prerequisites: retained Tools CheckTree and action dialogs; shared native/Claude skill commands and synchronized verification state.

## Scope

Open the existing retained management modal for bare `/skills`. Group packages by Claude/Native, then maintained, organization, machine, user, or repository scope. Show verification and enablement independently. Space applies enable/disable to a leaf or filtered group through existing commands; F3 offers verification for a leaf or filtered group. Preserve explicit slash subcommands and the original frontend's sequential selection fallback.

Opening the dialog remains metadata-only. Verification may auto-enable maintained packages under existing policy, while unsigned Claude packages still require explicit external enablement. Pin exact native identities and the activated Claude identity throughout each dialog; stale selection, integrity failure, revocation, and cancellation cannot authorize a replacement or bypass policy. Reflect acknowledged state immediately and preserve completed operations on close. Opt-in nested groups and group actions leave other management dialogs' behavior intact.

The rollback checkpoint preceding this work is `b39a14d`, following `6259c7f` (`feat(skills): implement focused review workflows and verification fixes`). The corrective checkpoint commit pins repository-normalized LF payload bytes so Git checkout preserves review package integrity; all 97 skill tests and a read-only committed-blob hash audit passed. This follow-up does not reopen Plan 108 or change milestone status.

## Verification

Exercise Scenario AW and MTP-269. Automated checks cover command routing, maintained and Claude verification, enable/disable reconciliation, exact selectors, changed identities, original-frontend behavior, empty catalogs, nested/filtered groups, safe group identities, action opt-in, native keyboard cancellation, and existing Tools/MCP behavior. Run the solution build, core-runtime, skills, and architecture suites, packaged documentation validation, and an adversarial review. Record completed checks here before closeout; distinguish headless terminal tests from physical terminal rehearsal.


## Closeout

Implemented on `feature/plan-108-focused-review-skills` after rollback checkpoint `b39a14d`. Skills uses the existing Tools modal with explicit nested format/scope groups and opt-in filtered group verification; native/Claude verification and enablement still dispatch through the existing application boundary. Modal opening does not refresh or activate packages. Explicit commands and the original frontend's sequential choices remain available.

Adversarial review found cached rows surviving mutable status filters and a queued Enter/Esc action starting after the parent dialog closed. Both were reproduced by real terminal-backend regressions and corrected. Filter membership changes now rebuild cached rows while preserving navigation, and action admission checks parent closure while installing cancellation in the same UI turn. Re-review returned clean.

- Full solution build: zero warnings and zero errors.
- Core runtime/terminal suite: 589 passed, including twelve new Skills command/tree cases and existing Tools/MCP/dialog checks.
- Skill suite: 98 passed, including maintained auto-enablement, Claude/native workflows, focused recovery, Git-normalized payload integrity, and a 32-package Claude group with persisted enable/disable outcomes.
- Architecture suite: 265 passed, one opt-in live-provider test skipped.
- Local Debug publish, focused public/private payload validation, packaged documentation validation (108 files), and published application version smoke check passed.
- `git diff --check` passed. Modal work remains uncommitted after the requested rollback checkpoint.

No live model calls were needed for this UI change. The terminal tests use the actual retained frontend with its headless backend; physical Windows/Unix terminal rehearsal remains the operator procedure in MTP-269 and is not claimed here.

## Group toggle follow-up

Operator feedback exposed a mixed-group retry loop: using “not all enabled” as the target state caused every Space to request enable again when even one package stayed disabled. Space now disables a group when any visible eligible member is enabled, and enables it when none are enabled. Host checks remain authoritative; unsupported or rejected packages are reported in completion counts. Batch progress is visible while work runs. Esc stops after the active item finishes, preserves its acknowledged state, skips remaining members, and leaves the tree open.

Two actual terminal-backend regressions cover a 32-member Claude group with one rejection and stopping a group after an in-flight change. A real catalog/verifier/policy-store regression checks 31 supported Claude packages plus one unsupported package, including persisted state after reloading policy. Adversarial review identified the risk of interrupting a policy write before catalog publication; the stop-after-current-item boundary prevents that interruption. The subsequent review was clean. Final validation totals above include these regressions; no live model calls or physical terminal rehearsal were needed or claimed.

## Shared skill execution follow-up

All manual, model-driven, and internal skills use checkpoint events in the existing tool/MCP activity collection and renderer. A manual invocation shows one live `SKILLS` block with invocation identity, bounded phase progress, and optional elapsed time. A model-driven invocation adds progress to its existing `invoke_skill` block. Completion replaces the live activity once; genuine host-input waits release it. Child runs use the same activity projection in their agent tabs. The original frontend uses the existing transient activity and prompt-preemption path. No command wrapper or separate activity store owns skill rendering.

Focused review preparation invokes the registered `git_show`, `git_diff`, `git_compare_branches`, and `read_file` tools through the central invocation pipeline. Each request pins its current registration and retains current availability, trust, path, executable, and sanitization policy. Working-tree snapshots page exact UTF-8 content with whole-file digest checks; immutable Git files use bounded batches and explicit per-file omissions. Tree metadata streams into bounded pages. Frozen reviewer reads remain the existing scoped tool and now share bounded line selection with `read_file`. Ordinary skill procedures also pin advertised registrations and use the tool's model-result projection.

Remote acquisition uses the existing host process manager because no registered tool supports remote Git acquisition. It fetches only source/base tips with `--depth=1 --no-tags --no-recurse-submodules`, with visible phase events. Comparison uses the two tips without history or merge-base discovery; local change review retains merge-base semantics and requires an explicit base for detached HEAD. New snapshots persist `ComparisonRevision`, while legacy `MergeBase` snapshots remain readable. Reviewer context records the actual comparison and scope kind. Reviewer tabs appear when the existing delegation scheduler admits the prepared assignments.

Exercise MTP-270. Automated regressions cover common lifecycle ownership, model/tool correlation, cancellation, terminal generation fencing, tool registration replacement, model projections, large escaped blobs, large dirty inventories, UTF-8 source/requirements paging, and comparison metadata.

Validation completed:

- Full solution build: zero warnings/errors.
- Core runtime: 605 passed; Skills: 106 passed; Model/tooling: 702 passed with eight opt-in skips; Native tools: 177 passed; Architecture: 265 passed with one opt-in skip; Parallel agents: 255 passed. Total: 2,110 passed, nine skipped. The large Claude-group persistence test initially encountered a Windows file-replacement access denial under concurrent suite load; both its isolated run and the subsequent complete Skills run passed.
- Adversarial reviews found and corrected internal-host lifecycle suspension, registration/projection gaps, serialized batch bounds, streamed inventory/status limits, UTF-8 snapshot paging, detached-HEAD base inference, and remote comparison metadata. Final reviews, including changed-file capture scope, returned no actionable findings.
- Authorized live Bitbucket PR 729 acquisition/preparation completed in 65.7 seconds: 45.9 seconds through fresh fetch, exactly two shallow commits, 49 distinct source/base blobs, and 62 ordinary tool invocations. Git reports 33 changed paths; capture retained 30 changed files plus three instruction/context files and reported three source exclusions. Transfer remained approximately 737 MB because shallow tips still contain large repository objects. This check exercised acquisition and preparation, not model review or physical terminal rendering.
- Local Debug publish, public/private review payload validation, packaged documentation validation (108 files), application version smoke check, and `git diff --check` passed.

Changes remain uncommitted after rollback checkpoint `b39a14d`.

Remote branch comparisons capture changed files plus applicable `AGENTS.md` and explicit requirements documents. Unchanged repository bodies are not preloaded. Remote reviews without a base retain snapshot scope. Fetches contain only the two shallow tips; source contents at those tips still contribute to transfer size.


## Final pipeline review

The final pass traced tool/MCP events, manual/model/internal skill checkpoints, main and child activity projections, retained and legacy rendering, and native skill model requests. No additional rendering-state or child-ownership defect was supported by an actual producer path. Separate transcript and live-activity state retains its existing ownership; no speculative refactor was introduced.

Regression tests reproduced valid JSON becoming malformed when MCP, native skill, and focused reviewer outputs contained credential-shaped free text. MCP now leaves structured sanitization to the central tool pipeline. Native skills and focused reviewer answers reuse `JsonOutputSanitizer.SanitizeJsonOrText`; ordinary child prose retains its existing behavior. Both MCP response shapes, the native answer, and all four focused reviewer roles failed before these fixes and pass afterward.

Native skill requests also contained the same tool result in both the canonical Tool message and an extra User continuation. Structured messages now deliver it once through the Tool message; the legacy input projection retains its result and continuation guidance. The initial procedure prompt still supplies the output-schema instruction. Tests cover successful and denied tool results, matching call identity, one result-bearing message, and the legacy projection. An existing cancellation fixture now waits until its handler reaches the intended cancellable work before testing cleanup; runtime cancellation behavior was unchanged.

Adversarial re-review of the runtime changes and updated regressions returned no actionable findings. Reviews explicitly examined existing paths and reuse, and did not promote unsupported ordering scenarios into changes.


Final-pass validation: solution build passed with zero warnings/errors; Core runtime 605 passed, Skills 106 passed with one failure, Model/tooling 702 passed with eight skips, Parallel agents 256 passed, MCP transports 35 passed with one skip, MCP lifecycle 47 passed, and Architecture 265 passed with one skip. Total: 2,016 passed, ten skipped, one failed. The remaining large-Claude-group persistence test hit `File.Move(overwrite: true)` access denial both alongside other suites and in a standalone Skills run. A separate plain .NET write/replace loop, containing no Threadsmith code, reproduced five and nine failures per 256 attempts in unique scratch directories; recorded HRESULT was `0x80070005` with ordinary Archive attributes on both files. The cause of that filesystem behavior is unresolved. No speculative retry or persistence change was introduced, and the suite is not claimed fully green.

Local Debug publish, focused public/private payload validation, packaged documentation validation (108 files), application version smoke check (`0.1.0`), and `git diff --check` passed. No further live model calls or physical terminal checks were performed for this pass. Changes remain uncommitted after checkpoint `b39a14d`.
