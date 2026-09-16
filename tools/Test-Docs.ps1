#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Drift check for the single-sourced knowledge layer (docs/ -> skill, README).

.DESCRIPTION
  Enforces the knowledge-layer contract (docs/design.md, "Measures of success"):

    1. docs/ is the single source of truth. The marked blocks
       (`<!-- filtrace:begin <id> -->` ... `<!-- filtrace:end <id> -->`) listed in the
       sync map below are embedded verbatim into their consumer surfaces (the
       shipped skill, the README); this check fails if any copy drifts from its
       source (line endings are normalized, so it is OS-agnostic). Blocks not in
       the map (e.g. `tools`) are reference-only and need no consumer copy.
    2. The shipped skill's YAML frontmatter is valid: `name` matches the skill
       directory, and `description` is present.
        3. The prepared EP1 ready-capture protocol remains non-executable, binds exact
            public inputs, balances pair order, separates runner and arm-masked answer
            grading, and caps sessions and host AI credits.
     4. Every canonical CLI command appears in the command catalog, and every MCP
         tool appears in the tool catalog - so new surface cannot ship undocumented.
     5. Every relative link in a shipped skill file (.agents/skills/filtrace/)
       resolves to a path inside the skill directory - so no link dangles once
       the skill is packed into the NuGet package or vendored via
       `gh skill install`, both of which carry only the skill directory (issue #10).
     6. Every file provided or linked by the shipped skill is carried into the MCP
         package.
     7. Packing every packable project produces only the CLI and MCP packages, and no
         repository workflow skill enters either archive. Only skills/filtrace/
         may ship, and only in KlutzyNinja.Filtrace.Mcp.

  Run from the filtrace subtree root (the directory holding filtrace.slnx).

.PARAMETER Fix
  Rewrite each drifted consumer block from its docs/ source instead of failing.
  Use after editing a block in docs/ to refresh every embedded copy.
#>
[CmdletBinding()]
param(
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$failures = [System.Collections.Generic.List[string]]::new()
function Add-Failure([string]$message) { $failures.Add($message) }

function Assert-ExactDocObjectMembers($Value, [string[]]$Expected, [string]$Context) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) {
        Add-Failure "$Context is not an object."
        return $false
    }
    [string[]] $actual = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    if ($actual.Count -ne $Expected.Count -or
        @($actual | Where-Object { $Expected -cnotcontains $_ }).Count -ne 0) {
        Add-Failure "$Context has malformed members."
        return $false
    }
    return $true
}

function Get-DocTextSha256([string]$Text) {
    $Text = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.UTF8Encoding]::new($false).GetBytes($Text))).ToLowerInvariant()
}

function Assert-UniqueDocJsonMembers([Text.Json.JsonElement]$RootElement, [string]$Context) {
    [System.Collections.Generic.Stack[Text.Json.JsonElement]] $pending =
        [System.Collections.Generic.Stack[Text.Json.JsonElement]]::new()
    $pending.Push($RootElement)
    while ($pending.Count -gt 0) {
        [Text.Json.JsonElement] $element = $pending.Pop()
        if ($element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
            [System.Collections.Generic.HashSet[string]] $names =
                [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $element.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw "$Context contains a duplicate member." }
                $pending.Push($property.Value)
            }
        }
        elseif ($element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
            foreach ($item in $element.EnumerateArray()) { $pending.Push($item) }
        }
    }
}

function Test-DocJsonSchema($Value, [string]$SchemaPath) {
    [string] $json = $Value | ConvertTo-Json -Depth 100 -Compress
    return [bool]($json | Test-Json -SchemaFile $SchemaPath `
            -ErrorAction SilentlyContinue -WarningAction SilentlyContinue)
}

# The block sync map: each marked block has one source-of-truth page in docs/ and
# the consumer surfaces that embed a verbatim copy.
$blocks = @(
    @{ Id = 'verbs'; Source = 'docs/workflow.md'; Consumers = @('.agents/skills/filtrace/references/guide.md') }
    @{ Id = 'scopes'; Source = 'docs/workflow.md'; Consumers = @('.agents/skills/filtrace/references/guide.md', 'README.md') }
    @{ Id = 'traps'; Source = 'docs/traps.md'; Consumers = @('.agents/skills/filtrace/references/guide.md') }
    @{ Id = 'agents-snippet'; Source = 'docs/workflow.md'; Consumers = @('README.md') }
)

# Return the inner text of a marked block (newlines normalized to LF), or $null if
# the block is absent.
function Get-DocBlock {
    param([string]$Path, [string]$Id)
    if (-not (Test-Path $Path)) { return $null }
    $text = (Get-Content -LiteralPath $Path -Raw) -replace "`r`n", "`n"
    $escaped = [regex]::Escape($Id)
    $pattern = "(?s)<!-- filtrace:begin $escaped -->\n(.*?)\n[ \t]*<!-- filtrace:end $escaped -->"
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

# Replace the inner text of a marked block in a file, preserving the file's
# newline style (CRLF if the file contains any CRLF, otherwise LF).
function Set-DocBlock {
    param([string]$Path, [string]$Id, [string]$Content)
    $raw = Get-Content -LiteralPath $Path -Raw
    $newline = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
    $body = ($Content -replace "`r`n", "`n") -replace "`n", $newline
    $escaped = [regex]::Escape($Id)
    $pattern = "(?s)(<!-- filtrace:begin $escaped -->\r?\n).*?(\r?\n[ \t]*<!-- filtrace:end $escaped -->)"
    $replaced = [regex]::Replace($raw, $pattern, { param($m) $m.Groups[1].Value + $body + $m.Groups[2].Value })
    Set-Content -LiteralPath $Path -Value $replaced -NoNewline
}

# 1. Block drift (or -Fix).
foreach ($block in $blocks) {
    $sourcePath = Join-Path $root $block.Source
    $sourceText = Get-DocBlock -Path $sourcePath -Id $block.Id
    if ($null -eq $sourceText) {
        Add-Failure "Source block '$($block.Id)' not found in $($block.Source)."
        continue
    }
    foreach ($consumer in $block.Consumers) {
        $consumerPath = Join-Path $root $consumer
        $consumerText = Get-DocBlock -Path $consumerPath -Id $block.Id
        if ($null -eq $consumerText) {
            Add-Failure "Consumer block '$($block.Id)' not found in $consumer."
            continue
        }
        if ($consumerText -ne $sourceText) {
            if ($Fix) {
                Set-DocBlock -Path $consumerPath -Id $block.Id -Content $sourceText
                Write-Host "Fixed: '$($block.Id)' in $consumer refreshed from $($block.Source)."
            }
            else {
                Add-Failure "Block '$($block.Id)' in $consumer has drifted from $($block.Source). Run tools/Test-Docs.ps1 -Fix."
            }
        }
    }
}

# 2. Skill frontmatter: name matches the directory, description present.
$skillPath = Join-Path $root '.agents/skills/filtrace/SKILL.md'
if (-not (Test-Path $skillPath)) {
    Add-Failure 'Shipped skill .agents/skills/filtrace/SKILL.md is missing.'
}
else {
    $skillRaw = Get-Content -LiteralPath $skillPath -Raw
    if ($skillRaw -notmatch "(?s)^---\r?\n(.*?)\r?\n---") {
        Add-Failure '.agents/skills/filtrace/SKILL.md has no YAML frontmatter block.'
    }
    else {
        $frontmatter = $Matches[1]
        $skillName = if ($frontmatter -match '(?m)^name:\s*(\S+)\s*$') { $Matches[1] } else { $null }
        if ($skillName -ne 'filtrace') {
            Add-Failure "Skill 'name' is '$skillName'; it must be 'filtrace' to match the .agents/skills/filtrace/ directory."
        }
        if ($frontmatter -notmatch '(?m)^description:\s*\S') {
            Add-Failure 'Skill frontmatter has no non-empty description.'
        }
    }
}

# 3. Prepared EP1 protocol: exact declarative shape plus semantic checks that bind
# the current tracked task/QA/fixture and the predeclared balanced pair order. This
# validates preparation only; the protocol and this check cannot authorize a run.
$protocolCount = 0
$protocolPath = Join-Path $root 'eval/protocols/ep1-ready-capture-v1.json'
if (-not (Test-Path -LiteralPath $protocolPath -PathType Leaf)) {
    Add-Failure 'Prepared EP1 protocol eval/protocols/ep1-ready-capture-v1.json is missing.'
}
else {
    $protocolCount = 1
    [string] $protocolRaw = [System.IO.File]::ReadAllText($protocolPath)
    $protocol = $null
    try {
        [Text.Json.JsonDocument] $protocolDocument = [Text.Json.JsonDocument]::Parse($protocolRaw)
        try { Assert-UniqueDocJsonMembers $protocolDocument.RootElement 'Prepared EP1 protocol' }
        finally { $protocolDocument.Dispose() }
        $protocol = $protocolRaw | ConvertFrom-Json -Depth 100
    }
    catch {
        Add-Failure "Prepared EP1 protocol is malformed: $($_.Exception.Message)"
    }

    if ($null -ne $protocol) {
        [void](Assert-ExactDocObjectMembers $protocol @(
            'schemaVersion', 'protocolId', 'state', 'question', 'authorization',
            'identity', 'inputs', 'arms', 'isolation', 'sample', 'execution',
            'bounds', 'validity', 'adjudication', 'records', 'measurement',
            'dispositions', 'reporting', 'freeze') 'Prepared EP1 protocol')
        [void](Assert-ExactDocObjectMembers $protocol.authorization @(
                'preparationBoundary', 'measuredExecutionAuthorized',
                'requiredNextAuthorization', 'terminalAction') 'Prepared EP1 authorization')
        [void](Assert-ExactDocObjectMembers $protocol.identity @(
            'sourceRepository', 'sourceRevision', 'cleanTree', 'buildCommand',
            'buildReuse', 'cliIdentity', 'skillIdentity', 'evaluatorIdentity',
            'hostIdentity', 'modelIdentity', 'promptIdentity',
            'permissionIdentity', 'environmentIdentity',
            'privateCheckpointRecordType', 'mismatchPolicy') 'Prepared EP1 identity')
        [void](Assert-ExactDocObjectMembers $protocol.inputs @(
            'executionRevisionPolicy', 'textHashNormalization', 'taskId',
            'taskPath', 'taskSha256', 'qaPath', 'qaLineSha256', 'fixturePath',
            'fixtureSha256', 'taskPrompt', 'requiredAnswerStrings',
            'expectedEvidence') 'Prepared EP1 inputs')
        [void](Assert-ExactDocObjectMembers $protocol.inputs.expectedEvidence @(
                'operation', 'metric', 'measure', 'process', 'includeChildren',
                'topFrame', 'frameResolutionPercent', 'contributingRecords',
                'recommendedMinimumRecords', 'nextOperation', 'nextFrame',
                'nextProcess') 'Prepared EP1 expected evidence')
        [void](Assert-ExactDocObjectMembers $protocol.arms @(
                'common', 'cliSkill', 'cliOnly', 'permittedDifferences') 'Prepared EP1 arms')
        [void](Assert-ExactDocObjectMembers $protocol.arms.common @(
                'agentHost', 'configuration', 'taskId', 'modelIdentity',
                'iterationsPerInvocation', 'maxSteps', 'nonSkillTool') 'Prepared EP1 common arm')
        [void](Assert-ExactDocObjectMembers $protocol.arms.cliSkill @(
                'label', 'arm', 'skillState', 'skillEvidence') 'Prepared EP1 skill arm')
        [void](Assert-ExactDocObjectMembers $protocol.arms.cliOnly @(
                'label', 'arm', 'skillState', 'skillEvidence') 'Prepared EP1 CLI arm')
        [void](Assert-ExactDocObjectMembers $protocol.isolation @(
                'workspace', 'home', 'inputs', 'analysisCache', 'hostCache',
                'osCache', 'oracle', 'fullLoop') 'Prepared EP1 isolation')
        [void](Assert-ExactDocObjectMembers $protocol.sample @(
                'validPairTarget', 'maximumPairs', 'maximumSessions',
                'replacementPairs', 'orderAlgorithm', 'orderSeed', 'pairs',
                'invalidSessionPolicy') 'Prepared EP1 sample')
        [void](Assert-ExactDocObjectMembers $protocol.execution @(
            'runnerPath', 'commandTemplate', 'labelTemplate',
            'outputDirectoryPolicy', 'preSessionChecks',
            'postSessionChecks') 'Prepared EP1 execution')
        [void](Assert-ExactDocObjectMembers $protocol.bounds @(
                'nativeTimeoutSecondsPerSession', 'maxHostOutputBytesPerSession',
                'maxHostArtifactBytesPerSession', 'maxProjectedRetainedBytesPerInvocation',
                'maxAiCreditsPerSession', 'maximumHostSessions',
                'maximumHostAiCredits', 'stopOnInvalidSession') 'Prepared EP1 bounds')
        [void](Assert-ExactDocObjectMembers $protocol.validity @(
                'machineRequirements', 'answerCriteria', 'falseConfidenceCriteria',
                'sessionValidWhen', 'pairValidWhen',
                'experimentIncompleteWhen') 'Prepared EP1 validity')
        [void](Assert-ExactDocObjectMembers $protocol.adjudication @(
            'timing', 'blindId', 'randomization', 'packet', 'hiddenFields',
            'grader', 'rubric', 'freeze', 'unblinding', 'qualityFailure',
            'integrityFailure', 'limitation') 'Prepared EP1 adjudication')
        [void](Assert-ExactDocObjectMembers $protocol.records @(
            'schemaPath', 'schemaSha256', 'validatorPath', 'recordTypes',
            'semanticChecks', 'validation') 'Prepared EP1 records')
        [void](Assert-ExactDocObjectMembers $protocol.measurement @(
            'primaryDimensions', 'observationalDimensions', 'clock',
            'practicalEquivalence', 'classification') 'Prepared EP1 measurement')
        [void](Assert-ExactDocObjectMembers $protocol.measurement.clock @(
            'source', 'start', 'stop', 'primaryField',
            'hostDurations') 'Prepared EP1 clock')
        [void](Assert-ExactDocObjectMembers $protocol.measurement.practicalEquivalence @(
            'quality', 'analysisCallsAbsolute', 'helpCallsAbsolute',
            'resultTokensRelative', 'wallMsRelative',
            'hostAiCreditsAbsolute', 'relativeDelta') 'Prepared EP1 equivalence')
        [void](Assert-ExactDocObjectMembers $protocol.measurement.classification @(
            'deltaOrientation', 'qualityStates', 'costStates',
            'qualityComparison', 'costComparison',
            'orderSensitive', 'aggregation', 'overall') 'Prepared EP1 classification')
        [void](Assert-ExactDocObjectMembers $protocol.dispositions @(
            'session', 'pair', 'terminal', 'blockerReproducibility',
            'routing') 'Prepared EP1 dispositions')
        [void](Assert-ExactDocObjectMembers $protocol.dispositions.session @(
            'validQualityPass', 'validQualityFail',
            'invalidEvidence') 'Prepared EP1 session dispositions')
        [void](Assert-ExactDocObjectMembers $protocol.dispositions.pair @(
            'valid', 'invalid') 'Prepared EP1 pair dispositions')
        [void](Assert-ExactDocObjectMembers $protocol.dispositions.terminal @(
            'descriptiveComplete', 'incompletePrecondition',
            'incompleteEvidenceIntegrity', 'incompleteBudget',
            'stoppedByUser') 'Prepared EP1 terminal dispositions')
        [void](Assert-ExactDocObjectMembers $protocol.reporting @(
                'mode', 'requiredSessionFields', 'pairDeltaOrientation',
            'sourceSeparation', 'aggregates', 'orderEffect', 'missingData', 'completion',
                'nextAction') 'Prepared EP1 reporting')
        [void](Assert-ExactDocObjectMembers $protocol.freeze @(
                'validator', 'protocolHashPolicy', 'mergeTreePolicy',
                'executionPrecondition') 'Prepared EP1 freeze')

        if ($protocol.schemaVersion -ne 1 -or
            $protocol.protocolId -cne 'ep1-ready-capture-v1' -or
            $protocol.state -cne 'prepared-not-authorized' -or
            $protocol.authorization.measuredExecutionAuthorized -ne $false -or
            $protocol.authorization.terminalAction -cne 'stop-and-return-to-user' -or
            $protocol.reporting.mode -cne 'descriptive-only-no-winner-classification' -or
            $protocol.reporting.nextAction -cne 'stop-and-return-to-user-no-automatic-routing') {
            Add-Failure 'Prepared EP1 protocol does not retain its stopped descriptive-only state.'
        }
        if (@($protocol.authorization.requiredNextAuthorization).Count -ne 4 -or
            $protocol.authorization.preparationBoundary -cne
                'roadmap-and-protocol-through-review-and-merge-only') {
            Add-Failure 'Prepared EP1 protocol authorization boundary is malformed.'
        }
        if ($protocol.identity.sourceRepository -cne
                'https://github.com/JeremyKuhne/filtrace.git' -or
            $protocol.identity.sourceRevision -cne
                'merge-commit-and-tree-recorded-in-private-checkpoint' -or
            $protocol.identity.cleanTree -cne
                'required-before-build-and-before-each-session' -or
            $protocol.identity.buildCommand -cne
                'dotnet build filtrace.slnx -c Release' -or
            $protocol.identity.buildReuse -cne
                'one-release-build-after-merge-reused-byte-for-byte-for-all-eight-sessions' -or
            $protocol.identity.privateCheckpointRecordType -cne 'private-checkpoint' -or
            $protocol.identity.mismatchPolicy -cne
                'stop-before-the-next-host-session-and-report-incomplete-precondition') {
            Add-Failure 'Prepared EP1 protocol immutable identity contract has drifted.'
        }
        if ($protocolRaw -match '(?i)gpt-5|internal only|sol fast|[A-Z]:\\') {
            Add-Failure 'Prepared EP1 protocol contains a private model identifier or absolute Windows path.'
        }

        $expectedEvidence = $protocol.inputs.expectedEvidence
        if ($protocol.inputs.textHashNormalization -cne
                'utf8-no-bom-with-crlf-and-cr-normalized-to-lf' -or
            $expectedEvidence.operation -cne 'rank' -or
            $expectedEvidence.metric -cne 'cpu' -or
            $expectedEvidence.measure -cne 'self' -or
            $expectedEvidence.process -cne 'HotLoopBench' -or
            $expectedEvidence.includeChildren -ne $true -or
            $expectedEvidence.topFrame -cne '?' -or
            $expectedEvidence.frameResolutionPercent -ne 0 -or
            $expectedEvidence.contributingRecords -ne 51 -or
            $expectedEvidence.recommendedMinimumRecords -ne 200 -or
            $expectedEvidence.nextOperation -cne 'callers' -or
            $expectedEvidence.nextFrame -cne '?' -or
            $expectedEvidence.nextProcess -cne 'HotLoopBench') {
            Add-Failure 'Prepared EP1 protocol expected evidence has drifted.'
        }
        [string[]] $requiredAnswerContract = @(
            '--process', 'HotLoopBench', '0%', '51', '200', 'callers', 'source')
        if (@($protocol.inputs.requiredAnswerStrings).Count -ne $requiredAnswerContract.Count -or
            @(for ($index = 0; $index -lt $requiredAnswerContract.Count; $index++) {
                    if ([string]$protocol.inputs.requiredAnswerStrings[$index] -cne
                        $requiredAnswerContract[$index]) { $index }
                }).Count -ne 0) {
            Add-Failure 'Prepared EP1 protocol machine answer anchors have drifted.'
        }
        if ($protocol.arms.common.agentHost -cne 'copilot' -or
            $protocol.arms.common.configuration -cne 'Release' -or
            $protocol.arms.common.taskId -cne $protocol.inputs.taskId -or
            $protocol.arms.common.modelIdentity -cne 'private-checkpoint-exact-match' -or
            $protocol.arms.common.iterationsPerInvocation -ne 1 -or
            $protocol.arms.common.maxSteps -ne 6 -or
            $protocol.arms.common.nonSkillTool -cne 'powershell' -or
            $protocol.arms.cliSkill.label -cne 'A' -or
            $protocol.arms.cliSkill.arm -cne 'cli-skill' -or
            $protocol.arms.cliSkill.skillState -cne 'normal-project-discovery-required' -or
            $protocol.arms.cliOnly.label -cne 'B' -or
            $protocol.arms.cliOnly.arm -cne 'cli' -or
            $protocol.arms.cliOnly.skillState -cne 'verified-no-skill' -or
            @($protocol.arms.permittedDifferences).Count -ne 3) {
            Add-Failure 'Prepared EP1 protocol arm contract has drifted.'
        }
        if ($protocol.isolation.workspace -cne 'unique-outside-repository-per-session' -or
            $protocol.isolation.home -cne 'unique-empty-owned-home-per-session' -or
            $protocol.isolation.analysisCache -cne 'no-adjacent-derived-cache-copied-or-reused' -or
            $protocol.isolation.hostCache -cne 'fresh-session-private-under-unique-home' -or
            $protocol.isolation.osCache -cne 'not-reset-balanced-order-and-reported-as-limitation' -or
            $protocol.isolation.oracle -cne
                'task-qa-and-expected-facts-never-copied-to-model-visible-surfaces') {
            Add-Failure 'Prepared EP1 protocol isolation contract has drifted.'
        }

        [string] $taskPath = Join-Path $root ([string]$protocol.inputs.taskPath)
        [string] $fixturePath = Join-Path $root ([string]$protocol.inputs.fixturePath)
        if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
            Add-Failure 'Prepared EP1 protocol task or fixture path does not resolve.'
        }
        else {
            $task = Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
            [string] $taskHash = Get-DocTextSha256 ([System.IO.File]::ReadAllText($taskPath))
            [string] $fixtureHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($taskHash -cne $protocol.inputs.taskSha256 -or
                $fixtureHash -cne $protocol.inputs.fixtureSha256 -or
                $task.id -cne $protocol.inputs.taskId -or
                $task.fixture -cne $protocol.inputs.fixturePath -or
                $task.prompt -cne $protocol.inputs.taskPrompt) {
                Add-Failure 'Prepared EP1 protocol task or fixture identity has drifted.'
            }
            [System.Collections.Generic.Dictionary[string, object]] $assertions =
                [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
            foreach ($assertion in @($task.assert | Where-Object {
                        $_.PSObject.Properties.Name -ccontains 'field' -and
                        $_.PSObject.Properties.Name -ccontains 'equals'
                    })) {
                if (-not $assertions.TryAdd([string]$assertion.field, $assertion.equals)) {
                    Add-Failure "Prepared EP1 task repeats field assertion '$($assertion.field)'."
                }
            }
            if (-not $assertions.ContainsKey('result.rows[0].frame') -or
                [string]$assertions['result.rows[0].frame'] -cne [string]$expectedEvidence.topFrame -or
                -not $assertions.ContainsKey('warnings[2].data.resolutionPercent') -or
                [int]$assertions['warnings[2].data.resolutionPercent'] -ne
                    [int]$expectedEvidence.frameResolutionPercent -or
                -not $assertions.ContainsKey('warnings[3].data.contributingRecords') -or
                [int]$assertions['warnings[3].data.contributingRecords'] -ne
                    [int]$expectedEvidence.contributingRecords -or
                @($task.assert | Where-Object {
                        $_.PSObject.Properties.Name -ccontains 'hintContains' -and
                        [string]$_.hintContains -ceq "--process 'HotLoopBench'"
                    }).Count -ne 1) {
                Add-Failure 'Prepared EP1 task assertions have drifted from expected evidence.'
            }
            [string[]] $expectedAnswerStrings = @($protocol.inputs.requiredAnswerStrings)
            if (@($task.expect).Count -ne $expectedAnswerStrings.Count -or
                @(for ($index = 0; $index -lt $expectedAnswerStrings.Count; $index++) {
                        if ([string]$task.expect[$index] -cne $expectedAnswerStrings[$index]) { $index }
                    }).Count -ne 0) {
                Add-Failure 'Prepared EP1 protocol answer strings have drifted from the task.'
            }
        }

        [System.Collections.Generic.List[string]] $qaLines = [System.Collections.Generic.List[string]]::new()
        foreach ($line in Get-Content -LiteralPath (Join-Path $root ([string]$protocol.inputs.qaPath))) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $qa = $line | ConvertFrom-Json } catch { continue }
            if ([string]$qa.id -ceq [string]$protocol.inputs.taskId) { $qaLines.Add($line) }
        }
        if ($qaLines.Count -ne 1 -or
            (Get-DocTextSha256 $qaLines[0]) -cne $protocol.inputs.qaLineSha256) {
            Add-Failure 'Prepared EP1 protocol QA identity has drifted.'
        }
        else {
            $qa = $qaLines[0] | ConvertFrom-Json
            if ($qa.question -cne $protocol.inputs.taskPrompt -or
                @($qa.answerContains).Count -ne @($protocol.inputs.requiredAnswerStrings).Count -or
                @(for ($index = 0; $index -lt @($qa.answerContains).Count; $index++) {
                        if ([string]$qa.answerContains[$index] -cne
                            [string]$protocol.inputs.requiredAnswerStrings[$index]) { $index }
                    }).Count -ne 0) {
                Add-Failure 'Prepared EP1 protocol QA content has drifted.'
            }
        }

        [object[]] $pairs = @($protocol.sample.pairs)
        if ($protocol.sample.validPairTarget -ne 4 -or
            $protocol.sample.maximumPairs -ne 4 -or
            $protocol.sample.maximumSessions -ne 8 -or
            $protocol.sample.replacementPairs -ne 0 -or
            $pairs.Count -ne 4 -or
            $protocol.sample.orderSeed -cnotmatch '^[0-9a-f]{32}$' -or
            $protocol.sample.orderAlgorithm -cne 'sha256-parity-two-balanced-blocks-v1') {
            Add-Failure 'Prepared EP1 protocol pair bounds or order seed are malformed.'
        }
        else {
            [System.Collections.Generic.List[string]] $expectedOrder = [System.Collections.Generic.List[string]]::new()
            foreach ($block in 0..1) {
                [byte[]] $digest = [Security.Cryptography.SHA256]::HashData(
                    [Text.UTF8Encoding]::new($false).GetBytes("$($protocol.sample.orderSeed):$block"))
                if (($digest[0] -band 1) -eq 0) {
                    $expectedOrder.Add('cli-skill,cli')
                    $expectedOrder.Add('cli,cli-skill')
                }
                else {
                    $expectedOrder.Add('cli,cli-skill')
                    $expectedOrder.Add('cli-skill,cli')
                }
            }
            [int] $skillFirst = 0
            for ($index = 0; $index -lt $pairs.Count; $index++) {
                [void](Assert-ExactDocObjectMembers $pairs[$index] @('pair', 'first', 'second') "Prepared EP1 pair $($index + 1)")
                [string] $actualOrder = "$($pairs[$index].first),$($pairs[$index].second)"
                if ($pairs[$index].pair -ne ($index + 1) -or
                    $actualOrder -cne $expectedOrder[$index]) {
                    Add-Failure "Prepared EP1 pair $($index + 1) does not match its frozen order seed."
                }
                if ($pairs[$index].first -ceq 'cli-skill') { $skillFirst++ }
            }
            if ($skillFirst -ne 2) { Add-Failure 'Prepared EP1 pair order is not balanced.' }
        }

        if ($protocol.bounds.maximumHostSessions -ne $protocol.sample.maximumSessions -or
            $protocol.bounds.maximumHostAiCredits -ne
                ($protocol.bounds.maximumHostSessions * $protocol.bounds.maxAiCreditsPerSession) -or
            $protocol.bounds.nativeTimeoutSecondsPerSession -ne 600 -or
            $protocol.bounds.maxHostOutputBytesPerSession -ne 10485760 -or
            $protocol.bounds.maxHostArtifactBytesPerSession -ne 16777216 -or
            $protocol.bounds.maxProjectedRetainedBytesPerInvocation -ne 2147483648 -or
            $protocol.bounds.maxAiCreditsPerSession -ne 30 -or
            $protocol.bounds.stopOnInvalidSession -ne $true -or
            $protocol.sample.invalidSessionPolicy -cne
                'retain-entire-pair-stop-incomplete-no-automatic-replacement') {
            Add-Failure 'Prepared EP1 protocol session/spend bounds are inconsistent.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $root ([string]$protocol.execution.runnerPath)) -PathType Leaf) -or
            $protocol.execution.commandTemplate -cnotmatch '^-?\.?/eval/Invoke-AgentEval\.ps1 ' -or
            $protocol.execution.commandTemplate -notmatch '-Model <private-model-id>' -or
            $protocol.execution.commandTemplate -notmatch '-ExpectedModel <private-model-id>' -or
            $protocol.execution.commandTemplate -notmatch '-Tasks scope-preserving-drill' -or
            $protocol.execution.commandTemplate -notmatch '-N 1' -or
            $protocol.execution.commandTemplate -notmatch '-Label ep1-rc-p<pair>-<position>-<arm>' -or
            $protocol.execution.labelTemplate -cne 'ep1-rc-p{pair}-{position}-{arm}' -or
            @($protocol.execution.preSessionChecks).Count -lt 5 -or
            @($protocol.execution.postSessionChecks).Count -lt 5) {
            Add-Failure 'Prepared EP1 protocol execution template or checks are malformed.'
        }
        [string[]] $answerCriterionIds = @($protocol.validity.answerCriteria | ForEach-Object { $_.id })
        foreach ($criterionId in @(
                'scope', 'attribution-restraint', 'evidence-quality',
                'scope-preserving-drill', 'unsupported-claim')) {
            if ($answerCriterionIds -cnotcontains $criterionId) {
                Add-Failure "Prepared EP1 protocol is missing answer criterion '$criterionId'."
            }
        }
        foreach ($criterion in @($protocol.validity.answerCriteria)) {
            [void](Assert-ExactDocObjectMembers $criterion @('id', 'rule') "Prepared EP1 answer criterion '$($criterion.id)'")
            if ([string]::IsNullOrWhiteSpace([string]$criterion.rule)) {
                Add-Failure "Prepared EP1 answer criterion '$($criterion.id)' has no rule."
            }
        }
        if (@($protocol.validity.machineRequirements).Count -ne 7 -or
            @($protocol.validity.falseConfidenceCriteria).Count -ne 4 -or
            $protocol.validity.sessionValidWhen -cne
                'all-machine-requirements-pass-and-a-schema-valid-blinded-grade-is-frozen-regardless-of-answer-quality' -or
            $protocol.validity.pairValidWhen -cne
                'both-sessions-are-valid-and-the-pair-grade-record-hash-precedes-arm-map-reveal' -or
            $protocol.validity.experimentIncompleteWhen -cne
                'evidence-integrity-precondition-budget-or-required-grade-prevents-the-four-pair-denominator') {
            Add-Failure 'Prepared EP1 protocol validity rules have drifted.'
        }
        [string[]] $expectedHiddenFields = @(
            'arm', 'pair position', 'model and skill evidence', 'cost and timing',
            'transcript and file paths')
        if ($protocol.adjudication.timing -cne
                'after-both-sessions-in-each-pair-before-starting-the-next-pair' -or
            $protocol.adjudication.blindId -cne
                'random-128-bit-id-per-session-generated-after-the-session-and-stored-only-in-the-private-arm-map' -or
            $protocol.adjudication.randomization -cne
                'generate-packet-and-blind-ids-with-System.Security.Cryptography.RandomNumberGenerator-16-bytes-each-and-sort-packet-answers-by-blind-id-ordinal' -or
            $protocol.adjudication.packet -cne
                'explicit-answer-availability-plus-exact-final-answer-text-or-null-randomized-within-pair-under-opaque-blind-ids' -or
            @($protocol.adjudication.hiddenFields).Count -ne $expectedHiddenFields.Count -or
            @(for ($index = 0; $index -lt $expectedHiddenFields.Count; $index++) {
                    if ([string]$protocol.adjudication.hiddenFields[$index] -cne
                        $expectedHiddenFields[$index]) { $index }
                }).Count -ne 0 -or
            $protocol.adjudication.grader -cne
                'user-or-independent-reviewer-without-the-private-arm-map' -or
            $protocol.adjudication.rubric -cne
                'grade-each-answer-criterion-pass-fail-with-an-exact-answer-quote-or-no-final-answer-marker-and-record-one-false-confidence-boolean' -or
            $protocol.adjudication.freeze -cne
                'serialize-and-sha256-the-pair-grade-record-before-revealing-the-arm-map' -or
            $protocol.adjudication.unblinding -cne
                'reveal-pair-position-and-arm-only-after-both-session-grades-are-frozen' -or
            $protocol.adjudication.qualityFailure -cne
                'no-final-answer-a-failed-criterion-or-false-confidence-is-a-valid-measured-quality-outcome-not-an-invalid-session' -or
            $protocol.adjudication.integrityFailure -cne
                'missing-or-malformed-session-packet-grade-or-freeze-evidence-stops-incomplete-before-the-next-pair' -or
            $protocol.adjudication.limitation -cne
                'answer-wording-can-suggest-skill-use-so-report-the-grading-as-arm-masked-not-inference-blind') {
            Add-Failure 'Prepared EP1 protocol arm-masked grading contract has drifted.'
        }
        [string[]] $recordTypes = @(
            'blinded-packet', 'blinded-grade', 'private-arm-map',
            'private-checkpoint', 'final-report')
        [string] $recordSchemaPath = Join-Path $root ([string]$protocol.records.schemaPath)
        if (-not (Test-Path -LiteralPath $recordSchemaPath -PathType Leaf) -or
            (Get-DocTextSha256 ([System.IO.File]::ReadAllText($recordSchemaPath))) -cne
                [string]$protocol.records.schemaSha256 -or
            @($protocol.records.recordTypes).Count -ne $recordTypes.Count -or
            @(for ($index = 0; $index -lt $recordTypes.Count; $index++) {
                    if ([string]$protocol.records.recordTypes[$index] -cne
                        $recordTypes[$index]) { $index }
                }).Count -ne 0 -or
            @($protocol.records.semanticChecks).Count -ne 7 -or
            $protocol.records.validation -cne
                'run-validator-against-the-retained-artifact-directory-before-each-grade-freeze-unblinding-and-final-report') {
            Add-Failure 'Prepared EP1 record schema contract has drifted.'
        }
        else {
            [string] $recordValidatorPath = Join-Path $root ([string]$protocol.records.validatorPath)
            if (-not (Test-Path -LiteralPath $recordValidatorPath -PathType Leaf)) {
                Add-Failure 'Prepared EP1 record validator is missing.'
            }
            else {
                try {
                    & $recordValidatorPath `
                        -ProtocolPath $protocolPath `
                        -SchemaPath $recordSchemaPath `
                        -SelfTest
                }
                catch {
                    Add-Failure "Prepared EP1 record validator failed: $($_.Exception.Message)"
                }
            }
            [string] $zeroHash = '0' * 64
            [string] $packetId = '1' * 32
            [string] $firstBlindId = '2' * 32
            [string] $secondBlindId = '3' * 32
            $criterion = [ordered]@{ pass = $true; evidence = 'exact answer quote' }
            $packetProbe = [ordered]@{
                schemaVersion = 1
                protocolId = 'ep1-ready-capture-v1'
                recordType = 'blinded-packet'
                protocolSha256 = $zeroHash
                packetId = $packetId
                answers = @(
                    [ordered]@{
                        blindId = $firstBlindId
                        answerAvailable = $true
                        answer = 'first answer'
                    },
                    [ordered]@{
                        blindId = $secondBlindId
                        answerAvailable = $true
                        answer = 'second answer'
                    })
            }
            $gradeProbe = [ordered]@{
                schemaVersion = 1
                protocolId = 'ep1-ready-capture-v1'
                recordType = 'blinded-grade'
                protocolSha256 = $zeroHash
                packetId = $packetId
                packetSha256 = $zeroHash
                gradeNonce = '4' * 32
                graderRole = 'user'
                grades = @(
                    [ordered]@{
                        blindId = $firstBlindId
                        criteria = [ordered]@{
                            scope = $criterion
                            'attribution-restraint' = $criterion
                            'evidence-quality' = $criterion
                            'scope-preserving-drill' = $criterion
                            'unsupported-claim' = $criterion
                        }
                        falseConfidence = [ordered]@{ triggered = $false; evidence = 'none' }
                    },
                    [ordered]@{
                        blindId = $secondBlindId
                        criteria = [ordered]@{
                            scope = $criterion
                            'attribution-restraint' = $criterion
                            'evidence-quality' = $criterion
                            'scope-preserving-drill' = $criterion
                            'unsupported-claim' = $criterion
                        }
                        falseConfidence = [ordered]@{ triggered = $false; evidence = 'none' }
                    })
            }
            $armMapProbe = [ordered]@{
                schemaVersion = 1
                protocolId = 'ep1-ready-capture-v1'
                recordType = 'private-arm-map'
                protocolSha256 = $zeroHash
                pair = 1
                packetId = $packetId
                packetSha256 = $zeroHash
                gradeSha256 = $zeroHash
                entries = @(
                    [ordered]@{
                        blindId = $firstBlindId
                        arm = 'cli-skill'
                        position = 'first'
                        resultSha256 = '5' * 64
                    },
                    [ordered]@{
                        blindId = $secondBlindId
                        arm = 'cli'
                        position = 'second'
                        resultSha256 = '6' * 64
                    })
            }
            $checkpointProbe = [ordered]@{
                schemaVersion = 1
                protocolId = 'ep1-ready-capture-v1'
                recordType = 'private-checkpoint'
                state = 'prepared-not-authorized'
                measuredExecutionAuthorized = $false
                protocolSha256 = $zeroHash
                sourceRepository = 'https://github.com/JeremyKuhne/filtrace.git'
                mergeCommit = '7' * 40
                mergeTree = '8' * 40
                buildCommand = 'dotnet build filtrace.slnx -c Release'
                sdkVersion = '10.0.100'
                osDescription = 'test-os'
                architecture = 'x64'
                cliBundleManifestSha256 = '9' * 64
                skillManifestSha256 = 'a' * 64
                evaluatorClosureSha256 = 'b' * 64
                hostExecutableSha256 = 'c' * 64
                hostVersion = '1.0.0'
                modelIdentity = 'private-model'
                taskSha256 = 'd' * 64
                qaLineSha256 = 'e' * 64
                fixtureSha256 = 'f' * 64
            }
            $reportProbe = [ordered]@{
                schemaVersion = 1
                protocolId = 'ep1-ready-capture-v1'
                recordType = 'final-report'
                protocolSha256 = $zeroHash
                terminalDisposition = 'incomplete-precondition'
                validPairs = 0
                hostSessions = 0
                hostAiCredits = 0
                sessionResultSha256 = @()
                gradeSha256 = @()
                sessions = @()
                armSummaries = @()
                pairedDeltas = @()
                orderSummaries = @()
                qualityState = 'unavailable'
                costStates = [ordered]@{
                    analysisCalls = 'unavailable'
                    helpCalls = 'unavailable'
                    resultTokens = 'unavailable'
                    wallMs = 'unavailable'
                    hostAiCredits = 'unavailable'
                }
                nextAction = 'stop-and-return-to-user'
            }
            foreach ($probe in @($packetProbe, $gradeProbe, $armMapProbe, $checkpointProbe, $reportProbe)) {
                if (-not (Test-DocJsonSchema $probe $recordSchemaPath)) {
                    Add-Failure "Prepared EP1 schema rejected valid '$($probe.recordType)' probe."
                }
            }
            $noAnswerPacket = ($packetProbe | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $noAnswerPacket.answers[0].answerAvailable = $false
            $noAnswerPacket.answers[0].answer = $null
            if (-not (Test-DocJsonSchema $noAnswerPacket $recordSchemaPath)) {
                Add-Failure 'Prepared EP1 schema rejected a valid no-answer packet.'
            }

            $invalidPacket = ($packetProbe | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidPacket | Add-Member -NotePropertyName arm -NotePropertyValue 'cli'
            $invalidAnswerPacket = ($packetProbe | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidAnswerPacket.answers[0].answerAvailable = $false
            $invalidGrade = ($gradeProbe | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidGrade.grades[0].criteria.PSObject.Properties.Remove('scope')
            $invalidArmMap = ($armMapProbe | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidArmMap.entries[1].arm = 'cli-skill'
            $invalidCheckpoint = ($checkpointProbe | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidCheckpoint.measuredExecutionAuthorized = $true
            $invalidReport = ($reportProbe | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidReport.terminalDisposition = 'descriptive-complete'
            foreach ($probe in @(
                    $invalidPacket, $invalidAnswerPacket, $invalidGrade, $invalidArmMap,
                    $invalidCheckpoint, $invalidReport)) {
                if (Test-DocJsonSchema $probe $recordSchemaPath) {
                    Add-Failure "Prepared EP1 schema accepted malformed '$($probe.recordType)' probe."
                }
            }
        }
        if (@($protocol.measurement.primaryDimensions).Count -ne 6 -or
            @($protocol.measurement.observationalDimensions).Count -ne 5 -or
            $protocol.measurement.clock.source -cne
                'System.Diagnostics.Stopwatch-in-Invoke-AgentEvalProcess' -or
            $protocol.measurement.clock.start -cne
                'immediately-before-contained-job-object-setup-and-host-process-start' -or
            $protocol.measurement.clock.stop -cne
                'after-host-exit-stream-drain-final-artifact-scan-and-input-integrity-check' -or
            $protocol.measurement.clock.primaryField -cne
                'iteration-wallMs-from-processResult-wallMs' -or
            $protocol.measurement.clock.hostDurations -cne
                'observational-only-never-substituted-for-primary-wallMs' -or
            $protocol.measurement.practicalEquivalence.analysisCallsAbsolute -ne 0 -or
            $protocol.measurement.practicalEquivalence.helpCallsAbsolute -ne 0 -or
            $protocol.measurement.practicalEquivalence.resultTokensRelative -ne 0.05 -or
            $protocol.measurement.practicalEquivalence.wallMsRelative -ne 0.05 -or
            $protocol.measurement.practicalEquivalence.hostAiCreditsAbsolute -ne 0 -or
            $protocol.measurement.classification.deltaOrientation -cne
                'cli-skill-minus-cli' -or
            @($protocol.measurement.classification.qualityStates).Count -ne 6 -or
            @($protocol.measurement.classification.costStates).Count -ne 6 -or
            $protocol.measurement.classification.qualityComparison -cne
                'per-pair-compare-seven-good-state-bits-answer-available-plus-five-criterion-passes-plus-not-false-confidence;same-if-identical;skill-higher-if-skill-is-no-worse-in-all-and-better-in-at-least-one;skill-lower-inverse;mixed-otherwise;aggregate-order-sensitive-if-non-same-directions-track-first-arm-in-both-orientations;otherwise-same-if-all-same,higher-or-lower-if-only-that-direction-plus-same,mixed-otherwise' -or
            $protocol.measurement.classification.costComparison -cne
                'per-dimension-compute-four-skill-minus-cli-paired-deltas;calls-help-and-credits-equivalent-only-at-zero;tokens-and-wall-equivalent-within-five-percent-of-cli,with-both-zero-equivalent-and-other-zero-baselines-unavailable;order-sensitive-if-orientation-subgroup-medians-have-opposite-non-equivalent-signs;mixed-if-pairs-have-both-non-equivalent-signs;otherwise-classify-the-median-paired-delta' -or
            $protocol.measurement.classification.overall -cne
                'no-winner-score-or-significance-claim') {
            Add-Failure 'Prepared EP1 measurement and classification contract has drifted.'
        }
        if ($protocol.dispositions.session.validQualityFail -cne
            'machine-evidence-valid-and-no-final-answer-any-criterion-fails-or-false-confidence-triggers' -or
            $protocol.dispositions.pair.valid -cne
                'both-sessions-have-valid-evidence-and-schema-valid-grades-frozen-before-unblinding-regardless-of-quality' -or
            @($protocol.dispositions.terminal.PSObject.Properties).Count -ne 5 -or
            $protocol.dispositions.blockerReproducibility -cne
                'call-a-precondition-blocked-only-after-the-same-non-metered-check-fails-twice;retain-a-started-invalid-session-as-incomplete-without-replacement' -or
            $protocol.dispositions.routing -cne
                'every-terminal-disposition-writes-a-schema-valid-final-report-and-stops-for-user-decision-with-no-automatic-next-work') {
            Add-Failure 'Prepared EP1 terminal disposition contract has drifted.'
        }
        foreach ($requiredField in @(
            'resultSha256', 'success', 'answerAvailable', 'falseConfidence',
            'calls', 'helpCalls', 'tokens', 'wallMs', 'hostUsage',
            'hostUsageFile', 'hostAiCredits', 'answerCriteria', 'transcript',
            'inputIdentity', 'pair', 'position')) {
            if (@($protocol.reporting.requiredSessionFields) -cnotcontains $requiredField) {
                Add-Failure "Prepared EP1 protocol is missing report field '$requiredField'."
            }
        }
        if ($protocol.reporting.pairDeltaOrientation -cne 'cli-skill-minus-cli' -or
            $protocol.reporting.sourceSeparation -cne
                'success-comes-from-schema-v3-runner-output-answerCriteria-and-falseConfidence-come-only-from-the-frozen-blinded-grade-record' -or
            @($protocol.reporting.aggregates).Count -ne 4 -or
            $protocol.reporting.orderEffect -cne
                'report orientation summaries separately and label disagreement order-sensitive' -or
            $protocol.reporting.missingData -cne
                'report unavailable never zero and stop incomplete for required fields' -or
            $protocol.reporting.completion -cne
                'descriptive-complete-only-after-four-valid-graded-pairs-including-quality-failures' -or
            $protocol.freeze.validator -cne 'tools/Test-Docs.ps1' -or
            $protocol.freeze.protocolHashPolicy -cne
                'record-lf-normalized-utf8-no-bom-sha256-after-merge-in-private-checkpoint' -or
            $protocol.freeze.executionPrecondition -cne
                'exact-protocol-hash-and-private-model-identity-match') {
            Add-Failure 'Prepared EP1 protocol reporting or freeze contract has drifted.'
        }
    }
}

# 4. Command / tool completeness: every canonical CLI command is in the command
# catalog, every MCP tool is in the tool catalog.
$verbsBlock = Get-DocBlock -Path (Join-Path $root 'docs/workflow.md') -Id 'verbs'
$commandsSource = Get-Content -LiteralPath (Join-Path $root 'src/Filtrace/Cli/TraceCommands.cs') -Raw
$allVerbs = @([regex]::Matches($commandsSource, '\[Command\("([^"]+)"\)\]') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$hiddenVerbs = @([regex]::Matches(
        $commandsSource,
        '\[Hidden\]\s*\r?\n\s*\[Command\("([^"]+)"\)\]') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$hiddenSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($hiddenVerb in $hiddenVerbs) { [void]$hiddenSet.Add($hiddenVerb) }
$verbs = @($allVerbs | Where-Object { -not $hiddenSet.Contains($_) })
if ($verbs.Count -eq 0) { Add-Failure 'No canonical [Command(...)] entries found in TraceCommands.cs.' }
foreach ($verb in $verbs) {
    if ($null -eq $verbsBlock -or $verbsBlock -notmatch "(?m)\b$([regex]::Escape($verb))\b") {
        Add-Failure "Command '$verb' is not documented in the 'verbs' block of docs/workflow.md."
    }
}

$toolsBlock = Get-DocBlock -Path (Join-Path $root 'docs/workflow.md') -Id 'tools'
$tools = @(Select-String -Path (Join-Path $root 'src/Filtrace.Mcp/TraceTools.cs') -Pattern 'Name = "(trace_[a-z_]+)"' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($tools.Count -eq 0) { Add-Failure 'No trace_* MCP tools found in TraceTools.cs.' }
foreach ($tool in $tools) {
    if ($null -eq $toolsBlock -or $toolsBlock -notmatch "(?m)\b$([regex]::Escape($tool))\b") {
        Add-Failure "Tool '$tool' is not documented in the 'tools' block of docs/workflow.md."
    }
}

# 5. Skill link integrity: every relative link in a shipped skill file must
# resolve to a path inside the skill directory. A link that escapes the directory
# (e.g. ../../../docs/workflow.md) dangles once the skill is packed into the NuGet
# package or vendored via `gh skill install`, both of which carry only the skill
# directory (issue #10). External links (scheme://, mailto:), protocol-relative
# links, and pure anchors are exempt; a scheme must be at least two characters so a
# Windows drive letter (e.g. C:\path) is treated as a path, not a URL scheme.
$skillDir = Join-Path $root '.agents/skills/filtrace'
$skillDirFull = [System.IO.Path]::GetFullPath($skillDir)
$linkCount = 0
# Absolute paths the shipped skill needs the MCP package to carry: each shipped skill
# file, plus every in-directory file it links to. Verified against the package in check 5.
$requiredInPackage = [System.Collections.Generic.List[string]]::new()
if (Test-Path $skillDir) {
    $linkPattern = '\[[^\]]*\]\(([^)\s]+)\)'
    foreach ($file in Get-ChildItem -LiteralPath $skillDir -Recurse -Filter *.md -File) {
        $name = [System.IO.Path]::GetRelativePath($root, $file.FullName) -replace '\\', '/'
        $content = Get-Content -LiteralPath $file.FullName -Raw
        [void]$requiredInPackage.Add($file.FullName)
        foreach ($match in [regex]::Matches($content, $linkPattern)) {
            $target = $match.Groups[1].Value
            # Exempt URLs (scheme: with a 2+ char scheme), protocol-relative (//host),
            # and pure anchors (#frag).
            if ($target -match '^(?:[a-z][a-z0-9+.-]+:|//|#)') { continue }
            $linkCount++
            $relative = ($target -split '#', 2)[0]
            if ([string]::IsNullOrEmpty($relative)) { continue }
            # A rooted target (drive-letter or leading separator, e.g. C:\x or /x) is not
            # an in-directory relative link and will not travel with the skill.
            if ([System.IO.Path]::IsPathRooted($relative)) {
                Add-Failure "Skill file '$name' links to '$target', which is an absolute path that will not resolve when the skill is packaged or vendored (issue #10). Use an absolute https URL or a path inside the skill directory."
                continue
            }
            $resolved = [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName $relative))
            $fromSkill = [System.IO.Path]::GetRelativePath($skillDirFull, $resolved)
            # `..` escapes only as a whole path segment (.. or ..<sep>), not as a leading
            # substring of a real name such as `..hidden`.
            if ($fromSkill -match '^\.\.([\\/]|$)') {
                Add-Failure "Skill file '$name' links to '$target', which escapes the skill directory and will dangle when the skill is packaged or vendored (issue #10). Use an absolute https URL or a path inside the skill directory."
            }
            elseif (-not (Test-Path -LiteralPath $resolved)) {
                Add-Failure "Skill file '$name' links to '$target', which does not resolve to an existing path."
            }
            else {
                # An in-directory file the skill references - it must travel in the package.
                [void]$requiredInPackage.Add($resolved)
            }
        }
    }
}

# 6. Skill packaging completeness: every shipped skill file and every in-directory
# file it links to must be carried into the MCP package - not merely resolvable in
# the repo. Check 4 proves the repo layout; a curated <None Pack> include can still
# drift from what SKILL.md references and ship a dangling link while check 4 passes
# (issue #10, round two). Read the authoritative pack list from MSBuild (it expands
# the include globs) instead of re-implementing glob semantics here.
$mcpProject = Join-Path $root 'src/Filtrace.Mcp/Filtrace.Mcp.csproj'
$packedCount = 0
if ($requiredInPackage.Count -gt 0) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host "  (skill-packaging check skipped: 'dotnet' not on PATH)" -ForegroundColor Yellow
    }
    elseif (-not (Test-Path $mcpProject)) {
        Add-Failure "Cannot verify skill packaging: '$mcpProject' was not found."
    }
    else {
        $itemsJson = & dotnet build $mcpProject -getItem:None 2>$null | Out-String
        $noneItems = $null
        if (-not [string]::IsNullOrWhiteSpace($itemsJson)) {
            try { $noneItems = ($itemsJson | ConvertFrom-Json).Items.None } catch { }
        }
        if ($null -eq $noneItems) {
            Add-Failure "Cannot verify skill packaging: 'dotnet build <Filtrace.Mcp> -getItem:None' returned no parseable None items."
        }
        else {
            # Full paths of the files the package will carry (None items marked Pack=true).
            $packedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($item in $noneItems) {
                if ($item.Pack -eq 'true' -and $item.FullPath) {
                    $full = [System.IO.Path]::GetFullPath($item.FullPath)
                    [void]$packedSet.Add($full)
                    # Count only the packed files under the skill directory (for the summary).
                    $fromSkill = [System.IO.Path]::GetRelativePath($skillDirFull, $full)
                    if ($fromSkill -notmatch '^\.\.([\\/]|$)' -and -not [System.IO.Path]::IsPathRooted($fromSkill)) { $packedCount++ }
                }
            }
            foreach ($required in ($requiredInPackage | Sort-Object -Unique)) {
                if (-not $packedSet.Contains([System.IO.Path]::GetFullPath($required))) {
                    $rel = [System.IO.Path]::GetRelativePath($skillDirFull, $required) -replace '\\', '/'
                    Add-Failure "The MCP package (Filtrace.Mcp.csproj) does not pack 'skills/filtrace/$rel', which the shipped skill provides or links to; the packaged skill would ship a missing file / dangling link. Extend the skill's None include (Pack=true) to cover it."
                }
            }
        }
    }
}

# 7. Package isolation: discover and inspect every packable project, not only
# evaluated MSBuild items or projects currently in the solution. This catches future
# broad content globs, new item types, and new packages that would otherwise pick up
# repository workflow skills.
$packageCount = 0
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "  (package-isolation check skipped: 'dotnet' not on PATH)" -ForegroundColor Yellow
}
else {
    $packageOutput = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace-packages-$([guid]::NewGuid().ToString('N'))"
    [System.Collections.Generic.Dictionary[string, string]] $vendoredFilesByHash = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($vendoredSkillDirectory in Get-ChildItem -LiteralPath (Join-Path $root '.agents/skills') -Directory | Where-Object Name -cne 'filtrace') {
        foreach ($vendoredFile in Get-ChildItem -LiteralPath $vendoredSkillDirectory.FullName -Recurse -File) {
            [string] $vendoredHash = (Get-FileHash -LiteralPath $vendoredFile.FullName -Algorithm SHA256).Hash
            if (-not $vendoredFilesByHash.ContainsKey($vendoredHash)) {
                $vendoredFilesByHash.Add($vendoredHash, [System.IO.Path]::GetRelativePath($root, $vendoredFile.FullName))
            }
        }
    }

    New-Item -ItemType Directory -Path $packageOutput | Out-Null
    try {
        [System.Collections.Generic.List[System.IO.FileInfo]] $packableProjects = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
        foreach ($project in Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.csproj') {
            $propertiesText = & dotnet msbuild $project.FullName '-getProperty:IsPackable,PackageId' 2>$null | Out-String
            try {
                $properties = $propertiesText | ConvertFrom-Json
            }
            catch {
                Add-Failure "Cannot read packability for '$([System.IO.Path]::GetRelativePath($root, $project.FullName))'."
                continue
            }

            if ($properties.Properties.IsPackable -eq 'true') {
                $packableProjects.Add($project)
            }
        }

        foreach ($project in $packableProjects) {
            $packText = & dotnet pack $project.FullName --configuration Release --no-restore --output $packageOutput /p:IncludeSymbols=false 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                Add-Failure "Cannot verify package isolation because packing '$([System.IO.Path]::GetRelativePath($root, $project.FullName))' failed:`n$packText"
            }
        }

        if ($packableProjects.Count -eq 0) {
            Add-Failure 'Package isolation found no packable projects; expected the CLI and MCP projects.'
        }
        else {
            [System.IO.FileInfo[]] $packages = @(Get-ChildItem -LiteralPath $packageOutput -File -Filter '*.nupkg')
            $packageCount = $packages.Count
            [System.Collections.Generic.HashSet[string]] $expectedPackageIds = [System.Collections.Generic.HashSet[string]]::new(
                [string[]]@('KlutzyNinja.Filtrace', 'KlutzyNinja.Filtrace.Mcp'),
                [System.StringComparer]::OrdinalIgnoreCase)
            [System.Collections.Generic.HashSet[string]] $actualPackageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

            foreach ($package in $packages) {
                $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
                try {
                    $nuspecEntries = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
                    if ($nuspecEntries.Count -ne 1) {
                        Add-Failure "Package '$($package.Name)' contains $($nuspecEntries.Count) nuspec files; expected one."
                        continue
                    }

                    $nuspecStream = $nuspecEntries[0].Open()
                    $nuspecReader = [System.IO.StreamReader]::new($nuspecStream)
                    try {
                        [xml] $nuspec = $nuspecReader.ReadToEnd()
                    }
                    finally {
                        $nuspecReader.Dispose()
                        $nuspecStream.Dispose()
                    }

                    [string] $packageId = $nuspec.package.metadata.id
                    [void]$actualPackageIds.Add($packageId)
                    foreach ($entry in $archive.Entries) {
                        [string] $entryPath = $entry.FullName -replace '\\', '/'
                        $skillMatch = [regex]::Match($entryPath, '(?i)(?:^|/)(?:\.agents/)?skills/([^/]+)(?:/|$)')
                        if ($skillMatch.Success) {
                            [string] $packagedSkillName = $skillMatch.Groups[1].Value
                            if ($packagedSkillName -cne 'filtrace') {
                                Add-Failure "Package '$packageId' contains repository workflow skill path '$entryPath'. Only the tool-shipped filtrace skill may be packaged."
                            }
                            elseif ($packageId -cne 'KlutzyNinja.Filtrace.Mcp') {
                                Add-Failure "Package '$packageId' contains '$entryPath'; the filtrace skill may ship only in KlutzyNinja.Filtrace.Mcp."
                            }
                        }

                        if ([string]::IsNullOrEmpty($entry.Name)) { continue }
                        $entryStream = $entry.Open()
                        $sha256 = [System.Security.Cryptography.SHA256]::Create()
                        try {
                            [string] $entryHash = [System.Convert]::ToHexString($sha256.ComputeHash($entryStream))
                        }
                        finally {
                            $sha256.Dispose()
                            $entryStream.Dispose()
                        }

                        if ($vendoredFilesByHash.ContainsKey($entryHash)) {
                            Add-Failure "Package '$packageId' entry '$entryPath' matches vendored workflow file '$($vendoredFilesByHash[$entryHash])'. Vendored skills must never be packaged."
                        }
                    }
                }
                finally {
                    $archive.Dispose()
                }
            }

            if ($actualPackageIds.Count -ne $expectedPackageIds.Count -or
                $actualPackageIds.Where({ -not $expectedPackageIds.Contains($_) }).Count -gt 0 -or
                $expectedPackageIds.Where({ -not $actualPackageIds.Contains($_) }).Count -gt 0) {
                Add-Failure "Packed package IDs are '$($actualPackageIds -join ', ')'; expected exactly '$($expectedPackageIds -join ', ')'."
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $packageOutput -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Checked $($blocks.Count) shared block(s), $protocolCount prepared protocol(s), $($verbs.Count) command(s), $($tools.Count) tool(s), $linkCount skill link(s), $packedCount packed skill file(s), $packageCount package archive(s)."

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Docs drift check FAILED with $($failures.Count) issue(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host 'Docs drift check passed.' -ForegroundColor Green
exit 0
