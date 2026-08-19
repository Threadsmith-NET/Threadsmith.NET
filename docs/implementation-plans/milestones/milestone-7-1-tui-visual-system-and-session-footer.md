## Milestone 7.1 — TUI Visual System and Session Footer  *(plans 24, 25, 26)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Give the conversation-first terminal a coherent, configurable visual language and a compact live session-status surface while preserving native terminal behavior.

**Deliverables:**
- Terminal-native default foreground and background instead of the hard-coded amber accent.
- A stable semantic text-role catalog with independently optional foreground, background, and decorations.
- Safe TUI theme contracts and deterministic layered configuration through `tui:themes[]`.
- Built-in `system`, `forge-dark`, `ocean`, and `high-contrast` themes.
- Host-owned `/theme` selection using the existing numbered Up/Down/Enter interaction.
- A measured footer feasibility spike with a pinned-row compatibility gate and composer-adjacent fallback.
- Responsive session status for current folder, repository, model, reasoning level, context usage, and cumulative session tokens.
- Provider-neutral, idempotent usage projection where the existing budget state is insufficient for the footer.
- Terminal-neutral automated coverage and real-terminal selection, clipboard, paste, resize, and streaming gates.

**Exit criteria:**
- With no theme configuration, Threadsmith inherits the terminal's foreground/background and does not impose amber text.
- Hyperlinks, tool success/failure, selection prompts/items/highlights, and other documented roles resolve through the active theme with safe fallback.
- At least four built-in themes and configured theme arrays are available through `/theme`; unknown or unsafe theme data fails locally and cannot inject terminal control sequences.
- The session status surface displays the required folder/repository/model/reasoning/context/token fields and updates from host-owned state.
- Context and token values have precise denominators/scopes and visibly distinguish estimated or unknown data.
- A permanently pinned row ships only if native mouse/keyboard selection, `Ctrl+C`, exact 10 KB/100 KB paste, streaming, selectors, resize, cancellation, and native scrollback all pass; otherwise the documented composer-adjacent footer ships.
- Headless and redirected output remain free of footer control sequences, and terminal-library/theme types do not leak across Core or extension boundaries.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
