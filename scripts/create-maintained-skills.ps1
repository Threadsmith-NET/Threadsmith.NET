$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\src\Threadsmith.Skills\MaintainedSkills'

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [System.IO.File]::WriteAllText($Path, ($Content.Trim() + "`n"), [System.Text.UTF8Encoding]::new($false))
}

function New-MaintainedSkill(
    [string]$Folder,
    [string]$Id,
    [string]$DisplayName,
    [string]$Description,
    [string[]]$Tags,
    [string[]]$RequiredTools,
    [string]$MinimumTrust,
    [string[]]$Workloads,
    [hashtable[]]$Steps,
    [hashtable]$Files) {
    $package = Join-Path $root $Folder
    Remove-Item $package -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    foreach ($entry in $Files.GetEnumerator()) {
        Write-Utf8NoBom (Join-Path $package $entry.Key) $entry.Value
    }

    $assets = foreach ($relative in ($Files.Keys | Sort-Object)) {
        $path = Join-Path $package $relative
        $kind = if ($relative -like '*input*.json') { 'input-schema' }
            elseif ($relative -like '*output*.json') { 'output-schema' }
            elseif ($relative -like '*.md') { 'instructions' }
            else { 'reference' }
        [ordered]@{
            path = ($relative -replace '\\', '/')
            bytes = (Get-Item $path).Length
            sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
            required = $true
            kind = $kind
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        skillId = [ordered]@{ value = $Id }
        packageId = "threadsmith.$Id"
        version = '1.0.0'
        displayName = $DisplayName
        description = $Description
        tags = $Tags
        publisher = 'Threadsmith.NET'
        license = 'MIT'
        assets = @($assets)
        requirements = [ordered]@{
            requiredTools = $RequiredTools
            optionalTools = @(@('git_status') | Where-Object { $_ -notin $RequiredTools })
            toolContractVersions = [ordered]@{}
            minimumTrust = $MinimumTrust
            approvalCategories = @('read-only discovery', 'governed plan proposal')
            model = [ordered]@{
                workloads = $Workloads
                requiresToolCalls = ($RequiredTools.Count -gt 0)
                requiresStructuredOutput = $true
                minimumContextWindow = 8192
                allowedProfiles = @()
                deniedProfiles = @()
            }
            minimumHostVersion = '1.0.0'
            maximumHostVersion = '1.999.999'
        }
        budget = [ordered]@{
            contentTokens = 8000
            workflowSteps = 8
            modelTurns = 6
            toolCalls = 24
            mutations = 16
            validationAttempts = 4
            delegatedChildren = 8
            parallelChildren = 4
            worktrees = 2
            reviewerFindings = 128
            wallTime = '00:20:00'
        }
        agents = @()
        workflow = [ordered]@{
            schemaVersion = 1
            workflowId = $Id
            steps = $Steps
        }
        signature = $null
    }
    Write-Utf8NoBom (Join-Path $package 'skill.json') ($manifest | ConvertTo-Json -Depth 20)
}

$planOutput = @'
{
  "type": "object",
  "additionalProperties": false,
  "required": ["schemaVersion", "plan"],
  "properties": {
    "schemaVersion": { "type": "integer", "minimum": 1, "maximum": 1 },
    "plan": {
      "type": "object",
      "additionalProperties": false,
      "required": ["schemaVersion", "revision", "summary", "steps", "risks", "outstandingQuestions"],
      "properties": {
        "schemaVersion": { "type": "integer", "minimum": 2, "maximum": 2 },
        "revision": { "type": "integer", "minimum": 1 },
        "summary": { "type": "string", "minLength": 1, "maxLength": 4000 },
        "steps": {
          "type": "array",
          "minItems": 1,
          "maxItems": 32,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["stepId", "title", "description", "fileIntents", "expectedOutcome", "validation"],
            "properties": {
              "stepId": {
                "type": "object",
                "additionalProperties": false,
                "required": ["value"],
                "properties": { "value": { "type": "string", "minLength": 36, "maxLength": 36 } }
              },
              "title": { "type": "string", "minLength": 1, "maxLength": 160 },
              "description": { "type": "string", "minLength": 1, "maxLength": 1000 },
              "fileIntents": { "type": "array", "maxItems": 64, "items": { "type": "object", "additionalProperties": false, "required": ["kind", "path"], "properties": { "kind": { "type": "string", "enum": ["Modify", "Create", "Delete", "Move", "Rename"] }, "path": { "type": "string", "maxLength": 512 }, "destinationPath": { "type": "string", "maxLength": 512 } } } },
              "expectedOutcome": { "type": "string", "minLength": 1, "maxLength": 1000 },
              "validation": { "type": "array", "maxItems": 32, "items": { "type": "string", "maxLength": 512 } }
            }
          }
        },
        "risks": { "type": "array", "maxItems": 64, "items": { "type": "string", "maxLength": 1000 } },
        "outstandingQuestions": { "type": "array", "maxItems": 64, "items": { "type": "string", "maxLength": 1000 } }
      }
    }
  }
}
'@
$hostResult = @'
{
  "type": "object",
  "additionalProperties": false,
  "required": ["accepted", "planId"],
  "properties": {
    "accepted": { "type": "boolean" },
    "planId": { "type": "string", "maxLength": 128 }
  }
}
'@

New-MaintainedSkill -Folder 'fix-analyzer-warnings' -Id 'fix-analyzer-warnings' -DisplayName 'Analyzer Fix' `
    -Description 'Investigate bounded analyzer diagnostics and propose a governed, testable fix plan.' `
    -Tags @('analyzers', 'csharp', 'planning') -RequiredTools @('read_file', 'search', 'find_symbol', 'find_references') `
    -MinimumTrust 'TrustedRead' -Workloads @('Planning', 'CodeEdit') `
    -Steps @(
        [ordered]@{ stepId='analyze'; kind='invokeProcedure'; dependsOn=@(); instructionAsset='instructions/analyze.md'; inputSchemaAsset='schemas/input.json'; outputSchemaAsset='schemas/plan-output.json'; maximumIterations=1; hostAction=$null },
        [ordered]@{ stepId='propose-plan'; kind='proposePlan'; dependsOn=@('analyze'); instructionAsset=$null; inputSchemaAsset=$null; outputSchemaAsset='schemas/host-result.json'; maximumIterations=1; hostAction='proposePlan' }
    ) -Files @{
        'instructions/analyze.md' = @'
Inspect only the supplied analyzer diagnostics and authorized repository scope. Confirm each diagnostic against current source and project configuration. Prefer existing patterns and the smallest coherent fix. Do not mutate files. Return exact schema-versioned propose_plan tool arguments with affected paths and meaningful build/test validation.
'@
        'schemas/input.json' = @'
{
  "type": "object",
  "additionalProperties": false,
  "required": ["diagnostics"],
  "properties": {
    "diagnostics": { "type": "array", "minItems": 1, "maxItems": 256, "items": { "type": "string", "maxLength": 2000 } },
    "scope": { "type": "array", "maxItems": 128, "items": { "type": "string", "maxLength": 512 } }
  }
}
'@
        'schemas/plan-output.json' = $planOutput
        'schemas/host-result.json' = $hostResult
    }

New-MaintainedSkill -Folder 'upgrade-package' -Id 'upgrade-package' -DisplayName 'Package Upgrade' `
    -Description 'Assess one bounded package upgrade and propose a governed compatibility and validation plan.' `
    -Tags @('packages', 'dependencies', 'planning') -RequiredTools @('read_file', 'search', 'list_files') `
    -MinimumTrust 'TrustedRead' -Workloads @('Planning', 'CodeEdit') `
    -Steps @(
        [ordered]@{ stepId='assess'; kind='invokeProcedure'; dependsOn=@(); instructionAsset='instructions/assess.md'; inputSchemaAsset='schemas/input.json'; outputSchemaAsset='schemas/plan-output.json'; maximumIterations=1; hostAction=$null },
        [ordered]@{ stepId='propose-plan'; kind='proposePlan'; dependsOn=@('assess'); instructionAsset=$null; inputSchemaAsset=$null; outputSchemaAsset='schemas/host-result.json'; maximumIterations=1; hostAction='proposePlan' }
    ) -Files @{
        'instructions/assess.md' = @'
Inspect central package management, all direct uses, relevant release constraints supplied by the user, and existing validation patterns. Do not edit project files or run package-manager mutation commands. Return exact schema-versioned propose_plan tool arguments for a phased upgrade including compatibility risks, affected projects, rollback, build, and focused test validation.
'@
        'schemas/input.json' = @'
{
  "type": "object",
  "additionalProperties": false,
  "required": ["packageId", "targetVersion"],
  "properties": {
    "packageId": { "type": "string", "minLength": 1, "maxLength": 256 },
    "targetVersion": { "type": "string", "minLength": 1, "maxLength": 128 },
    "constraints": { "type": "array", "maxItems": 64, "items": { "type": "string", "maxLength": 1000 } }
  }
}
'@
        'schemas/plan-output.json' = $planOutput
        'schemas/host-result.json' = $hostResult
    }

New-MaintainedSkill -Folder 'review-pr' -Id 'review-pr' -DisplayName 'Pull Request Review' `
    -Description 'Perform a bounded evidence-backed security, test, performance, and architecture review.' `
    -Tags @('review', 'security', 'testing', 'architecture') -RequiredTools @('read_file', 'search', 'git_status') `
    -MinimumTrust 'TrustedRead' -Workloads @('Review') `
    -Steps @(
        [ordered]@{ stepId='review'; kind='invokeProcedure'; dependsOn=@(); instructionAsset='instructions/review.md'; inputSchemaAsset='schemas/input.json'; outputSchemaAsset='schemas/review-output.json'; maximumIterations=1; hostAction=$null },
        [ordered]@{ stepId='summarize'; kind='summarize'; dependsOn=@('review'); instructionAsset='instructions/summarize.md'; inputSchemaAsset='schemas/review-output.json'; outputSchemaAsset='schemas/review-output.json'; maximumIterations=1; hostAction=$null }
    ) -Files @{
        'instructions/review.md' = @'
Review only the supplied change scope and admitted repository evidence. Find concrete correctness, security, performance, architecture, and test risks. Cite repository-relative paths and explain observable consequences. Do not propose cosmetic churn and do not mutate files. Return structured findings; use an empty findings array when no material issue is found.
'@
        'instructions/summarize.md' = @'
Deduplicate related findings without deleting distinct reviewer opinions. Preserve severity, confidence, location, consequence, and recommended disposition. Return only the same review schema.
'@
        'schemas/input.json' = @'
{
  "type": "object",
  "additionalProperties": false,
  "required": ["changeSummary", "paths"],
  "properties": {
    "changeSummary": { "type": "string", "minLength": 1, "maxLength": 8000 },
    "paths": { "type": "array", "minItems": 1, "maxItems": 256, "items": { "type": "string", "maxLength": 512 } },
    "focus": { "type": "array", "maxItems": 32, "items": { "type": "string", "maxLength": 128 } }
  }
}
'@
        'schemas/review-output.json' = @'
{
  "type": "object",
  "additionalProperties": false,
  "required": ["findings", "summary"],
  "properties": {
    "summary": { "type": "string", "minLength": 1, "maxLength": 4000 },
    "findings": {
      "type": "array",
      "maxItems": 128,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["category", "severity", "confidence", "path", "consequence", "recommendation"],
        "properties": {
          "category": { "type": "string", "maxLength": 128 },
          "severity": { "type": "string", "enum": ["info", "warning", "blocking"] },
          "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
          "path": { "type": "string", "maxLength": 512 },
          "consequence": { "type": "string", "maxLength": 2000 },
          "recommendation": { "type": "string", "maxLength": 2000 }
        }
      }
    }
  }
}
'@
    }
