#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ProtocolPath,
    [Parameter(Mandatory)][string] $SchemaPath,
    [string] $ArtifactDirectory,
    [string] $HostExecutablePath,
    [ValidateSet('PreUnblind', 'Final')][string] $Phase = 'Final',
    [switch] $RequireComplete,
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[string] $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'CopilotEval.Helpers.ps1')

function Get-RawHash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CanonicalTextHash([string] $Path) {
    [string] $text = [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n").Replace("`r", "`n")
    [byte[]] $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-TextHash([string] $Text) {
    [byte[]] $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Test-ReadyCaptureV3($Protocol) {
    return [string]::Equals([string]$Protocol.protocolId, 'ep1-ready-capture-v3', [StringComparison]::Ordinal)
}

function Get-ReadyCaptureInputs($Protocol) {
    if (Test-ReadyCaptureV3 $Protocol) { return @($Protocol.inputs.tasks) }
    return @($Protocol.inputs)
}

function Get-ReadyCaptureInput($Protocol, [string] $TaskId) {
    [object[]] $matches = @(Get-ReadyCaptureInputs $Protocol | Where-Object {
            [string]::Equals([string]$_.taskId, $TaskId, [StringComparison]::Ordinal)
        })
    if ($matches.Count -ne 1) { throw "Ready-capture task '$TaskId' is missing or repeated." }
    return $matches[0]
}

function Get-ReadyCapturePairTaskId($Protocol, [int] $Pair) {
    if ($Pair -lt 1 -or $Pair -gt @($Protocol.sample.pairs).Count) {
        throw "Ready-capture pair '$Pair' is outside the frozen schedule."
    }
    if (Test-ReadyCaptureV3 $Protocol) { return [string]$Protocol.sample.pairs[$Pair - 1].taskId }
    return [string]$Protocol.inputs.taskId
}

function Get-ReadyCaptureLabel($Protocol, [int] $Pair, [string] $Position, [string] $Arm) {
    if (Test-ReadyCaptureV3 $Protocol) {
        [string] $taskId = Get-ReadyCapturePairTaskId $Protocol $Pair
        return "ep1-v3-$taskId-p$Pair-$Position-$Arm"
    }
    return "ep1-rc-p$Pair-$Position-$Arm"
}

function Get-ManifestHash([object[]] $Entries) {
    [string[]] $canonical = @($Entries | ForEach-Object {
            [string] $path = if ($_.PSObject.Properties.Name -ccontains 'relativePath') {
            [string]$_.relativePath
            }
            else { [string]$_.path }
            [string]([ordered]@{
                path = $path
                    bytes = [long]$_.bytes
                    sha256 = [string]$_.sha256
                } | ConvertTo-Json -Compress)
        })
    [Array]::Sort($canonical, [StringComparer]::Ordinal)
    return Get-TextHash (ConvertTo-Json -InputObject $canonical -Compress)
}

function Get-CanonicalSkillManifest([string] $Root) {
    $inventory = Get-AgentEvalSkillInput -Root $Root
    [string[]] $entries = @($inventory.files | ForEach-Object {
            [string] $text = [System.IO.File]::ReadAllText($_.sourcePath).Replace("`r`n", "`n").Replace("`r", "`n")
            [byte[]] $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
            [string]([ordered]@{
                    path = ([string]$_.relativePath).Replace('\', '/')
                    bytes = [long]$bytes.Length
                    sha256 = Get-TextHash $text
                } | ConvertTo-Json -Compress)
        })
    [Array]::Sort($entries, [StringComparer]::Ordinal)
    return [pscustomobject]@{
        sha256 = Get-TextHash (ConvertTo-Json -InputObject $entries -Compress)
        fileCount = @($inventory.files).Count
    }
}

function Get-EvaluatorClosureHash($Protocol, [string] $ProtocolFile) {
    [string] $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetDirectoryName($ProtocolFile)) '../..'))
    [string[]] $taskPaths = @(Get-ReadyCaptureInputs $Protocol | ForEach-Object { [string]$_.taskPath })
    [string[]] $relativePaths = @(
           [System.IO.Path]::GetRelativePath($repositoryRoot, $ProtocolFile).Replace('\', '/')
        [string]$Protocol.records.schemaPath
        [string]$Protocol.records.validatorPath
        'eval/Invoke-AgentEval.ps1'
        'eval/Compare-EvalRuns.ps1'
        'eval/CopilotEval.Helpers.ps1'
        'eval/CopilotEval.PolicyHook.ps1'
        'eval/Get-OperationName.ps1'
        'tools/Get-TokenEstimate.ps1'
        'src/Filtrace/Cli/TraceCommands.cs'
        [string]$Protocol.inputs.qaPath) + $taskPaths
    if (Test-ReadyCaptureV3 $Protocol) {
        $relativePaths += @(
            'tools/Test-Docs.ps1',
            'eval/Test-ReadyCaptureProtocolV3.ps1',
            'eval/Invoke-Eval.ps1')
    }
    [string[]] $entries = @($relativePaths | Sort-Object -CaseSensitive -Unique | ForEach-Object {
            [string] $relativePath = $_
            [string] $path = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativePath))
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Evaluator closure file '$relativePath' does not exist."
            }
            [string]([ordered]@{
                    path = $relativePath
                    sha256 = Get-CanonicalTextHash $path
                } | ConvertTo-Json -Compress)
        })
    return Get-TextHash (ConvertTo-Json -InputObject $entries -Compress)
}

function Read-Record([string] $Path) {
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
                    if (-not $names.Add($property.Name)) {
                        throw "Record '$Path' repeats member '$($property.Name)'."
                    }
                    $pending.Push($property.Value)
                }
            }
            elseif ($element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                foreach ($item in $element.EnumerateArray()) { $pending.Push($item) }
            }
        }
    }
    finally {
        $document.Dispose()
    }

    $value = $raw | ConvertFrom-Json -Depth 100
    return [pscustomobject]@{
        Path = $Path
        Hash = Get-RawHash $Path
        Value = $value
        RecordType = if ($value.PSObject.Properties.Name -ccontains 'recordType') {
            [string]$value.recordType
        }
        elseif ($value.PSObject.Properties.Name -ccontains 'schemaVersion' -and
            $value.schemaVersion -eq 3) {
            'session-result'
        }
        else {
            throw "Record '$Path' has no recognized record type."
        }
    }
}

function Assert-EqualSet([object[]] $Left, [object[]] $Right, [string] $Context) {
    [string[]] $leftValues = @($Left | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive -Unique)
    [string[]] $rightValues = @($Right | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive -Unique)
    if ($leftValues.Count -ne $rightValues.Count) { throw "$Context differs." }
    for ($index = 0; $index -lt $leftValues.Count; $index++) {
        if (-not [string]::Equals($leftValues[$index], $rightValues[$index], [StringComparison]::Ordinal)) {
            throw "$Context differs."
        }
    }
}

function Assert-Schema($Record, [string] $Schema) {
    [string] $json = $Record.Value | ConvertTo-Json -Depth 100 -Compress
    $schemaErrors = @()
    [bool] $valid = $json | Test-Json -SchemaFile $Schema -ErrorVariable schemaErrors `
        -ErrorAction SilentlyContinue -WarningAction SilentlyContinue
    if (-not $valid) {
        [string] $detail = @($schemaErrors | ForEach-Object { $_.Exception.Message }) -join ' '
        throw "Record '$($Record.Path)' does not match the ready-capture schema. $detail"
    }
}

function Test-JsonEqual($Left, $Right) {
    [Text.Json.Nodes.JsonNode] $leftNode = [Text.Json.Nodes.JsonNode]::Parse(
        ($Left | ConvertTo-Json -Depth 100 -Compress))
    [Text.Json.Nodes.JsonNode] $rightNode = [Text.Json.Nodes.JsonNode]::Parse(
        ($Right | ConvertTo-Json -Depth 100 -Compress))
    return [Text.Json.Nodes.JsonNode]::DeepEquals($leftNode, $rightNode)
}

function Get-Median([double[]] $Values) {
    [double[]] $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Cannot compute a median over no values.' }
    if (($sorted.Count % 2) -eq 1) { return $sorted[[int][Math]::Floor($sorted.Count / 2)] }
    return ($sorted[($sorted.Count / 2) - 1] + $sorted[$sorted.Count / 2]) / 2.0
}

function Assert-MetricSummary($Summary, [double[]] $Values, [string] $Context) {
    if ([double]$Summary.median -ne (Get-Median $Values) -or
        [double]$Summary.minimum -ne ($Values | Measure-Object -Minimum).Minimum -or
        [double]$Summary.maximum -ne ($Values | Measure-Object -Maximum).Maximum) {
        throw "$Context does not match retained session values."
    }
}

function New-MetricSummary([double[]] $Values) {
    return [ordered]@{
        median = Get-Median $Values
        minimum = ($Values | Measure-Object -Minimum).Minimum
        maximum = ($Values | Measure-Object -Maximum).Maximum
    }
}

function Test-QualityPass($Session) {
    [object[]] $criteria = if ($Session.answerCriteria -is [System.Collections.IDictionary]) {
        @($Session.answerCriteria.Values)
    }
    else {
        @($Session.answerCriteria.PSObject.Properties.Value)
    }
    return [bool]$Session.answerAvailable -and -not [bool]$Session.falseConfidence -and
        @($criteria | Where-Object { -not $_.pass }).Count -eq 0
}

function New-ArmSummary([object[]] $Sessions, [string] $Arm) {
    [object[]] $armSessions = @($Sessions | Where-Object arm -eq $Arm)
    return [ordered]@{
        arm = $Arm
        sessionCount = $armSessions.Count
        qualityPassCount = @($armSessions | Where-Object { Test-QualityPass $_ }).Count
        noAnswerCount = @($armSessions | Where-Object { -not $_.answerAvailable }).Count
        falseConfidenceCount = @($armSessions | Where-Object falseConfidence).Count
        metrics = [ordered]@{
            analysisCalls = New-MetricSummary @($armSessions | ForEach-Object { [double]$_.calls })
            helpCalls = New-MetricSummary @($armSessions | ForEach-Object { [double]$_.helpCalls })
            resultTokens = New-MetricSummary @($armSessions | ForEach-Object { [double]$_.tokens })
            wallMs = New-MetricSummary @($armSessions | ForEach-Object { [double]$_.wallMs })
            hostAiCredits = New-MetricSummary @($armSessions | ForEach-Object { [double]$_.hostAiCredits })
        }
    }
}

function Get-QualityBits($Session) {
    return @(
        [bool]$Session.answerAvailable,
        [bool]$Session.answerCriteria.scope.pass,
        [bool]$Session.answerCriteria.'attribution-restraint'.pass,
        [bool]$Session.answerCriteria.'evidence-quality'.pass,
        [bool]$Session.answerCriteria.'scope-preserving-drill'.pass,
        [bool]$Session.answerCriteria.'unsupported-claim'.pass,
        -not [bool]$Session.falseConfidence)
}

function Get-PairedQualityState($SkillSession, $CliSession) {
    [bool[]] $skill = Get-QualityBits $SkillSession
    [bool[]] $cli = Get-QualityBits $CliSession
    [bool] $skillNoWorse = $true
    [bool] $cliNoWorse = $true
    [bool] $skillBetter = $false
    [bool] $cliBetter = $false
    for ($index = 0; $index -lt $skill.Count; $index++) {
        if ($cli[$index] -and -not $skill[$index]) { $skillNoWorse = $false; $cliBetter = $true }
        if ($skill[$index] -and -not $cli[$index]) { $cliNoWorse = $false; $skillBetter = $true }
    }
    if ($skillNoWorse -and $cliNoWorse) { return 'same-observed-quality' }
    if ($skillNoWorse -and $skillBetter) { return 'skill-higher-observed-quality' }
    if ($cliNoWorse -and $cliBetter) { return 'skill-lower-observed-quality' }
    return 'mixed-observed-quality'
}

function Get-QualityAggregate([object[]] $Pairs) {
    [string[]] $states = @($Pairs | ForEach-Object { [string]$_.qualityState })
    if (@($states | Where-Object { $_ -eq 'same-observed-quality' }).Count -eq $states.Count) {
        return 'same-observed-quality'
    }
    if (@($states | Where-Object { $_ -notin @('same-observed-quality', 'skill-higher-observed-quality') }).Count -eq 0) {
        return 'skill-higher-observed-quality'
    }
    if (@($states | Where-Object { $_ -notin @('same-observed-quality', 'skill-lower-observed-quality') }).Count -eq 0) {
        return 'skill-lower-observed-quality'
    }
    return 'mixed-observed-quality'
}

function Get-CostState([object[]] $Pairs, [string] $Member, [double] $Equivalence, [bool] $Relative) {
    [string] $sessionMember = switch ($Member) {
        'analysisCalls' { 'calls' }
        'resultTokens' { 'tokens' }
        default { $Member }
    }
    [System.Collections.Generic.List[object]] $normalized = [System.Collections.Generic.List[object]]::new()
    foreach ($pairEntry in $Pairs) {
        [double] $delta = [double]$pairEntry.deltas.$Member
        [double] $cli = [double]$pairEntry.cli.$sessionMember
        if ($Relative) {
            if ($cli -eq 0) {
                if ($delta -eq 0) { [void]$normalized.Add([pscustomobject]@{ pair = $pairEntry; value = 0.0 }) }
                else { return 'unavailable' }
            }
            else { [void]$normalized.Add([pscustomobject]@{ pair = $pairEntry; value = $delta / $cli }) }
        }
        else { [void]$normalized.Add([pscustomobject]@{ pair = $pairEntry; value = $delta }) }
    }
    [double] $skillFirstMedian = Get-Median @($normalized | Where-Object { $_.pair.firstArm -eq 'cli-skill' } | ForEach-Object value)
    [double] $cliFirstMedian = Get-Median @($normalized | Where-Object { $_.pair.firstArm -eq 'cli' } | ForEach-Object value)
    if ([Math]::Abs($skillFirstMedian) -gt $Equivalence -and
        [Math]::Abs($cliFirstMedian) -gt $Equivalence -and
        [Math]::Sign($skillFirstMedian) -ne [Math]::Sign($cliFirstMedian)) {
        return 'order-sensitive'
    }
    [bool] $hasLower = @($normalized | Where-Object { $_.value -lt -$Equivalence }).Count -gt 0
    [bool] $hasHigher = @($normalized | Where-Object { $_.value -gt $Equivalence }).Count -gt 0
    if ($hasLower -and $hasHigher) { return 'mixed' }
    [double] $median = Get-Median @($normalized | ForEach-Object value)
    if ([Math]::Abs($median) -le $Equivalence) { return 'practically-equivalent' }
    if ($median -lt 0) { return 'skill-lower' }
    return 'skill-higher'
}

function Get-ReadyCaptureQualityState([object[]] $Pairs) {
    [string] $qualityState = Get-QualityAggregate -Pairs $Pairs
    [string] $skillFirst = Get-QualityAggregate -Pairs @($Pairs | Where-Object firstArm -eq 'cli-skill')
    [string] $cliFirst = Get-QualityAggregate -Pairs @($Pairs | Where-Object firstArm -eq 'cli')
    if (($skillFirst -eq 'skill-higher-observed-quality' -and $cliFirst -eq 'skill-lower-observed-quality') -or
        ($skillFirst -eq 'skill-lower-observed-quality' -and $cliFirst -eq 'skill-higher-observed-quality')) {
        return 'order-sensitive'
    }
    return $qualityState
}

function New-ReadyCaptureCostStates([object[]] $Pairs) {
    return [ordered]@{
        analysisCalls = Get-CostState -Pairs $Pairs -Member analysisCalls -Equivalence 0 -Relative $false
        helpCalls = Get-CostState -Pairs $Pairs -Member helpCalls -Equivalence 0 -Relative $false
        resultTokens = Get-CostState -Pairs $Pairs -Member resultTokens -Equivalence 0.05 -Relative $true
        wallMs = Get-CostState -Pairs $Pairs -Member wallMs -Equivalence 0.05 -Relative $true
        hostAiCredits = Get-CostState -Pairs $Pairs -Member hostAiCredits -Equivalence 0 -Relative $false
    }
}

function New-ReadyCaptureTaskSummary([string] $TaskId, [object[]] $Sessions, [object[]] $Pairs) {
    [object[]] $taskSessions = @($Sessions | Where-Object taskId -eq $TaskId)
    [object[]] $taskPairs = @($Pairs | Where-Object taskId -eq $TaskId)
    if ($taskSessions.Count -ne 8 -or $taskPairs.Count -ne 4) {
        throw "EP1 v3 task '$TaskId' does not contain four complete pairs."
    }
    [object[]] $pairedDeltas = @($taskPairs | ForEach-Object {
            [ordered]@{
                taskId = $TaskId
                pair = $_.pair
                firstArm = $_.firstArm
                qualityState = $_.qualityState
                deltas = $_.deltas
            }
        })
    [object[]] $orderSummaries = @(@('cli-skill', 'cli') | ForEach-Object {
            [string] $firstArm = $_
            [object[]] $group = @($taskPairs | Where-Object firstArm -eq $firstArm)
            [ordered]@{
                firstArm = $firstArm
                pairCount = $group.Count
                qualityState = Get-QualityAggregate -Pairs $group
                medianDeltas = [ordered]@{
                    analysisCalls = Get-Median @($group | ForEach-Object { [double]$_.deltas.analysisCalls })
                    helpCalls = Get-Median @($group | ForEach-Object { [double]$_.deltas.helpCalls })
                    resultTokens = Get-Median @($group | ForEach-Object { [double]$_.deltas.resultTokens })
                    wallMs = Get-Median @($group | ForEach-Object { [double]$_.deltas.wallMs })
                    hostAiCredits = Get-Median @($group | ForEach-Object { [double]$_.deltas.hostAiCredits })
                }
            }
        })
    return [ordered]@{
        taskId = $TaskId
        armSummaries = @(
            (New-ArmSummary -Sessions $taskSessions -Arm 'cli-skill'),
            (New-ArmSummary -Sessions $taskSessions -Arm 'cli'))
        pairedDeltas = $pairedDeltas
        orderSummaries = $orderSummaries
        qualityState = Get-ReadyCaptureQualityState -Pairs $taskPairs
        costStates = New-ReadyCaptureCostStates -Pairs $taskPairs
    }
}

function Get-ResultCreditEvidence($Result) {
    if (@($Result.iterations).Count -ne 1) {
        return [pscustomobject]@{ available = $false; value = $null }
    }
    $iteration = $Result.iterations[0]
    if ($iteration.PSObject.Properties.Name -cnotcontains 'hostUsageFile' -or
        $null -eq $iteration.hostUsageFile -or
        -not $iteration.hostUsageFile.available -or
        $null -eq $iteration.hostUsageFile.value -or
        $iteration.hostUsageFile.value.PSObject.Properties.Name -cnotcontains 'totalPremiumRequestCost') {
        return [pscustomobject]@{ available = $false; value = $null }
    }
    $rawValue = $iteration.hostUsageFile.value.totalPremiumRequestCost
    if ($rawValue -isnot [byte] -and $rawValue -isnot [sbyte] -and
        $rawValue -isnot [short] -and $rawValue -isnot [ushort] -and
        $rawValue -isnot [int] -and $rawValue -isnot [uint] -and
        $rawValue -isnot [long] -and $rawValue -isnot [ulong] -and
        $rawValue -isnot [float] -and $rawValue -isnot [double] -and
        $rawValue -isnot [decimal]) {
        return [pscustomobject]@{ available = $false; value = $null }
    }
    [double] $value = [double]$rawValue
    if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) {
        return [pscustomobject]@{ available = $false; value = $null }
    }
    return [pscustomobject]@{ available = $true; value = $value }
}

function Assert-ArtifactBounds(
    [System.IO.FileInfo[]] $Files,
    [int] $MaximumFiles,
    [long] $MaximumRecordBytes,
    [long] $MaximumTotalBytes) {
    if ($Files.Count -gt $MaximumFiles) {
        throw "Ready-capture artifact count exceeds $MaximumFiles."
    }
    [long] $totalBytes = 0
    foreach ($file in $Files) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Ready-capture artifact '$($file.FullName)' is a reparse point."
        }
        if ($file.Length -gt $MaximumRecordBytes) {
            throw "Ready-capture artifact '$($file.FullName)' exceeds $MaximumRecordBytes bytes."
        }
        if ($file.Length -gt ($MaximumTotalBytes - $totalBytes)) {
            throw "Ready-capture artifact set exceeds $MaximumTotalBytes bytes."
        }
        $totalBytes += $file.Length
    }
}

function Assert-ReadyCaptureSessionMachineEvidence(
    $ResultRecord,
    [int] $Pair,
    [string] $Position,
    [string] $Arm,
    $Protocol,
    $Checkpoint,
    [string] $AuthorizationHash) {
    $result = $ResultRecord.Value
    [bool] $v3 = Test-ReadyCaptureV3 $Protocol
    [string] $taskId = Get-ReadyCapturePairTaskId $Protocol $Pair
    $taskInput = Get-ReadyCaptureInput $Protocol $taskId
    $checkpointInput = if ($v3) {
        [object[]] $matches = @($Checkpoint.inputs | Where-Object {
                [string]::Equals([string]$_.taskId, $taskId, [StringComparison]::Ordinal)
            })
        if ($matches.Count -ne 1) { throw "Ready-capture checkpoint task '$taskId' is missing or repeated." }
        $matches[0]
    }
    else { $Checkpoint }
    [string] $expectedLabel = Get-ReadyCaptureLabel $Protocol $Pair $Position $Arm
    [double] $creditLimitSentinel = if ($v3) { 0 } else { [double]$Protocol.bounds.maxAiCreditsPerSession }
    if ($result.schemaVersion -ne 3 -or $result.n -ne 1 -or
        -not [string]::Equals([string]$result.arm, $Arm, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$result.label, $expectedLabel, [StringComparison]::Ordinal) -or
        @($result.iterations).Count -ne 1 -or @($result.inputIdentity).Count -ne 1) {
        throw "Ready-capture pair '$Pair' references a malformed session result."
    }
    $inputIdentity = $result.inputIdentity[0]
    $iteration = $result.iterations[0]
    $isolation = $iteration.execution.isolation
    $inputPolicy = $iteration.execution.inputPolicy
    $creditEvidence = Get-ResultCreditEvidence $result
    if ($null -eq $isolation.maxAiCredits -or
        $isolation.maxAiCredits -isnot [ValueType] -or
        $isolation.maxAiCredits -is [bool]) {
        throw "Ready-capture pair '$Pair' has a nonnumeric host credit-limit sentinel."
    }
    [string[]] $expectedAvailableTools = if ([string]::Equals(
            $Arm, 'cli-skill', [StringComparison]::Ordinal)) {
        @('powershell', 'skill', 'view')
    }
    else { @('powershell') }
    [string[]] $knownTools = @(
        'powershell', 'read_powershell', 'stop_powershell', 'list_powershell',
        'apply_patch', 'view', 'web_fetch', 'fetch_copilot_cli_documentation',
        'skill', 'sql', 'session_store_sql', 'read_agent', 'list_agents',
        'write_agent', 'rg', 'glob', 'task')
    [string[]] $expectedExcludedTools = @($knownTools | Where-Object {
            $expectedAvailableTools -cnotcontains $_
        })
    if (-not [string]::Equals([string]$result.host, 'copilot', [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$result.configuration, [string]$Protocol.arms.common.configuration, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$result.authorizationSha256, $AuthorizationHash, [StringComparison]::Ordinal) -or
        -not $result.model.verified -or
        -not [string]::Equals([string]$result.model.observed, [string]$Checkpoint.modelIdentity, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$result.model.expected, [string]$Checkpoint.modelIdentity, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$iteration.task, $taskId, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$inputIdentity.task, $taskId, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$inputIdentity.taskSha256, [string]$taskInput.taskSha256, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$inputIdentity.qaSha256, [string]$taskInput.qaLineSha256, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$inputIdentity.fixtureSha256, [string]$taskInput.fixtureSha256, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$inputIdentity.inputClosureSha256, [string]$checkpointInput.inputClosureSha256, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$iteration.execution.fixture.sha256, [string]$taskInput.fixtureSha256, [StringComparison]::Ordinal) -or
        [int]$iteration.execution.processExitCode -ne 0 -or
        [int]$iteration.execution.resultExitCode -ne 0 -or
        [int]$iteration.execution.nativeTimeoutSeconds -ne [int]$Protocol.bounds.nativeTimeoutSecondsPerSession -or
        [long]$iteration.execution.hostOutputMaxBytes -ne [long]$Protocol.bounds.maxHostOutputBytesPerSession -or
        [long]$iteration.wallMs -lt 0 -or
        [long]$iteration.wallMs -gt ([long]$Protocol.bounds.nativeTimeoutSecondsPerSession * 1000) -or
        -not $creditEvidence.available -or
        (-not $v3 -and [double]$creditEvidence.value -gt $creditLimitSentinel) -or
        [int]$result.maxSteps -ne [int]$Protocol.arms.common.maxSteps -or
        [long]$result.strictRunBudget.maxBytes -ne [long]$Protocol.bounds.maxProjectedRetainedBytesPerInvocation -or
        [long]$result.strictRunBudget.projectedBytes -lt 0 -or
        [long]$result.strictRunBudget.projectedBytes -gt [long]$result.strictRunBudget.maxBytes -or
        [long]$iteration.execution.capturedBytes -lt 0 -or
        [long]$iteration.execution.capturedBytes -gt [long]$Protocol.bounds.maxHostOutputBytesPerSession -or
        [long]$iteration.execution.artifactBytes -lt 0 -or
        [long]$iteration.execution.artifactBytes -gt [long]$Protocol.bounds.maxHostArtifactBytesPerSession -or
        [long]$iteration.execution.hostRuntimeBytes -lt 0 -or
        [long]$iteration.execution.hostRuntimeBytes -gt [long]$Protocol.bounds.maxHostRuntimeBytesPerSession -or
        [long]$isolation.dynamicArtifactMaxBytes -ne [long]$Protocol.bounds.maxHostArtifactBytesPerSession -or
        [long]$isolation.hostRuntimeMaxBytes -ne [long]$Protocol.bounds.maxHostRuntimeBytesPerSession -or
        [long]$isolation.hostRuntimeMaxFileBytes -ne [long]$Protocol.bounds.maxHostRuntimeFileBytes -or
        [int]$isolation.hostRuntimeMaxEntries -ne [int]$Protocol.bounds.maxHostRuntimeEntries -or
        [double]$isolation.maxAiCredits -ne $creditLimitSentinel -or
        -not $isolation.workspaceOutsideRepository -or -not $isolation.builtinMcpsDisabled -or
        -not $isolation.shellDefaultDenied -or -not $isolation.writeDenied -or
        -not $isolation.urlDenied -or -not $isolation.readDenied -or
        -not $isolation.processTreeContained -or
        [bool]$isolation.noCustomInstructions -ne
            [string]::Equals($Arm, 'cli', [StringComparison]::Ordinal) -or
        [int]$isolation.executionPolicyMaxCalls -ne [int]$Protocol.arms.common.maxSteps -or
        [int]$isolation.executionPolicyCallCount -gt [int]$isolation.executionPolicyMaxCalls -or
        [int]$isolation.executionPolicyMaxViewCalls -ne [int]$Protocol.bounds.maxSkillViewCalls -or
        [long]$isolation.executionPolicyMaxViewBytes -ne [long]$Protocol.bounds.maxSkillRequestedBytes -or
        [long]$inputPolicy.fixtureMaxBytes -ne [long]$Protocol.bounds.maxFixtureBytes -or
        [int]$inputPolicy.cliMaxFiles -ne [int]$Protocol.bounds.maxCliFiles -or
        [int]$inputPolicy.cliMaxEntries -ne [int]$Protocol.bounds.maxCliEntries -or
        [long]$inputPolicy.cliMaxBytes -ne [long]$Protocol.bounds.maxCliBytes -or
        [int]$inputPolicy.skillMaxFiles -ne [int]$Protocol.bounds.maxSkillFiles -or
        [int]$inputPolicy.skillMaxEntries -ne [int]$Protocol.bounds.maxSkillEntries -or
        [long]$inputPolicy.skillMaxBytes -ne [long]$Protocol.bounds.maxSkillBytes -or
        [int]$inputPolicy.skillMaxViewCalls -ne [int]$Protocol.bounds.maxSkillViewCalls -or
        [long]$inputPolicy.skillMaxRequestedBytes -ne [long]$Protocol.bounds.maxSkillRequestedBytes -or
        -not [string]::Equals(
            (Get-ManifestHash @($iteration.execution.cli.inventory)),
            [string]$Checkpoint.cliBundleManifestSha256,
            [StringComparison]::Ordinal)) {
        throw "Ready-capture pair '$Pair' session identity or machine evidence does not match the checkpoint."
    }
    Assert-EqualSet @($isolation.availableTools) $expectedAvailableTools `
        "Ready-capture pair '$Pair' available tools"
    Assert-EqualSet @($isolation.excludedTools) $expectedExcludedTools `
        "Ready-capture pair '$Pair' excluded tools"
    Assert-EqualSet @($isolation.inheritedEnvironment) @(Get-AgentEvalAllowedEnvironmentNames) `
        "Ready-capture pair '$Pair' inherited environment"
    Assert-EqualSet @($isolation.ownedEnvironment) @(
        'HOME', 'USERPROFILE', 'XDG_CONFIG_HOME', 'APPDATA', 'LOCALAPPDATA', 'COPILOT_HOME') `
        "Ready-capture pair '$Pair' owned environment"
    if ([string]::Equals($Arm, 'cli-skill', [StringComparison]::Ordinal)) {
        if (-not $iteration.skill.provided -or -not $iteration.skill.observed -or
            -not $iteration.skill.verified -or $null -eq $iteration.skill.discovery -or
            -not $iteration.skill.discovery.enabled -or
            -not [string]::Equals([string]$iteration.skill.discovery.source, 'project', [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                (Get-ManifestHash @($iteration.skill.inventory)),
                [string]$Checkpoint.skillManifestSha256,
                [StringComparison]::Ordinal)) {
            throw "Ready-capture pair '$Pair' lacks the checkpointed project-skill evidence."
        }
    }
    elseif ($iteration.skill.provided -or $iteration.skill.observed -or
        $iteration.skill.verified -or $null -ne $iteration.skill.discovery -or
        ($null -ne $iteration.skill.inventory -and
            @($iteration.skill.inventory).Count -ne 0)) {
        throw "Ready-capture pair '$Pair' CLI-only session contains skill evidence."
    }
}

function Test-ReadyCaptureBudgetExceeded(
    $ResultRecord,
    [string] $Reason,
    $Protocol,
    [double] $TotalCredits) {
    $result = $ResultRecord.Value
    $iteration = $result.iterations[0]
    switch ($Reason) {
        'budget:host-ai-credits' {
            if (Test-ReadyCaptureV3 $Protocol) { return $false }
            $creditEvidence = Get-ResultCreditEvidence $result
            return $creditEvidence.available -and
                ([double]$creditEvidence.value -gt [double]$Protocol.bounds.maxAiCreditsPerSession -or
                    $TotalCredits -gt [double]$Protocol.bounds.maximumHostAiCredits)
        }
        'budget:wall-ms' {
            return [long]$iteration.wallMs -gt
                ([long]$Protocol.bounds.nativeTimeoutSecondsPerSession * 1000)
        }
        'budget:host-output-bytes' {
            return [long]$iteration.execution.capturedBytes -gt
                [long]$Protocol.bounds.maxHostOutputBytesPerSession
        }
        'budget:host-artifact-bytes' {
            return [long]$iteration.execution.artifactBytes -gt
                [long]$Protocol.bounds.maxHostArtifactBytesPerSession
        }
        'budget:host-runtime-bytes' {
            return [long]$iteration.execution.hostRuntimeBytes -gt
                [long]$Protocol.bounds.maxHostRuntimeBytesPerSession
        }
        'budget:projected-retained-bytes' {
            return [long]$result.strictRunBudget.projectedBytes -gt
                [long]$Protocol.bounds.maxProjectedRetainedBytesPerInvocation
        }
        default { return $false }
    }
}

function Test-ReadyCaptureAnyBudgetExceeded(
    $ResultRecord,
    $Protocol,
    [double] $TotalCredits) {
    foreach ($reason in @(
            'budget:host-ai-credits',
            'budget:wall-ms',
            'budget:host-output-bytes',
            'budget:host-artifact-bytes',
            'budget:host-runtime-bytes',
            'budget:projected-retained-bytes')) {
        if (Test-ReadyCaptureBudgetExceeded `
                -ResultRecord $ResultRecord `
                -Reason $reason `
                -Protocol $Protocol `
                -TotalCredits $TotalCredits) {
            return $true
        }
    }
    return $false
}

function Get-TrustedHostVersion([string] $Path) {
    [System.Diagnostics.ProcessStartInfo] $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Path
    [void]$startInfo.ArgumentList.Add('--version')
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    [System.Diagnostics.Process] $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Failed to start trusted host '$Path'." }
        [System.Threading.Tasks.Task[string]] $stdout = $process.StandardOutput.ReadToEndAsync()
        [System.Threading.Tasks.Task[string]] $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Trusted host '$Path' did not report its version within 30 seconds."
        }
        [string] $stdoutText = $stdout.GetAwaiter().GetResult()
        [string] $stderrText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Trusted host '$Path' version probe exited $($process.ExitCode): $stderrText"
        }
        return $stdoutText.Trim()
    }
    finally {
        $process.Dispose()
    }
}

function Test-ArtifactSet(
    [string] $Directory,
    [string] $Protocol,
    [string] $Schema,
    [bool] $Complete,
    [bool] $ValidateSessionSchema,
    [string] $ValidationPhase,
    [string] $TrustedHostPath) {
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Ready-capture artifact directory '$Directory' does not exist."
    }
    $protocolValue = [System.IO.File]::ReadAllText($Protocol) | ConvertFrom-Json -Depth 100
    [bool] $v3 = Test-ReadyCaptureV3 $protocolValue
    if ($v3 -and ($protocolValue.state -cne 'prepared-not-authorized' -or
            $protocolValue.authorization.measuredExecutionAuthorized -ne $false -or
            @($protocolValue.inputs.tasks).Count -ne 2 -or
            @($protocolValue.sample.pairs).Count -ne 8 -or
            [int]$protocolValue.sample.validPairTarget -ne 8 -or
            [int]$protocolValue.sample.maximumSessions -ne 16 -or
            [int]$protocolValue.bounds.maximumHostSessions -ne 16 -or
            $protocolValue.bounds.hostAiCreditLimit -cne 'none' -or
            $protocolValue.bounds.PSObject.Properties.Name -ccontains 'maximumHostAiCredits' -or
            $protocolValue.bounds.PSObject.Properties.Name -ccontains 'maxAiCreditsPerSession')) {
        throw 'EP1 v3 protocol must remain a stopped, uncapped 2-task/8-pair/16-session experiment.'
    }
    if ($v3) {
        [string] $skillPath = Join-Path $repositoryRoot ([string]$protocolValue.inputs.skill.entrypointPath)
        $skillManifest = Get-CanonicalSkillManifest $repositoryRoot
        if ($protocolValue.inputs.skill.entrypointPath -cne '.agents/skills/filtrace/SKILL.md' -or
            -not (Test-Path -LiteralPath $skillPath -PathType Leaf) -or
            $skillManifest.fileCount -ne [int]$protocolValue.inputs.skill.fileCount -or
            -not [string]::Equals(
                [string]$skillManifest.sha256,
                [string]$protocolValue.inputs.skill.manifestTextSha256,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                (Get-CanonicalTextHash $skillPath),
                [string]$protocolValue.inputs.skill.entrypointTextSha256,
                [StringComparison]::Ordinal) -or
            ([System.OperatingSystem]::IsWindows() -and
                -not [string]::Equals(
                    (Get-RawHash $skillPath),
                    [string]$protocolValue.inputs.skill.entrypointRawSha256Windows,
                    [StringComparison]::Ordinal))) {
            throw 'EP1 v3 shipped skill entrypoint does not match the pinned updated source.'
        }
    }
    [string] $protocolHash = Get-CanonicalTextHash $Protocol
    if (-not [string]::Equals(
            (Get-CanonicalTextHash $Schema),
            [string]$protocolValue.records.schemaSha256,
            [StringComparison]::Ordinal)) {
        throw 'The supplied ready-capture schema does not match the frozen protocol.'
    }
    [System.IO.FileSystemInfo] $artifactRootInfo = Get-Item -LiteralPath $Directory -Force
    if (($artifactRootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Ready-capture artifact directory is a reparse point.'
    }
    [System.Collections.Generic.List[System.IO.FileInfo]] $artifactFileList =
        [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    foreach ($artifactPath in [System.IO.Directory]::EnumerateFileSystemEntries($Directory)) {
        if ($artifactFileList.Count -ge [int]$protocolValue.bounds.maxArtifactFiles) {
            throw "Ready-capture artifact count exceeds $($protocolValue.bounds.maxArtifactFiles)."
        }
        [System.IO.FileAttributes] $attributes = [System.IO.File]::GetAttributes($artifactPath)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Ready-capture artifact '$artifactPath' is a reparse point."
        }
        if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
            throw 'Ready-capture artifact directory must contain only top-level files.'
        }
        $artifactFileList.Add([System.IO.FileInfo]::new($artifactPath))
    }
    [System.IO.FileInfo[]] $allArtifactFiles = @($artifactFileList | Sort-Object FullName)
    Assert-ArtifactBounds `
        -Files $allArtifactFiles `
        -MaximumFiles ([int]$protocolValue.bounds.maxArtifactFiles) `
        -MaximumRecordBytes ([long]$protocolValue.bounds.maxArtifactRecordBytes) `
        -MaximumTotalBytes ([long]$protocolValue.bounds.maxArtifactSetBytes)
    [System.IO.FileInfo[]] $artifactFiles = @($allArtifactFiles | Where-Object {
            $_.Extension -ieq '.json'
        })
    [object[]] $records = @($artifactFiles | ForEach-Object { Read-Record $_.FullName })
    [object[]] $packets = @($records | Where-Object RecordType -eq 'blinded-packet')
    [object[]] $grades = @($records | Where-Object RecordType -eq 'blinded-grade')
    [object[]] $maps = @($records | Where-Object RecordType -eq 'private-arm-map')
    [object[]] $checkpoints = @($records | Where-Object RecordType -eq 'private-checkpoint')
    [object[]] $authorizations = @($records | Where-Object RecordType -eq 'private-authorization')
    [object[]] $reports = @($records | Where-Object RecordType -eq 'final-report')
    [object[]] $results = @($records | Where-Object RecordType -eq 'session-result')
    if ($records.Count -ne ($packets.Count + $grades.Count + $maps.Count +
            $checkpoints.Count + $authorizations.Count + $reports.Count + $results.Count)) {
        throw 'Ready-capture artifact set contains an unrecognized JSON record type.'
    }

    foreach ($record in @($packets + $grades + $maps + $checkpoints + $authorizations + $reports)) {
        Assert-Schema $record $Schema
        if (-not [string]::Equals(
                [string]$record.Value.protocolSha256,
                $protocolHash,
                [StringComparison]::Ordinal)) {
            throw "Record '$($record.Path)' does not match the retained protocol hash."
        }
    }

    [System.Collections.Generic.List[string]] $invalidResultHashes =
        [System.Collections.Generic.List[string]]::new()
    if ($reports.Count -eq 1) {
        foreach ($invalidSession in @($reports[0].Value.invalidSessions)) {
            $invalidResultHashes.Add([string]$invalidSession.resultSha256)
        }
    }
    if (@($invalidResultHashes | Sort-Object -Unique).Count -ne $invalidResultHashes.Count) {
        throw 'Ready-capture final report repeats an invalid-session result hash.'
    }
    [object[]] $validResults = @($results | Where-Object {
            -not $invalidResultHashes.Contains([string]$_.Hash)
        })
    if ($ValidateSessionSchema -and @($validResults).Count -gt 0) {
        [string] $resultValidator = Join-Path $PSScriptRoot 'Compare-EvalRuns.ps1'
        & $resultValidator -ValidateResultPath @($validResults | ForEach-Object Path) | Out-Null
    }

    if ($packets.Count -ne $grades.Count -or $packets.Count -ne $maps.Count) {
        throw 'Ready-capture packet, grade, and arm-map counts differ.'
    }
    if ($checkpoints.Count -ne 1) { throw 'Ready-capture evidence must contain one private checkpoint.' }
    if ($authorizations.Count -gt 1 -or ($results.Count -gt 0 -and $authorizations.Count -ne 1)) {
        throw 'Started ready-capture evidence must contain one private execution authorization.'
    }
    if ($reports.Count -gt 1) { throw 'Ready-capture evidence contains multiple final reports.' }
    if ($ValidationPhase -eq 'PreUnblind' -and $reports.Count -ne 0) {
        throw 'Pre-unblinding validation does not accept a final report.'
    }
    if ($ValidationPhase -eq 'Final' -and $reports.Count -ne 1) {
        throw 'Final ready-capture validation requires one final report.'
    }
    $checkpoint = $checkpoints[0].Value
    [string] $expectedEvaluatorHash = Get-EvaluatorClosureHash $protocolValue $Protocol
    if (-not [string]::Equals([string]$checkpoint.evaluatorClosureSha256, $expectedEvaluatorHash, [StringComparison]::Ordinal)) {
        throw 'Ready-capture checkpoint identities do not match the frozen protocol and host.'
    }
    if ($v3 -and (@($checkpoint.inputs).Count -ne 2 -or
            $checkpoint.requestedModel -cne 'GPT-6 Sol')) {
        throw 'EP1 v3 checkpoint must bind both public inputs and the requested model.'
    }
    if ($v3 -and [System.OperatingSystem]::IsWindows() -and
        -not [string]::Equals(
            [string]$checkpoint.skillManifestSha256,
            [string]$protocolValue.inputs.skill.manifestSha256Windows,
            [StringComparison]::Ordinal)) {
        throw 'EP1 v3 checkpoint uses a skill manifest other than the pinned updated shipped skill.'
    }
    foreach ($taskInput in Get-ReadyCaptureInputs $protocolValue) {
        [string] $taskId = [string]$taskInput.taskId
        [string] $taskPath = Join-Path $repositoryRoot ([string]$taskInput.taskPath)
        $task = [System.IO.File]::ReadAllText($taskPath) | ConvertFrom-Json -Depth 100
        [string] $expectedClosureHash = Get-AgentEvalTaskInputClosureHash -Task $task -Root $repositoryRoot
        $checkpointInput = if ($v3) {
            [object[]] $matches = @($checkpoint.inputs | Where-Object {
                    [string]::Equals([string]$_.taskId, $taskId, [StringComparison]::Ordinal)
                })
            if ($matches.Count -ne 1) { throw "EP1 v3 checkpoint task '$taskId' is missing or repeated." }
            $matches[0]
        }
        else { $checkpoint }
        if (-not [string]::Equals([string]$checkpointInput.taskSha256, [string]$taskInput.taskSha256, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpointInput.qaLineSha256, [string]$taskInput.qaLineSha256, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpointInput.fixtureSha256, [string]$taskInput.fixtureSha256, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpointInput.inputClosureSha256, $expectedClosureHash, [StringComparison]::Ordinal) -or
            ($v3 -and (-not [string]::Equals([string]$task.id, $taskId, [StringComparison]::Ordinal) -or
                    -not [string]::Equals((Get-CanonicalTextHash $taskPath), [string]$taskInput.taskSha256, [StringComparison]::Ordinal) -or
                    -not [string]::Equals((Get-RawHash (Join-Path $repositoryRoot ([string]$taskInput.fixturePath))), [string]$taskInput.fixtureSha256, [StringComparison]::Ordinal)))) {
            throw "Ready-capture checkpoint task '$taskId' does not match the frozen public inputs."
        }
        if ($v3) {
            [string[]] $qaMatches = @(Get-Content -LiteralPath (Join-Path $repositoryRoot ([string]$protocolValue.inputs.qaPath)) |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Where-Object { ($_ | ConvertFrom-Json -Depth 100).id -ceq $taskId })
            if ($qaMatches.Count -ne 1 -or
                -not [string]::Equals((Get-TextHash $qaMatches[0]), [string]$taskInput.qaLineSha256, [StringComparison]::Ordinal)) {
                throw "Ready-capture checkpoint QA row '$taskId' has drifted."
            }
        }
    }
    if ($v3 -and $authorizations.Count -eq 1 -and
        (-not [string]::Equals(
                [string]$authorizations[0].Value.modelIdentitySha256,
                (Get-TextHash ([string]$checkpoint.modelIdentity)),
                [StringComparison]::Ordinal) -or
            $authorizations[0].Value.hostAiCreditLimit -cne 'none' -or
            [int]$authorizations[0].Value.maximumHostSessions -ne 16)) {
        throw 'EP1 v3 authorization does not bind the exact GPT-6 Sol checkpoint without a credit cap.'
    }
    if ($ValidateSessionSchema -and $results.Count -gt 0) {
        if ([string]::IsNullOrWhiteSpace($TrustedHostPath)) {
            throw '-HostExecutablePath is required when validating retained session results.'
        }
        [string] $trustedHost = (Resolve-Path -LiteralPath $TrustedHostPath).Path
        [StringComparison] $pathComparison = if ([System.OperatingSystem]::IsWindows()) {
            [StringComparison]::OrdinalIgnoreCase
        }
        else { [StringComparison]::Ordinal }
        if (-not [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$checkpoint.hostExecutablePath),
                $trustedHost,
                $pathComparison) -or
            -not [string]::Equals(
                [string]$checkpoint.hostExecutableSha256,
                (Get-RawHash $trustedHost),
                [StringComparison]::Ordinal)) {
            throw 'Ready-capture checkpoint host identity does not match the trusted executable.'
        }
        [string] $head = (& git -C $repositoryRoot rev-parse HEAD | Out-String).Trim()
        [int] $headExitCode = $LASTEXITCODE
        [string] $tree = (& git -C $repositoryRoot rev-parse 'HEAD^{tree}' | Out-String).Trim()
        [int] $treeExitCode = $LASTEXITCODE
        [string] $status = (& git -C $repositoryRoot status --porcelain | Out-String).Trim()
        [int] $statusExitCode = $LASTEXITCODE
        [string] $sdkVersion = (& dotnet --version | Out-String).Trim()
        [int] $sdkExitCode = $LASTEXITCODE
        [string] $osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        [string] $architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
        [string] $hostVersion = Get-TrustedHostVersion $trustedHost
        if ($headExitCode -ne 0 -or $treeExitCode -ne 0 -or $statusExitCode -ne 0 -or
            $sdkExitCode -ne 0 -or
            -not [string]::Equals([string]$checkpoint.mergeCommit, $head, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpoint.mergeTree, $tree, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpoint.sdkVersion, $sdkVersion, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpoint.osDescription, $osDescription, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpoint.architecture, $architecture, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$checkpoint.hostVersion, $hostVersion, [StringComparison]::Ordinal) -or
            -not [string]::IsNullOrEmpty($status)) {
            throw 'Ready-capture validation requires the exact clean checkpointed source revision.'
        }
    }
    if ($Complete -and
        ($packets.Count -ne [int]$protocolValue.sample.validPairTarget -or
            $grades.Count -ne [int]$protocolValue.sample.validPairTarget -or
            $maps.Count -ne [int]$protocolValue.sample.validPairTarget -or
            $results.Count -ne [int]$protocolValue.sample.maximumSessions -or $reports.Count -ne 1)) {
        throw "Complete ready-capture evidence must contain $($protocolValue.sample.validPairTarget) pairs and $($protocolValue.sample.maximumSessions) session results."
    }
    if ($Complete -and
        -not [string]::Equals(
            [string]$reports[0].Value.terminalDisposition,
            'descriptive-complete',
            [StringComparison]::Ordinal)) {
        throw 'Complete ready-capture evidence must use the descriptive-complete disposition.'
    }

    [System.Collections.Generic.HashSet[string]] $seenPacketIds =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [System.Collections.Generic.HashSet[string]] $seenBlindIds =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [System.Collections.Generic.HashSet[string]] $seenGradeNonces =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [System.Collections.Generic.HashSet[int]] $seenPairs = [System.Collections.Generic.HashSet[int]]::new()
    [System.Collections.Generic.HashSet[string]] $referencedResultHashes =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($mapRecord in $maps) {
        $map = $mapRecord.Value
        if (-not $seenPairs.Add([int]$map.pair)) { throw "Ready-capture pair '$($map.pair)' is repeated." }
        [object[]] $matchingPackets = @($packets | Where-Object {
                [string]::Equals([string]$_.Value.packetId, [string]$map.packetId, [StringComparison]::Ordinal)
            })
        [object[]] $matchingGrades = @($grades | Where-Object {
                [string]::Equals([string]$_.Value.packetId, [string]$map.packetId, [StringComparison]::Ordinal)
            })
        if ($matchingPackets.Count -ne 1 -or $matchingGrades.Count -ne 1 -or
            -not $seenPacketIds.Add([string]$map.packetId)) {
            throw "Ready-capture pair '$($map.pair)' does not have one unique packet and grade."
        }
        $packetRecord = $matchingPackets[0]
        $gradeRecord = $matchingGrades[0]
        if (-not $seenGradeNonces.Add([string]$gradeRecord.Value.gradeNonce)) {
            throw "Ready-capture pair '$($map.pair)' repeats a grade nonce."
        }
        [string[]] $packetBlindIds = @($packetRecord.Value.answers | ForEach-Object { [string]$_.blindId })
        [string[]] $gradeBlindIds = @($gradeRecord.Value.grades | ForEach-Object { [string]$_.blindId })
        [string[]] $mapBlindIds = @($map.entries | ForEach-Object { [string]$_.blindId })
        if (@($packetBlindIds | Sort-Object -Unique).Count -ne 2) {
            throw "Ready-capture pair '$($map.pair)' repeats a packet blind id."
        }
        foreach ($blindId in $packetBlindIds) {
            if (-not $seenBlindIds.Add($blindId)) {
                throw "Ready-capture pair '$($map.pair)' reuses a blind id from another pair."
            }
        }
        [string[]] $sortedBlindIds = @($packetBlindIds | Sort-Object -CaseSensitive)
        for ($blindIndex = 0; $blindIndex -lt $packetBlindIds.Count; $blindIndex++) {
            if (-not [string]::Equals(
                    $packetBlindIds[$blindIndex],
                    $sortedBlindIds[$blindIndex],
                    [StringComparison]::Ordinal)) {
                throw "Ready-capture pair '$($map.pair)' packet answers are not sorted by blind id."
            }
        }
        Assert-EqualSet $packetBlindIds $gradeBlindIds "Ready-capture pair '$($map.pair)' grade blind ids"
        Assert-EqualSet $packetBlindIds $mapBlindIds "Ready-capture pair '$($map.pair)' arm-map blind ids"

        [string] $expectedTaskId = Get-ReadyCapturePairTaskId $protocolValue ([int]$map.pair)
        $expectedPair = $protocolValue.sample.pairs[[int]$map.pair - 1]
        if ($v3 -and (-not [string]::Equals([string]$map.taskId, $expectedTaskId, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$packetRecord.Value.taskId, $expectedTaskId, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$gradeRecord.Value.taskId, $expectedTaskId, [StringComparison]::Ordinal))) {
            throw "Ready-capture pair '$($map.pair)' mixes task identities before unblinding."
        }
        foreach ($position in @('first', 'second')) {
            [object[]] $positionEntries = @($map.entries | Where-Object position -eq $position)
            if ($positionEntries.Count -ne 1 -or
                -not [string]::Equals(
                    [string]$positionEntries[0].arm,
                    [string]$expectedPair.$position,
                    [StringComparison]::Ordinal)) {
                throw "Ready-capture pair '$($map.pair)' does not match the frozen arm schedule."
            }
        }
        if (-not [string]::Equals([string]$gradeRecord.Value.packetSha256, $packetRecord.Hash, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$map.packetSha256, $packetRecord.Hash, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$map.gradeSha256, $gradeRecord.Hash, [StringComparison]::Ordinal)) {
            throw "Ready-capture pair '$($map.pair)' has a packet or grade hash mismatch."
        }

        foreach ($answer in $packetRecord.Value.answers) {
            [bool] $available = [bool]$answer.answerAvailable
            if ($available -ne (-not [string]::IsNullOrWhiteSpace([string]$answer.answer))) {
                throw "Ready-capture pair '$($map.pair)' has inconsistent answer availability."
            }
        }

        foreach ($entry in $map.entries) {
            if (-not $referencedResultHashes.Add([string]$entry.resultSha256)) {
                throw "Ready-capture result hash '$($entry.resultSha256)' is reused."
            }
            [object[]] $matchingResults = @($results | Where-Object {
                    [string]::Equals([string]$_.Hash, [string]$entry.resultSha256, [StringComparison]::Ordinal)
                })
            if ($matchingResults.Count -ne 1) {
                throw "Ready-capture pair '$($map.pair)' does not reference one retained session result."
            }
            Assert-ReadyCaptureSessionMachineEvidence `
                -ResultRecord $matchingResults[0] `
                -Pair ([int]$map.pair) `
                -Position ([string]$entry.position) `
                -Arm ([string]$entry.arm) `
                -Protocol $protocolValue `
                -Checkpoint $checkpoint `
                -AuthorizationHash ([string]$authorizations[0].Hash)
        }
    }
    Assert-EqualSet @($referencedResultHashes) @($validResults | ForEach-Object Hash) `
        'Ready-capture finalized session-result hashes'
    for ($pairNumber = 1; $pairNumber -le $maps.Count; $pairNumber++) {
        if (-not $seenPairs.Contains($pairNumber)) {
            throw 'Ready-capture finalized pairs are not a contiguous prefix of the frozen schedule.'
        }
    }

    if ($reports.Count -eq 1) {
        $report = $reports[0].Value
        if ($v3 -and (@($report.sessionResultSha256).Count -ne $results.Count -or
                @($report.gradeSha256).Count -ne $grades.Count)) {
            throw 'EP1 v3 final report must list every distinct retained session and grade hash.'
        }
        Assert-EqualSet @($report.sessionResultSha256) @($results | ForEach-Object Hash) `
            'Ready-capture final-report session hashes'
        Assert-EqualSet @($report.gradeSha256) @($grades | ForEach-Object Hash) `
            'Ready-capture final-report grade hashes'
        [string] $authorizationHash = if ($authorizations.Count -eq 1) { $authorizations[0].Hash } else { $null }
        if (-not [string]::Equals(
                [string]$report.authorizationSha256,
                $authorizationHash,
                [StringComparison]::Ordinal)) {
            throw 'Ready-capture final-report authorization hash does not match retained evidence.'
        }
        Assert-EqualSet @($report.sessions | ForEach-Object { $_.resultSha256 }) @($validResults | ForEach-Object Hash) `
            'Ready-capture final-report session rows'
        Assert-EqualSet @($report.invalidSessions | ForEach-Object { $_.resultSha256 }) $invalidResultHashes `
            'Ready-capture final-report invalid-session rows'
        if ([int]$report.hostSessions -ne $results.Count -or
            [int]$report.validPairs -ne $maps.Count) {
            throw 'Ready-capture final-report counts do not match retained evidence.'
        }
        [object[]] $invalidSessionRows = @($report.invalidSessions)
        if ($invalidSessionRows.Count -gt 2 -or
            ($invalidSessionRows.Count -gt 0 -and $maps.Count -ge @($protocolValue.sample.pairs).Count)) {
            throw 'Ready-capture invalid sessions exceed the next unfinalized pair.'
        }
        for ($invalidIndex = 0; $invalidIndex -lt $invalidSessionRows.Count; $invalidIndex++) {
            [string] $expectedInvalidPosition = if ($invalidIndex -eq 0) { 'first' } else { 'second' }
            if ([int]$invalidSessionRows[$invalidIndex].pair -ne ($maps.Count + 1) -or
                -not [string]::Equals(
                    [string]$invalidSessionRows[$invalidIndex].position,
                    $expectedInvalidPosition,
                    [StringComparison]::Ordinal)) {
                throw 'Ready-capture invalid sessions are not the next contiguous session prefix.'
            }
        }
        [double] $creditTotal = 0
        for ($sessionIndex = 0; $sessionIndex -lt @($report.sessions).Count; $sessionIndex++) {
            $session = $report.sessions[$sessionIndex]
            [int] $expectedPairNumber = [int][Math]::Floor($sessionIndex / 2) + 1
            [string] $expectedPosition = if (($sessionIndex % 2) -eq 0) { 'first' } else { 'second' }
            [string] $expectedTaskId = Get-ReadyCapturePairTaskId $protocolValue $expectedPairNumber
            if ([int]$session.pair -ne $expectedPairNumber -or
                -not [string]::Equals([string]$session.position, $expectedPosition, [StringComparison]::Ordinal) -or
                ($v3 -and -not [string]::Equals([string]$session.taskId, $expectedTaskId, [StringComparison]::Ordinal))) {
                throw 'Ready-capture final-report sessions are not in frozen pair/position order.'
            }
            [object[]] $matchingMaps = @($maps | Where-Object { [int]$_.Value.pair -eq [int]$session.pair })
            if ($matchingMaps.Count -ne 1 -or
                @($matchingMaps[0].Value.entries | Where-Object {
                        [string]::Equals([string]$_.resultSha256, [string]$session.resultSha256, [StringComparison]::Ordinal) -and
                        [string]::Equals([string]$_.arm, [string]$session.arm, [StringComparison]::Ordinal) -and
                        [string]::Equals([string]$_.position, [string]$session.position, [StringComparison]::Ordinal)
                    }).Count -ne 1) {
                throw 'Ready-capture final-report session rows do not match the private arm maps.'
            }
            $mapEntry = @($matchingMaps[0].Value.entries | Where-Object {
                    [string]::Equals([string]$_.resultSha256, [string]$session.resultSha256, [StringComparison]::Ordinal)
                })[0]
            $packetRecord = @($packets | Where-Object {
                    [string]::Equals([string]$_.Value.packetId, [string]$matchingMaps[0].Value.packetId, [StringComparison]::Ordinal)
                })[0]
            $gradeRecord = @($grades | Where-Object {
                    [string]::Equals([string]$_.Value.packetId, [string]$matchingMaps[0].Value.packetId, [StringComparison]::Ordinal)
                })[0]
            $packetAnswer = @($packetRecord.Value.answers | Where-Object {
                    [string]::Equals([string]$_.blindId, [string]$mapEntry.blindId, [StringComparison]::Ordinal)
                })[0]
            $grade = @($gradeRecord.Value.grades | Where-Object {
                    [string]::Equals([string]$_.blindId, [string]$mapEntry.blindId, [StringComparison]::Ordinal)
                })[0]
            $resultRecord = @($results | Where-Object {
                    [string]::Equals([string]$_.Hash, [string]$session.resultSha256, [StringComparison]::Ordinal)
                })[0]
            $iteration = $resultRecord.Value.iterations[0]
            [bool] $answerAvailable = -not [string]::IsNullOrWhiteSpace([string]$iteration.answer)
            [double] $credits = [double]$iteration.hostUsageFile.value.totalPremiumRequestCost
            $creditTotal += $credits
            if ([double]::IsInfinity($creditTotal)) {
                throw 'Ready-capture host AI credit usage cannot be represented as a finite total.'
            }
            if ([bool]$session.success -ne [bool]$iteration.success -or
                -not [string]::Equals([string]$session.authorizationSha256, $authorizationHash, [StringComparison]::Ordinal) -or
                [bool]$session.answerAvailable -ne $answerAvailable -or
                [bool]$session.answerAvailable -ne [bool]$packetAnswer.answerAvailable -or
                -not [string]::Equals([string]$packetAnswer.answer, [string]$iteration.answer, [StringComparison]::Ordinal) -or
                [bool]$session.falseConfidence -ne [bool]$grade.falseConfidence.triggered -or
                [int]$session.calls -ne [int]$iteration.calls -or
                [int]$session.helpCalls -ne [int]$iteration.helpCalls -or
                [int]$session.tokens -ne [int]$iteration.tokens -or
                [int]$session.wallMs -ne [int]$iteration.wallMs -or
                [double]$session.hostAiCredits -ne $credits -or
                -not (Test-JsonEqual $session.hostUsage $iteration.hostUsage) -or
                -not (Test-JsonEqual $session.hostUsageFile $iteration.hostUsageFile) -or
                -not (Test-JsonEqual $session.answerCriteria $grade.criteria) -or
                -not (Test-JsonEqual $session.transcript $iteration.transcript) -or
                -not (Test-JsonEqual $session.inputIdentity $resultRecord.Value.inputIdentity[0])) {
                throw 'Ready-capture final-report session evidence does not match its retained result and grade.'
            }
            if (-not $answerAvailable) {
                if (@($grade.criteria.PSObject.Properties.Value | Where-Object {
                            $_.pass -or -not [string]::Equals(
                                [string]$_.evidence, 'no final answer', [StringComparison]::Ordinal)
                        }).Count -ne 0 -or
                    $grade.falseConfidence.triggered -or
                    -not [string]::Equals(
                        [string]$grade.falseConfidence.evidence, 'none', [StringComparison]::Ordinal)) {
                    throw 'Ready-capture no-answer grade does not use the frozen no-answer rubric.'
                }
            }
            else {
                if (@($grade.criteria.PSObject.Properties.Value | Where-Object {
                            -not ([string]$iteration.answer).Contains(
                                [string]$_.evidence, [StringComparison]::Ordinal)
                        }).Count -ne 0 -or
                    ($grade.falseConfidence.triggered -and
                        -not ([string]$iteration.answer).Contains(
                            [string]$grade.falseConfidence.evidence, [StringComparison]::Ordinal)) -or
                    (-not $grade.falseConfidence.triggered -and
                        -not [string]::Equals(
                            [string]$grade.falseConfidence.evidence, 'none', [StringComparison]::Ordinal))) {
                    throw 'Ready-capture grade evidence is not grounded in the retained answer.'
                }
            }
        }
        [System.Collections.Generic.List[bool]] $invalidSessionMachineValidity =
            [System.Collections.Generic.List[bool]]::new()
        [System.Collections.Generic.List[object]] $invalidSessionResults =
            [System.Collections.Generic.List[object]]::new()
        foreach ($invalidSession in $invalidSessionRows) {
            [object[]] $matchingResults = @($results | Where-Object {
                    [string]::Equals([string]$_.Hash, [string]$invalidSession.resultSha256, [StringComparison]::Ordinal)
                })
            [int] $invalidPairNumber = [Convert]::ToInt32(
                $invalidSession.PSObject.Properties['pair'].Value,
                [Globalization.CultureInfo]::InvariantCulture)
            $expectedPairSchedule = $protocolValue.sample.pairs[$invalidPairNumber - 1]
            [string] $expectedTaskId = Get-ReadyCapturePairTaskId $protocolValue $invalidPairNumber
            [string] $expectedArm = [string]$expectedPairSchedule.PSObject.Properties[
                [string]$invalidSession.position].Value
            $creditEvidence = if ($matchingResults.Count -eq 1) {
                Get-ResultCreditEvidence $matchingResults[0].Value
            }
            else { [pscustomobject]@{ available = $false; value = $null } }
            if ($matchingResults.Count -ne 1 -or
                -not [string]::Equals([string]$invalidSession.authorizationSha256, $authorizationHash, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$matchingResults[0].Value.authorizationSha256, $authorizationHash, [StringComparison]::Ordinal) -or
                -not [string]::Equals(
                    [string]$invalidSession.arm,
                    $expectedArm,
                    [StringComparison]::Ordinal) -or
                -not [string]::Equals(
                    [string]$matchingResults[0].Value.arm,
                    [string]$invalidSession.arm,
                    [StringComparison]::Ordinal) -or
                -not [string]::Equals(
                    [string]$matchingResults[0].Value.label,
                    (Get-ReadyCaptureLabel $protocolValue $invalidPairNumber ([string]$invalidSession.position) ([string]$invalidSession.arm)),
                    [StringComparison]::Ordinal) -or
                ($v3 -and -not [string]::Equals([string]$invalidSession.taskId, $expectedTaskId, [StringComparison]::Ordinal)) -or
                ($creditEvidence.available -and
                    ($null -eq $invalidSession.hostAiCredits -or
                        [double]$invalidSession.hostAiCredits -ne [double]$creditEvidence.value)) -or
                (-not $creditEvidence.available -and $null -ne $invalidSession.hostAiCredits)) {
                throw 'Ready-capture invalid-session evidence does not match the frozen schedule.'
            }
            if ($creditEvidence.available) {
                $creditTotal += [double]$creditEvidence.value
                if ([double]::IsInfinity($creditTotal)) {
                    throw 'Ready-capture host AI credit usage cannot be represented as a finite total.'
                }
            }
            $invalidSessionResults.Add($matchingResults[0])
            [bool] $machineEvidenceValid = $true
            if ($ValidateSessionSchema) {
                try {
                    [string] $resultValidator = Join-Path $PSScriptRoot 'Compare-EvalRuns.ps1'
                    & $resultValidator -ValidateResultPath $matchingResults[0].Path | Out-Null
                }
                catch { $machineEvidenceValid = $false }
            }
            if ($machineEvidenceValid) {
                try {
                    Assert-ReadyCaptureSessionMachineEvidence `
                        -ResultRecord $matchingResults[0] `
                        -Pair $invalidPairNumber `
                        -Position ([string]$invalidSession.position) `
                        -Arm ([string]$invalidSession.arm) `
                        -Protocol $protocolValue `
                        -Checkpoint $checkpoint `
                        -AuthorizationHash $authorizationHash
                }
                catch { $machineEvidenceValid = $false }
            }
            $invalidSessionMachineValidity.Add($machineEvidenceValid)
        }
        [bool] $allCreditsAvailable = @($report.invalidSessions | Where-Object {
                $null -eq $_.hostAiCredits
            }).Count -eq 0
        if (($allCreditsAvailable -and
                ($null -eq $report.hostAiCredits -or [double]$report.hostAiCredits -ne $creditTotal)) -or
            (-not $allCreditsAvailable -and $null -ne $report.hostAiCredits)) {
            throw 'Ready-capture final-report host AI credits do not match retained usage evidence.'
        }

        [bool] $stoppedAtFirstMachineFailure = $invalidSessionMachineValidity.Count -gt 0 -and
            -not $invalidSessionMachineValidity[$invalidSessionMachineValidity.Count - 1]
        for ($validPrefixIndex = 0;
            $validPrefixIndex -lt ($invalidSessionMachineValidity.Count - 1);
            $validPrefixIndex++) {
            if (-not $invalidSessionMachineValidity[$validPrefixIndex]) {
                $stoppedAtFirstMachineFailure = $false
            }
        }
        [bool] $allInvalidSessionsMachineValid =
            @($invalidSessionMachineValidity | Where-Object { -not $_ }).Count -eq 0
        [bool] $finalFailureExceedsBudget = $false
        if ($stoppedAtFirstMachineFailure) {
            try {
                $finalFailureExceedsBudget = Test-ReadyCaptureAnyBudgetExceeded `
                    -ResultRecord $invalidSessionResults[$invalidSessionResults.Count - 1] `
                    -Protocol $protocolValue `
                    -TotalCredits $creditTotal
            }
            catch { $finalFailureExceedsBudget = $false }
        }

        if (-not [string]::Equals(
                [string]$report.terminalDisposition,
                'descriptive-complete',
                [StringComparison]::Ordinal) -and
            $maps.Count -ge [int]$protocolValue.sample.validPairTarget) {
            throw "$($protocolValue.sample.validPairTarget) valid pairs require the descriptive-complete disposition."
        }

        switch ([string]$report.terminalDisposition) {
            'descriptive-complete' {
                if (-not [string]::Equals(
                        [string]$report.terminalReason,
                        "$($protocolValue.sample.validPairTarget)-valid-pairs",
                        [StringComparison]::Ordinal)) {
                    throw 'A descriptive-complete report must record the exact valid-pair count.'
                }
            }
            'incomplete-precondition' {
                if (-not ([string]$report.terminalReason).StartsWith('precondition:', [StringComparison]::Ordinal) -or
                    @($report.invalidSessions).Count -ne 0 -or
                    $results.Count -ne $referencedResultHashes.Count) {
                    throw 'A precondition disposition may retain only a finalized pair prefix.'
                }
            }
            'incomplete-evidence-integrity' {
                if (-not ([string]$report.terminalReason).StartsWith('evidence:', [StringComparison]::Ordinal) -or
                    @($report.invalidSessions).Count -eq 0 -or
                    -not $stoppedAtFirstMachineFailure -or
                    $finalFailureExceedsBudget -or
                    @($report.invalidSessions | Where-Object {
                            -not ([string]$_.reason).StartsWith('evidence:', [StringComparison]::Ordinal)
                        }).Count -ne 0) {
                    throw 'An evidence-integrity disposition requires only evidence-class invalid sessions.'
                }
            }
            'incomplete-budget' {
                [bool] $budgetExceeded = $false
                if ($stoppedAtFirstMachineFailure) {
                    try {
                        $budgetExceeded = Test-ReadyCaptureBudgetExceeded `
                            -ResultRecord $invalidSessionResults[$invalidSessionResults.Count - 1] `
                            -Reason ([string]$report.terminalReason) `
                            -Protocol $protocolValue `
                            -TotalCredits $creditTotal
                    }
                    catch { $budgetExceeded = $false }
                }
                if (-not $budgetExceeded -or
                    @($report.invalidSessions | Where-Object {
                            -not [string]::Equals(
                                [string]$_.reason,
                                [string]$report.terminalReason,
                                [StringComparison]::Ordinal)
                        }).Count -ne 0) {
                    throw 'A budget disposition requires retained evidence of its named exceeded bound.'
                }
            }
            'stopped-by-user' {
                if (-not ([string]$report.terminalReason).StartsWith('user:', [StringComparison]::Ordinal) -or
                    -not $allInvalidSessionsMachineValid -or
                    @($report.invalidSessions | Where-Object {
                            -not ([string]$_.reason).StartsWith('user:', [StringComparison]::Ordinal)
                        }).Count -ne 0) {
                    throw 'A user-stopped disposition cannot conceal invalid session evidence.'
                }
            }
        }

        if (-not [string]::Equals(
                [string]$report.terminalDisposition,
                'descriptive-complete',
                [StringComparison]::Ordinal)) {
            if (@($report.armSummaries).Count -ne 0 -or
                @($report.pairedDeltas).Count -ne 0 -or
                @($report.orderSummaries).Count -ne 0 -or
                ($v3 -and @($report.taskSummaries).Count -ne 0) -or
                -not [string]::Equals([string]$report.qualityState, 'unavailable', [StringComparison]::Ordinal) -or
                @($report.costStates.PSObject.Properties.Value | Where-Object {
                        -not [string]::Equals([string]$_, 'unavailable', [StringComparison]::Ordinal)
                    }).Count -ne 0) {
                throw 'Incomplete ready-capture reports must leave aggregate classifications unavailable.'
            }
        }
        else {
            foreach ($arm in @('cli-skill', 'cli')) {
            [object[]] $armSessions = @($report.sessions | Where-Object arm -eq $arm)
            [object[]] $summaries = @($report.armSummaries | Where-Object arm -eq $arm)
            if ($summaries.Count -ne 1 -or [int]$summaries[0].sessionCount -ne $armSessions.Count -or
                [int]$summaries[0].qualityPassCount -ne @($armSessions | Where-Object {
                        $_.answerAvailable -and -not $_.falseConfidence -and
                        @($_.answerCriteria.PSObject.Properties.Value | Where-Object { -not $_.pass }).Count -eq 0
                    }).Count -or
                [int]$summaries[0].noAnswerCount -ne @($armSessions | Where-Object { -not $_.answerAvailable }).Count -or
                [int]$summaries[0].falseConfidenceCount -ne @($armSessions | Where-Object falseConfidence).Count) {
                throw "Ready-capture final-report '$arm' summary counts do not match session evidence."
            }
            Assert-MetricSummary $summaries[0].metrics.analysisCalls @($armSessions | ForEach-Object { [double]$_.calls }) "$arm analysis calls"
            Assert-MetricSummary $summaries[0].metrics.helpCalls @($armSessions | ForEach-Object { [double]$_.helpCalls }) "$arm help calls"
            Assert-MetricSummary $summaries[0].metrics.resultTokens @($armSessions | ForEach-Object { [double]$_.tokens }) "$arm result tokens"
            Assert-MetricSummary $summaries[0].metrics.wallMs @($armSessions | ForEach-Object { [double]$_.wallMs }) "$arm wall time"
            Assert-MetricSummary $summaries[0].metrics.hostAiCredits @($armSessions | ForEach-Object { [double]$_.hostAiCredits }) "$arm host AI credits"
        }

        [System.Collections.Generic.List[object]] $computedPairs = [System.Collections.Generic.List[object]]::new()
        foreach ($pairNumber in 1..$maps.Count) {
            [object[]] $pairRows = @($report.sessions | Where-Object { [int]$_.pair -eq $pairNumber })
            [object[]] $reportedPairs = @($report.pairedDeltas | Where-Object { [int]$_.pair -eq $pairNumber })
            if ($pairRows.Count -ne 2 -or $reportedPairs.Count -ne 1) {
                throw "Ready-capture final-report pair '$pairNumber' is not represented exactly once."
            }
            $skill = @($pairRows | Where-Object arm -eq 'cli-skill')[0]
            $cli = @($pairRows | Where-Object arm -eq 'cli')[0]
            $computed = [pscustomobject]@{
                taskId = Get-ReadyCapturePairTaskId $protocolValue $pairNumber
                pair = $pairNumber
                firstArm = @($pairRows | Where-Object position -eq 'first')[0].arm
                qualityState = Get-PairedQualityState $skill $cli
                skill = $skill
                cli = $cli
                deltas = [pscustomobject]@{
                    analysisCalls = [double]$skill.calls - [double]$cli.calls
                    helpCalls = [double]$skill.helpCalls - [double]$cli.helpCalls
                    resultTokens = [double]$skill.tokens - [double]$cli.tokens
                    wallMs = [double]$skill.wallMs - [double]$cli.wallMs
                    hostAiCredits = [double]$skill.hostAiCredits - [double]$cli.hostAiCredits
                }
            }
            [void]$computedPairs.Add($computed)
            if (-not [string]::Equals([string]$reportedPairs[0].firstArm, [string]$computed.firstArm, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$reportedPairs[0].qualityState, [string]$computed.qualityState, [StringComparison]::Ordinal) -or
                ($v3 -and -not [string]::Equals([string]$reportedPairs[0].taskId, [string]$computed.taskId, [StringComparison]::Ordinal)) -or
                -not (Test-JsonEqual $reportedPairs[0].deltas $computed.deltas)) {
                throw "Ready-capture final-report pair '$pairNumber' deltas do not match retained sessions."
            }
        }

        [string] $qualityState = Get-QualityAggregate -Pairs @($computedPairs)
        [object[]] $skillFirst = @($computedPairs | Where-Object firstArm -eq 'cli-skill')
        [object[]] $cliFirst = @($computedPairs | Where-Object firstArm -eq 'cli')
        [string] $skillFirstQuality = Get-QualityAggregate -Pairs $skillFirst
        [string] $cliFirstQuality = Get-QualityAggregate -Pairs $cliFirst
        if (($skillFirstQuality -eq 'skill-higher-observed-quality' -and $cliFirstQuality -eq 'skill-lower-observed-quality') -or
            ($skillFirstQuality -eq 'skill-lower-observed-quality' -and $cliFirstQuality -eq 'skill-higher-observed-quality')) {
            $qualityState = 'order-sensitive'
        }
        if (-not [string]::Equals([string]$report.qualityState, $qualityState, [StringComparison]::Ordinal)) {
            throw 'Ready-capture final-report quality classification does not match retained grades.'
        }
        foreach ($firstArm in @('cli-skill', 'cli')) {
            [object[]] $group = @($computedPairs | Where-Object firstArm -eq $firstArm)
            [object[]] $summaries = @($report.orderSummaries | Where-Object firstArm -eq $firstArm)
            if ($summaries.Count -ne 1 -or [int]$summaries[0].pairCount -ne $group.Count -or
                -not [string]::Equals([string]$summaries[0].qualityState, (Get-QualityAggregate -Pairs $group), [StringComparison]::Ordinal)) {
                throw "Ready-capture final-report '$firstArm' order summary is invalid."
            }
            foreach ($member in @('analysisCalls', 'helpCalls', 'resultTokens', 'wallMs', 'hostAiCredits')) {
                if ([double]$summaries[0].medianDeltas.$member -ne
                    (Get-Median @($group | ForEach-Object { [double]$_.deltas.$member }))) {
                    throw "Ready-capture final-report '$firstArm' order delta '$member' is invalid."
                }
            }
        }
        $expectedCostStates = [ordered]@{
            analysisCalls = Get-CostState -Pairs @($computedPairs) -Member analysisCalls -Equivalence 0 -Relative $false
            helpCalls = Get-CostState -Pairs @($computedPairs) -Member helpCalls -Equivalence 0 -Relative $false
            resultTokens = Get-CostState -Pairs @($computedPairs) -Member resultTokens -Equivalence 0.05 -Relative $true
            wallMs = Get-CostState -Pairs @($computedPairs) -Member wallMs -Equivalence 0.05 -Relative $true
            hostAiCredits = Get-CostState -Pairs @($computedPairs) -Member hostAiCredits -Equivalence 0 -Relative $false
        }
            if (-not (Test-JsonEqual $report.costStates $expectedCostStates)) {
                throw 'Ready-capture final-report cost classification does not match retained sessions.'
            }
            if ($v3) {
                if (@($report.taskSummaries).Count -ne 2) {
                    throw 'EP1 v3 must report one independently graded summary for each task.'
                }
                foreach ($taskInput in @($protocolValue.inputs.tasks)) {
                    [string] $taskId = [string]$taskInput.taskId
                    [object[]] $matches = @($report.taskSummaries | Where-Object taskId -eq $taskId)
                    $expectedSummary = New-ReadyCaptureTaskSummary `
                        -TaskId $taskId -Sessions @($report.sessions) -Pairs @($computedPairs)
                    if ($matches.Count -ne 1 -or
                        -not (Test-JsonEqual $matches[0] $expectedSummary)) {
                        throw "EP1 v3 task '$taskId' summary does not match retained sessions and grades."
                    }
                }
            }
        }
    }

    return [pscustomobject]@{
        pairs = $maps.Count
        sessions = $results.Count
        reports = $reports.Count
        protocolSha256 = $protocolHash
    }
}

function Write-JsonFile([string] $Path, $Value) {
    [string] $json = $Value | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($Path, "$json`n", [Text.UTF8Encoding]::new($false))
}

function Invoke-SelfTest {
    [string] $root = Join-Path $PSScriptRoot "self-test-ready-capture-$([Guid]::NewGuid().ToString('N'))"
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    try {
        [string] $boundsRoot = Join-Path $root 'bounds'
        [System.IO.Directory]::CreateDirectory($boundsRoot) | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $boundsRoot 'first.json'), [byte[]]::new(4))
        [System.IO.File]::WriteAllBytes((Join-Path $boundsRoot 'second.json'), [byte[]]::new(6))
        [System.IO.File]::WriteAllBytes((Join-Path $boundsRoot '.hidden.bin'), [byte[]]::new(1))
        [System.IO.File]::WriteAllBytes((Join-Path $boundsRoot 'retained.bin'), [byte[]]::new(2))
        [System.IO.FileInfo[]] $boundFiles = @(Get-ChildItem -LiteralPath $boundsRoot -File -Force)
        Assert-ArtifactBounds $boundFiles 4 6 13
        foreach ($limits in @(
            [pscustomobject]@{ files = 3; record = 6; total = 13 },
            [pscustomobject]@{ files = 4; record = 5; total = 13 },
            [pscustomobject]@{ files = 4; record = 6; total = 12 })) {
            [bool] $rejected = $false
            try { Assert-ArtifactBounds $boundFiles $limits.files $limits.record $limits.total }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Ready-capture artifact bound mutation unexpectedly passed.' }
        }
        Remove-Item -LiteralPath $boundsRoot -Recurse -Force

        $protocolValue = [System.IO.File]::ReadAllText($ProtocolPath) | ConvertFrom-Json -Depth 100
        [bool] $v3 = Test-ReadyCaptureV3 $protocolValue
        [string] $protocolId = [string]$protocolValue.protocolId
        [string] $protocolHash = Get-CanonicalTextHash $ProtocolPath
        [string] $zeroHash = '0' * 64
        [int] $pairTarget = [int]$protocolValue.sample.validPairTarget
        [int] $sessionTarget = [int]$protocolValue.sample.maximumSessions
        [double] $creditsPerSession = if ($v3) { 50 } else { 1 }
        [double] $creditLimitSentinel = if ($v3) { 0 } else { 30 }
        [object[]] $taskInputs = @(Get-ReadyCaptureInputs $protocolValue)
        $inputClosureHashes = @{}
        foreach ($taskInput in $taskInputs) {
            $task = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot ([string]$taskInput.taskPath))) |
                ConvertFrom-Json -Depth 100
            $inputClosureHashes[[string]$taskInput.taskId] =
                Get-AgentEvalTaskInputClosureHash -Task $task -Root $repositoryRoot
        }
        [string] $evaluatorClosureHash = Get-EvaluatorClosureHash $protocolValue $ProtocolPath
        [string] $hostPath = (Resolve-Path -LiteralPath $SchemaPath).Path
        $cliInventory = @([pscustomobject]@{ relativePath = 'filtrace.exe'; bytes = 1; sha256 = '3' * 64 })
        $skillInventory = if ($v3) {
            $skillSource = Get-AgentEvalSkillInput -Root $repositoryRoot
            @($skillSource.files)
        }
        else { @([pscustomobject]@{ path = 'SKILL.md'; bytes = 1; sha256 = '4' * 64 }) }
        [string] $cliManifestHash = Get-ManifestHash $cliInventory
        [string] $skillManifestHash = Get-ManifestHash $skillInventory
        [System.Collections.Generic.List[object]] $maps = [System.Collections.Generic.List[object]]::new()
        [System.Collections.Generic.List[object]] $sessions = [System.Collections.Generic.List[object]]::new()
        [System.Collections.Generic.List[string]] $gradeHashes = [System.Collections.Generic.List[string]]::new()
        [System.Collections.Generic.List[string]] $resultHashes = [System.Collections.Generic.List[string]]::new()

        $checkpoint = [ordered]@{
            schemaVersion = 1; protocolId = $protocolId; recordType = 'private-checkpoint'
            state = 'prepared-not-authorized'; measuredExecutionAuthorized = $false
            protocolSha256 = $protocolHash; sourceRepository = 'https://github.com/JeremyKuhne/filtrace.git'
            mergeCommit = '1' * 40; mergeTree = '2' * 40; buildCommand = 'dotnet build filtrace.slnx -c Release'
            sdkVersion = '10.0.100'; osDescription = 'self-test'; architecture = 'x64'
            cliBundleManifestSha256 = $cliManifestHash; skillManifestSha256 = $skillManifestHash
            evaluatorClosureSha256 = $evaluatorClosureHash; hostExecutableSha256 = Get-RawHash $hostPath
            hostExecutablePath = $hostPath; hostVersion = '1.0.0'; modelIdentity = 'private-model'
        }
        if ($v3) {
            $checkpoint['requestedModel'] = 'GPT-6 Sol'
            $checkpoint['inputs'] = @($taskInputs | ForEach-Object {
                    [ordered]@{
                        taskId = $_.taskId
                        taskSha256 = $_.taskSha256
                        qaLineSha256 = $_.qaLineSha256
                        fixtureSha256 = $_.fixtureSha256
                        inputClosureSha256 = $inputClosureHashes[[string]$_.taskId]
                    }
                })
        }
        else {
            $checkpoint['taskSha256'] = $taskInputs[0].taskSha256
            $checkpoint['qaLineSha256'] = $taskInputs[0].qaLineSha256
            $checkpoint['fixtureSha256'] = $taskInputs[0].fixtureSha256
            $checkpoint['inputClosureSha256'] = $inputClosureHashes[[string]$taskInputs[0].taskId]
        }
        Write-JsonFile (Join-Path $root 'checkpoint.json') $checkpoint
        $authorization = [ordered]@{
            schemaVersion = 1; protocolId = $protocolId; recordType = 'private-authorization'
            measuredExecutionAuthorized = $true; protocolSha256 = $protocolHash
            maximumHostSessions = $sessionTarget
            terminalAction = 'stop-after-ready-capture-report'
            authorizedAt = '2026-09-15T00:00:00Z'; authorizationEvidence = 'self-test authorization'
        }
        if ($v3) {
            $authorization['modelIdentitySha256'] = Get-TextHash ([string]$checkpoint.modelIdentity)
            $authorization['hostAiCreditLimit'] = 'none'
        }
        else { $authorization['maximumHostAiCredits'] = 240 }
        [string] $authorizationPath = Join-Path $root 'authorization.json'
        Write-JsonFile $authorizationPath $authorization
        [string] $authorizationHash = Get-RawHash $authorizationPath
        [string[]] $ownedEnvironment = @(
            'HOME', 'USERPROFILE', 'XDG_CONFIG_HOME', 'APPDATA', 'LOCALAPPDATA', 'COPILOT_HOME')
        [string[]] $knownTools = @(
            'powershell', 'read_powershell', 'stop_powershell', 'list_powershell',
            'apply_patch', 'view', 'web_fetch', 'fetch_copilot_cli_documentation',
            'skill', 'sql', 'session_store_sql', 'read_agent', 'list_agents',
            'write_agent', 'rg', 'glob', 'task')

        foreach ($pairNumber in 1..$pairTarget) {
            [string] $taskId = Get-ReadyCapturePairTaskId $protocolValue $pairNumber
            $taskInput = Get-ReadyCaptureInput $protocolValue $taskId
            [string] $packetId = '{0:x32}' -f $pairNumber
            [string] $firstBlindId = '{0:x32}' -f (100 + ($pairNumber * 2))
            [string] $secondBlindId = '{0:x32}' -f (101 + ($pairNumber * 2))
            $packet = [ordered]@{
                schemaVersion = 1; protocolId = $protocolId; recordType = 'blinded-packet'
                protocolSha256 = $protocolHash; packetId = $packetId
                answers = @(
                    [ordered]@{
                        blindId = $firstBlindId
                        answerAvailable = $pairNumber -ne 1
                        answer = if ($pairNumber -eq 1) { $null } else { 'first answer' }
                    },
                    [ordered]@{ blindId = $secondBlindId; answerAvailable = $true; answer = 'second answer' })
            }
            if ($v3) { $packet['taskId'] = $taskId }
            [string] $packetPath = Join-Path $root "pair-$pairNumber-packet.json"
            Write-JsonFile $packetPath $packet
            [string] $packetHash = Get-RawHash $packetPath
            $criterion = [ordered]@{ pass = $true; evidence = 'answer' }
            $noAnswerCriterion = [ordered]@{ pass = $false; evidence = 'no final answer' }
            $firstCriterion = if ($pairNumber -eq 1) { $noAnswerCriterion } else { $criterion }
            $grade = [ordered]@{
                schemaVersion = 1; protocolId = $protocolId; recordType = 'blinded-grade'
                protocolSha256 = $protocolHash; packetId = $packetId; packetSha256 = $packetHash
                gradeNonce = '{0:x32}' -f (200 + $pairNumber); graderRole = 'user'
                grades = @(
                    [ordered]@{
                        blindId = $firstBlindId
                        criteria = [ordered]@{ scope = $firstCriterion; 'attribution-restraint' = $firstCriterion; 'evidence-quality' = $firstCriterion; 'scope-preserving-drill' = $firstCriterion; 'unsupported-claim' = $firstCriterion }
                        falseConfidence = [ordered]@{ triggered = $false; evidence = 'none' }
                    },
                    [ordered]@{
                        blindId = $secondBlindId
                        criteria = [ordered]@{ scope = $criterion; 'attribution-restraint' = $criterion; 'evidence-quality' = $criterion; 'scope-preserving-drill' = $criterion; 'unsupported-claim' = $criterion }
                        falseConfidence = [ordered]@{ triggered = $false; evidence = 'none' }
                    })
            }
            if ($v3) { $grade['taskId'] = $taskId }
            [string] $gradePath = Join-Path $root "pair-$pairNumber-grade.json"
            Write-JsonFile $gradePath $grade
            [string] $gradeHash = Get-RawHash $gradePath
            [void]$gradeHashes.Add($gradeHash)

            [object[]] $armPositions = if ($protocolValue.sample.pairs[$pairNumber - 1].first -ceq 'cli-skill') {
                @([pscustomobject]@{ arm = 'cli-skill'; position = 'first'; blindId = $firstBlindId }, [pscustomobject]@{ arm = 'cli'; position = 'second'; blindId = $secondBlindId })
            }
            else {
                @([pscustomobject]@{ arm = 'cli'; position = 'first'; blindId = $firstBlindId }, [pscustomobject]@{ arm = 'cli-skill'; position = 'second'; blindId = $secondBlindId })
            }
            [System.Collections.Generic.List[object]] $entries = [System.Collections.Generic.List[object]]::new()
            foreach ($armPosition in $armPositions) {
                $answerRecord = @($packet.answers | Where-Object blindId -eq $armPosition.blindId)[0]
                $gradeRecord = @($grade.grades | Where-Object blindId -eq $armPosition.blindId)[0]
                $inputIdentity = [ordered]@{
                    task = $taskId
                    taskSha256 = $taskInput.taskSha256
                    qaSha256 = $taskInput.qaLineSha256
                    fixtureSha256 = $taskInput.fixtureSha256
                    inputClosureSha256 = $inputClosureHashes[$taskId]
                }
                [bool] $withSkill = [string]::Equals(
                    [string]$armPosition.arm, 'cli-skill', [StringComparison]::Ordinal)
                [object[]] $sessionSkillInventory = [object[]]::new(0)
                if ($withSkill) { $sessionSkillInventory = @($skillInventory) }
                [string[]] $availableTools = if ($withSkill) {
                    @('powershell', 'skill', 'view')
                }
                else { @('powershell') }
                [string[]] $excludedTools = @($knownTools | Where-Object {
                        $availableTools -cnotcontains $_
                    })
                $iteration = [ordered]@{
                        task = $taskId; iteration = 1; success = [bool]$answerRecord.answerAvailable; calls = 1; helpCalls = 0
                    tokens = 100; wallMs = 1000; hostUsage = [ordered]@{ premiumRequests = 1 }
                        hostUsageFile = [ordered]@{ available = $true; value = [ordered]@{ totalPremiumRequestCost = $creditsPerSession } }
                    answer = $answerRecord.answer; transcript = @([ordered]@{ kind = 'filtrace' })
                    execution = [ordered]@{
                        nativeTimeoutSeconds = 600
                        hostOutputMaxBytes = 10485760
                        capturedBytes = 1
                        artifactBytes = 1
                        hostRuntimeBytes = 1
                        processExitCode = 0
                        resultExitCode = 0
                        fixture = [ordered]@{ sha256 = $taskInput.fixtureSha256 }
                        cli = [ordered]@{ inventory = $cliInventory }
                        isolation = [ordered]@{
                            workspaceOutsideRepository = $true
                            inheritedEnvironment = @(Get-AgentEvalAllowedEnvironmentNames)
                            ownedEnvironment = $ownedEnvironment
                            noCustomInstructions = -not $withSkill
                            builtinMcpsDisabled = $true
                            availableTools = $availableTools
                            excludedTools = $excludedTools
                            shellDefaultDenied = $true
                            writeDenied = $true
                            urlDenied = $true
                            readDenied = $true
                            executionPolicyMaxCalls = 6
                            executionPolicyCallCount = 1
                            executionPolicyMaxViewCalls = 4
                            executionPolicyMaxViewBytes = 67108864
                            dynamicArtifactMaxBytes = 16777216
                            hostRuntimeMaxBytes = 268435456
                            hostRuntimeMaxFileBytes = 134217728
                            hostRuntimeMaxEntries = 1024
                            processTreeContained = $true
                            maxAiCredits = $creditLimitSentinel
                        }
                        inputPolicy = [ordered]@{
                            fixtureMaxBytes = 536870912
                            cliMaxFiles = 256
                            cliMaxEntries = 512
                            cliMaxBytes = 536870912
                            skillMaxFiles = 64
                            skillMaxEntries = 128
                            skillMaxBytes = 16777216
                            skillMaxViewCalls = 4
                            skillMaxRequestedBytes = 67108864
                        }
                    }
                    skill = [ordered]@{
                        provided = $withSkill
                        observed = $withSkill
                        verified = $withSkill
                        inventory = $sessionSkillInventory
                        discovery = if ($withSkill) {
                            [ordered]@{ enabled = $true; source = 'project' }
                        }
                        else { $null }
                    }
                }
                $result = [ordered]@{
                    schemaVersion = 3
                    host = 'copilot'
                    configuration = 'Release'
                    authorizationSha256 = $authorizationHash
                    model = [ordered]@{
                        expected = 'private-model'
                        observed = 'private-model'
                        verified = $true
                    }
                    arm = $armPosition.arm
                    label = Get-ReadyCaptureLabel $protocolValue $pairNumber ([string]$armPosition.position) ([string]$armPosition.arm)
                    n = 1
                    maxSteps = 6
                    strictRunBudget = [ordered]@{ projectedBytes = 1; maxBytes = 2147483648 }
                    inputIdentity = @($inputIdentity)
                    iterations = @($iteration)
                }
                [string] $resultPath = Join-Path $root "pair-$pairNumber-$($armPosition.position)-result.json"
                Write-JsonFile $resultPath $result
                [string] $resultHash = Get-RawHash $resultPath
                [void]$resultHashes.Add($resultHash)
                [void]$entries.Add([ordered]@{ blindId = $armPosition.blindId; arm = $armPosition.arm; position = $armPosition.position; resultSha256 = $resultHash })
                [void]$sessions.Add([ordered]@{
                    pair = $pairNumber; position = $armPosition.position; arm = $armPosition.arm; resultSha256 = $resultHash
                    authorizationSha256 = $authorizationHash
                    success = [bool]$iteration.success; answerAvailable = [bool]$answerRecord.answerAvailable; falseConfidence = $false; calls = 1; helpCalls = 0
                    tokens = 100; wallMs = 1000; hostAiCredits = $creditsPerSession; hostUsage = $iteration.hostUsage
                    hostUsageFile = $iteration.hostUsageFile; answerCriteria = $gradeRecord.criteria
                    transcript = $iteration.transcript; inputIdentity = $inputIdentity
                })
                if ($v3) { $sessions[$sessions.Count - 1]['taskId'] = $taskId }
            }
            $map = [ordered]@{
                schemaVersion = 1; protocolId = $protocolId; recordType = 'private-arm-map'
                protocolSha256 = $protocolHash; pair = $pairNumber; packetId = $packetId
                packetSha256 = $packetHash; gradeSha256 = $gradeHash; entries = @($entries)
            }
            if ($v3) { $map['taskId'] = $taskId }
            Write-JsonFile (Join-Path $root "pair-$pairNumber-map.json") $map
            [void]$maps.Add($map)
        }

        $zeroDeltas = [ordered]@{ analysisCalls = 0; helpCalls = 0; resultTokens = 0; wallMs = 0; hostAiCredits = 0 }
        [object[]] $pairReports = @(1..$pairTarget | ForEach-Object {
                [int] $pairNumber = $_
                [object[]] $pairSessions = @($sessions | Where-Object { $_.pair -eq $pairNumber })
                $pairReport = [ordered]@{
                    pair = $pairNumber
                    firstArm = @($pairSessions | Where-Object position -eq 'first')[0].arm
                    qualityState = Get-PairedQualityState `
                        -SkillSession @($pairSessions | Where-Object arm -eq 'cli-skill')[0] `
                        -CliSession @($pairSessions | Where-Object arm -eq 'cli')[0]
                    deltas = $zeroDeltas
                }
                if ($v3) {
                    $pairReport['taskId'] = Get-ReadyCapturePairTaskId $protocolValue $pairNumber
                    $pairReport['skill'] = @($pairSessions | Where-Object arm -eq 'cli-skill')[0]
                    $pairReport['cli'] = @($pairSessions | Where-Object arm -eq 'cli')[0]
                }
                $pairReport
            })
        [string] $reportQuality = Get-ReadyCaptureQualityState -Pairs $pairReports
        [object[]] $orderSummaries = @(@('cli-skill', 'cli') | ForEach-Object {
                [string] $firstArm = $_
                [object[]] $group = @($pairReports | Where-Object firstArm -eq $firstArm)
                [ordered]@{
                    firstArm = $firstArm
                    pairCount = $group.Count
                    qualityState = Get-QualityAggregate -Pairs $group
                    medianDeltas = $zeroDeltas
                }
            })
        $report = [ordered]@{
            schemaVersion = 1; protocolId = $protocolId; recordType = 'final-report'
            protocolSha256 = $protocolHash; terminalDisposition = 'descriptive-complete'; validPairs = $pairTarget
            terminalReason = "$pairTarget-valid-pairs"
            hostSessions = $sessionTarget; hostAiCredits = ($sessionTarget * $creditsPerSession)
            authorizationSha256 = $authorizationHash
            sessionResultSha256 = @($resultHashes)
            gradeSha256 = @($gradeHashes); sessions = @($sessions)
            invalidSessions = @()
            armSummaries = @(
                (New-ArmSummary -Sessions @($sessions) -Arm 'cli-skill'),
                (New-ArmSummary -Sessions @($sessions) -Arm 'cli'))
            pairedDeltas = $pairReports
            orderSummaries = $orderSummaries
            qualityState = $reportQuality
            costStates = [ordered]@{ analysisCalls = 'practically-equivalent'; helpCalls = 'practically-equivalent'; resultTokens = 'practically-equivalent'; wallMs = 'practically-equivalent'; hostAiCredits = 'practically-equivalent' }
            nextAction = 'stop-and-return-to-user'
        }
        if ($v3) {
            $report['pairedDeltas'] = @($pairReports | ForEach-Object {
                    [ordered]@{
                        taskId = $_.taskId
                        pair = $_.pair
                        firstArm = $_.firstArm
                        qualityState = $_.qualityState
                        deltas = $_.deltas
                    }
                })
            $report['taskSummaries'] = @($taskInputs | ForEach-Object {
                    New-ReadyCaptureTaskSummary -TaskId ([string]$_.taskId) `
                        -Sessions @($sessions) -Pairs $pairReports
                })
        }
        Write-JsonFile (Join-Path $root 'final-report.json') $report
        $result = Test-ArtifactSet $root $ProtocolPath $SchemaPath $true $false 'Final' $null
        if ($result.pairs -ne $pairTarget -or $result.sessions -ne $sessionTarget) {
            throw 'Ready-capture self-test did not validate the complete artifact set.'
        }

        $incompleteCompleteReport = ($report | ConvertTo-Json -Depth 100) | ConvertFrom-Json
        $incompleteCompleteReport.terminalDisposition = 'incomplete-precondition'
        $incompleteCompleteReport.terminalReason = "precondition:self-test after $pairTarget pairs"
        $incompleteCompleteReport.armSummaries = @()
        $incompleteCompleteReport.pairedDeltas = @()
        $incompleteCompleteReport.orderSummaries = @()
        if ($v3) { $incompleteCompleteReport.taskSummaries = @() }
        $incompleteCompleteReport.qualityState = 'unavailable'
        foreach ($property in $incompleteCompleteReport.costStates.PSObject.Properties) {
            $property.Value = 'unavailable'
        }
        Write-JsonFile (Join-Path $root 'final-report.json') $incompleteCompleteReport
        [bool] $incompleteCompleteRejected = $false
        try { [void](Test-ArtifactSet $root $ProtocolPath $SchemaPath $false $false 'Final' $null) }
        catch {
            $incompleteCompleteRejected = $_.Exception.Message.Contains(
                "$pairTarget valid pairs require", [StringComparison]::Ordinal)
        }
        if (-not $incompleteCompleteRejected) {
            throw 'Ready-capture complete-pair incomplete disposition unexpectedly passed.'
        }
        Write-JsonFile (Join-Path $root 'final-report.json') $report

        [string] $zeroSessionRoot = "$root-zero-session"
        [System.IO.Directory]::CreateDirectory($zeroSessionRoot) | Out-Null
        try {
            Copy-Item -LiteralPath (Join-Path $root 'checkpoint.json') -Destination $zeroSessionRoot
            $zeroSessionReport = ($report | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $zeroSessionReport.terminalDisposition = 'incomplete-precondition'
            $zeroSessionReport.terminalReason = 'precondition:self-test before first session'
            $zeroSessionReport.validPairs = 0
            $zeroSessionReport.hostSessions = 0
            $zeroSessionReport.hostAiCredits = 0
            $zeroSessionReport.authorizationSha256 = $null
            $zeroSessionReport.sessionResultSha256 = @()
            $zeroSessionReport.gradeSha256 = @()
            $zeroSessionReport.sessions = @()
            $zeroSessionReport.invalidSessions = @()
            $zeroSessionReport.armSummaries = @()
            $zeroSessionReport.pairedDeltas = @()
            $zeroSessionReport.orderSummaries = @()
            if ($v3) { $zeroSessionReport.taskSummaries = @() }
            $zeroSessionReport.qualityState = 'unavailable'
            foreach ($property in $zeroSessionReport.costStates.PSObject.Properties) {
                $property.Value = 'unavailable'
            }
            Write-JsonFile (Join-Path $zeroSessionRoot 'final-report.json') $zeroSessionReport
            [void](Test-ArtifactSet $zeroSessionRoot $ProtocolPath $SchemaPath $false $true 'Final' $null)
        }
        finally {
            Remove-Item -LiteralPath $zeroSessionRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        [string] $preUnblindRoot = "$root-pre-unblind"
        Copy-Item -LiteralPath $root -Destination $preUnblindRoot -Recurse
        try {
            Remove-Item -LiteralPath (Join-Path $preUnblindRoot 'final-report.json')
            [void](Test-ArtifactSet $preUnblindRoot $ProtocolPath $SchemaPath $false $false 'PreUnblind' $null)
        }
        finally {
            Remove-Item -LiteralPath $preUnblindRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        [string] $prefixRoot = "$root-finalized-prefix"
        Copy-Item -LiteralPath $root -Destination $prefixRoot -Recurse
        try {
            Get-ChildItem -LiteralPath $prefixRoot -File | Where-Object {
                $_.Name -match '^pair-[2-8]-'
            } | Remove-Item -Force
            $prefixReport = ($report | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $prefixReport.terminalDisposition = 'incomplete-precondition'
            $prefixReport.terminalReason = 'precondition:self-test before pair 2'
            $prefixReport.validPairs = 1
            $prefixReport.hostSessions = 2
            $prefixReport.hostAiCredits = 2 * $creditsPerSession
            $prefixReport.sessionResultSha256 = @($prefixReport.sessionResultSha256 | Select-Object -First 2)
            $prefixReport.gradeSha256 = @($prefixReport.gradeSha256 | Select-Object -First 1)
            $prefixReport.sessions = @($prefixReport.sessions | Select-Object -First 2)
            $prefixReport.invalidSessions = @()
            $prefixReport.armSummaries = @()
            $prefixReport.pairedDeltas = @()
            $prefixReport.orderSummaries = @()
            if ($v3) { $prefixReport.taskSummaries = @() }
            $prefixReport.qualityState = 'unavailable'
            foreach ($property in $prefixReport.costStates.PSObject.Properties) {
                $property.Value = 'unavailable'
            }
            Write-JsonFile (Join-Path $prefixRoot 'final-report.json') $prefixReport
            [void](Test-ArtifactSet $prefixRoot $ProtocolPath $SchemaPath $false $false 'Final' $null)

            $prefixReport.terminalDisposition = 'incomplete-budget'
            $prefixReport.terminalReason = 'budget:host-output-bytes'
            Write-JsonFile (Join-Path $prefixRoot 'final-report.json') $prefixReport
            [bool] $unsupportedBudgetRejected = $false
            try { [void](Test-ArtifactSet $prefixRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
            catch { $unsupportedBudgetRejected = $true }
            if (-not $unsupportedBudgetRejected) {
                throw 'Ready-capture unsupported early budget stop unexpectedly passed.'
            }
        }
        finally {
            Remove-Item -LiteralPath $prefixRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        [string] $userStopRoot = "$root-user-stop"
        Copy-Item -LiteralPath $root -Destination $userStopRoot -Recurse
        try {
            Get-ChildItem -LiteralPath $userStopRoot -File | Where-Object {
                $_.Name -match '^pair-[3-8]-' -or
                $_.Name -in @('pair-2-packet.json', 'pair-2-grade.json', 'pair-2-map.json', 'pair-2-second-result.json')
            } | Remove-Item -Force
            $userReport = ($report | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $userReport.terminalDisposition = 'stopped-by-user'
            $userReport.terminalReason = 'user:self-test after pair 2 first session'
            $userReport.validPairs = 1
            $userReport.hostSessions = 3
            $userReport.hostAiCredits = 3 * $creditsPerSession
            $userReport.sessionResultSha256 = @($userReport.sessionResultSha256 | Select-Object -First 3)
            $userReport.gradeSha256 = @($userReport.gradeSha256 | Select-Object -First 1)
            $userReport.sessions = @($userReport.sessions | Select-Object -First 2)
            $userReport.invalidSessions = @([ordered]@{
                    pair = 2; position = 'first'; arm = $protocolValue.sample.pairs[1].first
                    resultSha256 = $resultHashes[2]; authorizationSha256 = $authorizationHash
                    reason = 'user:self-test stopped after session'; hostAiCredits = $creditsPerSession
                })
            if ($v3) {
                $userReport.invalidSessions[0]['taskId'] = Get-ReadyCapturePairTaskId $protocolValue 2
                $userReport.taskSummaries = @()
            }
            $userReport.armSummaries = @()
            $userReport.pairedDeltas = @()
            $userReport.orderSummaries = @()
            $userReport.qualityState = 'unavailable'
            foreach ($property in $userReport.costStates.PSObject.Properties) {
                $property.Value = 'unavailable'
            }
            Write-JsonFile (Join-Path $userStopRoot 'final-report.json') $userReport
            [void](Test-ArtifactSet $userStopRoot $ProtocolPath $SchemaPath $false $false 'Final' $null)
        }
        finally {
            Remove-Item -LiteralPath $userStopRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        [string] $incompleteRoot = "$root-incomplete"
        [System.IO.Directory]::CreateDirectory($incompleteRoot) | Out-Null
        try {
            Copy-Item -LiteralPath (Join-Path $root 'checkpoint.json') -Destination $incompleteRoot
            Copy-Item -LiteralPath (Join-Path $root 'authorization.json') -Destination $incompleteRoot
            [string] $invalidResultName = 'pair-1-first-result.json'
            [string] $invalidResultPath = Join-Path $incompleteRoot $invalidResultName
            $invalidResultValue = [System.IO.File]::ReadAllText((Join-Path $root $invalidResultName)) |
                ConvertFrom-Json -Depth 100
            $invalidResultValue.iterations[0].execution.processExitCode = 1
            Write-JsonFile $invalidResultPath $invalidResultValue
            [string] $invalidResultHash = Get-RawHash $invalidResultPath
            $incompleteReport = [ordered]@{
                schemaVersion = 1; protocolId = $protocolId; recordType = 'final-report'
                protocolSha256 = $protocolHash; terminalDisposition = 'incomplete-evidence-integrity'
                terminalReason = 'evidence:self-test invalid session'
                validPairs = 0; hostSessions = 1; hostAiCredits = $creditsPerSession
                authorizationSha256 = $authorizationHash
                sessionResultSha256 = @($invalidResultHash); gradeSha256 = @(); sessions = @()
                invalidSessions = @([ordered]@{
                        pair = 1; position = 'first'; arm = $protocolValue.sample.pairs[0].first
                        resultSha256 = $invalidResultHash; authorizationSha256 = $authorizationHash
                        reason = 'evidence:self-test invalid evidence'
                        hostAiCredits = $creditsPerSession
                    })
                armSummaries = @(); pairedDeltas = @(); orderSummaries = @()
                qualityState = 'unavailable'
                costStates = [ordered]@{ analysisCalls = 'unavailable'; helpCalls = 'unavailable'; resultTokens = 'unavailable'; wallMs = 'unavailable'; hostAiCredits = 'unavailable' }
                nextAction = 'stop-and-return-to-user'
            }
            if ($v3) {
                $incompleteReport['taskSummaries'] = @()
                $incompleteReport.invalidSessions[0]['taskId'] = Get-ReadyCapturePairTaskId $protocolValue 1
            }
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $incompleteReport
            [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null)

            $negativeAccountingCases = @(
                [pscustomobject]@{
                    name = 'negative wall time'
                    mutate = { param($value) $value.iterations[0].wallMs = -1 }
                },
                [pscustomobject]@{
                    name = 'negative projected bytes'
                    mutate = { param($value) $value.strictRunBudget.projectedBytes = -1 }
                },
                [pscustomobject]@{
                    name = 'negative captured bytes'
                    mutate = { param($value) $value.iterations[0].execution.capturedBytes = -1 }
                },
                [pscustomobject]@{
                    name = 'negative artifact bytes'
                    mutate = { param($value) $value.iterations[0].execution.artifactBytes = -1 }
                },
                [pscustomobject]@{
                    name = 'negative runtime bytes'
                    mutate = { param($value) $value.iterations[0].execution.hostRuntimeBytes = -1 }
                })
            foreach ($negativeAccountingCase in $negativeAccountingCases) {
                $negativeAccountingResult = [System.IO.File]::ReadAllText((Join-Path $root $invalidResultName)) |
                    ConvertFrom-Json -Depth 100
                & $negativeAccountingCase.mutate $negativeAccountingResult
                Write-JsonFile $invalidResultPath $negativeAccountingResult
                [string] $negativeAccountingHash = Get-RawHash $invalidResultPath
                $negativeAccountingReport = ($incompleteReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
                $negativeAccountingReport.sessionResultSha256 = @($negativeAccountingHash)
                $negativeAccountingReport.invalidSessions[0].resultSha256 = $negativeAccountingHash
                Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $negativeAccountingReport
                [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null)
            }

            Write-JsonFile $invalidResultPath $invalidResultValue
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $incompleteReport

            Copy-Item -LiteralPath (Join-Path $root $invalidResultName) -Destination $invalidResultPath -Force
            [string] $validNoAnswerHash = Get-RawHash $invalidResultPath
            $validNoAnswerReport = ($incompleteReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $validNoAnswerReport.sessionResultSha256 = @($validNoAnswerHash)
            $validNoAnswerReport.invalidSessions[0].resultSha256 = $validNoAnswerHash
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $validNoAnswerReport
            [bool] $validNoAnswerRejected = $false
            try { [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
            catch { $validNoAnswerRejected = $true }
            if (-not $validNoAnswerRejected) {
                throw 'Ready-capture valid no-answer result was accepted as invalid evidence.'
            }

            $secondOnlyValue = [System.IO.File]::ReadAllText((Join-Path $root 'pair-1-second-result.json')) |
                ConvertFrom-Json -Depth 100
            $secondOnlyValue.iterations[0].execution.processExitCode = 1
            Write-JsonFile $invalidResultPath $secondOnlyValue
            [string] $secondOnlyHash = Get-RawHash $invalidResultPath
            $secondOnlyReport = ($incompleteReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $secondOnlyReport.sessionResultSha256 = @($secondOnlyHash)
            $secondOnlyReport.invalidSessions[0].position = 'second'
            $secondOnlyReport.invalidSessions[0].arm = $protocolValue.sample.pairs[0].second
            $secondOnlyReport.invalidSessions[0].resultSha256 = $secondOnlyHash
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $secondOnlyReport
            [bool] $secondOnlyRejected = $false
            try { [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
            catch { $secondOnlyRejected = $true }
            if (-not $secondOnlyRejected) {
                throw 'Ready-capture second-position-only invalid session unexpectedly passed.'
            }

            Copy-Item -LiteralPath (Join-Path $root 'pair-1-first-result.json') -Destination $invalidResultPath -Force
            [string] $validFirstHash = Get-RawHash $invalidResultPath
            [string] $invalidSecondPath = Join-Path $incompleteRoot 'pair-1-second-result.json'
            Write-JsonFile $invalidSecondPath $secondOnlyValue
            [string] $invalidSecondHash = Get-RawHash $invalidSecondPath
            $twoSessionReport = ($incompleteReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $twoSessionReport.hostSessions = 2
            $twoSessionReport.hostAiCredits = 2 * $creditsPerSession
            $twoSessionReport.sessionResultSha256 = @($validFirstHash, $invalidSecondHash)
            $twoSessionReport.invalidSessions = @(
                [ordered]@{
                    pair = 1; position = 'first'; arm = $protocolValue.sample.pairs[0].first
                    resultSha256 = $validFirstHash; authorizationSha256 = $authorizationHash
                    reason = 'evidence:self-test retained valid first session'; hostAiCredits = $creditsPerSession
                },
                [ordered]@{
                    pair = 1; position = 'second'; arm = $protocolValue.sample.pairs[0].second
                    resultSha256 = $invalidSecondHash; authorizationSha256 = $authorizationHash
                    reason = 'evidence:self-test invalid second session'; hostAiCredits = $creditsPerSession
                })
            if ($v3) {
                foreach ($entry in $twoSessionReport.invalidSessions) {
                    $entry['taskId'] = Get-ReadyCapturePairTaskId $protocolValue 1
                }
            }
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $twoSessionReport
            [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null)

            Remove-Item -LiteralPath $invalidSecondPath -Force
            Write-JsonFile $invalidResultPath $invalidResultValue
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $incompleteReport

            $invalidCreditsReport = ($incompleteReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidCreditsReport.invalidSessions[0].hostAiCredits = 0
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $invalidCreditsReport
            [bool] $invalidCreditsRejected = $false
            try { [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
            catch { $invalidCreditsRejected = $true }
            if (-not $invalidCreditsRejected) { throw 'Ready-capture invalid-session credit mutation unexpectedly passed.' }

            $invalidDispositionReport = ($incompleteReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $invalidDispositionReport.terminalDisposition = 'stopped-by-user'
            $invalidDispositionReport.terminalReason = 'user:self-test'
            $invalidDispositionReport.invalidSessions[0].reason = 'user:self-test'
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $invalidDispositionReport
            [bool] $invalidDispositionRejected = $false
            try { [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
            catch { $invalidDispositionRejected = $true }
            if (-not $invalidDispositionRejected) { throw 'Ready-capture terminal-disposition mutation unexpectedly passed.' }

            $budgetCases = @(
                if (-not $v3) {
                    [pscustomobject]@{
                        reason = 'budget:host-ai-credits'
                        mutate = { param($value) $value.iterations[0].hostUsageFile.value.totalPremiumRequestCost = 31 }
                        credits = 31
                    }
                }
                [pscustomobject]@{
                    reason = 'budget:wall-ms'
                    mutate = { param($value) $value.iterations[0].wallMs = 600001 }
                    credits = $creditsPerSession
                },
                [pscustomobject]@{
                    reason = 'budget:host-output-bytes'
                    mutate = { param($value) $value.iterations[0].execution.capturedBytes = 10485761 }
                    credits = $creditsPerSession
                },
                [pscustomobject]@{
                    reason = 'budget:host-artifact-bytes'
                    mutate = { param($value) $value.iterations[0].execution.artifactBytes = 16777217 }
                    credits = $creditsPerSession
                },
                [pscustomobject]@{
                    reason = 'budget:host-runtime-bytes'
                    mutate = { param($value) $value.iterations[0].execution.hostRuntimeBytes = 268435457 }
                    credits = $creditsPerSession
                },
                [pscustomobject]@{
                    reason = 'budget:projected-retained-bytes'
                    mutate = { param($value) $value.strictRunBudget.projectedBytes = 2147483649 }
                    credits = $creditsPerSession
                })
            foreach ($budgetCase in $budgetCases) {
                $budgetResult = ($invalidResultValue | ConvertTo-Json -Depth 100) | ConvertFrom-Json
                $budgetResult.iterations[0].execution.processExitCode = 0
                & $budgetCase.mutate $budgetResult
                Write-JsonFile $invalidResultPath $budgetResult
                [string] $budgetResultHash = Get-RawHash $invalidResultPath
                $budgetReport = ($incompleteReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
                $budgetReport.terminalDisposition = 'incomplete-budget'
                $budgetReport.terminalReason = $budgetCase.reason
                $budgetReport.hostAiCredits = $budgetCase.credits
                $budgetReport.sessionResultSha256 = @($budgetResultHash)
                $budgetReport.invalidSessions[0].resultSha256 = $budgetResultHash
                $budgetReport.invalidSessions[0].reason = $budgetCase.reason
                $budgetReport.invalidSessions[0].hostAiCredits = $budgetCase.credits
                Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $budgetReport
                [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null)
            }

            if ($v3) {
                $noCreditCapReport = ($budgetReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
                $noCreditCapReport.terminalReason = 'budget:host-ai-credits'
                $noCreditCapReport.invalidSessions[0].reason = 'budget:host-ai-credits'
                Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $noCreditCapReport
                [bool] $inventedCreditCapRejected = $false
                try { [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
                catch { $inventedCreditCapRejected = $true }
                if (-not $inventedCreditCapRejected) {
                    throw 'EP1 v3 invented host AI credit ceiling unexpectedly passed.'
                }
            }

            $budgetAsEvidenceReport = ($budgetReport | ConvertTo-Json -Depth 100) | ConvertFrom-Json
            $budgetAsEvidenceReport.terminalDisposition = 'incomplete-evidence-integrity'
            $budgetAsEvidenceReport.terminalReason = 'evidence:self-test relabeled budget failure'
            $budgetAsEvidenceReport.invalidSessions[0].reason = 'evidence:self-test relabeled budget failure'
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $budgetAsEvidenceReport
            [bool] $budgetAsEvidenceRejected = $false
            try { [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
            catch { $budgetAsEvidenceRejected = $true }
            if (-not $budgetAsEvidenceRejected) {
                throw 'Ready-capture budget failure was accepted as generic evidence failure.'
            }

            $budgetReport.terminalReason = 'budget:host-output-bytes'
            $budgetReport.invalidSessions[0].reason = 'budget:host-output-bytes'
            Write-JsonFile (Join-Path $incompleteRoot 'final-report.json') $budgetReport
            [bool] $wrongBudgetDimensionRejected = $false
            try { [void](Test-ArtifactSet $incompleteRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
            catch { $wrongBudgetDimensionRejected = $true }
            if (-not $wrongBudgetDimensionRejected) {
                throw 'Ready-capture wrong budget dimension unexpectedly passed.'
            }
        }
        finally {
            Remove-Item -LiteralPath $incompleteRoot -Recurse -Force -ErrorAction SilentlyContinue
        }

        if ($v3) {
            [string] $modelRoot = "$root-model-mismatch"
            Copy-Item -LiteralPath $root -Destination $modelRoot -Recurse
            try {
                [string] $modelResultPath = Join-Path $modelRoot 'pair-5-first-result.json'
                $modelResult = [System.IO.File]::ReadAllText($modelResultPath) | ConvertFrom-Json -Depth 100
                $modelResult.model.observed = 'gpt-5.6-sol'
                Write-JsonFile $modelResultPath $modelResult
                [string] $changedResultHash = Get-RawHash $modelResultPath
                [string] $modelMapPath = Join-Path $modelRoot 'pair-5-map.json'
                $modelMap = [System.IO.File]::ReadAllText($modelMapPath) | ConvertFrom-Json -Depth 100
                @($modelMap.entries | Where-Object position -eq 'first')[0].resultSha256 = $changedResultHash
                Write-JsonFile $modelMapPath $modelMap
                $modelReport = ($report | ConvertTo-Json -Depth 100) | ConvertFrom-Json
                $modelReport.sessionResultSha256[8] = $changedResultHash
                $modelReport.sessions[8].resultSha256 = $changedResultHash
                Write-JsonFile (Join-Path $modelRoot 'final-report.json') $modelReport
                [bool] $modelMismatchRejected = $false
                try { [void](Test-ArtifactSet $modelRoot $ProtocolPath $SchemaPath $true $false 'Final' $null) }
                catch {
                    $modelMismatchRejected = $_.Exception.Message.Contains(
                        'session identity or machine evidence', [StringComparison]::Ordinal)
                }
                if (-not $modelMismatchRejected) {
                    throw 'EP1 v3 accepted a changed host model despite reconciled artifact hashes.'
                }
            }
            finally {
                Remove-Item -LiteralPath $modelRoot -Recurse -Force -ErrorAction SilentlyContinue
            }

            [string] $lateStopRoot = "$root-invalid-pair-five"
            Copy-Item -LiteralPath $root -Destination $lateStopRoot -Recurse
            try {
                Get-ChildItem -LiteralPath $lateStopRoot -File | Where-Object {
                    $_.Name -match '^pair-[5-8]-'
                } | Remove-Item -Force
                [string] $lateResultPath = Join-Path $lateStopRoot 'pair-5-first-result.json'
                $lateResult = [System.IO.File]::ReadAllText((Join-Path $root 'pair-5-first-result.json')) |
                    ConvertFrom-Json -Depth 100
                $lateResult.iterations[0].execution.processExitCode = 1
                Write-JsonFile $lateResultPath $lateResult
                [string] $lateHash = Get-RawHash $lateResultPath
                $lateReport = ($report | ConvertTo-Json -Depth 100) | ConvertFrom-Json
                $lateReport.terminalDisposition = 'incomplete-evidence-integrity'
                $lateReport.terminalReason = 'evidence:self-test invalid ninth session'
                $lateReport.validPairs = 4
                $lateReport.hostSessions = 9
                $lateReport.hostAiCredits = 9 * $creditsPerSession
                $lateReport.sessionResultSha256 = @($lateReport.sessionResultSha256 | Select-Object -First 8) + @($lateHash)
                $lateReport.gradeSha256 = @($lateReport.gradeSha256 | Select-Object -First 4)
                $lateReport.sessions = @($lateReport.sessions | Select-Object -First 8)
                $lateReport.invalidSessions = @([ordered]@{
                        taskId = Get-ReadyCapturePairTaskId $protocolValue 5
                        pair = 5
                        position = 'first'
                        arm = $protocolValue.sample.pairs[4].first
                        resultSha256 = $lateHash
                        authorizationSha256 = $authorizationHash
                        reason = 'evidence:self-test invalid ninth session'
                        hostAiCredits = $creditsPerSession
                    })
                $lateReport.armSummaries = @()
                $lateReport.pairedDeltas = @()
                $lateReport.orderSummaries = @()
                $lateReport.taskSummaries = @()
                $lateReport.qualityState = 'unavailable'
                foreach ($property in $lateReport.costStates.PSObject.Properties) {
                    $property.Value = 'unavailable'
                }
                Write-JsonFile (Join-Path $lateStopRoot 'final-report.json') $lateReport
                $lateValidation = Test-ArtifactSet $lateStopRoot $ProtocolPath $SchemaPath $false $false 'Final' $null
                if ($lateValidation.pairs -ne 4 -or $lateValidation.sessions -ne 9) {
                    throw 'EP1 v3 did not retain the stopped ninth session.'
                }

                [string] $replacementPath = Join-Path $lateStopRoot 'pair-6-first-result.json'
                Copy-Item -LiteralPath (Join-Path $root 'pair-6-first-result.json') -Destination $replacementPath
                $lateReport.hostSessions = 10
                $lateReport.hostAiCredits = 10 * $creditsPerSession
                $lateReport.sessionResultSha256 += (Get-RawHash $replacementPath)
                $lateReport.invalidSessions += [ordered]@{
                    taskId = Get-ReadyCapturePairTaskId $protocolValue 6
                    pair = 6
                    position = 'first'
                    arm = $protocolValue.sample.pairs[5].first
                    resultSha256 = Get-RawHash $replacementPath
                    authorizationSha256 = $authorizationHash
                    reason = 'evidence:self-test attempted replacement'
                    hostAiCredits = $creditsPerSession
                }
                Write-JsonFile (Join-Path $lateStopRoot 'final-report.json') $lateReport
                [bool] $replacementRejected = $false
                try { [void](Test-ArtifactSet $lateStopRoot $ProtocolPath $SchemaPath $false $false 'Final' $null) }
                catch { $replacementRejected = $true }
                if (-not $replacementRejected) {
                    throw 'EP1 v3 accepted a replacement session after the invalid ninth session.'
                }
            }
            finally {
                Remove-Item -LiteralPath $lateStopRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        $mutations = @(
            @{ Name = 'grade blind ids'; File = 'pair-1-grade.json'; Change = { param($v) $v.grades[1].blindId = 'f' * 32 } },
            @{ Name = 'answer availability'; File = 'pair-1-packet.json'; Change = { param($v) $v.answers[0].answerAvailable = $true } },
            @{ Name = 'packet order'; File = 'pair-1-packet.json'; Change = { param($v) $v.answers = @($v.answers[1], $v.answers[0]) } },
            @{ Name = 'arm-map blind ids'; File = 'pair-1-map.json'; Change = { param($v) $v.entries[1].blindId = 'e' * 32 } },
            @{ Name = 'pair schedule'; File = 'pair-2-map.json'; Change = { param($v) $first = $v.entries[0].arm; $v.entries[0].arm = $v.entries[1].arm; $v.entries[1].arm = $first } },
            @{ Name = 'protocol hash'; File = 'checkpoint.json'; Change = { param($v) $v.protocolSha256 = $zeroHash } },
            @{ Name = 'authorization'; File = 'authorization.json'; Change = { param($v) $v.maximumHostSessions = 7 } },
            @{ Name = 'packet hash'; File = 'pair-1-map.json'; Change = { param($v) $v.packetSha256 = $zeroHash } },
            @{ Name = 'grade hash'; File = 'pair-1-map.json'; Change = { param($v) $v.gradeSha256 = $zeroHash } },
            @{ Name = 'result hash'; File = 'pair-1-map.json'; Change = { param($v) $v.entries[0].resultSha256 = $zeroHash } },
            @{ Name = 'arm balance'; File = 'pair-1-map.json'; Change = { param($v) $v.entries[1].arm = $v.entries[0].arm } },
            @{ Name = 'report hashes'; File = 'final-report.json'; Change = { param($v) $v.sessionResultSha256 = @($v.sessionResultSha256 | Select-Object -First 7) } },
            @{ Name = 'report session'; File = 'final-report.json'; Change = { param($v) $v.sessions[0].tokens++ } },
            @{ Name = 'report aggregate'; File = 'final-report.json'; Change = { param($v) $v.armSummaries[0].metrics.resultTokens.median++ } }
        )
        if ($v3) {
            $mutations += @(
                @{ Name = 'cross-task map'; File = 'pair-2-map.json'; Change = { param($v) $v.taskId = 'quality-first-unresolved' } },
                @{ Name = 'packet arm leak'; File = 'pair-2-packet.json'; Change = { param($v) $v | Add-Member -NotePropertyName arm -NotePropertyValue 'cli-skill' } },
                @{ Name = 'wrong checkpoint model'; File = 'checkpoint.json'; Change = { param($v) $v.requestedModel = 'GPT-5.6 Sol' } },
                @{ Name = 'wrong checkpoint skill manifest'; File = 'checkpoint.json'; Change = { param($v) $v.skillManifestSha256 = $zeroHash } },
                @{ Name = 'invented authorization credit cap'; File = 'authorization.json'; Change = { param($v) $v | Add-Member -NotePropertyName maximumHostAiCredits -NotePropertyValue 240 } },
                @{ Name = 'task aggregate'; File = 'final-report.json'; Change = { param($v) $v.taskSummaries[1].armSummaries[0].metrics.wallMs.minimum++ } }
            )
        }
        foreach ($mutation in $mutations) {
            [string] $copy = Join-Path ([System.IO.Path]::GetDirectoryName($root)) "$(Split-Path $root -Leaf)-$($mutation.Name)"
            Copy-Item -LiteralPath $root -Destination $copy -Recurse
            try {
                [string] $path = Join-Path $copy $mutation.File
                $value = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json -Depth 100
                & $mutation.Change $value
                Write-JsonFile $path $value
                [bool] $rejected = $false
                try { [void](Test-ArtifactSet $copy $ProtocolPath $SchemaPath $true $false 'Final' $null) }
                catch { $rejected = $true }
                if (-not $rejected) { throw "Ready-capture mutation '$($mutation.Name)' unexpectedly passed." }
            }
            finally {
                Remove-Item -LiteralPath $copy -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        [string] $alternateSchema = "$root-alternate-schema.json"
        try {
            [System.IO.File]::WriteAllText(
                $alternateSchema,
                ([System.IO.File]::ReadAllText($SchemaPath) + "`n"),
                [Text.UTF8Encoding]::new($false))
            [bool] $alternateSchemaRejected = $false
            try { [void](Test-ArtifactSet $root $ProtocolPath $alternateSchema $true $false 'Final' $null) }
            catch { $alternateSchemaRejected = $true }
            if (-not $alternateSchemaRejected) { throw 'Ready-capture alternate-schema mutation unexpectedly passed.' }
        }
        finally {
            Remove-Item -LiteralPath $alternateSchema -Force -ErrorAction SilentlyContinue
        }
        [string] $symlinkRoot = "$root-symlink"
        [string] $symlinkTarget = "$root-symlink-target.json"
        Copy-Item -LiteralPath $root -Destination $symlinkRoot -Recurse
        try {
            [System.IO.File]::WriteAllText($symlinkTarget, '{}', [Text.UTF8Encoding]::new($false))
            [void][System.IO.File]::CreateSymbolicLink(
                (Join-Path $symlinkRoot 'outside.json'),
                $symlinkTarget)
            [bool] $symlinkRejected = $false
            try { [void](Test-ArtifactSet $symlinkRoot $ProtocolPath $SchemaPath $true $false 'Final' $null) }
            catch { $symlinkRejected = $_.Exception.Message.Contains('reparse point', [StringComparison]::Ordinal) }
            if (-not $symlinkRejected) { throw 'Ready-capture symlink mutation unexpectedly passed.' }
        }
        finally {
            Remove-Item -LiteralPath $symlinkRoot -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $symlinkTarget -Force -ErrorAction SilentlyContinue
        }
        [string] $nestedJsonRoot = "$root-nested-json"
        Copy-Item -LiteralPath $root -Destination $nestedJsonRoot -Recurse
        try {
            [string] $nestedDirectory = Join-Path $nestedJsonRoot 'nested'
            [System.IO.Directory]::CreateDirectory($nestedDirectory) | Out-Null
            [System.IO.File]::WriteAllText(
                (Join-Path $nestedDirectory 'hidden-result.json'),
                '{}',
                [Text.UTF8Encoding]::new($false))
            [bool] $nestedJsonRejected = $false
            try { [void](Test-ArtifactSet $nestedJsonRoot $ProtocolPath $SchemaPath $true $false 'Final' $null) }
            catch { $nestedJsonRejected = $true }
            if (-not $nestedJsonRejected) { throw 'Ready-capture nested-JSON mutation unexpectedly passed.' }
        }
        finally {
            Remove-Item -LiteralPath $nestedJsonRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        [string] $emptyDirectoryRoot = "$root-empty-directory"
        Copy-Item -LiteralPath $root -Destination $emptyDirectoryRoot -Recurse
        try {
            [System.IO.Directory]::CreateDirectory((Join-Path $emptyDirectoryRoot 'empty')) | Out-Null
            [bool] $emptyDirectoryRejected = $false
            try { [void](Test-ArtifactSet $emptyDirectoryRoot $ProtocolPath $SchemaPath $true $false 'Final' $null) }
            catch {
                $emptyDirectoryRejected = $_.Exception.Message.Contains(
                    'only top-level files', [StringComparison]::Ordinal)
            }
            if (-not $emptyDirectoryRejected) {
                throw 'Ready-capture empty-directory mutation unexpectedly passed.'
            }
        }
        finally {
            Remove-Item -LiteralPath $emptyDirectoryRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host 'Ready-capture record contract passed.'
    }
    finally {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    Invoke-SelfTest
}
else {
    if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
        throw '-ArtifactDirectory is required unless -SelfTest is specified.'
    }
    $validated = Test-ArtifactSet $ArtifactDirectory $ProtocolPath $SchemaPath $RequireComplete $true $Phase $HostExecutablePath
    Write-Host "Ready-capture records validated: $($validated.pairs) pair(s), $($validated.sessions) session(s), $($validated.reports) report(s)."
}
