#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[string] $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[string] $evalDirectory = Join-Path $root 'eval'
[string] $protocolDirectory = Join-Path $evalDirectory 'protocols'
. (Join-Path $evalDirectory 'CopilotEval.Helpers.ps1')
. (Join-Path $evalDirectory 'Get-OperationName.ps1')

function Get-TextHash([string] $Text) {
    [byte[]] $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text.Replace("`r`n", "`n").Replace("`r", "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-FileTextHash([string] $Path) {
    return Get-TextHash ([System.IO.File]::ReadAllText($Path))
}

function Read-StrictJson([string] $Path) {
    [string] $raw = [System.IO.File]::ReadAllText($Path)
    [Text.Json.JsonDocument] $document = [Text.Json.JsonDocument]::Parse($raw)
    try {
        [System.Collections.Generic.Stack[Text.Json.JsonElement]] $pending =
            [System.Collections.Generic.Stack[Text.Json.JsonElement]]::new()
        $pending.Push($document.RootElement)
        while ($pending.Count -gt 0) {
            [Text.Json.JsonElement] $element = $pending.Pop()
            if ($element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                [System.Collections.Generic.HashSet[string]] $names =
                    [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($property in $element.EnumerateObject()) {
                    if (-not $names.Add($property.Name)) { throw "Duplicate JSON member '$($property.Name)' in '$Path'." }
                    $pending.Push($property.Value)
                }
            }
            elseif ($element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                foreach ($item in $element.EnumerateArray()) { $pending.Push($item) }
            }
        }
    }
    finally { $document.Dispose() }
    return $raw | ConvertFrom-Json -Depth 100
}

function Assert-Members($Object, [string[]] $Names, [string] $Context) {
    if ($null -eq $Object -or $Object -is [string] -or $Object -is [ValueType]) {
        throw "$Context is not an object."
    }
    [string[]] $actual = @($Object.PSObject.Properties.Name)
    if ($actual.Count -ne $Names.Count -or
        @($actual | Where-Object { $Names -cnotcontains $_ }).Count -ne 0) {
        throw "$Context has malformed members."
    }
}

function Assert-Sequence([object[]] $Actual, [object[]] $Expected, [string] $Context) {
    if ($Actual.Count -ne $Expected.Count) { throw "$Context has the wrong length." }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not [string]::Equals([string]$Actual[$index], [string]$Expected[$index], [StringComparison]::Ordinal)) {
            throw "$Context differs at index $index."
        }
    }
}

function Assert-JsonEqual($Left, $Right, [string] $Context) {
    [Text.Json.Nodes.JsonNode] $leftJson = [Text.Json.Nodes.JsonNode]::Parse(
        ($Left | ConvertTo-Json -Depth 100 -Compress))
    [Text.Json.Nodes.JsonNode] $rightJson = [Text.Json.Nodes.JsonNode]::Parse(
        ($Right | ConvertTo-Json -Depth 100 -Compress))
    if (-not [Text.Json.Nodes.JsonNode]::DeepEquals($leftJson, $rightJson)) {
        throw "$Context has drifted from the hash-checked v2 fairness contract."
    }
}

function Get-Assertion(
    $Task,
    [string] $Field,
    [ValidateSet('equals', 'endsWith')][string] $Kind = 'equals',
    [ValidateRange(0, 2)][int] $Step = 0) {
    [object[]] $matching = @($Task.assert | Where-Object {
            $_.PSObject.Properties.Name -ccontains 'field' -and
            [string]::Equals([string]$_.field, $Field, [StringComparison]::Ordinal) -and
            $_.PSObject.Properties.Name -ccontains 'step' -and
            [int]$_.step -eq $Step
        })
    if ($matching.Count -ne 1 -or $matching[0].PSObject.Properties.Name -cnotcontains $Kind) {
        throw "Task '$($Task.id)' must have one step-$Step '$Kind' assertion for '$Field'."
    }
    return $matching[0].PSObject.Properties[$Kind].Value
}

[string] $protocolPath = Join-Path $protocolDirectory 'ep1-ready-capture-v3.json'
[string] $schemaPath = Join-Path $protocolDirectory 'ep1-ready-capture-record-v3.schema.json'
$historicalHashes = [ordered]@{
    'ep1-ready-capture-v1.json' = 'c073cd3a36394b818c43b03d3d6302966d0c20100ac6e90cadf8b777c69a95aa'
    'ep1-ready-capture-record-v1.schema.json' = '03c487a8724eef2c87dd31d3315f84e1d06b15f8f2f1c304d731fd7a6305f8e8'
    'ep1-ready-capture-v2.json' = 'de68de5025cf502f0e1a1eb916a35f8e8ae7f3c709bc44b9ae4451827c1d16be'
    'ep1-ready-capture-record-v2.schema.json' = 'ffb7f7ac988c12d25231c5f2bd3d113ab2e02d81f756fc0eadd1cb57d0644abf'
}
foreach ($entry in $historicalHashes.GetEnumerator()) {
    if ((Get-FileTextHash (Join-Path $protocolDirectory $entry.Key)) -cne $entry.Value) {
        throw "Frozen historical EP1 artifact '$($entry.Key)' has drifted."
    }
}
$v2 = Read-StrictJson (Join-Path $protocolDirectory 'ep1-ready-capture-v2.json')
$protocol = Read-StrictJson $protocolPath
$null = Read-StrictJson $schemaPath
Assert-Members $protocol @(
    'schemaVersion', 'protocolId', 'state', 'revision', 'question', 'authorization',
    'identity', 'inputs', 'arms', 'isolation', 'sample', 'execution', 'bounds',
    'validity', 'adjudication', 'records', 'measurement', 'dispositions',
    'reporting', 'freeze') 'EP1 v3 protocol'
if ($protocol.schemaVersion -ne 1 -or $protocol.protocolId -cne 'ep1-ready-capture-v3' -or
    $protocol.state -cne 'prepared-not-authorized' -or
    $protocol.question -cne
        'Does the updated shipped Filtrace skill improve evidence-backed investigation compared with the same current Filtrace CLI without the skill?') {
    throw 'EP1 v3 is not the sole prepared skill-versus-CLI question.'
}
Assert-Members $protocol.revision @('supersedes', 'priorDisposition', 'changes', 'unchanged') 'EP1 v3 revision'
if ($protocol.revision.supersedes -cne 'ep1-ready-capture-v2' -or
    @($protocol.revision.changes).Count -lt 4 -or @($protocol.revision.unchanged).Count -lt 3) {
    throw 'EP1 v3 must identify the historical v2 contract and its bounded changes.'
}
Assert-Members $protocol.authorization @(
    'preparationBoundary', 'measuredExecutionAuthorized', 'requiredNextAuthorization',
    'terminalAction') 'EP1 v3 authorization'
if ($protocol.authorization.preparationBoundary -cne 'local-protocol-and-offline-validation-only' -or
    $protocol.authorization.measuredExecutionAuthorized -ne $false -or
    $protocol.authorization.terminalAction -cne 'stop-and-return-to-user') {
    throw 'EP1 v3 cannot authorize a host session through protocol preparation.'
}
Assert-Sequence @($protocol.authorization.requiredNextAuthorization) @(
    'exact v3 protocol content hash',
    'exact private GPT-6 Sol model identifier and matching model identity SHA-256',
    'maximum 16 independently isolated host sessions',
    'stop after the descriptive ready-capture report') 'EP1 v3 required authorization'

Assert-Members $protocol.identity @(
    'sourceRepository', 'sourceRevision', 'cleanTree', 'buildCommand', 'buildReuse',
    'cliIdentity', 'skillIdentity', 'evaluatorIdentity', 'hostIdentity', 'modelIdentity',
    'promptIdentity', 'permissionIdentity', 'environmentIdentity',
    'privateCheckpointRecordType', 'privateAuthorizationRecordType', 'mismatchPolicy') 'EP1 v3 identity'
if ($protocol.identity.sourceRepository -cne $v2.identity.sourceRepository -or
    $protocol.identity.buildCommand -cne $v2.identity.buildCommand -or
    $protocol.identity.cleanTree -cne $v2.identity.cleanTree -or
    $protocol.identity.buildReuse -cne 'one-release-build-reused-byte-for-byte-for-all-sixteen-sessions' -or
    $protocol.identity.modelIdentity -cne 'GPT-6-Sol-exact-private-identifier-checked-before-each-session-no-substitution' -or
    $protocol.identity.mismatchPolicy -cne $v2.identity.mismatchPolicy) {
    throw 'EP1 v3 CLI, model, or source identity does not retain the frozen boundary.'
}
if ([System.IO.File]::ReadAllText($protocolPath) -match '(?i)gpt-5|[a-z]:\\|(?:"\\\\\\\\[^\\])|maximumHostAiCredits|maxAiCreditsPerSession') {
    throw 'EP1 v3 exposes an old model, an absolute path, or a host AI credit ceiling.'
}
Assert-Members $protocol.arms @('common', 'cliSkill', 'cliOnly', 'permittedDifferences') 'EP1 v3 arms'
Assert-Members $protocol.arms.common @(
    'agentHost', 'configuration', 'modelIdentity', 'iterationsPerInvocation',
    'maxSteps', 'nonSkillTool') 'EP1 v3 common arm'
if ($protocol.arms.common.agentHost -cne 'copilot' -or
    $protocol.arms.common.configuration -cne 'Release' -or
    $protocol.arms.common.modelIdentity -cne 'private-checkpoint-exact-match' -or
    $protocol.arms.common.iterationsPerInvocation -ne 1 -or
    $protocol.arms.common.maxSteps -ne 6 -or
    $protocol.arms.common.nonSkillTool -cne 'powershell') {
    throw 'EP1 v3 arms must use one exact Copilot model and six analysis steps.'
}
Assert-JsonEqual $protocol.arms.cliSkill $v2.arms.cliSkill 'Skill arm'
Assert-JsonEqual $protocol.arms.cliOnly $v2.arms.cliOnly 'CLI arm'
Assert-JsonEqual $protocol.arms.permittedDifferences $v2.arms.permittedDifferences 'Permitted differences'
Assert-JsonEqual $protocol.isolation $v2.isolation 'Session isolation'

Assert-Members $protocol.inputs @('executionRevisionPolicy', 'textHashNormalization', 'qaPath', 'skill', 'tasks') 'EP1 v3 inputs'
if ($protocol.inputs.qaPath -cne 'eval/mcp-qa.jsonl' -or
    $protocol.inputs.textHashNormalization -cne $v2.inputs.textHashNormalization -or
    @($protocol.inputs.tasks).Count -ne 2) {
    throw 'EP1 v3 must bind exactly two public task/QA/fixture inputs.'
}
Assert-Members $protocol.inputs.skill @(
    'entrypointPath', 'entrypointTextSha256', 'manifestTextSha256', 'entrypointRawSha256Windows',
    'manifestSha256Windows', 'fileCount') 'EP1 v3 shipped skill'
[string] $skillPath = Join-Path $root '.agents/skills/filtrace/SKILL.md'
if ($protocol.inputs.skill.entrypointPath -cne '.agents/skills/filtrace/SKILL.md' -or
    $protocol.identity.skillIdentity -cne
        'pinned-updated-shipped-skill-entrypoint-and-manifest-plus-private-checkpoint-and-session-inventory-sha256' -or
    $protocol.inputs.skill.entrypointTextSha256 -cne (Get-FileTextHash $skillPath) -or
    $protocol.inputs.skill.fileCount -ne 11) {
    throw 'EP1 v3 shipped skill entrypoint no longer matches the updated source.'
}
$shippedSkill = Get-AgentEvalSkillInput -Root $root
if (@($shippedSkill.files).Count -ne [int]$protocol.inputs.skill.fileCount) {
    throw 'EP1 v3 shipped skill inventory has drifted.'
}
[string[]] $portableEntries = @($shippedSkill.files | ForEach-Object {
        [string] $text = [System.IO.File]::ReadAllText($_.sourcePath).Replace("`r`n", "`n").Replace("`r", "`n")
        [byte[]] $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
        [string]([ordered]@{
                path = ([string]$_.relativePath).Replace('\', '/')
                bytes = [long]$bytes.Length
                sha256 = Get-TextHash $text
            } | ConvertTo-Json -Compress)
    })
[Array]::Sort($portableEntries, [StringComparer]::Ordinal)
if ((Get-TextHash (ConvertTo-Json -InputObject $portableEntries -Compress)) -cne
    $protocol.inputs.skill.manifestTextSha256) {
    throw 'EP1 v3 cross-platform skill file manifest no longer matches the pinned updated source.'
}
if ([System.OperatingSystem]::IsWindows()) {
    [object[]] $entrypoint = @($shippedSkill.files | Where-Object relativePath -ceq 'SKILL.md')
    [string[]] $entries = @($shippedSkill.files | ForEach-Object {
            [string]([ordered]@{
                    path = [string]$_.relativePath
                    bytes = [long]$_.bytes
                    sha256 = [string]$_.sha256
                } | ConvertTo-Json -Compress)
        })
    [Array]::Sort($entries, [StringComparer]::Ordinal)
    [string] $manifestHash = Get-TextHash (ConvertTo-Json -InputObject $entries -Compress)
    if ($entrypoint.Count -ne 1 -or
        $entrypoint[0].sha256 -cne $protocol.inputs.skill.entrypointRawSha256Windows -or
        $manifestHash -cne $protocol.inputs.skill.manifestSha256Windows) {
        throw 'EP1 v3 Windows skill bytes or complete skill manifest no longer match the pinned source.'
    }
}
[string[]] $expectedTasks = @('quality-first-unresolved', 'mixed-unknown-resolved')
[string[]] $expectedTaskPaths = @(
    'eval/tasks/30-quality-first-unresolved.json',
    'eval/tasks/31-mixed-unknown-resolved.json')
[string[]] $expectedFixtures = @(
    'tests/Filtrace.Core.Tests/Fixtures/etw.etl',
    'tests/Filtrace.Core.Tests/Fixtures/mixed-unknown.speedscope.json')
$baselines = Read-StrictJson (Join-Path $evalDirectory 'baselines.json')
[string[]] $qaLines = @(Get-Content -LiteralPath (Join-Path $root $protocol.inputs.qaPath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
for ($taskIndex = 0; $taskIndex -lt 2; $taskIndex++) {
    $input = $protocol.inputs.tasks[$taskIndex]
    [string] $taskId = $expectedTasks[$taskIndex]
    Assert-Members $input @(
        'taskId', 'taskPath', 'taskSha256', 'qaLineSha256', 'fixturePath',
        'fixtureSha256', 'taskPrompt', 'requiredAnswerStrings', 'expectedEvidence',
        'baselineCalls', 'baselineTokens') "EP1 v3 input '$taskId'"
    if ($input.taskId -cne $taskId -or $input.taskPath -cne $expectedTaskPaths[$taskIndex] -or
        $input.fixturePath -cne $expectedFixtures[$taskIndex] -or
        $input.taskSha256 -cne (Get-FileTextHash (Join-Path $root $input.taskPath)) -or
        $input.fixtureSha256 -cne (Get-FileHash -LiteralPath (Join-Path $root $input.fixturePath) -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw "EP1 v3 public task or fixture '$taskId' has drifted."
    }
    $task = Read-StrictJson (Join-Path $root $input.taskPath)
    [object[]] $qaMatches = @($qaLines | Where-Object {
            (($_ | ConvertFrom-Json -Depth 100).id) -ceq $taskId
        })
    if ($qaMatches.Count -ne 1 -or
        (Get-TextHash ([string]$qaMatches[0])) -cne $input.qaLineSha256) {
        throw "EP1 v3 task '$taskId' QA identity has drifted."
    }
    $qa = $qaMatches[0] | ConvertFrom-Json -Depth 100
    if ($task.id -cne $taskId -or $task.fixture -cne $input.fixturePath -or
        $task.prompt -cne $input.taskPrompt -or
        $qa.question -cne $task.prompt -or
        $qa.fixture -cne $task.fixture -or $qa.os -cne $task.os) {
        throw "EP1 v3 task '$taskId' prompt, OS, or QA mirror has drifted."
    }
    if ($task.prompt -match '(?i)filtrace skill|callers|symbolResolutionRate|GPT-6|App\.Work|App\.Main|\b60\s*%|\b25\s*%|\b100\s*ms\b') {
        throw "EP1 v3 task '$taskId' leaks an evaluator-only treatment or answer into the model prompt."
    }
    if ($taskIndex -eq 1 -and
        (-not $task.prompt.Contains('profiled self weight', [StringComparison]::Ordinal) -or
            $task.prompt.Contains('sampled work', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'EP1 v3 evented speedscope prompt must describe profiled self weight, not periodic samples.'
    }
    [string[]] $answerContract = if ($taskIndex -eq 0) {
        @('--process', 'HotLoopBench', '?', '0%', '51', '200', 'info')
    }
    else {
        @('Speedscope', '?', '60%', 'App.Work', '25%', '100', 'info')
    }
    Assert-Sequence @($input.requiredAnswerStrings) $answerContract "Task '$taskId' frozen answer anchors"
    Assert-Sequence @($task.expect) @($input.requiredAnswerStrings) "Task '$taskId' answer anchors"
    Assert-Sequence @($qa.answerContains) @($input.requiredAnswerStrings) "Task '$taskId' QA answer anchors"
    Assert-Sequence @($qa.expectTools) @('trace_rank', 'trace_info') "Task '$taskId' QA tools"
    Assert-Sequence @($qa.expectOperations) @('rank', 'info') "Task '$taskId' QA operations"
    [int] $lastStep = if ($taskIndex -eq 0) { 1 } else { 2 }
    if (@($task.assert | Where-Object {
                $_.PSObject.Properties.Name -cnotcontains 'step' -or
                [int]$_.step -lt 0 -or [int]$_.step -gt $lastStep
            }).Count -ne 0) {
        throw "Task '$taskId' must assign every assertion to a frozen canonical step."
    }
    [object[]] $policyFamilies = @(Get-AgentEvalTaskCommandFamilies `
            -Task $task -AllowedVerbs @('rank', 'info', 'callers'))
    [string[]] $expectedFamilies = if ($taskIndex -eq 0) {
        @('rank', 'info')
    }
    else { @('rank', 'info', 'callers') }
    Assert-Sequence @($policyFamilies | ForEach-Object verb) $expectedFamilies "Task '$taskId' strict hook verbs"
    Assert-Sequence @(
        Get-AgentEvalTaskExpectedOperations $task | Sort-Object -CaseSensitive
    ) @('info', 'rank') "Task '$taskId' required strict CLI operations"
    if (@($task.steps).Count -ne ($lastStep + 1) -or
        [int]$input.baselineCalls -ne ($lastStep + 1) -or
        [int]$input.baselineCalls -ne @($task.steps).Count -or
        [int]$input.baselineCalls -ne [int]$baselines.tasks.$taskId.calls -or
        [int]$input.baselineTokens -ne [int]$baselines.tasks.$taskId.tokens -or
        [int]$input.baselineTokens -le 0 -or [int]$input.baselineCalls -lt 1 -or
        [int]$input.baselineCalls -gt 6) {
        throw "EP1 v3 task '$taskId' baseline does not match its measured deterministic path."
    }
    if ($taskIndex -eq 0) {
        if (@($task.steps | Where-Object { $_.PSObject.Properties.Name -ccontains 'optional' }).Count -ne 0) {
            throw 'ETW rank and info are both required canonical steps.'
        }
        Assert-Sequence @($task.steps[0].args) @(
            'rank', '{fixture}', '--metric', 'cpu', '--process', 'HotLoopBench',
            '--children', 'include', '--top', '3') 'ETW rank command'
        Assert-Sequence @($task.steps[1].args) @(
            'info', '{fixture}', '--process', 'HotLoopBench',
            '--children', 'include') 'ETW scoped info command'
        Assert-Members $input.expectedEvidence @(
            'operation', 'metric', 'measure', 'process', 'includeChildren',
            'topFrame', 'frameResolutionPercent', 'contributingRecords',
            'recommendedMinimumRecords', 'infoSampleCount',
            'infoSymbolResolutionRate', 'nextOperation', 'nextPathSuffix',
            'nextProcess', 'nextIncludeChildren', 'forbiddenDrill') 'ETW quality-first evidence'
        if ($task.os -cne 'windows' -or $input.expectedEvidence.operation -cne 'rank' -or
            $input.expectedEvidence.metric -cne 'cpu' -or
            $input.expectedEvidence.measure -cne 'self' -or
            $input.expectedEvidence.process -cne 'HotLoopBench' -or
            $input.expectedEvidence.includeChildren -ne $true -or
            $input.expectedEvidence.topFrame -cne '?' -or
            $input.expectedEvidence.frameResolutionPercent -ne 0 -or
            $input.expectedEvidence.contributingRecords -ne 51 -or
            $input.expectedEvidence.recommendedMinimumRecords -ne 200 -or
            $input.expectedEvidence.infoSampleCount -ne 51 -or
            $input.expectedEvidence.infoSymbolResolutionRate -ne 0 -or
            $input.expectedEvidence.nextOperation -cne 'info' -or
            $input.expectedEvidence.nextPathSuffix -cne '\etw.etl' -or
            $input.expectedEvidence.nextProcess -cne 'HotLoopBench' -or
            $input.expectedEvidence.nextIncludeChildren -ne $true -or
            $input.expectedEvidence.forbiddenDrill -cne 'callers ?' -or
            $task.steps[0].args -cnotcontains '--process' -or
            $task.steps[0].args -cnotcontains 'HotLoopBench' -or
            (Get-Assertion $task 'result.rows[0].frame') -cne '?' -or
            (Get-Assertion $task 'result.rows[0].weight') -ne 51 -or
            (Get-Assertion $task 'result.contributingRecordCount') -ne 51 -or
            (Get-Assertion $task 'context.scope.processMode') -cne 'name' -or
            (Get-Assertion $task 'context.scope.process') -cne 'HotLoopBench' -or
            (Get-Assertion $task 'context.scope.rootProcessIds[0]') -ne 40356 -or
            (Get-Assertion $task 'context.scope.descendantProcessIds[0]') -ne 48348 -or
            (Get-Assertion $task 'context.scope.includeChildren') -ne $true -or
            (Get-Assertion $task 'warnings[2].data.resolutionPercent') -ne 0 -or
            (Get-Assertion $task 'warnings[3].data.contributingRecords') -ne 51 -or
            (Get-Assertion $task 'hints[0].operation') -cne 'info' -or
            (Get-Assertion $task 'hints[0].arguments.path' 'endsWith') -cne
                $input.expectedEvidence.nextPathSuffix -or
            (Get-Assertion $task 'hints[0].arguments.process') -cne 'HotLoopBench' -or
            (Get-Assertion $task 'hints[0].arguments.includeChildren') -ne $true -or
            (Get-Assertion $task 'context.operation' -Step 1) -cne 'info' -or
            (Get-Assertion $task 'result.path' -Kind endsWith -Step 1) -cne '\etw.etl' -or
            (Get-Assertion $task 'result.format' -Step 1) -cne 'Etl' -or
            (Get-Assertion $task 'result.sampleCount' -Step 1) -ne 51 -or
            (Get-Assertion $task 'result.symbolResolutionRate' -Step 1) -ne 0 -or
            (Get-Assertion $task 'result.cpuSampling.source' -Step 1) -cne 'etw-perfinfo' -or
            (Get-Assertion $task 'warnings[1].code' -Step 1) -cne 'scope_applied' -or
            @($task.assert | Where-Object {
                    $_.step -eq 0 -and $_.PSObject.Properties.Name -ccontains 'hintContains' -and
                    [string]::Equals(
                        [string]$_.hintContains,
                        "info <trace> --process 'HotLoopBench'",
                        [StringComparison]::Ordinal)
                }).Count -ne 1 -or
            @($task.assert | Where-Object {
                    $_.step -eq 1 -and $_.PSObject.Properties.Name -ccontains 'jsonContains' -and
                    [string]::Equals(
                        [string]$_.jsonContains,
                        "Scoped to the 'HotLoopBench' process tree",
                        [StringComparison]::Ordinal)
                }).Count -ne 1 -or
            $qa.forbidOperations -cnotcontains 'callers') {
            throw 'EP1 v3 ETW evidence must keep the unresolved row, denominator, and scoped quality-first next step.'
        }
    }
    else {
        if (@($task.steps | Select-Object -First 2 | Where-Object {
                    $_.PSObject.Properties.Name -ccontains 'optional'
                }).Count -ne 0) {
            throw 'Mixed rank and info may not be marked optional.'
        }
        Assert-Members $task.steps[2] @('args', 'optional') 'Mixed optional caller step'
        if ($task.steps[2].optional -isnot [bool] -or -not $task.steps[2].optional) {
            throw 'The resolved mixed caller drill must be explicitly optional.'
        }
        Assert-Sequence @($task.steps[0].args) @(
            'rank', '{fixture}', '--metric', 'cpu',
            '--children', 'include', '--top', '3') 'Mixed rank command'
        Assert-Sequence @($task.steps[1].args) @(
            'info', '{fixture}', '--children', 'include') 'Mixed info command'
        Assert-Sequence @($task.steps[2].args) @(
            'callers', '{fixture}', 'App.Work', '--children', 'include') 'Mixed optional resolved caller'
        Assert-Sequence @($qa.forbidFrames) @('?') 'Mixed unresolved caller attempts'
        Assert-Members $input.expectedEvidence @(
            'operation', 'metric', 'measure', 'topFrame', 'resolvedFrame',
            'otherResolvedFrame', 'unresolvedRows', 'resolvedRows', 'positiveIntervals',
            'contributingRecords', 'unresolvedWeightMs', 'resolvedWorkWeightMs',
            'otherResolvedWeightMs', 'totalWeightMs', 'unknownSharePercent',
            'resolvedWorkSharePercent', 'resolvedCallerFrame', 'resolvedCallerWeightMs',
            'unknownWeightNotExplainedByResolvedCallerMs', 'aggregateSampleCount',
            'aggregateSymbolResolutionRate', 'aggregateSymbolResolutionRateIgnored',
            'cpuSamplingSource', 'nextOperation', 'nextPathSuffix',
            'nextIncludeChildren', 'optionalResolvedOperation', 'optionalResolvedFrame',
            'permittedResolvedDrill') 'Mixed speedscope row evidence'
        if ($task.os -cne 'any' -or $input.expectedEvidence.operation -cne 'rank' -or
            $input.expectedEvidence.metric -cne 'cpu' -or
            $input.expectedEvidence.measure -cne 'self' -or
            $input.expectedEvidence.topFrame -cne '?' -or
            $input.expectedEvidence.resolvedFrame -cne 'App.Work' -or
            $input.expectedEvidence.otherResolvedFrame -cne 'App.Other' -or
            $input.expectedEvidence.unresolvedRows -ne 1 -or
            $input.expectedEvidence.resolvedRows -ne 2 -or
            $input.expectedEvidence.positiveIntervals -ne 3 -or
            $input.expectedEvidence.contributingRecords -ne 3 -or
            $input.expectedEvidence.unresolvedWeightMs -ne 60 -or
            $input.expectedEvidence.resolvedWorkWeightMs -ne 25 -or
            $input.expectedEvidence.otherResolvedWeightMs -ne 15 -or
            $input.expectedEvidence.totalWeightMs -ne 100 -or
            $input.expectedEvidence.unknownSharePercent -ne 60 -or
            $input.expectedEvidence.resolvedWorkSharePercent -ne 25 -or
            $input.expectedEvidence.resolvedCallerFrame -cne 'App.Main' -or
            $input.expectedEvidence.resolvedCallerWeightMs -ne 25 -or
            $input.expectedEvidence.unknownWeightNotExplainedByResolvedCallerMs -ne 60 -or
            $input.expectedEvidence.aggregateSampleCount -ne 3 -or
            $input.expectedEvidence.aggregateSymbolResolutionRate -ne 1 -or
            $input.expectedEvidence.aggregateSymbolResolutionRateIgnored -ne $true -or
            $input.expectedEvidence.cpuSamplingSource -cne 'speedscope-profile-declared-time-weights' -or
            $input.expectedEvidence.nextOperation -cne 'info' -or
            $input.expectedEvidence.nextPathSuffix -cne 'mixed-unknown.speedscope.json' -or
            $input.expectedEvidence.nextIncludeChildren -ne $true -or
            $input.expectedEvidence.optionalResolvedOperation -cne 'callers' -or
            $input.expectedEvidence.optionalResolvedFrame -cne 'App.Work' -or
            $input.expectedEvidence.permittedResolvedDrill -cne 'callers-only-for-a-resolved-row' -or
            (Get-Assertion $task 'result.rows[0].frame') -cne '?' -or
            (Get-Assertion $task 'result.rows[0].weight') -ne 60 -or
            (Get-Assertion $task 'result.rows[0].percentOfScope') -ne 60 -or
            (Get-Assertion $task 'result.rows[1].frame') -cne $input.expectedEvidence.resolvedFrame -or
            (Get-Assertion $task 'result.rows[1].weight') -ne 25 -or
            (Get-Assertion $task 'result.rows[1].percentOfScope') -ne 25 -or
            (Get-Assertion $task 'result.rows[2].frame') -cne $input.expectedEvidence.otherResolvedFrame -or
            (Get-Assertion $task 'result.rows[2].weight') -ne 15 -or
            (Get-Assertion $task 'result.rows[2].percentOfScope') -ne 15 -or
            (Get-Assertion $task 'result.scopeWeight') -ne 100 -or
            (Get-Assertion $task 'context.unit') -cne 'ms' -or
            (Get-Assertion $task 'result.contributingRecordCount') -ne 3 -or
            (Get-Assertion $task 'hints[0].operation') -cne 'info' -or
            (Get-Assertion $task 'hints[0].arguments.path' 'endsWith') -cne
                $input.expectedEvidence.nextPathSuffix -or
            (Get-Assertion $task 'hints[0].arguments.includeChildren') -ne $true -or
            (Get-Assertion $task 'hints[1].operation') -cne 'callers' -or
            (Get-Assertion $task 'hints[1].arguments.frame') -cne 'App.Work' -or
            (Get-Assertion $task 'hints[1].arguments.metric') -cne 'cpu' -or
            (Get-Assertion $task 'context.operation' -Step 1) -cne 'info' -or
            (Get-Assertion $task 'result.path' -Kind endsWith -Step 1) -cne
                'mixed-unknown.speedscope.json' -or
            (Get-Assertion $task 'result.format' -Step 1) -cne 'Speedscope' -or
            (Get-Assertion $task 'result.sampleCount' -Step 1) -ne 3 -or
            (Get-Assertion $task 'result.symbolResolutionRate' -Step 1) -ne 1 -or
            (Get-Assertion $task 'result.cpuSampling.weightUnit' -Step 1) -cne 'ms' -or
            (Get-Assertion $task 'result.cpuSampling.source' -Step 1) -cne
                'speedscope-profile-declared-time-weights' -or
            (Get-Assertion $task 'context.operation' -Step 2) -cne 'callers' -or
            (Get-Assertion $task 'result.focus' -Step 2) -cne 'App.Work' -or
            (Get-Assertion $task 'result.targetWeight' -Step 2) -ne 25 -or
            (Get-Assertion $task 'result.percentOfScope' -Step 2) -ne 25 -or
            (Get-Assertion $task 'result.scopeWeight' -Step 2) -ne 100 -or
            (Get-Assertion $task 'result.callers[0].caller' -Step 2) -cne 'App.Main' -or
            (Get-Assertion $task 'result.callers[0].weight' -Step 2) -ne 25 -or
            (Get-Assertion $task 'result.callers[0].percentOfTarget' -Step 2) -ne 100 -or
            (Get-Assertion $task 'result.contributingRecordCount' -Step 2) -ne 1 -or
            $qa.forbidOperations -cnotcontains 'source' -or
            @($task.assert | Where-Object {
                    $_.step -eq 0 -and $_.PSObject.Properties.Name -ccontains 'hintContains' -and
                    [string]::Equals([string]$_.hintContains, 'info <trace>', [StringComparison]::Ordinal)
                }).Count -ne 1 -or
            @($task.assert | Where-Object {
                    $_.step -eq 0 -and $_.PSObject.Properties.Name -ccontains 'hintContains' -and
                    [string]::Equals([string]$_.hintContains, "callers 'App.Work'", [StringComparison]::Ordinal)
                }).Count -ne 1 -or
            @($task.assert | Where-Object {
                    $_.step -eq 0 -and $_.PSObject.Properties.Name -ccontains 'field' -and
                    $_.field -match 'symbolResolutionRate'
                }).Count -ne 0) {
            throw 'EP1 v3 mixed speedscope evidence must grade unresolved and resolved rows, not aggregate resolution metadata.'
        }
    }
}

$unknownCaller = [pscustomobject]@{
    verb = 'callers'; isHelp = $false
    argv = @('callers', '<TRACE>', '?', '--children', 'include', '--format', 'json')
}
$reorderedUnknownCaller = [pscustomobject]@{
    verb = 'callers'; isHelp = $false
    argv = @('callers', '<TRACE>', '--children', 'include', '?', '--format', 'json')
}
$resolvedCaller = [pscustomobject]@{
    verb = 'callers'; isHelp = $false
    argv = @('callers', '<TRACE>', 'App.Work', '--children', 'include', '--format', 'json')
}
$forbiddenFrameRule = [pscustomobject]@{ forbidFrames = @('?') }
if (-not (Test-AgentEvalUnresolvedCallerAttempt $unknownCaller) -or
    -not (Test-AgentEvalUnresolvedCallerAttempt $reorderedUnknownCaller) -or
    (Test-AgentEvalUnresolvedCallerAttempt $resolvedCaller) -or
    -not (Test-AgentEvalForbiddenFrameAttempt $forbiddenFrameRule ([pscustomobject]@{
                attemptedUnresolvedCaller = $true
            })) -or
    (Test-AgentEvalForbiddenFrameAttempt $forbiddenFrameRule ([pscustomobject]@{
                attemptedUnresolvedCaller = $false
            }))) {
    throw 'EP1 v3 must mark attempted callers ? as a quality failure without penalizing App.Work.'
}

Assert-Members $protocol.sample @(
    'validPairTarget', 'maximumPairs', 'maximumSessions', 'replacementPairs',
    'orderAlgorithm', 'orderSeed', 'taskSchedule', 'pairs', 'invalidSessionPolicy') 'EP1 v3 sample'
if ($protocol.sample.validPairTarget -ne 8 -or $protocol.sample.maximumPairs -ne 8 -or
    $protocol.sample.maximumSessions -ne 16 -or $protocol.sample.replacementPairs -ne 0 -or
    $protocol.sample.orderAlgorithm -cne 'sha256-parity-two-balanced-blocks-per-task-v1' -or
    $protocol.sample.orderSeed -cne 'b3b82538d6de28adf6dece625e8d5402' -or
    $protocol.sample.taskSchedule -cne 'round-robin-pair-order-across-two-tasks' -or
    $protocol.sample.invalidSessionPolicy -cne $v2.sample.invalidSessionPolicy -or
    @($protocol.sample.pairs).Count -ne 8) {
    throw 'EP1 v3 must retain eight scheduled pairs, zero replacements, and sixteen sessions.'
}
$firstCounts = @{'quality-first-unresolved' = 0; 'mixed-unknown-resolved' = 0}
for ($index = 0; $index -lt 8; $index++) {
    $pair = $protocol.sample.pairs[$index]
    [string] $taskId = $expectedTasks[$index % 2]
    [int] $withinTask = [int][Math]::Floor($index / 2)
    [int] $block = [int][Math]::Floor($withinTask / 2)
    [byte[]] $digest = [Security.Cryptography.SHA256]::HashData(
        [Text.UTF8Encoding]::new($false).GetBytes("$($protocol.sample.orderSeed):${taskId}:$block"))
    [string] $first = if ((($digest[0] -band 1) -eq 0) -eq (($withinTask % 2) -eq 0)) {
        'cli-skill'
    }
    else { 'cli' }
    [string] $second = if ($first -ceq 'cli-skill') { 'cli' } else { 'cli-skill' }
    Assert-Members $pair @('pair', 'taskId', 'first', 'second') "EP1 v3 pair $($index + 1)"
    if ($pair.pair -ne ($index + 1) -or $pair.taskId -cne $taskId -or
        $pair.first -cne $first -or $pair.second -cne $second) {
        throw "EP1 v3 pair $($index + 1) does not match its task-balanced hashed order."
    }
    if ($first -ceq 'cli-skill') { $firstCounts[$taskId]++ }
}
if ($firstCounts['quality-first-unresolved'] -ne 2 -or
    $firstCounts['mixed-unknown-resolved'] -ne 2) {
    throw 'Each EP1 v3 task must have two skill-first and two CLI-first pairs.'
}

Assert-Members $protocol.execution @(
    'runnerPath', 'commandTemplate', 'labelTemplate', 'outputDirectoryPolicy',
    'preSessionChecks', 'postSessionChecks') 'EP1 v3 execution'
if ($protocol.execution.runnerPath -cne 'eval/Invoke-AgentEval.ps1' -or
    $protocol.execution.labelTemplate -cne 'ep1-v3-{task}-p{pair}-{position}-{arm}' -or
    $protocol.execution.commandTemplate -notmatch '-Tasks <task-id>' -or
    $protocol.execution.commandTemplate -notmatch '-Model <private-gpt-6-sol-id> -ExpectedModel <private-gpt-6-sol-id>' -or
    $protocol.execution.commandTemplate -notmatch '-AuthorizationSha256 <private-auth-sha256>' -or
    $protocol.execution.commandTemplate -notmatch '-Label ep1-v3-<task-id>-p<pair>-<position>-<arm>' -or
    $protocol.execution.commandTemplate -notmatch '-NativeTimeoutSeconds 600' -or
    $protocol.execution.commandTemplate -notmatch '-MaxSteps 6' -or
    $protocol.execution.commandTemplate -notmatch '-NoHostAiCreditLimit' -or
    $protocol.execution.commandTemplate -match 'MaxAiCredits|SkillEntrypointPath' -or
    $protocol.execution.preSessionChecks -cnotcontains
        'updated shipped Filtrace skill entrypoint and complete manifest match the pinned protocol' -or
    @($protocol.execution.preSessionChecks).Count -lt 6 -or
    @($protocol.execution.postSessionChecks).Count -lt 6) {
    throw 'EP1 v3 runner must pin the same CLI/model/steps and explicitly remove only the host credit ceiling.'
}
Assert-Members $protocol.bounds @(
    'nativeTimeoutSecondsPerSession', 'maxPowerShellInitialWaitSeconds',
    'maxHostOutputBytesPerSession', 'maxHostArtifactBytesPerSession',
    'maxProjectedRetainedBytesPerInvocation', 'maxArtifactFiles',
    'maxArtifactRecordBytes', 'maxArtifactSetBytes', 'maxHostRuntimeBytesPerSession',
    'maxHostRuntimeFileBytes', 'maxHostRuntimeEntries', 'maxFixtureBytes',
    'maxCliFiles', 'maxCliEntries', 'maxCliBytes', 'maxSkillFiles',
    'maxSkillEntries', 'maxSkillBytes', 'maxSkillViewCalls',
    'maxSkillRequestedBytes', 'hostAiCreditLimit', 'maximumHostSessions',
    'stopOnInvalidSession') 'EP1 v3 bounds'
foreach ($property in $v2.bounds.PSObject.Properties) {
    if ($property.Name -in @(
            'maximumHostSessions', 'maximumHostAiCredits', 'maxAiCreditsPerSession',
            'maxArtifactFiles')) { continue }
    if ($protocol.bounds.($property.Name) -ne $property.Value) {
        throw "EP1 v3 non-credit bound '$($property.Name)' has drifted from v2."
    }
}
if ($protocol.bounds.maximumHostSessions -ne 16 -or
    $protocol.bounds.maxArtifactFiles -ne 48 -or
    $protocol.bounds.hostAiCreditLimit -cne 'none') {
    throw 'EP1 v3 must permit 43 bounded files, sixteen sessions, and observed uncapped host credit usage.'
}

Assert-Members $protocol.validity @(
    'machineRequirements', 'criterionIds', 'taskRubrics', 'sessionValidWhen',
    'pairValidWhen', 'attemptedForbiddenDrill', 'experimentIncompleteWhen') 'EP1 v3 validity'
Assert-Sequence @($protocol.validity.criterionIds) @(
    'scope', 'attribution-restraint', 'evidence-quality',
    'scope-preserving-drill', 'unsupported-claim') 'EP1 v3 criterion IDs'
if (@($protocol.validity.machineRequirements).Count -lt 7 -or
    @($protocol.validity.taskRubrics).Count -ne 2 -or
    $protocol.validity.sessionValidWhen -cne $v2.validity.sessionValidWhen -or
    $protocol.validity.pairValidWhen -cne $v2.validity.pairValidWhen -or
    $protocol.validity.attemptedForbiddenDrill -cne
        'callers-question-mark-attempt-is-a-runner-quality-failure-not-an-evidence-integrity-failure;blind-grade-uses-final-answer-only') {
    throw 'EP1 v3 must grade quality separately from machine evidence in each task.'
}
for ($index = 0; $index -lt 2; $index++) {
    $rubric = $protocol.validity.taskRubrics[$index]
    Assert-Members $rubric @(
        'taskId', 'criteria', 'falseConfidenceCriteria') "EP1 v3 task rubric $index"
    Assert-Members $rubric.criteria @(
        'scope', 'attribution-restraint', 'evidence-quality',
        'scope-preserving-drill', 'unsupported-claim') "EP1 v3 task criteria $index"
    if ($rubric.taskId -cne $expectedTasks[$index] -or
        @($rubric.falseConfidenceCriteria).Count -lt 3 -or
        @($rubric.criteria.PSObject.Properties.Value | Where-Object {
                [string]::IsNullOrWhiteSpace([string]$_)
            }).Count -ne 0) {
        throw 'EP1 v3 task-specific blinded rubric is incomplete.'
    }
    if ($index -eq 0 -and
        ($rubric.criteria.scope -notmatch 'HotLoopBench' -or
            $rubric.criteria.'attribution-restraint' -notmatch '\?' -or
            $rubric.criteria.'evidence-quality' -notmatch '51.*200' -or
            $rubric.criteria.'scope-preserving-drill' -notmatch "callers '\?'")) {
        throw 'EP1 v3 ETW rubric no longer grades scoped quality before an unresolved drill.'
    }
    if ($index -eq 1 -and
        ($rubric.criteria.'attribution-restraint' -notmatch 'unknown.*denominator' -or
            $rubric.criteria.'evidence-quality' -notmatch 'symbolResolutionRate.*1\.0' -or
            $rubric.criteria.'scope-preserving-drill' -notmatch "resolved.*App\.Main.*25ms.*unknown 60ms.*callers '\?'" -or
            @($rubric.falseConfidenceCriteria | Where-Object {
                    $_ -match 'unknown 60ms.*App\.Work.*App\.Main'
                }).Count -ne 1)) {
        throw 'EP1 v3 speedscope rubric no longer grades mixed rows instead of aggregate quality.'
    }
}
Assert-Members $protocol.adjudication @(
    'timing', 'blindId', 'randomization', 'packetConstruction', 'packet',
    'visibleTaskId', 'hiddenFields', 'grader', 'rubric', 'freeze',
    'unblinding', 'qualityFailure', 'integrityFailure', 'limitation') 'EP1 v3 adjudication'
foreach ($property in $v2.adjudication.PSObject.Properties) {
    if ($protocol.adjudication.($property.Name) -cne $property.Value -and
        $property.Name -notin @('hiddenFields', 'packet')) {
        throw "EP1 v3 arm-masked adjudication field '$($property.Name)' has drifted."
    }
}
Assert-Sequence @($protocol.adjudication.hiddenFields) @($v2.adjudication.hiddenFields) 'EP1 v3 blinded hidden fields'
if ($protocol.adjudication.visibleTaskId -cne 'task-id-only-for-task-specific-rubric-never-arm-or-position' -or
    $protocol.adjudication.packet -cne $v2.adjudication.packet) {
    throw 'EP1 v3 blinded packets may expose the task only, not arm, order, cost, or files.'
}

Assert-Members $protocol.records @(
    'schemaPath', 'schemaSha256', 'validatorPath', 'recordHashPolicy',
    'validationPhases', 'recordTypes', 'semanticChecks', 'validation') 'EP1 v3 records'
if ($protocol.records.schemaPath -cne 'eval/protocols/ep1-ready-capture-record-v3.schema.json' -or
    $protocol.records.schemaSha256 -cne (Get-FileTextHash $schemaPath) -or
    $protocol.records.validatorPath -cne 'eval/Test-ReadyCaptureRecords.ps1' -or
    $protocol.records.recordHashPolicy -cne $v2.records.recordHashPolicy -or
    @($protocol.records.semanticChecks).Count -lt 12) {
    throw 'EP1 v3 record schema, exact hash, or integrity checks have drifted.'
}
Assert-Sequence @($protocol.records.recordTypes) @($v2.records.recordTypes) 'EP1 v3 record types'
Assert-JsonEqual $protocol.records.validationPhases $v2.records.validationPhases 'Record validation phases'

Assert-Members $protocol.measurement @(
    'primaryDimensions', 'observationalDimensions', 'clock',
    'practicalEquivalence', 'classification') 'EP1 v3 measurement'
Assert-JsonEqual $protocol.measurement.primaryDimensions $v2.measurement.primaryDimensions 'Primary metrics'
Assert-JsonEqual $protocol.measurement.observationalDimensions $v2.measurement.observationalDimensions 'Observed metrics'
Assert-JsonEqual $protocol.measurement.clock $v2.measurement.clock 'Measurement clock'
Assert-JsonEqual $protocol.measurement.practicalEquivalence $v2.measurement.practicalEquivalence 'Practical equivalence'
if ($protocol.measurement.classification.overall -cne $v2.measurement.classification.overall -or
    $protocol.measurement.classification.deltaOrientation -cne 'cli-skill-minus-cli' -or
    $protocol.measurement.classification.qualityComparison -cne
        'per-pair-compare-eight-good-state-bits-answer-available-plus-five-criterion-passes-plus-not-false-confidence-plus-attested-runner-success;report-each-task-and-overall-descriptively-with-order-sensitivity') {
    throw 'EP1 v3 comparisons must include attested runner success alongside blinded quality.'
}
Assert-Members $protocol.dispositions @(
    'session', 'pair', 'terminal', 'blockerReproducibility', 'routing') 'EP1 v3 dispositions'
Assert-Members $protocol.dispositions.session @(
    'validQualityPass', 'validQualityFail', 'invalidEvidence') 'EP1 v3 session dispositions'
if ($protocol.dispositions.session.validQualityPass -cne
        'machine-evidence-valid-and-runner-success-and-all-five-criteria-pass-without-false-confidence' -or
    $protocol.dispositions.session.validQualityFail -cne
        'machine-evidence-valid-and-runner-failure-or-no-final-answer-or-any-criterion-fails-or-false-confidence-triggers' -or
    $protocol.dispositions.session.invalidEvidence -cne $v2.dispositions.session.invalidEvidence) {
    throw 'EP1 v3 runner success changes quality, not evidence validity or blinded grades.'
}
Assert-JsonEqual $protocol.dispositions.pair $v2.dispositions.pair 'Pair dispositions'
if ($protocol.dispositions.terminal.descriptiveComplete -cne 'eight-valid-graded-pairs-retained' -or
    $protocol.dispositions.terminal.incompleteBudget -match 'credit' -or
    $protocol.dispositions.routing -cne $v2.dispositions.routing) {
    throw 'EP1 v3 must stop after eight valid graded pairs or the first non-credit failure.'
}
Assert-Members $protocol.reporting @(
    'mode', 'requiredSessionFields', 'sourceSeparation', 'pairDeltaOrientation',
    'aggregates', 'orderEffect', 'missingData', 'completion', 'nextAction') 'EP1 v3 reporting'
if ($protocol.reporting.mode -cne $v2.reporting.mode -or
    $protocol.reporting.sourceSeparation -cne $v2.reporting.sourceSeparation -or
    $protocol.reporting.pairDeltaOrientation -cne $v2.reporting.pairDeltaOrientation -or
    $protocol.reporting.nextAction -cne $v2.reporting.nextAction -or
    $protocol.reporting.requiredSessionFields -cnotcontains 'taskId' -or
    $protocol.reporting.aggregates -cnotcontains 'per-task-arm-quality-cost-and-order-summaries' -or
    $protocol.reporting.completion -cne
        'descriptive-complete-only-after-eight-valid-graded-pairs-four-per-task-including-quality-failures') {
    throw 'EP1 v3 report must recompute per-task summaries without a winner claim.'
}
Assert-Members $protocol.freeze @(
    'validator', 'protocolHashPolicy', 'mergeTreePolicy', 'executionPrecondition') 'EP1 v3 freeze'
if ($protocol.freeze.validator -cne 'tools/Test-Docs.ps1' -or
    $protocol.freeze.protocolHashPolicy -cne
        'record-lf-normalized-utf8-no-bom-sha256-after-final-review-in-private-checkpoint' -or
    $protocol.freeze.executionPrecondition -cne
        'exact-v3-protocol-hash-and-private-GPT-6-Sol-model-identity-match-after-separate-authorization') {
    throw 'EP1 v3 remains unlaunched until exact hash, model, and authorization are checkpointed.'
}

& (Join-Path $root $protocol.records.validatorPath) `
    -ProtocolPath $protocolPath -SchemaPath $schemaPath -SelfTest
Write-Host 'EP1 v3 stopped protocol, public inputs, schema, and fake artifacts validated.'
