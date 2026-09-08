# C# script conversation availability

**Delivery track:** Maintenance
**Status:** Automated checks passed; physical-terminal/model verification remains manual
**Prerequisites:** Existing isolated scripting worker, availability/policy pipeline, and repository trust lifecycle.

An enabled C# Script tool at FullyTrustedAutomation was withheld from ordinary model requests because its ExecutesCode definition lacked conversation availability. The shared trust selector and parser also omitted the required trust level. After those paths were enabled, a direct application publish exposed a separate packaging failure: the project reference copied the worker apphost but omitted `Threadsmith.Scripting.Worker.deps.json`, so the worker exited before `Main` when launched from the installed application directory.

Mark the existing script definition conversation-available, expose explicit automation trust through the shared interaction layer, and copy the worker dependency manifest into direct application publishes. Preserve default-disabled availability, executable policy, trust, allow/deny, approval, and worker restrictions.

Verification covers actual worker output and tool-result continuation through the conversation pipeline, disabled and policy-blocked advertisement, both trust command aliases, selector cancellation/invalid input, direct-publish manifest presence, and the architecture/build gates. Set `THREADSMITH_SCRIPT_INTEGRATION=1` and run the `CSharpScript` tests in `Threadsmith.ModelTooling.Tests` for the opt-in worker/source-asset checks. MTP-030G owns the physical-terminal/model workflow.

No prompt assets, worker permissions, script file access, or automatic trust/enablement are changed.

## Verification results

- Solution build: zero warnings and errors.
- C# scripting: 10 passed, including all seven real-conversation integration cases.
- Repository conversational trust flows: 9 passed.
- Trust/help: 4 passed.
- Direct self-contained application publish: worker launched from the publish directory under the restricted process environment and returned `Success=true`, `Output=42`, `Error=null`, and `IsTruncated=false`.
- Architecture gate: 192 passed; one unrelated opt-in live-provider test skipped.
