Required JSON shape:
{
  "summary": "bounded synthesis",
  "findings": [
    {
      "category": "behavior|risk|test|architecture|other",
      "summary": "cited finding",
      "evidenceIds": ["exact evidence GUID"],
      "locations": ["repository/relative/path"],
      "symbols": ["optional stable symbol"],
      "confidence": 0.0,
      "uncertainty": null,
      "risk": null,
      "recommendation": null
    }
  ],
  "unresolvedQuestions": ["one self-contained unresolved-question string"],
  "coverageNotes": ["one self-contained coverage-note string"]
}
Every finding requires at least one exact evidenceId shown in supplied evidence or a tool result.
Use locations only for repository-relative paths inside the assigned scope. Empty findings are
allowed when the summary and coverage notes honestly explain that evidence was insufficient.
Coverage notes must identify which requested claims were covered and any deliberate scope omissions.
unresolvedQuestions and coverageNotes are arrays of strings, not arrays of objects. Each unresolved-
question string must identify the attempted evidence collection and explain why further available
evidence collection cannot resolve it.