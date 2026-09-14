#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Compare two labeled agent-eval runs (baseline vs candidate) and report the delta.

.DESCRIPTION
  The tuning loop (M5) changes a surface the live runner presents - an MCP tool
  description/server instruction, CLI command/output behavior, or discovered
  project skill - and asks whether the change helped without regressing. Score a
  baseline and candidate with the same host, arm, model, task set, and bounds so
  only the measured surface changes, then run this to compare:

    ./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Arm cli-skill -Model <model-id> -ExpectedModel <model-id> -Tasks <task-id> -N 5 -Label baseline
    # ... edit the shipped skill, preserving every other run input ...
    ./eval/Invoke-AgentEval.ps1 -AgentHost copilot -Arm cli-skill -Model <model-id> -ExpectedModel <model-id> -Tasks <task-id> -N 5 -Label candidate
    ./eval/Compare-EvalRuns.ps1 -Baseline baseline -Candidate candidate

  Runs are paired by full identity (host/arm/model), so the report shows whether a
  change that helps one model regresses another - a paired descriptive signal a second
  host would otherwise provide - and runs from different hosts/arms (whose token
  and call counts are not comparable) never pair silently. The verdict is the
  configured regression policy:

    - a success drop on any task/run            -> REGRESSION
    - token growth over the tolerance (15%)      -> REGRESSION
    - a run present on only one side             -> REJECT (cannot verify)
    - otherwise: higher success, fewer calls, or
      a token drop over 5%                        -> improved; smaller deltas neutral

  Exit code is 1 if any regression or unpaired run is found (so it can gate a
  tuning round), else 0.

.PARAMETER Baseline
  The label of the baseline runs (as passed to Invoke-AgentEval.ps1 -Label).

.PARAMETER Candidate
  The label of the candidate runs.

.PARAMETER ResultsDir
  Where the result JSON files live. Defaults to eval/results.

.PARAMETER TokenGrowthTolerance
  Allowed fractional token growth before a task counts as a regression. Defaults
  to 0.15 (the design's budget).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [string]$ResultsDir,
    [double]$TokenGrowthTolerance = 0.15
)

$ErrorActionPreference = 'Stop'
[long] $MaxResultBytes = 256MB
if ([string]::Equals($Baseline, $Candidate, [StringComparison]::Ordinal)) {
  throw 'Baseline and candidate labels must differ.'
}
if ([double]::IsNaN($TokenGrowthTolerance) -or
  [double]::IsInfinity($TokenGrowthTolerance) -or
  $TokenGrowthTolerance -lt 0 -or $TokenGrowthTolerance -gt 1) {
  throw 'TokenGrowthTolerance must be a finite fraction from 0 through 1.'
}
if (-not $ResultsDir) { $ResultsDir = Join-Path $PSScriptRoot 'results' }
if (-not (Test-Path $ResultsDir)) { throw "No results directory at '$ResultsDir'. Run Invoke-AgentEval.ps1 -Label first." }

function Test-FiniteNumber($Value) {
  if ($Value -isnot [byte] -and $Value -isnot [sbyte] -and
    $Value -isnot [short] -and $Value -isnot [ushort] -and
    $Value -isnot [int] -and $Value -isnot [uint] -and
    $Value -isnot [long] -and $Value -isnot [ulong] -and
    $Value -isnot [float] -and $Value -isnot [double] -and
    $Value -isnot [decimal]) {
    return $false
    }

  [double] $number = [double]$Value
  return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)
}

function Test-JsonObject($Value) {
  return $null -ne $Value -and $Value -is [pscustomobject]
}

function Test-JsonArray($Value) {
  return $Value -is [object[]]
}

function Assert-ExactObjectMembers(
  $Value,
  [string[]] $RequiredMembers,
  [string[]] $AllowedMembers,
  [string] $Context) {
  if (-not (Test-JsonObject $Value)) { throw "$Context is not an object." }
  [string[]] $members = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
  if (@($RequiredMembers | Where-Object { $members -cnotcontains $_ }).Count -ne 0 -or
    @($members | Where-Object { $AllowedMembers -cnotcontains $_ }).Count -ne 0) {
    throw "$Context has malformed members."
  }
}

function Assert-UniqueJsonMembers(
  [System.Text.Json.JsonElement] $Element,
  [string] $Context) {
  if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
    [System.Collections.Generic.HashSet[string]] $names =
      [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
      if (-not $names.Add($property.Name)) {
        throw "$Context repeats member '$($property.Name)'."
      }
      Assert-UniqueJsonMembers -Element $property.Value -Context "$Context.$($property.Name)"
    }
  }
  elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
    foreach ($item in $Element.EnumerateArray()) {
      Assert-UniqueJsonMembers -Element $item -Context "$Context[]"
    }
  }
}

function Assert-ResultEvidenceSchema($Payload, [string] $Path) {
  foreach ($arrayField in @('summary', 'transport', 'inputIdentity', 'iterations', 'warnings')) {
    if (-not (Test-JsonArray $Payload.$arrayField)) {
      throw "Schema-v3 result '$Path' $arrayField is not an array."
    }
  }
  foreach ($row in @($Payload.transport)) {
    Assert-ExactObjectMembers `
      -Value $row `
      -RequiredMembers @('Task', 'MedText', 'MedStructured', 'MedWire', 'MedHostResult', 'Shape') `
      -AllowedMembers @('Task', 'MedText', 'MedStructured', 'MedWire', 'MedHostResult', 'Shape') `
      -Context "Schema-v3 result '$Path' transport row"
  }
  if ($null -ne $Payload.strictRunBudget) {
    Assert-ExactObjectMembers $Payload.strictRunBudget @('projectedBytes', 'maxBytes') @('projectedBytes', 'maxBytes') `
      "Schema-v3 result '$Path' strictRunBudget"
  }

  [string[]] $iterationMembers = @(
    'task', 'iteration', 'success', 'calls', 'helpCalls', 'tokens', 'wallMs',
    'textTokens', 'structuredTokens', 'wireTokens', 'hostResultTokens', 'resultShape',
    'hostUsage', 'hostUsageFile', 'observedModel', 'observedModels', 'operations',
    'attemptedOperations', 'execution', 'skill', 'answer', 'note', 'transcript')
  foreach ($iteration in @($Payload.iterations)) {
    Assert-ExactObjectMembers $iteration $iterationMembers $iterationMembers `
      "Schema-v3 result '$Path' iteration"
    foreach ($arrayField in @('observedModels', 'operations', 'attemptedOperations', 'transcript')) {
      if (-not (Test-JsonArray $iteration.$arrayField)) {
        throw "Schema-v3 result '$Path' iteration $arrayField is not an array."
      }
    }
    if ($null -ne $iteration.hostUsage) {
      Assert-ExactObjectMembers $iteration.hostUsage `
        @('premiumRequests', 'totalApiDurationMs', 'sessionDurationMs') `
        @('premiumRequests', 'totalApiDurationMs', 'sessionDurationMs', 'codeChanges') `
        "Schema-v3 result '$Path' hostUsage"
      if ($iteration.hostUsage.PSObject.Properties.Name -ccontains 'codeChanges') {
        Assert-ExactObjectMembers $iteration.hostUsage.codeChanges `
          @('filesModified', 'linesAdded', 'linesRemoved') `
          @('filesModified', 'linesAdded', 'linesRemoved') `
          "Schema-v3 result '$Path' hostUsage codeChanges"
      }
    }
    if ($null -ne $iteration.hostUsageFile) {
      Assert-ExactObjectMembers $iteration.hostUsageFile `
        @('available', 'bytes', 'path', 'reason', 'sha256', 'value') `
        @('available', 'bytes', 'path', 'reason', 'sha256', 'value') `
        "Schema-v3 result '$Path' hostUsageFile"
      if ($null -ne $iteration.hostUsageFile.value) {
        Assert-ExactObjectMembers $iteration.hostUsageFile.value `
          @('currentModel', 'tokenDetails', 'totalPremiumRequestCost', 'totalUserRequests') `
          @('currentModel', 'tokenDetails', 'totalPremiumRequestCost', 'totalUserRequests') `
          "Schema-v3 result '$Path' hostUsageFile value"
        Assert-ExactObjectMembers $iteration.hostUsageFile.value.tokenDetails `
          @('input', 'cache_read', 'cache_write', 'output') `
          @('input', 'cache_read', 'cache_write', 'output') `
          "Schema-v3 result '$Path' tokenDetails"
        foreach ($tokenKind in @('input', 'cache_read', 'cache_write', 'output')) {
          Assert-ExactObjectMembers $iteration.hostUsageFile.value.tokenDetails.$tokenKind `
            @('tokenCount') @('tokenCount') "Schema-v3 result '$Path' tokenDetails.$tokenKind"
        }
      }
    }

    Assert-ExactObjectMembers $iteration.execution `
      @('runId', 'workspace', 'capturedBytes', 'artifactBytes', 'hostRuntimeBytes',
        'processExitCode', 'resultExitCode', 'hostOutput', 'cli', 'fixture', 'isolation', 'inputPolicy') `
      @('runId', 'workspace', 'capturedBytes', 'artifactBytes', 'hostRuntimeBytes',
        'processExitCode', 'resultExitCode', 'hostOutput', 'cli', 'fixture', 'isolation', 'inputPolicy') `
      "Schema-v3 result '$Path' execution"
    if ($null -ne $iteration.execution.hostOutput) {
      Assert-ExactObjectMembers $iteration.execution.hostOutput `
        @('retained', 'stdoutPath', 'stderrPath', 'diagnosticPath', 'stdoutBytes', 'stderrBytes', 'artifactBytes') `
        @('retained', 'stdoutPath', 'stderrPath', 'diagnosticPath', 'stdoutBytes', 'stderrBytes', 'artifactBytes') `
        "Schema-v3 result '$Path' hostOutput"
    }
    Assert-ExactObjectMembers $iteration.execution.cli `
      @('sourcePath', 'sourceSha256', 'path', 'sha256', 'inventory') `
      @('sourcePath', 'sourceSha256', 'path', 'sha256', 'inventory') `
      "Schema-v3 result '$Path' CLI evidence"
    if (-not (Test-JsonArray $iteration.execution.cli.inventory)) {
      throw "Schema-v3 result '$Path' CLI inventory is not an array."
    }
    foreach ($file in @($iteration.execution.cli.inventory)) {
      Assert-ExactObjectMembers $file `
        @('sourcePath', 'path', 'relativePath', 'bytes', 'sha256') `
        @('sourcePath', 'path', 'relativePath', 'bytes', 'sha256') `
        "Schema-v3 result '$Path' CLI inventory entry"
    }
    Assert-ExactObjectMembers $iteration.execution.fixture @('path', 'sha256') @('path', 'sha256') `
      "Schema-v3 result '$Path' fixture evidence"
    [string[]] $isolationMembers = @(
      'workspaceOutsideRepository', 'inheritedEnvironment', 'ownedEnvironment',
      'noCustomInstructions', 'builtinMcpsDisabled', 'availableTools', 'excludedTools',
      'shellDefaultDenied', 'writeDenied', 'urlDenied', 'readDenied',
      'executionPolicyMaxCalls', 'executionPolicyCallCount', 'executionPolicyCommandHashes',
      'executionPolicyMaxHelpCalls', 'executionPolicyHelpCallCount', 'executionPolicyHelpCommandHashes',
      'executionPolicyMaxViewCalls', 'executionPolicyMaxViewBytes', 'executionPolicyViewRequests',
      'hookConfigurationPath', 'dynamicArtifactMaxBytes', 'hostRuntimeMaxBytes',
      'hostRuntimeMaxFileBytes', 'hostRuntimeMaxEntries', 'processTreeContained')
    Assert-ExactObjectMembers $iteration.execution.isolation $isolationMembers $isolationMembers `
      "Schema-v3 result '$Path' isolation evidence"
    foreach ($arrayField in @(
        'inheritedEnvironment', 'ownedEnvironment', 'availableTools', 'excludedTools',
        'executionPolicyCommandHashes', 'executionPolicyHelpCommandHashes',
        'executionPolicyViewRequests')) {
      if (-not (Test-JsonArray $iteration.execution.isolation.$arrayField)) {
        throw "Schema-v3 result '$Path' isolation $arrayField is not an array."
      }
    }
    foreach ($viewRequest in @($iteration.execution.isolation.executionPolicyViewRequests)) {
      Assert-ExactObjectMembers $viewRequest `
        @('requestHash', 'arguments', 'requestedBytes') `
        @('requestHash', 'arguments', 'requestedBytes') `
        "Schema-v3 result '$Path' view request"
      Assert-ExactObjectMembers $viewRequest.arguments @('path') @('path', 'view_range') `
        "Schema-v3 result '$Path' view arguments"
    }
    [string[]] $inputPolicyMembers = @(
      'fixtureTrackedAndClean', 'fixtureMaxBytes', 'cliMaxFiles', 'cliMaxEntries',
      'cliMaxBytes', 'cliFiles', 'cliEntries', 'cliBytes', 'skillMaxFiles',
      'skillMaxEntries', 'skillMaxBytes', 'skillMaxViewCalls', 'skillMaxRequestedBytes')
    Assert-ExactObjectMembers $iteration.execution.inputPolicy $inputPolicyMembers $inputPolicyMembers `
      "Schema-v3 result '$Path' inputPolicy"

    [string[]] $skillMembers = @(
      'provided', 'sourcePath', 'installedPath', 'sha256', 'sourceByteSha256',
      'sourceTextSha256', 'sourceContextSha256', 'textNormalization', 'inventory',
      'observed', 'observedSha256', 'observedTextSha256', 'verified', 'failure',
      'evidenceCallIds', 'sourceBytes', 'sourceChars', 'sourceLineCount',
      'sourceTerminalNewline', 'observedChars', 'returnedPayloadChars',
      'terminalNewlineOmissions', 'requestedBytes', 'reads', 'discovery')
    Assert-ExactObjectMembers $iteration.skill $skillMembers $skillMembers `
      "Schema-v3 result '$Path' skill evidence"
    foreach ($arrayField in @('inventory', 'evidenceCallIds', 'reads')) {
      if (-not (Test-JsonArray $iteration.skill.$arrayField)) {
        throw "Schema-v3 result '$Path' skill $arrayField is not an array."
      }
    }
    if ($null -ne $iteration.skill.discovery) {
      Assert-ExactObjectMembers $iteration.skill.discovery `
        @('name', 'commandName', 'source', 'enabled', 'path') `
        @('name', 'commandName', 'source', 'enabled', 'path', 'description', 'userInvocable') `
        "Schema-v3 result '$Path' skill discovery"
    }
    foreach ($file in @($iteration.skill.inventory)) {
      Assert-ExactObjectMembers $file @('path', 'sha256', 'bytes') @('path', 'sha256', 'bytes') `
        "Schema-v3 result '$Path' skill inventory entry"
    }
    [string[]] $readMembers = @(
      'callId', 'path', 'sourceBytes', 'sourceByteSha256', 'sourceTextSha256',
      'sourceLineCount', 'requestHash', 'startLine', 'endLine', 'clampedEndLine',
      'requestedBytes', 'contentChars', 'contentSha256', 'payloadChars', 'payloadSha256',
      'logicalPayloadChars', 'logicalPayloadSha256', 'payloadStartOffset',
      'payloadEndOffsetExclusive', 'payloadFirstLine', 'payloadLastLine', 'protocol',
      'terminalNewlineOmitted', 'continuationLine', 'coverageContinuationLine', 'warnings')
    foreach ($read in @($iteration.skill.reads)) {
      Assert-ExactObjectMembers $read $readMembers $readMembers `
        "Schema-v3 result '$Path' skill read"
    }
    [string[]] $transcriptMembers = @(
      'callId', 'kind', 'operation', 'cmd', 'ok', 'info', 'textTokens',
      'structuredTokens', 'wireTokens', 'hostResultTokens')
    foreach ($entry in @($iteration.transcript)) {
      Assert-ExactObjectMembers $entry $transcriptMembers $transcriptMembers `
        "Schema-v3 result '$Path' transcript entry"
    }
  }
}

function Get-ResultTimestamp([string] $Json, [string] $Path) {
  [System.Text.Json.JsonDocument] $document = [System.Text.Json.JsonDocument]::Parse($Json)
  try {
    if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
      throw "Result '$Path' is not an object."
    }
    [int] $timestampCount = 0
    [string] $timestampText = $null
    [bool] $timestampIsString = $false
    foreach ($property in $document.RootElement.EnumerateObject()) {
      if ([string]::Equals($property.Name, 'timestamp', [StringComparison]::Ordinal)) {
        $timestampCount++
        $timestampIsString = $property.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String
        if ($timestampIsString) { $timestampText = $property.Value.GetString() }
      }
    }
    [DateTimeOffset] $timestamp = [DateTimeOffset]::MinValue
    if ($timestampCount -ne 1 -or -not $timestampIsString -or
      -not [DateTimeOffset]::TryParseExact(
        $timestampText,
        'o',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$timestamp)) {
      throw "Result '$Path' has an invalid timestamp."
    }
    return $timestamp.ToUniversalTime()
  }
  finally {
    $document.Dispose()
  }
}

function Get-ResultMedian([object[]] $Values) {
  [long[]] $sorted = @($Values | ForEach-Object { [long]$_ } | Sort-Object)
  [int] $count = $sorted.Count
  if ($count -eq 0) { return 0 }
  if ($count % 2) { return [int]$sorted[[int][math]::Floor($count / 2)] }
  return [int][math]::Round(([double]$sorted[$count / 2 - 1] + [double]$sorted[$count / 2]) / 2.0)
}

function Assert-ResultPayload($Payload, [string] $Path) {
  if (-not (Test-JsonObject $Payload)) { throw "Result '$Path' is not an object." }
  [string[]] $requiredRoot = @('schemaVersion', 'host', 'arm', 'label', 'timestamp', 'summary')
  [string[]] $rootMembers = @($Payload.PSObject.Properties.Name)
  foreach ($member in $requiredRoot) {
    if ($rootMembers -cnotcontains $member) { throw "Result '$Path' is missing '$member'." }
  }

  if ($Payload.schemaVersion -isnot [long] -and $Payload.schemaVersion -isnot [int]) {
    throw "Result '$Path' has a non-integer schemaVersion."
  }
  [long] $schemaVersionValue = [long]$Payload.schemaVersion
  if ($schemaVersionValue -ne 2 -and $schemaVersionValue -ne 3) {
    throw "Result '$Path' has unsupported schemaVersion $schemaVersionValue."
  }
  [int] $schemaVersion = [int]$schemaVersionValue
  [string[]] $allowedRootMembers = if ($schemaVersion -eq 3) {
    @('schemaVersion', 'host', 'model', 'arm', 'label', 'n', 'maxSteps',
      'inputIdentity', 'strictRunBudget', 'mcpDll', 'tokenAccounting', 'warnings',
      'timestamp', 'summary', 'transport', 'iterations')
  }
  else {
    @('schemaVersion', 'host', 'model', 'arm', 'label', 'strictRunBudget', 'mcpDll',
      'tokenAccounting', 'warnings', 'timestamp', 'summary', 'transport')
  }
  if (@($rootMembers | Where-Object { $allowedRootMembers -cnotcontains $_ }).Count -ne 0) {
    throw "Result '$Path' has an unknown root member."
  }
  foreach ($member in @('host', 'arm', 'label')) {
    if ($Payload.$member -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Payload.$member)) {
      throw "Result '$Path' has an invalid '$member'."
    }
  }
  if (-not (Test-JsonArray $Payload.summary)) { throw "Result '$Path' summary is not an array." }
  [object[]] $summary = $Payload.summary
  if ($summary.Count -eq 0) { throw "Result '$Path' has an empty summary." }
  [System.Collections.Generic.HashSet[string]] $tasks = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  foreach ($row in $summary) {
    if (-not (Test-JsonObject $row)) { throw "Result '$Path' has a non-object summary row." }
    [string[]] $rowMembers = @($row.PSObject.Properties.Name)
    [string[]] $allowedRowMembers = @(
      'Task', 'Success%', 'MedCalls', 'MedTokens', 'MedMs',
      'MedHelpCalls', 'SuccessCount')
    if (@($rowMembers | Where-Object { $allowedRowMembers -cnotcontains $_ }).Count -ne 0) {
      throw "Result '$Path' summary row has an unknown member."
    }
    foreach ($member in @('Task', 'Success%', 'MedCalls', 'MedTokens', 'MedMs')) {
      if ($rowMembers -cnotcontains $member) { throw "Result '$Path' summary row is missing '$member'." }
    }
    if ($row.Task -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$row.Task)) {
      throw "Result '$Path' has an invalid summary task."
    }
    if (-not $tasks.Add([string]$row.Task)) { throw "Result '$Path' repeats summary task '$($row.Task)'." }
    foreach ($member in @('Success%', 'MedCalls', 'MedTokens', 'MedMs')) {
      if (-not (Test-FiniteNumber $row.$member)) { throw "Result '$Path' has an invalid '$member' value." }
    }
    [double] $success = [double]$row.'Success%'
    if ($success -lt 0 -or $success -gt 100 -or $success -ne [math]::Truncate($success)) {
      throw "Result '$Path' has an out-of-range Success% value."
    }
    foreach ($member in @('MedCalls', 'MedTokens', 'MedMs')) {
      if ([double]$row.$member -lt 0 -or [double]$row.$member -gt [int]::MaxValue -or
        [double]$row.$member -ne [math]::Truncate([double]$row.$member)) {
        throw "Result '$Path' has an out-of-range '$member' value."
      }
    }
  }

  if ($schemaVersion -eq 2) {
    [bool] $strictCopilotCliArm = [string]::Equals([string]$Payload.host, 'copilot', [StringComparison]::Ordinal) -and
      ([string]::Equals([string]$Payload.arm, 'cli', [StringComparison]::Ordinal) -or
        [string]::Equals([string]$Payload.arm, 'cli-skill', [StringComparison]::Ordinal))
    if ($strictCopilotCliArm) { throw "Legacy result '$Path' cannot represent a strict Copilot CLI arm." }
    if ($rootMembers -cnotcontains 'model' -or $Payload.model -isnot [string] -or
      [string]::IsNullOrWhiteSpace([string]$Payload.model)) {
      throw "Legacy result '$Path' has an invalid model."
    }
    return
  }

  foreach ($member in @('model', 'n', 'maxSteps', 'inputIdentity', 'iterations')) {
    if ($rootMembers -cnotcontains $member) { throw "Schema-v3 result '$Path' is missing '$member'." }
  }
  if (-not (Test-JsonObject $Payload.model)) { throw "Schema-v3 result '$Path' model is not an object." }
  [string[]] $modelMembers = @($Payload.model.PSObject.Properties.Name)
  [string[]] $expectedModelMembers = @('requested', 'expected', 'observed', 'observedDistinct', 'verified')
  if ($modelMembers.Count -ne $expectedModelMembers.Count -or
    @($modelMembers | Where-Object { $expectedModelMembers -cnotcontains $_ }).Count -ne 0) {
    throw "Schema-v3 result '$Path' model has malformed members."
  }
  foreach ($member in @('requested', 'expected', 'observed', 'observedDistinct', 'verified')) {
    if ($modelMembers -cnotcontains $member) { throw "Schema-v3 result '$Path' model is missing '$member'." }
  }
  [bool] $defaultCopilotMcpModel = $null -eq $Payload.model.requested -and
    [string]::Equals([string]$Payload.host, 'copilot', [StringComparison]::Ordinal) -and
    [string]::Equals([string]$Payload.arm, 'mcp', [StringComparison]::Ordinal)
  [bool] $requestedModelMatches = $Payload.model.requested -is [string] -and
    -not [string]::IsNullOrWhiteSpace([string]$Payload.model.requested) -and
    [string]::Equals([string]$Payload.model.requested, [string]$Payload.model.observed, [StringComparison]::Ordinal)
  [bool] $strictCopilotCliArm = [string]::Equals([string]$Payload.host, 'copilot', [StringComparison]::Ordinal) -and
    ([string]::Equals([string]$Payload.arm, 'cli', [StringComparison]::Ordinal) -or
      [string]::Equals([string]$Payload.arm, 'cli-skill', [StringComparison]::Ordinal))
  [bool] $expectedModelMatches = ($null -eq $Payload.model.expected -and -not $strictCopilotCliArm) -or
    ($Payload.model.expected -is [string] -and
      -not [string]::IsNullOrWhiteSpace([string]$Payload.model.expected) -and
      [string]::Equals([string]$Payload.model.expected, [string]$Payload.model.observed, [StringComparison]::Ordinal))
  if (-not (Test-JsonArray $Payload.model.observedDistinct)) {
    throw "Schema-v3 result '$Path' model observedDistinct is not an array."
  }
  [object[]] $observedDistinct = $Payload.model.observedDistinct
  if ($Payload.model.observed -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Payload.model.observed) -or
    $Payload.model.verified -isnot [bool] -or -not $Payload.model.verified -or
    (-not $defaultCopilotMcpModel -and -not $requestedModelMatches) -or -not $expectedModelMatches -or
    $observedDistinct.Count -ne 1 -or $observedDistinct[0] -isnot [string] -or
    -not [string]::Equals([string]$observedDistinct[0], [string]$Payload.model.observed, [StringComparison]::Ordinal)) {
    throw "Schema-v3 result '$Path' does not contain verified exact model identity."
  }
  if ($Payload.n -isnot [long] -and $Payload.n -isnot [int] -or [long]$Payload.n -lt 1 -or [long]$Payload.n -gt 1000 -or
    $Payload.maxSteps -isnot [long] -and $Payload.maxSteps -isnot [int] -or [long]$Payload.maxSteps -lt 1 -or [long]$Payload.maxSteps -gt 64) {
    throw "Schema-v3 result '$Path' has invalid iteration bounds."
  }

  if (-not (Test-JsonArray $Payload.iterations)) { throw "Schema-v3 result '$Path' iterations is not an array." }
  [object[]] $iterations = $Payload.iterations
  if ($iterations.Count -ne ($tasks.Count * [int]$Payload.n)) {
    throw "Schema-v3 result '$Path' has an invalid iteration count."
  }
  if (-not (Test-JsonArray $Payload.inputIdentity)) {
    throw "Schema-v3 result '$Path' inputIdentity is not an array."
  }
  [object[]] $inputIdentity = $Payload.inputIdentity
  if ($inputIdentity.Count -ne $tasks.Count) {
    throw "Schema-v3 result '$Path' has an invalid input identity count."
  }
  [System.Collections.Generic.Dictionary[string, object]] $inputsByTask =
    [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
  foreach ($input in $inputIdentity) {
    if (-not (Test-JsonObject $input)) {
      throw "Schema-v3 result '$Path' has a non-object input identity."
    }
    [string[]] $inputMembers = @($input.PSObject.Properties.Name)
    [string[]] $expectedInputMembers = @(
      'task', 'taskSha256', 'qaSha256', 'fixtureSha256', 'inputClosureSha256')
    if ($inputMembers.Count -ne $expectedInputMembers.Count -or
      @($inputMembers | Where-Object { $expectedInputMembers -cnotcontains $_ }).Count -ne 0 -or
      $input.task -isnot [string] -or -not $tasks.Contains([string]$input.task)) {
      throw "Schema-v3 result '$Path' has a malformed input identity."
    }
    foreach ($member in @('taskSha256', 'qaSha256', 'fixtureSha256', 'inputClosureSha256')) {
      if ($input.$member -isnot [string] -or $input.$member -cnotmatch '^[0-9a-f]{64}$') {
        throw "Schema-v3 result '$Path' has a malformed input identity hash."
      }
    }
    if (-not $inputsByTask.TryAdd([string]$input.task, $input)) {
      throw "Schema-v3 result '$Path' repeats input identity task '$($input.task)'."
    }
  }
  foreach ($row in $summary) {
    [string[]] $rowMembers = @($row.PSObject.Properties.Name)
    if ($rowMembers -cnotcontains 'MedHelpCalls' -or -not (Test-FiniteNumber $row.MedHelpCalls) -or
      [double]$row.MedHelpCalls -lt 0 -or [double]$row.MedHelpCalls -gt [int]::MaxValue -or
      [double]$row.MedHelpCalls -ne [math]::Truncate([double]$row.MedHelpCalls)) {
      throw "Schema-v3 result '$Path' has an invalid 'MedHelpCalls' value."
    }
  }
  [System.Collections.Generic.HashSet[string]] $iterationKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
  foreach ($iteration in $iterations) {
    if (-not (Test-JsonObject $iteration)) { throw "Schema-v3 result '$Path' has a non-object iteration." }
    [string[]] $iterationMembers = @($iteration.PSObject.Properties.Name)
    foreach ($member in @('task', 'iteration', 'success', 'calls', 'helpCalls', 'tokens', 'wallMs', 'observedModel', 'observedModels')) {
      if ($iterationMembers -cnotcontains $member) { throw "Schema-v3 result '$Path' iteration is missing '$member'." }
    }
    if ($iteration.task -isnot [string] -or -not $tasks.Contains([string]$iteration.task) -or
      $iteration.iteration -isnot [long] -and $iteration.iteration -isnot [int] -or
      [long]$iteration.iteration -lt 1 -or [long]$iteration.iteration -gt [long]$Payload.n -or
      $iteration.success -isnot [bool]) {
      throw "Schema-v3 result '$Path' has an invalid iteration identity."
    }
    $taskInput = $inputsByTask[[string]$iteration.task]
    if ($iterationMembers -cnotcontains 'execution' -or
      -not (Test-JsonObject $iteration.execution) -or
      @($iteration.execution.PSObject.Properties.Name) -cnotcontains 'fixture' -or
      -not (Test-JsonObject $iteration.execution.fixture) -or
      @($iteration.execution.fixture.PSObject.Properties.Name) -cnotcontains 'sha256' -or
      $iteration.execution.fixture.sha256 -isnot [string] -or
      -not [string]::Equals(
        [string]$iteration.execution.fixture.sha256,
        [string]$taskInput.fixtureSha256,
        [StringComparison]::Ordinal)) {
      throw "Schema-v3 result '$Path' iteration input identity does not match task '$($iteration.task)'."
    }
    [string] $iterationKey = "$($iteration.task)/$($iteration.iteration)"
    if (-not $iterationKeys.Add($iterationKey)) { throw "Schema-v3 result '$Path' repeats iteration '$iterationKey'." }
    foreach ($member in @('calls', 'helpCalls', 'tokens', 'wallMs')) {
      if (-not (Test-FiniteNumber $iteration.$member) -or [double]$iteration.$member -lt 0 -or
        [double]$iteration.$member -gt [int]::MaxValue -or
        [double]$iteration.$member -ne [math]::Truncate([double]$iteration.$member)) {
        throw "Schema-v3 result '$Path' has an invalid iteration '$member'."
      }
    }
    if (-not (Test-JsonArray $iteration.observedModels)) {
      throw "Schema-v3 result '$Path' iteration observedModels is not an array."
    }
    [object[]] $observedModels = $iteration.observedModels
    if ($iteration.observedModel -isnot [string] -or
      -not [string]::Equals([string]$iteration.observedModel, [string]$Payload.model.observed, [StringComparison]::Ordinal) -or
      $observedModels.Count -ne 1 -or $observedModels[0] -isnot [string] -or
      -not [string]::Equals([string]$observedModels[0], [string]$Payload.model.observed, [StringComparison]::Ordinal)) {
      throw "Schema-v3 result '$Path' iteration model identity does not match the report."
    }
  }

  foreach ($row in $summary) {
    [object[]] $taskIterations = @($iterations | Where-Object {
        [string]::Equals([string]$_.task, [string]$row.Task, [StringComparison]::Ordinal)
      })
    if ($taskIterations.Count -ne [int]$Payload.n) {
      throw "Schema-v3 result '$Path' does not contain every iteration for task '$($row.Task)'."
    }
    [int] $successCount = @($taskIterations | Where-Object { $_.success }).Count
    [string[]] $rowMembers = @($row.PSObject.Properties | ForEach-Object { $_.Name })
    if ($rowMembers -ccontains 'SuccessCount') {
      if (($row.SuccessCount -isnot [int] -and $row.SuccessCount -isnot [long]) -or
        [long]$row.SuccessCount -ne $successCount) {
        throw "Schema-v3 result '$Path' summary 'SuccessCount' does not match task '$($row.Task)' iterations."
      }
    }
    else {
      $row | Add-Member -NotePropertyName SuccessCount -NotePropertyValue $successCount
    }
    $expectedSummary = [ordered]@{
      'Success%' = [int][math]::Round(100.0 * $successCount / [int]$Payload.n)
      MedCalls = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.calls })
      MedHelpCalls = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.helpCalls })
      MedTokens = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.tokens })
      MedMs = Get-ResultMedian -Values @($taskIterations | ForEach-Object { $_.wallMs })
    }
    foreach ($member in $expectedSummary.Keys) {
      if ([long]$row.$member -ne [long]$expectedSummary[$member]) {
        throw "Schema-v3 result '$Path' summary '$member' does not match task '$($row.Task)' iterations."
      }
    }
  }
  Assert-ResultEvidenceSchema -Payload $Payload -Path $Path
}

function Get-LabelFilePattern([string] $Label) {
  [string] $safeLabel = $Label -replace '[^\w.-]', '_'
  return "-$([regex]::Escape($safeLabel))-\d{8}-\d{6}-\d{3}\.json$"
}

# Load only generated top-level result files for the requested labels. Any matching
# file that cannot be parsed or validated makes the comparison unverifiable.
$labelSelectors = @(
  [pscustomobject]@{ label = $Baseline; pattern = Get-LabelFilePattern $Baseline }
  [pscustomobject]@{ label = $Candidate; pattern = Get-LabelFilePattern $Candidate }
)
if ([string]::Equals($labelSelectors[0].pattern, $labelSelectors[1].pattern, [StringComparison]::Ordinal)) {
  throw 'Baseline and candidate labels map to the same result filename pattern.'
}
$all = Get-ChildItem -LiteralPath $ResultsDir -File -Filter '*.json' | Where-Object {
  [string] $name = $_.Name
  @($labelSelectors | Where-Object { $name -cmatch $_.pattern }).Count -gt 0
} | ForEach-Object {
  [string] $path = $_.FullName
  [string] $name = $_.Name
  [object[]] $matchingLabels = @($labelSelectors | Where-Object { $name -cmatch $_.pattern })
  if ($matchingLabels.Count -ne 1) { throw "Result '$path' has an ambiguous filename label." }
  if ($_.Length -gt $MaxResultBytes) {
    throw "Result '$path' exceeds $MaxResultBytes bytes."
  }
  [string] $json = Get-Content -LiteralPath $path -Raw
  try {
    [System.Text.Json.JsonDocument] $document = [System.Text.Json.JsonDocument]::Parse($json)
    try { Assert-UniqueJsonMembers -Element $document.RootElement -Context "Result '$path'" }
    finally { $document.Dispose() }
    $payload = $json | ConvertFrom-Json
  }
  catch { throw "Unreadable matching result '$path': $($_.Exception.Message)" }
  [DateTimeOffset] $parsedTimestamp = Get-ResultTimestamp -Json $json -Path $path
  Assert-ResultPayload -Payload $payload -Path $path
  if (-not [string]::Equals(
      [string]$payload.label, [string]$matchingLabels[0].label, [StringComparison]::Ordinal)) {
    throw "Result '$path' filename label does not match payload label '$($payload.label)'."
  }
  [string] $model = if ([int]$payload.schemaVersion -eq 2) { [string]$payload.model } else { [string]$payload.model.observed }
  [string] $inputIdentity = if ([int]$payload.schemaVersion -eq 3) {
    [string[]] $canonicalInputs = @($payload.inputIdentity | ForEach-Object {
        [string]([ordered]@{
            task = $_.task
            taskSha256 = $_.taskSha256
            qaSha256 = $_.qaSha256
            fixtureSha256 = $_.fixtureSha256
            inputClosureSha256 = $_.inputClosureSha256
          } | ConvertTo-Json -Compress)
      })
    [Array]::Sort($canonicalInputs, [StringComparer]::Ordinal)
    [string](ConvertTo-Json -InputObject $canonicalInputs -Compress)
  }
  else { '' }
  [pscustomobject]@{
    key       = ('{0}/{1}/{2}' -f $payload.host, $payload.arm, $model)
    label     = [string]$payload.label
    timestamp = $parsedTimestamp
    schemaVersion = [int]$payload.schemaVersion
    n         = if ([int]$payload.schemaVersion -eq 3) { [int]$payload.n } else { $null }
    maxSteps  = if ([int]$payload.schemaVersion -eq 3) { [int]$payload.maxSteps } else { $null }
    inputIdentity = $inputIdentity
    summary   = $payload.summary
  }
}

# The latest payload per (label, run) where run = host/arm/model.
function Get-LatestRun([string]$Label) {
  [System.Collections.Generic.Dictionary[string, object]] $latest =
    [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
  foreach ($run in @($all)) {
    if (-not [string]::Equals([string]$run.label, $Label, [StringComparison]::Ordinal)) { continue }
    $existing = $null
    if (-not $latest.TryGetValue([string]$run.key, [ref]$existing) -or
      [DateTimeOffset]$run.timestamp -gt [DateTimeOffset]$existing.timestamp) {
      $latest[[string]$run.key] = $run
    }
    }
  return @($latest.Values)
}

$base = @(Get-LatestRun $Baseline)
$cand = @(Get-LatestRun $Candidate)
if ($base.Count -eq 0) { throw "No runs labeled '$Baseline' in $ResultsDir." }
if ($cand.Count -eq 0) { throw "No runs labeled '$Candidate' in $ResultsDir." }

# Index a payload's summary rows by task id.
function Get-TaskMap($Payload) {
  [System.Collections.Generic.Dictionary[string, object]] $m =
    [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Payload.summary) { $m[[string]$row.Task] = $row }
    return $m
}

$rows = [System.Collections.Generic.List[object]]::new()
$regressions = 0
$improvements = 0
$unpaired = 0

# Pair on the full run identity (host/arm/model) so runs from different hosts or
# arms - whose token/call counts are not comparable - never pair silently.
$runs = @(@($base.key) + @($cand.key) | Sort-Object -CaseSensitive -Unique)
foreach ($run in $runs) {
  $b = $base | Where-Object { $_.key -ceq $run } | Select-Object -First 1
  $c = $cand | Where-Object { $_.key -ceq $run } | Select-Object -First 1
    if (-not $b) { $rows.Add([pscustomobject]@{ Run = $run; Task = '(all)'; Success = '-'; Calls = '-'; Tokens = '-'; Verdict = 'no baseline' }); $unpaired++; continue }
    if (-not $c) { $rows.Add([pscustomobject]@{ Run = $run; Task = '(all)'; Success = '-'; Calls = '-'; Tokens = '-'; Verdict = 'no candidate' }); $unpaired++; continue }
    if ($b.schemaVersion -ne $c.schemaVersion -or
      ($b.schemaVersion -eq 3 -and ($b.n -ne $c.n -or $b.maxSteps -ne $c.maxSteps -or
          -not [string]::Equals($b.inputIdentity, $c.inputIdentity, [StringComparison]::Ordinal)))) {
      $rows.Add([pscustomobject]@{ Run = $run; Task = '(all)'; Success = '-'; Calls = '-'; Tokens = '-'; Verdict = 'incompatible run contract' })
      $unpaired++
      continue
    }

    $bm = Get-TaskMap $b
    $cm = Get-TaskMap $c
    foreach ($t in @(@($bm.Keys) + @($cm.Keys) | Sort-Object -CaseSensitive -Unique)) {
        $br = $bm[$t]; $cr = $cm[$t]
        if (-not $br -or -not $cr) {
            $rows.Add([pscustomobject]@{ Run = $run; Task = $t; Success = '-'; Calls = '-'; Tokens = '-'; Verdict = 'missing task' })
            $unpaired++
            continue
        }
        $ds = if ($b.schemaVersion -eq 3) {
          [int]$cr.SuccessCount - [int]$br.SuccessCount
        }
        else {
          [int]$cr.'Success%' - [int]$br.'Success%'
        }
        $dc = [int]$cr.MedCalls - [int]$br.MedCalls
        $bt = [double]$br.MedTokens; $ct = [double]$cr.MedTokens
        [bool] $unboundedTokenGrowth = $bt -eq 0 -and $ct -gt 0
        $dtFrac = if ($bt -gt 0) { ($ct - $bt) / $bt } else { 0 }

        $verdict = 'neutral'
        if ($ds -lt 0) { $verdict = 'REGRESSION'; $regressions++ }
        elseif ($unboundedTokenGrowth -or $dtFrac -gt $TokenGrowthTolerance) { $verdict = 'REGRESSION'; $regressions++ }
        elseif ($ds -gt 0 -or $dtFrac -lt -0.05 -or $dc -lt 0) { $verdict = 'improved'; $improvements++ }

        $sign = if ($dtFrac -ge 0) { '+' } else { '' }
        [string] $tokenDisplay = if ($unboundedTokenGrowth) {
          '{0}->{1} (unbounded)' -f $br.MedTokens, $cr.MedTokens
        }
        else {
          '{0}->{1} ({2}{3}%)' -f $br.MedTokens, $cr.MedTokens, $sign, [int][math]::Round($dtFrac * 100)
        }
        [string] $successDisplay = if ($b.schemaVersion -eq 3) {
          '{0}/{1}->{2}/{3}' -f $br.SuccessCount, $b.n, $cr.SuccessCount, $c.n
        }
        else {
          '{0}->{1}' -f $br.'Success%', $cr.'Success%'
        }
        $rows.Add([pscustomobject]@{
                Run     = $run
                Task    = $t
            Success = $successDisplay
                Calls   = ('{0}->{1}' -f $br.MedCalls, $cr.MedCalls)
                Tokens  = $tokenDisplay
                Verdict = $verdict
            })
    }
}

Write-Host ''
Write-Host "Baseline '$Baseline' vs candidate '$Candidate' ($($runs.Count) run(s)):"
$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($regressions -gt 0 -or $unpaired -gt 0) {
    Write-Host "Verdict: REJECT - $regressions regression(s), $unpaired unpaired run/task(s), $improvements improvement(s)." -ForegroundColor Red
    exit 1
}
Write-Host "Verdict: ACCEPT - 0 regressions, $improvements improvement(s)." -ForegroundColor Green
exit 0
