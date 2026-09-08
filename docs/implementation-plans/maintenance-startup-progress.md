# Startup progress during repository opening and restore

**Delivery track:** Maintenance
**Status:** Automated verification passed; physical-terminal acceptance remains manual
**Prerequisites:** Shared interaction controller and existing frontend activity surfaces.

The startup coordinator previously awaited the complete repository-open workflow before presenting semantic-loading activity. A slow restore therefore left the TUIKit frame static even though the existing spinner worked once semantic loading began. The observed session opened its repository at 15:02:28, selected its solution at 15:04:45, and completed semantic loading with PartialCompilation at 15:05:34 on September 8, 2026.

The shared controller now presents the existing activity during repository opening and solution selection/restore, outside trust and solution prompts. Semantic loading keeps its existing activity. These startup activities include monotonic elapsed duration; frontend spinner implementations and restore/semantic execution are unchanged.

Verification holds host operations pending and checks that progress is active before completion, then checks release on success, failure, and cancellation. The existing startup integration verifies all three activity phases in order. The manual startup check additionally requires an animated spinner during a delayed restore; automated checks do not claim physical-terminal acceptance.

## Verification results

- Solution build: zero warnings and errors.
- Stalled-startup and existing TUIKit checks: 18 passed.
- Existing activity lifetime checks: 5 passed.
- Repository startup, trust, and selection workflows: 15 passed.
- Architecture gate: 191 passed; one unrelated opt-in live-provider test skipped.
