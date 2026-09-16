#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runner = Join-Path $root 'eval/Invoke-AgentEval.ps1'
$compareRunner = Join-Path $root 'eval/Compare-EvalRuns.ps1'
$helpers = Join-Path $root 'eval/CopilotEval.Helpers.ps1'
$policyHook = Join-Path $root 'eval/CopilotEval.PolicyHook.ps1'
$fakeHost = Join-Path $root 'tools/fixtures/Fake-CopilotEvalHost.ps1'
$pwshPath = (Get-Process -Id $PID).Path
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace agent eval contract $([Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
[System.Collections.Generic.List[System.IDisposable]] $policyLedgers =
    [System.Collections.Generic.List[System.IDisposable]]::new()
. $helpers
. (Join-Path $root 'eval/Get-OperationName.ps1')

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function New-TestExecutionPolicy(
    [string] $Name,
    [int] $MaxCalls,
    [int] $TaskMaxCalls = 0,
    [switch] $WithSkill) {
    [string] $runDirectory = Join-Path $temporaryRoot $Name
    [string] $workspace = Join-Path $runDirectory 'workspace'
    [string] $isolatedHomePath = Join-Path $runDirectory 'home'
    [System.IO.Directory]::CreateDirectory($workspace) | Out-Null
    [System.IO.Directory]::CreateDirectory($isolatedHomePath) | Out-Null
    [string] $cliPath = Join-Path $workspace 'filtrace.exe'
    [string] $fixturePath = Join-Path $workspace 'trace.nettrace'
    [System.IO.File]::WriteAllText($cliPath, 'test apphost')
    [System.IO.File]::WriteAllText($fixturePath, 'test fixture')
    [string] $skillPath = if ($WithSkill) {
        [string] $path = Join-Path $workspace '.agents/skills/filtrace/SKILL.md'
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path)) | Out-Null
        [string] $skillText = (@(
            '---'
            'name: café `bounded`'
            ''
            '<workflow value="β">read exactly</workflow>'
            'preserve  trailing  spaces  '
            'last line') -join "`r`n") + "`r`n"
        [System.IO.File]::WriteAllText($path, $skillText, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText(
            (Join-Path $workspace '.agents/skills/filtrace/reference.md'),
            "related detail`r`n",
            [System.Text.UTF8Encoding]::new($false))
        $path
    }
    else { $null }
    $context = [pscustomobject]@{
        runId = [Guid]::NewGuid().ToString('D')
        runDirectory = $runDirectory
        workspace = $workspace
        home = $isolatedHomePath
        isolateHome = $true
        cliPath = $cliPath
        fixturePath = $fixturePath
        skillPath = $skillPath
        skillInventory = if ($skillPath) {
            @(
                [pscustomobject]@{ path = 'SKILL.md' }
                [pscustomobject]@{ path = 'reference.md' })
        }
        else { @() }
        immutableFiles = [System.Collections.Generic.List[object]]::new()
    }
    $task = Get-Content -LiteralPath (Join-Path $root 'eval/tasks/04-gc-report.json') -Raw | ConvertFrom-Json
    $task | Add-Member -NotePropertyName maxCalls -NotePropertyValue $(
        if ($TaskMaxCalls -gt 0) { $TaskMaxCalls } else { $null })
    $executionPolicy = Initialize-CopilotEvalExecutionPolicy `
        -Context $context `
        -Task $task `
        -AllowedVerbs @('report') `
        -MaxCalls $MaxCalls `
        -HookSourcePath $policyHook
    $policyLedgers.Add($executionPolicy.ledger)
    $hookConfiguration = Get-Content -LiteralPath $executionPolicy.hookConfigurationPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{
        context = $context
        executionPolicy = $executionPolicy
        preToolHook = @($hookConfiguration.hooks.preToolUse)[0]
        command = "& '$cliPath' 'report' '$fixturePath' '--kind' 'gc' '--format' 'json'"
    }
}

function Invoke-TestPolicyHook {
    param(
        [Parameter(Mandatory)] $Hook,
        [string] $SessionId,
        [string] $WorkingDirectory,
        [string] $ToolName,
        $ToolArguments,
        [string] $RawInput
    )

    [string] $hookInput = if ($PSBoundParameters.ContainsKey('RawInput')) {
        $RawInput
    }
    else {
        [string]([ordered]@{
                sessionId = $SessionId
                timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                cwd = $WorkingDirectory
                toolName = $ToolName
                toolArgs = $ToolArguments
            } | ConvertTo-Json -Depth 8 -Compress)
    }
    [string[]] $output = @($hookInput | & ([string]$Hook.exec) @($Hook.args))
    Assert-True ($LASTEXITCODE -eq 0) 'Policy hook process did not exit cleanly.'
    Assert-True ($output.Count -eq 1) `
        "Policy hook for '$ToolName' did not emit exactly one decision; count=$($output.Count), output='$($output -join ' | ')'."
    return $output[0] | ConvertFrom-Json
}

function Invoke-TestRawLedgerRequest(
    [string] $PipeName,
    [string] $RequestText,
    [switch] $OmitTerminator) {
    [System.IO.Pipes.NamedPipeClientStream] $pipe =
        [System.IO.Pipes.NamedPipeClientStream]::new(
            '.',
            $PipeName,
            [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::Asynchronous)
    [System.IO.StreamReader] $reader = $null
    [System.IO.StreamWriter] $writer = $null
    try {
        $pipe.Connect(2000)
        $reader = [System.IO.StreamReader]::new($pipe, [System.Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        if ($OmitTerminator) {
            $writer.Write($RequestText)
            $writer.Flush()
        }
        else {
            $writer.WriteLine($RequestText)
        }
        $responseTask = $reader.ReadLineAsync()
        if (-not $responseTask.Wait(4000)) { throw 'Policy ledger did not bound its request read.' }
        return $responseTask.GetAwaiter().GetResult() | ConvertFrom-Json
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $reader) { $reader.Dispose() }
        $pipe.Dispose()
    }
}

function Invoke-TestLedgerRequest([string] $PipeName, $Request) {
    return Invoke-TestRawLedgerRequest `
        -PipeName $PipeName `
        -RequestText ($Request | ConvertTo-Json -Depth 6 -Compress)
}

function Close-TestLedgerConnection([string] $PipeName) {
    [System.IO.Pipes.NamedPipeClientStream] $pipe =
        [System.IO.Pipes.NamedPipeClientStream]::new(
            '.',
            $PipeName,
            [System.IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(2000) }
    finally { $pipe.Dispose() }
}

function Invoke-FakeRun {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Mode,
        [ValidateSet('cli', 'cli-skill')][string] $Arm = 'cli',
        [string] $Label,
        [string] $OutDir,
        [int] $TimeoutSeconds = 30,
        [int] $MaxOutputBytes = 10485760,
        [int] $MaxArtifactBytes = 16777216
    )

    if (-not $OutDir) { $OutDir = Join-Path $temporaryRoot $Name }
    [System.IO.Directory]::CreateDirectory($OutDir) | Out-Null
    [object] $previousMode = [System.Environment]::GetEnvironmentVariable(
        'FILTRACE_AGENT_EVAL_FAKE_MODE',
        [System.EnvironmentVariableTarget]::Process)
    [string[]] $sentinelNames = @(
        'GITHUB_TOKEN', 'GH_TOKEN', 'COPILOT_ALLOW_ALL', 'COPILOT_MODEL', 'COPILOT_PROVIDER',
        'COPILOT_CUSTOM_INSTRUCTIONS_DIRS', 'OTEL_EXPORTER_OTLP_ENDPOINT', 'FILTRACE_AGENT_EVAL_SECRET')
    [System.Collections.Generic.Dictionary[string, object]] $previousSentinels =
        [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($sentinelName in $sentinelNames) {
            $previousSentinels[$sentinelName] = [System.Environment]::GetEnvironmentVariable(
                $sentinelName, [System.EnvironmentVariableTarget]::Process)
            [System.Environment]::SetEnvironmentVariable(
                $sentinelName, "must-not-reach-child-$sentinelName", [System.EnvironmentVariableTarget]::Process)
        }
        $env:FILTRACE_AGENT_EVAL_FAKE_MODE = $Mode
        & $runner `
            -AgentHost copilot `
            -Arm $Arm `
            -Model expected-model `
            -ExpectedModel expected-model `
            -Tasks gc-report `
            -N 1 `
            -Label $Label `
            -OutDir $OutDir `
            -CopilotPath $pwshPath `
            -CopilotAdapterPath $fakeHost `
            -NativeTimeoutSeconds $TimeoutSeconds `
            -MaxHostOutputBytes $MaxOutputBytes `
            -MaxHostArtifactBytes $MaxArtifactBytes
    }
    finally {
        [System.Environment]::SetEnvironmentVariable(
            'FILTRACE_AGENT_EVAL_FAKE_MODE',
            $previousMode,
            [System.EnvironmentVariableTarget]::Process)
        foreach ($sentinelName in $sentinelNames) {
            [System.Environment]::SetEnvironmentVariable(
                $sentinelName,
                $previousSentinels[$sentinelName],
                [System.EnvironmentVariableTarget]::Process)
        }
    }

    $resultPaths = @(Get-ChildItem -LiteralPath $OutDir -Filter '*.json')
    if ($resultPaths.Count -eq 0) { throw "Fake case '$Name' did not write a result." }
    $resultPath = $resultPaths | Sort-Object LastWriteTimeUtc | Select-Object -Last 1
    return Get-Content -LiteralPath $resultPath.FullName -Raw | ConvertFrom-Json
}

function Assert-FakeRunThrows {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Mode,
        [Parameter(Mandatory)][string] $ExpectedMessage,
        [string] $OutDir,
        [int] $TimeoutSeconds = 30,
        [int] $MaxOutputBytes = 10485760,
        [int] $MaxArtifactBytes = 16777216
    )

    [bool] $threw = $false
    [string] $actualMessage = '<no exception>'
    try {
        [void](Invoke-FakeRun `
                -Name $Name `
                -Mode $Mode `
                -OutDir $OutDir `
                -TimeoutSeconds $TimeoutSeconds `
                -MaxOutputBytes $MaxOutputBytes `
                -MaxArtifactBytes $MaxArtifactBytes)
    }
    catch {
        $actualMessage = $_.Exception.ToString()
        $threw = $actualMessage.Contains($ExpectedMessage, [StringComparison]::Ordinal)
    }
    Assert-True $threw "Fake case '$Name' did not fail with '$ExpectedMessage'. Actual: $actualMessage"
}

function Assert-FakeParserFailureArtifacts {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Mode,
        [Parameter(Mandatory)][string] $ExpectedMessage,
        [Parameter(Mandatory)][string] $ExpectedRawText,
        [ValidateSet('cli', 'cli-skill')][string] $Arm = 'cli'
    )

    [string] $outDirectory = Join-Path $temporaryRoot $Name
    [bool] $threw = $false
    [string] $actualMessage = '<no exception>'
    try {
        [void](Invoke-FakeRun -Name $Name -Mode $Mode -Arm $Arm -OutDir $outDirectory)
    }
    catch {
        $actualMessage = $_.Exception.ToString()
        $threw = $actualMessage.Contains($ExpectedMessage, [StringComparison]::Ordinal)
    }
    Assert-True $threw `
        "Fake parser case '$Name' did not fail with '$ExpectedMessage'. Actual: $actualMessage"
    Assert-True `
        (@(Get-ChildItem -LiteralPath $outDirectory -File -Filter '*.json').Count -eq 0) `
        "Fake parser case '$Name' published a result."
    $stdoutFiles = @(Get-ChildItem -LiteralPath $outDirectory -File -Recurse -Filter 'host-stdout.jsonl')
    $stderrFiles = @(Get-ChildItem -LiteralPath $outDirectory -File -Recurse -Filter 'host-stderr.log')
    Assert-True ($stdoutFiles.Count -eq 1) "Fake parser case '$Name' did not retain one stdout file."
    Assert-True ($stderrFiles.Count -eq 1) "Fake parser case '$Name' did not retain one stderr file."
    [string] $rawStdout = [System.IO.File]::ReadAllText($stdoutFiles[0].FullName, [System.Text.Encoding]::UTF8)
    Assert-True `
        ($rawStdout.Contains($ExpectedRawText, [StringComparison]::Ordinal)) `
        "Fake parser case '$Name' did not retain the failing raw line."
}

function Read-TestResult([string] $Path) {
    [string] $json = [System.IO.File]::ReadAllText($Path)
    [System.Text.Json.JsonDocument] $document = [System.Text.Json.JsonDocument]::Parse($json)
    try {
        [System.Text.Json.JsonElement] $timestamp = $document.RootElement.GetProperty('timestamp')
        if ($timestamp.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw "Test result '$Path' did not contain a string timestamp."
        }
        $result = $json | ConvertFrom-Json
        $result.timestamp = $timestamp.GetString()
        return $result
    }
    finally {
        $document.Dispose()
    }
}

try {
    [System.Text.RegularExpressions.RegexOptions] $modelPatternOptions =
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    [regex] $hostedModelIdPattern = [regex]::new(
        '(?<![A-Za-z0-9])(?:claude|gpt|gemini)-(?=[A-Za-z0-9.-]*[0-9])[A-Za-z0-9.-]+',
        $modelPatternOptions)
    [string[]] $publicModelSurfaces = @(
        $runner
        $compareRunner
        (Join-Path $root 'eval/README.md')
        (Join-Path $root 'docs/roadmap.md'))
    foreach ($publicModelSurface in $publicModelSurfaces) {
        [string] $publicModelText = [System.IO.File]::ReadAllText($publicModelSurface)
        Assert-True (-not $hostedModelIdPattern.IsMatch($publicModelText)) `
            "Public evaluator surface '$publicModelSurface' contains a concrete hosted-model identifier."
    }

    Assert-True (Test-AgentEvalPathContained -Path $root -Root $root) `
        'Repository-root equality was not treated as path containment.'
    [bool] $configurationEscapeRejected = $false
    try { [void](Get-AgentEvalCliOutputDirectory -Root $root -Configuration '..\..\outside') }
    catch {
        $configurationEscapeRejected = $_.Exception.Message.Contains('escaped', [StringComparison]::Ordinal)
    }
    Assert-True $configurationEscapeRejected 'Build configuration escaped the repository CLI output root.'
    [bool] $runnerConfigurationEscapeRejected = $false
    try { & $runner -Configuration '..\..\outside' }
    catch {
        $runnerConfigurationEscapeRejected = $_.Exception.Message.Contains('escaped', [StringComparison]::Ordinal)
    }
    Assert-True $runnerConfigurationEscapeRejected `
        'The evaluator entry point accepted an escaping build configuration.'

    [string] $sourceReparseRepository = Join-Path $temporaryRoot 'source reparse repository'
    [string] $sourceReparseTarget = Join-Path $temporaryRoot 'source reparse target'
    [System.IO.Directory]::CreateDirectory((Join-Path $sourceReparseTarget 'Filtrace/bin/Release/net10.0')) | Out-Null
    [System.IO.Directory]::CreateDirectory($sourceReparseRepository) | Out-Null
    $sourceLinkType = if ([System.OperatingSystem]::IsWindows()) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $sourceLinkType -Path (Join-Path $sourceReparseRepository 'src') -Target $sourceReparseTarget | Out-Null
    [bool] $sourceReparseRejected = $false
    try { [void](Get-AgentEvalCliOutputDirectory -Root $sourceReparseRepository -Configuration Release) }
    catch {
        $sourceReparseRejected = $_.Exception.Message.Contains('reparse point', [StringComparison]::Ordinal)
    }
    Assert-True $sourceReparseRejected 'Strict CLI source accepted a reparse ancestor.'

    [string] $skillReparseRepository = Join-Path $temporaryRoot 'skill reparse repository'
    [string] $skillReparseTarget = Join-Path $temporaryRoot 'skill reparse target'
    [System.IO.Directory]::CreateDirectory((Join-Path $skillReparseTarget 'skills/filtrace')) | Out-Null
    [System.IO.Directory]::CreateDirectory($skillReparseRepository) | Out-Null
    New-Item -ItemType $sourceLinkType -Path (Join-Path $skillReparseRepository '.agents') -Target $skillReparseTarget | Out-Null
    [bool] $skillReparseRejected = $false
    try {
        Assert-AgentEvalNoReparsePoint `
            -Path (Join-Path $skillReparseRepository '.agents/skills/filtrace') `
            -Boundary $skillReparseRepository
    }
    catch {
        $skillReparseRejected = $_.Exception.Message.Contains('reparse point', [StringComparison]::Ordinal)
    }
    Assert-True $skillReparseRejected 'Strict skill source accepted a reparse ancestor.'

    [string] $instructionAncestor = Join-Path $temporaryRoot 'instruction ancestor'
    [string] $instructionWorkspace = Join-Path $instructionAncestor 'child/workspace'
    [System.IO.Directory]::CreateDirectory($instructionWorkspace) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $instructionAncestor 'AGENTS.md'), 'test instructions')
    [bool] $ancestorInstructionsRejected = $false
    try { Assert-AgentEvalNoAncestorInstructions -Workspace $instructionWorkspace }
    catch {
        $ancestorInstructionsRejected = $_.Exception.Message.Contains(
            'has ancestor instructions', [StringComparison]::Ordinal)
    }
    Assert-True $ancestorInstructionsRejected 'Strict workspace accepted an ancestor AGENTS.md.'

    [System.Management.Automation.Language.Token[]] $runnerTokens = $null
    [System.Management.Automation.Language.ParseError[]] $runnerErrors = $null
    $runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $runner, [ref]$runnerTokens, [ref]$runnerErrors)
    Assert-True ($runnerErrors.Count -eq 0) 'Invoke-AgentEval.ps1 did not parse for isolated function tests.'
    foreach ($functionName in @(
            'Assert-AgentEvalEventObject', 'Assert-AgentEvalEvidenceEvent',
            'Assert-AgentEvalUniqueJsonMembers', 'ConvertFrom-AgentEvalJsonLines',
            'Write-AgentEvalNewFile', 'Save-AgentEvalHostOutput',
            'ConvertTo-AgentEvalResultJson', 'Get-AgentEvalTaskExpectedOperations',
            'Split-ArgString', 'Get-AgentEvalMediatedOperation')) {
        $functionAst = $runnerAst.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
            }, $true)
        Assert-True ($null -ne $functionAst) "Could not extract '$functionName' from Invoke-AgentEval.ps1."
        Invoke-Expression $functionAst.Extent.Text
    }
    $cpuTask = Get-Content -LiteralPath (Join-Path $root 'eval/tasks/01-cpu-hotspot.json') -Raw | ConvertFrom-Json
    [string[]] $cpuOperations = @(Get-AgentEvalTaskExpectedOperations $cpuTask)
    Assert-True ($cpuOperations.Count -eq 2 -and $cpuOperations -contains 'rank' -and
        $cpuOperations -contains 'callers') `
        'Strict task operation derivation did not require both CPU analysis steps.'
    Assert-True ((Get-AgentEvalMediatedOperation 'filtrace report <TRACE> --kind jit') -ceq 'jit' -and
        (Get-AgentEvalMediatedOperation 'rank <TRACE> --metric cpu') -ceq 'rank') `
        'Mediated CLI commands did not retain their actual operation intent.'

    [string[]] $chatterEventTypes = @(
        'assistant.idle', 'assistant.message_delta', 'assistant.message_start',
        'assistant.reasoning', 'assistant.turn_end', 'assistant.turn_start',
        'hook.end', 'hook.start', 'model.call_finished', 'model.call_start',
        'model.captured_assignment_context', 'model.messages_snapshot',
        'model.model_call_success', 'model.response', 'model.turn_ended',
        'model.turn_started', 'session.background_tasks_changed', 'session.info',
        'session.managed_settings_resolved', 'session.mcp_servers_loaded',
        'session.shutdown', 'session.start', 'session.usage_checkpoint',
        'session.warning', 'system.message', 'tool.execution_partial_result',
        'user.message')
    [string[]] $chatterLines = @($chatterEventTypes | ForEach-Object {
            [string]([ordered]@{ type = $_; data = [ordered]@{} } |
                ConvertTo-Json -Depth 4 -Compress)
        })
    $chatterLines += [string]([ordered]@{
            type = 'result'
            exitCode = 0
            usage = [ordered]@{
                premiumRequests = 1; totalApiDurationMs = 1; sessionDurationMs = 1
            }
        } | ConvertTo-Json -Depth 4 -Compress)
    Assert-True (@(ConvertFrom-AgentEvalJsonLines $chatterLines).Count -eq 1) `
        'The isolated JSONL parser retained known protocol chatter as evidence.'
    [object[]] $evidenceRecords = @(
        [ordered]@{ type = 'session.skills_loaded'; data = [ordered]@{ skills = @() } }
        [ordered]@{ type = 'session.tools_updated'; data = [ordered]@{ model = 'expected-model' } }
        [ordered]@{ type = 'model.model_call_started'; data = [ordered]@{ model = 'provider-model' } }
        [ordered]@{
            type = 'model.message'
            data = [ordered]@{ message = [ordered]@{ role = 'assistant'; content = $null } }
        }
        [ordered]@{ type = 'assistant.message'; data = [ordered]@{ content = 'answer' } }
        [ordered]@{
            type = 'tool.execution_start'
            data = [ordered]@{
                toolCallId = 'call-1'; toolName = 'powershell'; arguments = [ordered]@{}
            }
        }
        [ordered]@{
            type = 'tool.execution_complete'
            data = [ordered]@{
                toolCallId = 'call-1'; success = $true; result = [ordered]@{}
            }
        }
        [ordered]@{
            type = 'result'
            exitCode = 0
            usage = [ordered]@{
                premiumRequests = 1; totalApiDurationMs = 1; sessionDurationMs = 1
            }
        })
    [string[]] $evidenceLines = @($evidenceRecords | ForEach-Object {
            [string]($_ | ConvertTo-Json -Depth 6 -Compress)
        })
    Assert-True (@(ConvertFrom-AgentEvalJsonLines $evidenceLines).Count -eq 8) `
        'The isolated JSONL parser did not retain every exact evidence event.'
    [bool] $unknownRecordedEventRejected = $false
    try { [void](ConvertFrom-AgentEvalJsonLines @('{"type":"future.event","data":{}}')) }
    catch { $unknownRecordedEventRejected = $_.Exception.Message.Contains('unknown event type', [StringComparison]::Ordinal) }
    Assert-True $unknownRecordedEventRejected 'The isolated JSONL parser accepted an unrecorded event type.'
    [bool] $caseVariantEventRejected = $false
    try { [void](ConvertFrom-AgentEvalJsonLines @('{"type":"RESULT","exitCode":0}')) }
    catch { $caseVariantEventRejected = $_.Exception.Message.Contains('unknown event type', [StringComparison]::Ordinal) }
    Assert-True $caseVariantEventRejected 'The isolated JSONL parser accepted a case-variant event type.'
    [bool] $missingEventTypeRejected = $false
    try { [void](ConvertFrom-AgentEvalJsonLines @('{"data":{}}')) }
    catch {
        $missingEventTypeRejected = $_.Exception.Message.Contains(
            "without exactly one lowercase 'type' member", [StringComparison]::Ordinal)
    }
    Assert-True $missingEventTypeRejected 'The isolated JSONL parser accepted an event without a type member.'
    [bool] $nonStringEventTypeRejected = $false
    try { [void](ConvertFrom-AgentEvalJsonLines @('{"type":1}')) }
    catch {
        $nonStringEventTypeRejected = $_.Exception.Message.Contains(
            'non-string type', [StringComparison]::Ordinal)
    }
    Assert-True $nonStringEventTypeRejected 'The isolated JSONL parser accepted a non-string event type.'

    $serializedLedger = (ConvertTo-AgentEvalResultJson ([ordered]@{
                request = [ordered]@{ arguments = [ordered]@{ view_range = @(251, 500) } }
            })) | ConvertFrom-Json
    Assert-True ($serializedLedger.request.arguments.view_range -is [object[]] -and
        @($serializedLedger.request.arguments.view_range).Count -eq 2 -and
        @($serializedLedger.request.arguments.view_range | Where-Object {
                $_ -isnot [int] -and $_ -isnot [long]
            }).Count -eq 0) `
        'Result serialization did not preserve nested numeric view_range arguments.'
    $tooDeep = [ordered]@{ value = 'leaf' }
    foreach ($depth in 1..33) { $tooDeep = [ordered]@{ child = $tooDeep } }
    [bool] $depthWarningRejected = $false
    try { [void](ConvertTo-AgentEvalResultJson $tooDeep 3>$null) }
    catch { $depthWarningRejected = $true }
    Assert-True $depthWarningRejected 'Result serialization did not fail on a depth warning.'

    [string] $mcpOnlyRoot = Join-Path $temporaryRoot 'mcp only root'
    [string] $mcpOnlyFixture = Join-Path $mcpOnlyRoot 'fixture.nettrace'
    [System.IO.Directory]::CreateDirectory($mcpOnlyRoot) | Out-Null
    [System.IO.File]::WriteAllText($mcpOnlyFixture, 'mcp fixture')
    $mcpOnlyContext = New-CopilotEvalContext `
        -Root $mcpOnlyRoot `
        -OutDir (Join-Path $temporaryRoot 'mcp only output') `
        -Arm mcp `
        -FixturePath $mcpOnlyFixture `
        -Configuration Release
    Assert-True ([string]::IsNullOrEmpty([string]$mcpOnlyContext.cliPath) -and
        [string]::IsNullOrEmpty([string]$mcpOnlyContext.cliSourcePath) -and
        $mcpOnlyContext.workspace -eq $mcpOnlyRoot) `
        'MCP-only context creation still required or exposed a CLI apphost.'

    [string] $builtCliDll = Join-Path $root 'src/Filtrace/bin/Release/net10.0/filtrace.dll'
    [string] $hiddenCliDll = "$builtCliDll.$([Guid]::NewGuid().ToString('N')).hidden"
    [string] $mcpDllPath = Join-Path $root 'src/Filtrace.Mcp/bin/Release/net10.0/Filtrace.Mcp.dll'
    [string] $builtCliHash = Get-AgentEvalFileHash $builtCliDll
    [string] $mcpOnlyRunOutput = Join-Path $temporaryRoot 'mcp only process output'
    try {
        Move-Item -LiteralPath $builtCliDll -Destination $hiddenCliDll
        $env:FILTRACE_AGENT_EVAL_FAKE_MODE = 'mcp-success'
        & $runner `
            -AgentHost copilot `
            -Arm mcp `
            -Model expected-model `
            -Tasks gc-report `
            -N 1 `
            -McpDll $mcpDllPath `
            -OutDir $mcpOnlyRunOutput `
            -CopilotPath $pwshPath `
            -CopilotAdapterPath $fakeHost
        [System.IO.FileInfo] $mcpOnlyResultPath =
            Get-ChildItem -LiteralPath $mcpOnlyRunOutput -Filter '*.json' | Select-Object -First 1
        $mcpOnlyResult = Get-Content -LiteralPath $mcpOnlyResultPath.FullName -Raw | ConvertFrom-Json
        Assert-True ($mcpOnlyResult.iterations[0].success -eq $true) `
            'MCP-only process run did not complete while the CLI DLL was absent.'
    }
    finally {
        Remove-Item Env:FILTRACE_AGENT_EVAL_FAKE_MODE -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $hiddenCliDll -PathType Leaf) {
            Move-Item -LiteralPath $hiddenCliDll -Destination $builtCliDll
        }
    }
    Assert-True ((Get-AgentEvalFileHash $builtCliDll) -eq $builtCliHash) `
        'MCP-only process probe did not restore the CLI DLL exactly.'

    [string] $fallbackRunDirectory = Join-Path $temporaryRoot 'output retention fallback'
    [string] $fallbackLogDirectory = Join-Path $fallbackRunDirectory 'logs'
    [System.IO.Directory]::CreateDirectory($fallbackLogDirectory) | Out-Null
    $fallbackContext = [pscustomobject]@{
        runDirectory = $fallbackRunDirectory
        logDirectory = $fallbackLogDirectory
        home = $null
        isolateHome = $false
        immutableFiles = @()
    }
    $fallbackResult = Save-AgentEvalHostOutput `
        -ProcessResult ([pscustomobject]@{
            stdoutText = 'x' * 100
            stderrText = ''
            artifactBytes = 1000
        }) `
        -Context $fallbackContext `
        -MaxArtifactBytes 1024
    Assert-True (-not $fallbackResult.retained) 'Over-budget host output was retained in full.'
    Assert-True (Test-Path -LiteralPath $fallbackResult.diagnosticPath -PathType Leaf) `
        'Over-budget host output did not leave a bounded diagnostic.'
    Assert-True ((Get-Item -LiteralPath $fallbackResult.diagnosticPath).Length -le 24) `
        'Fallback output diagnostic exceeded the remaining artifact budget.'

    $success = Invoke-FakeRun -Name 'cli success' -Mode delayed-success
    Assert-True ($success.arm -eq 'cli') "Expected cli arm, got '$($success.arm)'."
    Assert-True ($success.model.observed -eq 'expected-model') 'The observed model was not retained.'
    Assert-True ($success.model.verified -eq $true) 'The observed model was not verified.'
    Assert-True ($success.iterations[0].success -eq $true) "Grounded CLI iteration failed: $($success.iterations[0].note)"
    Assert-True ($success.iterations[0].hostUsage.sessionDurationMs -eq 25 -and
        $success.iterations[0].wallMs -ge 200 -and
        $success.iterations[0].wallMs -ne $success.iterations[0].hostUsage.sessionDurationMs) `
        'Primary wallMs did not remain on the evaluator-owned monotonic process clock.'
    $filtraceEntry = @($success.iterations[0].transcript | Where-Object { $_.kind -eq 'filtrace' })
    Assert-True ($filtraceEntry.Count -eq 1 -and $filtraceEntry[0].ok) 'Matched successful tool completion was not retained.'
    Assert-True ($success.iterations[0].execution.workspace.Contains(' ', [StringComparison]::Ordinal)) 'Owned workspace did not preserve its spaced path.'
    Assert-True (Test-Path -LiteralPath $success.iterations[0].execution.cli.path) 'Recorded current-checkout CLI path does not exist.'
    Assert-True `
        ($success.iterations[0].execution.cli.path.StartsWith($success.iterations[0].execution.workspace, [StringComparison]::Ordinal)) `
        'Strict CLI execution path was outside the owned workspace.'
    Assert-True `
        ((Get-FileHash -LiteralPath $success.iterations[0].execution.cli.path -Algorithm SHA256).Hash.ToLowerInvariant() -eq
        $success.iterations[0].execution.cli.sha256) `
        'Recorded current-checkout CLI hash did not match.'
    Assert-True `
        ($success.iterations[0].execution.cli.sourceSha256 -eq $success.iterations[0].execution.cli.sha256) `
        'Owned apphost hash did not match its current-checkout source.'
    Assert-True (@($success.iterations[0].execution.cli.inventory).Count -eq 20) 'Owned CLI inventory did not contain the selected runtime bundle.'
    foreach ($runtimeFile in @($success.iterations[0].execution.cli.inventory)) {
        Assert-True ((Get-FileHash -LiteralPath $runtimeFile.path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $runtimeFile.sha256) `
            "Owned CLI inventory hash did not match '$($runtimeFile.relativePath)'."
    }
    $ownedCliOutput = & $success.iterations[0].execution.cli.path `
        report $success.iterations[0].execution.fixture.path --kind gc --format json
    Assert-True ($LASTEXITCODE -eq 0) 'Owned current-checkout apphost did not execute successfully.'
    $ownedCliResult = $ownedCliOutput | ConvertFrom-Json
    Assert-True ($ownedCliResult.result.gcCount -eq 7) 'Owned apphost did not analyze the copied fixture correctly.'
    Assert-True `
        ((Get-FileHash -LiteralPath $success.iterations[0].execution.fixture.path -Algorithm SHA256).Hash.ToLowerInvariant() -eq
        $success.iterations[0].execution.fixture.sha256) `
        'Recorded owned fixture hash did not match.'
    Assert-True ($success.iterations[0].execution.isolation.shellDefaultDenied -eq $true) `
        'Hook fallback did not retain the normal shell deny.'
    Assert-True ($success.iterations[0].execution.isolation.writeDenied -eq $true) `
        'Strict launch did not retain the write deny.'
    Assert-True ($success.iterations[0].execution.isolation.urlDenied -eq $true) `
        'Strict launch did not retain the URL deny.'
    Assert-True ($success.iterations[0].execution.isolation.readDenied -eq $true) `
        'Strict launch did not retain the read deny.'
    Assert-True ($success.iterations[0].execution.isolation.executionPolicyCallCount -eq 1) `
        'Live execution policy did not retain its allowed command count.'
    Assert-True (@($success.iterations[0].execution.isolation.executionPolicyCommandHashes).Count -eq 1) `
        'Live execution policy did not retain its allowed command hash.'
    Assert-True ($success.iterations[0].execution.isolation.executionPolicyHelpCallCount -eq 0) `
        'Analysis-only run unexpectedly consumed a help allowance.'
    Assert-True ($success.iterations[0].execution.isolation.processTreeContained -eq $true) `
        'Strict Windows run did not retain process-tree containment evidence.'
    Assert-True (@($success.iterations[0].operations) -contains 'gc') `
        'Strict report-kind operation was not retained for intent grading.'
    Assert-True ($success.iterations[0].execution.hostOutput.retained -eq $true) `
        'Successful fake run did not retain exact host output.'
    Assert-True (Test-Path -LiteralPath $success.iterations[0].execution.hostOutput.stdoutPath -PathType Leaf) `
        'Successful fake run stdout path does not exist.'
    Assert-True (Test-Path -LiteralPath $success.iterations[0].execution.hostOutput.stderrPath -PathType Leaf) `
        'Successful fake run stderr path does not exist.'
    [string] $successRawStdout = [System.IO.File]::ReadAllText(
        $success.iterations[0].execution.hostOutput.stdoutPath,
        [System.Text.Encoding]::UTF8)
    Assert-True ($successRawStdout.Contains('"session.skills_loaded"', [StringComparison]::Ordinal)) `
        'Successful fake run did not retain the modern skills event.'
    Assert-True ($successRawStdout.Contains('"initial_wait":30', [StringComparison]::Ordinal)) `
        'Successful fake run did not retain the modern PowerShell metadata.'
    Assert-True (@($success.model.observedDistinct) -notcontains 'provider-model') `
        'Provider-level model metadata was used as strict host identity.'
    [void](Invoke-FakeRun -Name 'schema validation success' -Mode success -Label validation)
    [string] $successResultPath = @(Get-ChildItem -LiteralPath (Join-Path $temporaryRoot 'schema validation success') -File -Filter '*.json' |
        Sort-Object LastWriteTimeUtc | Select-Object -Last 1).FullName
    & $compareRunner -ValidateResultPath $successResultPath | Out-Null
    [string] $gcTaskPath = Join-Path $root 'eval/tasks/04-gc-report.json'
    Assert-True ($success.inputIdentity[0].taskSha256 -eq
        (Get-AgentEvalCanonicalTextFileHash $gcTaskPath)) `
        'The retained task identity did not use canonical text hashing.'
    [string] $lfTextPath = Join-Path $temporaryRoot 'canonical-lf.txt'
    [string] $crlfTextPath = Join-Path $temporaryRoot 'canonical-crlf.txt'
    [System.IO.File]::WriteAllText($lfTextPath, "first`nsecond`n", [Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($crlfTextPath, "first`r`nsecond`r`n", [Text.UTF8Encoding]::new($false))
    Assert-True ((Get-AgentEvalCanonicalTextFileHash $lfTextPath) -eq
        (Get-AgentEvalCanonicalTextFileHash $crlfTextPath)) `
        'Canonical text hashing changed across LF and CRLF inputs.'
    Assert-True ($success.iterations[0].hostUsageFile.available -eq $true -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.input.tokenCount -eq 20 -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.cache_read.tokenCount -eq 5 -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.cache_write.tokenCount -eq 15 -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.output.tokenCount -eq 10) `
        'Detailed host token accounting was not retained from the usage output file.'
    $fractionalPremiumUsage = Invoke-FakeRun `
        -Name 'fractional premium usage' `
        -Mode fractional-premium-usage
    Assert-True ($fractionalPremiumUsage.iterations[0].success -eq $true -and
        $fractionalPremiumUsage.iterations[0].hostUsage.premiumRequests -eq 0.33) `
        'Finite nonnegative fractional premium usage was rejected.'

    $missingUsage = Invoke-FakeRun -Name 'cli missing usage output' -Mode missing-usage-output
    Assert-True ($missingUsage.iterations[0].success -eq $true -and
        $missingUsage.iterations[0].hostUsageFile.available -eq $false -and
        -not [string]::IsNullOrWhiteSpace([string]$missingUsage.iterations[0].hostUsageFile.reason)) `
        'Missing host usage output was not retained explicitly as unavailable.'
    $missingAllUsage = Invoke-FakeRun -Name 'cli missing all usage' -Mode missing-all-usage
    Assert-True ($missingAllUsage.iterations[0].success -eq $false -and
        $null -eq $missingAllUsage.iterations[0].hostUsage -and
        $missingAllUsage.iterations[0].hostUsageFile.available -eq $false) `
        'An iteration without either valid usage source unexpectedly passed.'
    Assert-FakeRunThrows `
        -Name 'malformed usage output' `
        -Mode malformed-usage-output `
        -ExpectedMessage 'Copilot usage output was malformed.'
    Assert-FakeRunThrows `
        -Name 'duplicate usage output' `
        -Mode duplicate-usage-output `
        -ExpectedMessage 'Copilot usage output was malformed.'
    foreach ($mode in @(
            'empty-usage-output', 'usage-missing-token-details', 'usage-missing-token-count',
            'usage-missing-premium', 'usage-wrong-type', 'usage-negative-token')) {
        Assert-FakeRunThrows `
            -Name "invalid usage $mode" `
            -Mode $mode `
            -ExpectedMessage 'Copilot usage output schema was malformed.'
    }
    $usageModelMismatch = Invoke-FakeRun -Name 'usage model mismatch' -Mode usage-model-mismatch
    Assert-True ($usageModelMismatch.iterations[0].success -eq $false -and
        $usageModelMismatch.iterations[0].hostUsageFile.available -eq $true) `
        'Usage accounting for a different model unexpectedly passed.'
    $oversizedSessionDuration = Invoke-FakeRun `
        -Name 'oversized session duration' `
        -Mode oversized-session-duration
    Assert-True ($oversizedSessionDuration.iterations[0].success -eq $false) `
        'A session duration above the persisted metric range unexpectedly passed.'

    $legacyToolArguments = Invoke-FakeRun -Name 'cli legacy tool arguments' -Mode legacy-tool-arguments
    Assert-True ($legacyToolArguments.iterations[0].success -eq $true) `
        'The established two-member PowerShell argument form no longer passed.'
    $helpSuccess = Invoke-FakeRun -Name 'cli help success' -Mode help-success
    Assert-True ($helpSuccess.iterations[0].success -eq $true -and
        $helpSuccess.iterations[0].calls -eq 1 -and
        $helpSuccess.iterations[0].helpCalls -eq 1 -and
        $helpSuccess.iterations[0].execution.isolation.executionPolicyCallCount -eq 1 -and
        $helpSuccess.iterations[0].execution.isolation.executionPolicyHelpCallCount -eq 1) `
        'Bounded top-level help did not remain separate from one successful analysis call.'
    $recordedPolicyDenial = Invoke-FakeRun -Name 'cli recorded policy denial' -Mode recorded-policy-denial
    Assert-True ($recordedPolicyDenial.iterations[0].success -eq $true) `
        'A correlated pre-tool denial was misclassified as an executed command.'
    Assert-True ($recordedPolicyDenial.iterations[0].calls -eq 1) `
        'The retained pre-tool denial changed the executed filtrace call count.'
    Assert-True `
        (@($recordedPolicyDenial.iterations[0].transcript | Where-Object { $_.kind -eq 'denied' }).Count -eq 1) `
        'The retained pre-tool denial was not preserved as a nonexecuted attempt.'
    Assert-True ($recordedPolicyDenial.iterations[0].execution.isolation.executionPolicyCallCount -eq 1) `
        'The retained two-attempt shape did not reconcile to its one-command ledger.'

    $policyProbe = New-TestExecutionPolicy -Name 'policy hook contract' -MaxCalls 3
    $policySessionId = $policyProbe.context.runId
    $policyWorkspace = $policyProbe.context.workspace
    $validArguments = [ordered]@{
        command = $policyProbe.command
        description = 'Analyze the owned trace with filtrace'
    }
    $initialState = Get-CopilotEvalExecutionPolicyState $policyProbe.executionPolicy
    Assert-True ($initialState.callCount -eq 0) 'Fresh single-stage policy did not begin at count zero.'
    $policyHashProbe = New-TestExecutionPolicy -Name 'policy hash binding contract' -MaxCalls 1
    [string] $policyHashText = [System.IO.File]::ReadAllText($policyHashProbe.executionPolicy.policyPath)
    [System.IO.File]::WriteAllText(
        $policyHashProbe.executionPolicy.policyPath,
        $policyHashText.Replace('"maxCalls": 1', '"maxCalls": 2'),
        [System.Text.UTF8Encoding]::new($false))
    $policyHashDecision = Invoke-TestPolicyHook `
        -Hook $policyHashProbe.preToolHook `
        -SessionId $policyHashProbe.context.runId `
        -WorkingDirectory $policyHashProbe.context.workspace `
        -ToolName powershell `
        -ToolArguments ([ordered]@{
            command = $policyHashProbe.command
            description = 'Reject changed policy bytes'
        })
    Assert-True ($policyHashDecision.permissionDecision -eq 'deny') `
        'Policy hook accepted bytes that did not match the generated policy hash.'
    foreach ($policyMutation in @(
            'root-member-case', 'scalar-families', 'family-member-case', 'scalar-enum-values',
            'scalar-view-files', 'view-file-member-case')) {
        $invalidPolicyProbe = New-TestExecutionPolicy `
            -Name "invalid policy $policyMutation" `
            -MaxCalls 1 `
            -WithSkill
        [string] $invalidPolicyText = [System.IO.File]::ReadAllText($invalidPolicyProbe.executionPolicy.policyPath)
        if ($policyMutation -eq 'root-member-case') {
            $invalidPolicyText = $invalidPolicyText.Replace('"schemaVersion": 6', '"SchemaVersion": 6')
        }
        elseif ($policyMutation -eq 'family-member-case') {
            $invalidPolicyText = $invalidPolicyText.Replace('"verb": "report"', '"Verb": "report"')
        }
        elseif ($policyMutation -eq 'view-file-member-case') {
            $invalidPolicyText = $invalidPolicyText.Replace('"path":', '"Path":')
        }
        else {
            $invalidPolicy = $invalidPolicyText | ConvertFrom-Json
            if ($policyMutation -eq 'scalar-families') {
                $invalidPolicy.commandFamilies = $invalidPolicy.commandFamilies[0]
            }
            elseif ($policyMutation -eq 'scalar-enum-values') {
                $enumOption = @($invalidPolicy.commandFamilies[0].options | Where-Object { $_.kind -eq 'enum' })[0]
                $enumOption.values = $enumOption.values[0]
            }
            else {
                $invalidPolicy.viewFiles = $invalidPolicy.viewFiles[0]
            }
            $invalidPolicyText = $invalidPolicy | ConvertTo-Json -Depth 8
        }
        [System.IO.File]::WriteAllText(
            $invalidPolicyProbe.executionPolicy.policyPath,
            $invalidPolicyText,
            [System.Text.UTF8Encoding]::new($false))
        [int] $expectedHashIndex = [Array]::IndexOf(
            [object[]]$invalidPolicyProbe.preToolHook.args,
            '-ExpectedPolicySha256')
        Assert-True ($expectedHashIndex -ge 0) 'Invalid-policy probe did not contain an expected hash argument.'
        $invalidPolicyProbe.preToolHook.args[$expectedHashIndex + 1] =
            (Get-FileHash -LiteralPath $invalidPolicyProbe.executionPolicy.policyPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $invalidPolicyDecision = Invoke-TestPolicyHook `
            -Hook $invalidPolicyProbe.preToolHook `
            -SessionId $invalidPolicyProbe.context.runId `
            -WorkingDirectory $invalidPolicyProbe.context.workspace `
            -ToolName powershell `
            -ToolArguments ([ordered]@{
                command = $invalidPolicyProbe.command
                description = 'Reject malformed policy'
            })
        Assert-True ($invalidPolicyDecision.permissionDecision -eq 'deny') `
            "Policy hook accepted malformed policy '$policyMutation'."
    }
    $ledgerOwnershipProbe = New-TestExecutionPolicy -Name 'parent-owned ledger contract' -MaxCalls 1
    Close-TestLedgerConnection -PipeName $ledgerOwnershipProbe.executionPolicy.ledgerPipeName
    $oversizedLedgerDecision = Invoke-TestRawLedgerRequest `
        -PipeName $ledgerOwnershipProbe.executionPolicy.ledgerPipeName `
        -RequestText ('x' * 65537) `
        -OmitTerminator
    Assert-True ($oversizedLedgerDecision.allowed -eq $false) `
        'Parent-owned ledger did not promptly deny an unterminated oversized request.'
    [System.Diagnostics.Stopwatch] $stalledLedgerStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stalledLedgerDecision = Invoke-TestRawLedgerRequest `
        -PipeName $ledgerOwnershipProbe.executionPolicy.ledgerPipeName `
        -RequestText 'x' `
        -OmitTerminator
    $stalledLedgerStopwatch.Stop()
    Assert-True ($stalledLedgerDecision.allowed -eq $false -and
        $stalledLedgerStopwatch.Elapsed.TotalSeconds -lt 4) `
        'Parent-owned ledger did not bound a stalled unterminated request.'
    foreach ($hostileRequest in @(
            [ordered]@{ category = 'reset'; hash = ('0' * 64) }
            [ordered]@{ category = 'command'; hash = ('0' * 64); commandHashes = @() }
            [ordered]@{ Category = 'command'; hash = ('0' * 64) })) {
        $hostileDecision = Invoke-TestLedgerRequest `
            -PipeName $ledgerOwnershipProbe.executionPolicy.ledgerPipeName `
            -Request $hostileRequest
        Assert-True ($hostileDecision.allowed -eq $false) `
            'Parent-owned ledger accepted a reset, overwrite, or case-variant request.'
    }
    [object[]] $directBypassRequests = @(
        [ordered]@{
            category = 'command'
            command = $ledgerOwnershipProbe.command.Replace("'gc'", "'jit'")
        }
        [ordered]@{ category = 'help'; command = $ledgerOwnershipProbe.command }
        [ordered]@{
            category = 'command'
            command = "& '$($ledgerOwnershipProbe.context.cliPath)' '--help'"
        })
    foreach ($directBypassRequest in $directBypassRequests) {
        $directBypassDecision = Invoke-TestLedgerRequest `
            -PipeName $ledgerOwnershipProbe.executionPolicy.ledgerPipeName `
            -Request $directBypassRequest
        Assert-True ($directBypassDecision.allowed -eq $false) `
            'A direct client consumed a mismatched or out-of-policy command allowance.'
    }
    $emptyLedgerState = Get-CopilotEvalExecutionPolicyState $ledgerOwnershipProbe.executionPolicy
    Assert-True ($emptyLedgerState.callCount -eq 0 -and $emptyLedgerState.helpCallCount -eq 0) `
        'A denied direct client request changed command or help capacity.'
    $firstLedgerDecision = Invoke-TestPolicyHook `
        -Hook $ledgerOwnershipProbe.preToolHook `
        -SessionId $ledgerOwnershipProbe.context.runId `
        -WorkingDirectory $ledgerOwnershipProbe.context.workspace `
        -ToolName powershell `
        -ToolArguments ([ordered]@{
            command = $ledgerOwnershipProbe.command
            description = 'Consume the sole analysis allowance'
        })
    Assert-True ($firstLedgerDecision.permissionDecision -eq 'allow') `
        'Parent-owned ledger denied its first policy-valid analysis allowance.'
    $exhaustedLedgerDecision = Invoke-TestPolicyHook `
        -Hook $ledgerOwnershipProbe.preToolHook `
        -SessionId $ledgerOwnershipProbe.context.runId `
        -WorkingDirectory $ledgerOwnershipProbe.context.workspace `
        -ToolName powershell `
        -ToolArguments ([ordered]@{
            command = $ledgerOwnershipProbe.command
            description = 'Prove hostile requests did not reset the allowance'
        })
    $ledgerOwnershipState = Get-CopilotEvalExecutionPolicyState $ledgerOwnershipProbe.executionPolicy
    Assert-True ($exhaustedLedgerDecision.permissionDecision -eq 'deny' -and
        $ledgerOwnershipState.callCount -eq 1) `
        'Hostile ledger requests reset or forged the authoritative allowance state.'
    $malformedInput = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -RawInput '{'
    Assert-True ($malformedInput.permissionDecision -eq 'deny') 'Malformed hook input was not denied.'
    [string] $validToolArgumentsJson = [string]($validArguments | ConvertTo-Json -Compress)
    [string] $duplicateToolArgumentsJson = $validToolArgumentsJson.Replace(
        '"command":',
        '"command":"duplicate","command":')
    $duplicateToolArgumentsDecision = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName powershell `
        -ToolArguments $duplicateToolArgumentsJson
    Assert-True ($duplicateToolArgumentsDecision.permissionDecision -eq 'deny') `
        'Duplicate string-valued tool arguments were normalized and allowed.'
    [string] $validHookInputJson = [string]([ordered]@{
            sessionId = $policySessionId
            timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            cwd = $policyWorkspace
            toolName = 'powershell'
            toolArgs = $validArguments
        } | ConvertTo-Json -Depth 8 -Compress)
    [string] $duplicateHookInputJson = $validHookInputJson.Replace(
        '"toolName":"powershell"',
        '"toolName":"view","toolName":"powershell"')
    $duplicateHookInput = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -RawInput $duplicateHookInputJson
    Assert-True ($duplicateHookInput.permissionDecision -eq 'deny') `
        'Duplicate hook envelope members were normalized and allowed.'
    [string] $caseVariantInputJson = [string]([ordered]@{
            SessionId = $policySessionId
            timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            cwd = $policyWorkspace
            toolName = 'powershell'
            toolArgs = $validArguments
        } | ConvertTo-Json -Depth 8 -Compress)
    $caseVariantInput = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -RawInput $caseVariantInputJson
    Assert-True ($caseVariantInput.permissionDecision -eq 'deny') `
        'Case-variant top-level hook input member was accepted.'
    $unknownTool = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName web_fetch `
        -ToolArguments ([ordered]@{ url = 'https://example.com' })
    Assert-True ($unknownTool.permissionDecision -eq 'deny') 'Unknown tool was not denied before execution.'
    $caseVariantPowerShell = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName PowerShell `
        -ToolArguments $validArguments
    Assert-True ($caseVariantPowerShell.permissionDecision -eq 'deny') `
        'Case-variant PowerShell tool name was allowed before execution.'
    $caseVariantHelp = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName powershell `
        -ToolArguments ([ordered]@{
            command = "& '$($policyProbe.context.cliPath)' '--HELP'"
            description = 'Show case-variant help'
        })
    Assert-True ($caseVariantHelp.permissionDecision -eq 'deny') `
        'Case-variant help token was allowed before execution.'
    $invalidCommands = @(
        "& 'Get-Date'",
        "$($policyProbe.command) | Out-String",
        "begin { Write-Output 'decoy' } end { $($policyProbe.command) }",
        "process { Write-Output 'decoy' } end { $($policyProbe.command) }",
        "end { $($policyProbe.command) } clean { Write-Output 'decoy' }",
        "param(`$value = (Write-Output 'decoy')); $($policyProbe.command)",
        "trap { Write-Output 'decoy' }; $($policyProbe.command)",
        "using namespace System`n$($policyProbe.command)",
        "#requires -Modules Microsoft.PowerShell.Utility`n$($policyProbe.command)",
        $policyProbe.command.Replace("'gc'", '$(Get-Date)'),
        $policyProbe.command.Replace("'--format' 'json'", "'--native-symbols' 'true' '--format' 'json'"),
        $policyProbe.command.Replace("'--format' 'json'", "'--symbol-path' 'https://example.com/symbols' '--format' 'json'"),
        $policyProbe.command.Replace("'--format' 'json'", "'--output' 'result.json' '--format' 'json'"))
    foreach ($invalidCommand in $invalidCommands) {
        $decision = Invoke-TestPolicyHook `
            -Hook $policyProbe.preToolHook `
            -SessionId $policySessionId `
            -WorkingDirectory $policyWorkspace `
            -ToolName powershell `
            -ToolArguments ([ordered]@{ command = $invalidCommand; description = 'Analyze the owned trace' })
        Assert-True ($decision.permissionDecision -eq 'deny') "Unsafe command '$invalidCommand' was not denied."
    }
    foreach ($invalidDescription in @('', "line one`nline two", ('x' * 257))) {
        $decision = Invoke-TestPolicyHook `
            -Hook $policyProbe.preToolHook `
            -SessionId $policySessionId `
            -WorkingDirectory $policyWorkspace `
            -ToolName powershell `
            -ToolArguments ([ordered]@{ command = $policyProbe.command; description = $invalidDescription })
        Assert-True ($decision.permissionDecision -eq 'deny') 'Adversarial PowerShell description was not denied.'
    }
    [object[]] $invalidPowerShellArguments = @(
        [ordered]@{ Command = $policyProbe.command; description = 'Case command member' }
        [ordered]@{ command = $policyProbe.command; Description = 'Case description member' }
        [ordered]@{ command = $policyProbe.command; description = 'Case mode member'; Mode = 'sync' }
        [ordered]@{ command = $policyProbe.command; description = 'Case wait member'; Initial_Wait = 30 }
        [ordered]@{ description = 'Missing command' }
        [ordered]@{ command = $policyProbe.command }
        [ordered]@{ command = @($policyProbe.command); description = 'Invalid command type' }
        [ordered]@{ command = $policyProbe.command; description = 'Invalid mode type'; mode = $true }
        [ordered]@{ command = $policyProbe.command; description = 'Async mode'; mode = 'async' }
        [ordered]@{ command = $policyProbe.command; description = 'Repl mode'; mode = 'repl' }
        [ordered]@{ command = $policyProbe.command; description = 'Case variant'; mode = 'Sync' }
        [ordered]@{ command = $policyProbe.command; description = 'String wait'; initial_wait = '30' }
        [ordered]@{ command = $policyProbe.command; description = 'Boolean wait'; initial_wait = $true }
        [ordered]@{ command = $policyProbe.command; description = 'Zero wait'; initial_wait = 0 }
        [ordered]@{ command = $policyProbe.command; description = 'Unbounded wait'; initial_wait = 31 }
        [ordered]@{ command = $policyProbe.command; description = 'Env'; env = @{} }
        [ordered]@{ command = $policyProbe.command; description = 'Environment'; environment = @{} }
        [ordered]@{ command = $policyProbe.command; description = 'Input'; input = 'value' }
        [ordered]@{ command = $policyProbe.command; description = 'Timeout'; timeout = 30 }
        [ordered]@{ command = $policyProbe.command; description = 'Sandbox'; sandbox = $true })
    foreach ($invalidArguments in $invalidPowerShellArguments) {
        $decision = Invoke-TestPolicyHook `
            -Hook $policyProbe.preToolHook `
            -SessionId $policySessionId `
            -WorkingDirectory $policyWorkspace `
            -ToolName powershell `
            -ToolArguments $invalidArguments
        Assert-True ($decision.permissionDecision -eq 'deny') 'Invalid PowerShell metadata was not denied.'
    }
    $firstPreTool = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName powershell `
        -ToolArguments $validArguments
    Assert-True ($firstPreTool.permissionDecision -eq 'allow') 'First bounded pre-tool command was denied.'
    $firstState = Get-CopilotEvalExecutionPolicyState $policyProbe.executionPolicy
    Assert-True ($firstState.callCount -eq 1) 'First allowed command did not atomically advance count to one.'
    [string] $stringArguments = $validArguments | ConvertTo-Json -Compress
    $secondPreTool = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName powershell `
        -ToolArguments $stringArguments
    Assert-True ($secondPreTool.permissionDecision -eq 'allow') 'JSON-string toolArgs were not parsed and allowed.'
    $secondState = Get-CopilotEvalExecutionPolicyState $policyProbe.executionPolicy
    Assert-True ($secondState.callCount -eq 2) 'Second allowed command did not atomically advance count to two.'
    $modernArguments = [ordered]@{
        command = $policyProbe.command
        description = 'Inspect trace metadata'
        mode = 'sync'
        initial_wait = 30
    }
    $thirdPreTool = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName powershell `
        -ToolArguments $modernArguments
    Assert-True ($thirdPreTool.permissionDecision -eq 'allow') 'Observed modern PowerShell metadata was denied.'
    $thirdState = Get-CopilotEvalExecutionPolicyState $policyProbe.executionPolicy
    Assert-True ($thirdState.callCount -eq 3) 'Modern pre-tool command did not atomically advance count to three.'
    $excessPreTool = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName powershell `
        -ToolArguments $validArguments
    Assert-True ($excessPreTool.permissionDecision -eq 'deny') 'Pre-tool command above MaxSteps was not denied.'
    $boundedState = Get-CopilotEvalExecutionPolicyState $policyProbe.executionPolicy
    Assert-True ($boundedState.callCount -eq 3 -and @($boundedState.commandHashes).Count -eq 3) `
        'Bounded policy state did not retain exactly three allowed commands.'

    $singleCallProbe = New-TestExecutionPolicy `
        -Name 'single analysis call with help policy hook contract' `
        -MaxCalls 6 `
        -TaskMaxCalls 1
    $singleHelpDecision = Invoke-TestPolicyHook `
        -Hook $singleCallProbe.preToolHook `
        -SessionId $singleCallProbe.context.runId `
        -WorkingDirectory $singleCallProbe.context.workspace `
        -ToolName powershell `
        -ToolArguments ([ordered]@{
            command = "& '$($singleCallProbe.context.cliPath)' '--help'"
            description = 'Show bounded help'
        })
    $singleAnalysisDecision = Invoke-TestPolicyHook `
        -Hook $singleCallProbe.preToolHook `
        -SessionId $singleCallProbe.context.runId `
        -WorkingDirectory $singleCallProbe.context.workspace `
        -ToolName powershell `
        -ToolArguments ([ordered]@{
            command = $singleCallProbe.command
            description = 'Analyze the owned trace with filtrace'
        })
    $singleCallState = Get-CopilotEvalExecutionPolicyState $singleCallProbe.executionPolicy
    Assert-True ($singleHelpDecision.permissionDecision -eq 'allow' -and
        $singleAnalysisDecision.permissionDecision -eq 'allow' -and
        $singleCallState.helpCallCount -eq 1 -and $singleCallState.callCount -eq 1) `
        'One bounded help call exhausted a task maxCalls=1 analysis budget.'

    $specialFileProbe = New-TestExecutionPolicy -Name 'special file policy hook contract' -MaxCalls 1
    Remove-Item -LiteralPath $specialFileProbe.context.cliPath -Force
    [System.IO.Directory]::CreateDirectory($specialFileProbe.context.cliPath) | Out-Null
    $specialFileDecision = Invoke-TestPolicyHook `
        -Hook $specialFileProbe.preToolHook `
        -SessionId $specialFileProbe.context.runId `
        -WorkingDirectory $specialFileProbe.context.workspace `
        -ToolName powershell `
        -ToolArguments ([ordered]@{
            command = $specialFileProbe.command
            description = 'Analyze the owned trace with filtrace'
        })
    Assert-True ($specialFileDecision.permissionDecision -eq 'deny') `
        'A non-file apphost path was not denied before execution.'

    $concurrentProbe = New-TestExecutionPolicy -Name 'concurrent policy hook contract' -MaxCalls 2
    [string] $concurrentInput = [string]([ordered]@{
            sessionId = $concurrentProbe.context.runId
            timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            cwd = $concurrentProbe.context.workspace
            toolName = 'powershell'
            toolArgs = [ordered]@{
                command = $concurrentProbe.command
                description = 'Analyze the owned trace with filtrace'
            }
        } | ConvertTo-Json -Depth 8 -Compress)
    [System.Collections.Generic.List[System.Diagnostics.Process]] $hookProcesses =
        [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($index in 1..8) {
        [System.Diagnostics.ProcessStartInfo] $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = [string]$concurrentProbe.preToolHook.exec
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @($concurrentProbe.preToolHook.args)) { [void]$startInfo.ArgumentList.Add([string]$argument) }
        [System.Diagnostics.Process] $hookProcess = [System.Diagnostics.Process]::new()
        $hookProcess.StartInfo = $startInfo
        Assert-True $hookProcess.Start() 'Concurrent policy hook process did not start.'
        $hookProcess.StandardInput.WriteLine($concurrentInput)
        $hookProcess.StandardInput.Close()
        $hookProcesses.Add($hookProcess)
    }
    [int] $concurrentAllows = 0
    try {
        foreach ($hookProcess in $hookProcesses) {
            Assert-True ($hookProcess.WaitForExit(10000)) 'Concurrent policy hook did not finish within 10 seconds.'
            [string] $hookOutput = $hookProcess.StandardOutput.ReadToEnd()
            [string] $hookError = $hookProcess.StandardError.ReadToEnd()
            Assert-True ($hookProcess.ExitCode -eq 0) "Concurrent policy hook failed: $hookError"
            $hookDecision = $hookOutput | ConvertFrom-Json
            Assert-True ($hookDecision.permissionDecision -in @('allow', 'deny')) 'Concurrent policy hook emitted an unknown decision.'
            if ($hookDecision.permissionDecision -eq 'allow') { $concurrentAllows++ }
        }
    }
    finally {
        foreach ($hookProcess in $hookProcesses) { $hookProcess.Dispose() }
    }
    Assert-True ($concurrentAllows -eq 2) `
        'Concurrent pre-tool decisions did not saturate exactly at the two-call cap.'
    $concurrentState = Get-CopilotEvalExecutionPolicyState $concurrentProbe.executionPolicy
    Assert-True ($concurrentState.callCount -eq 2) `
        'Concurrent policy state did not retain exactly two atomic consumptions.'

    $viewProbe = New-TestExecutionPolicy -Name 'view policy hook contract' -MaxCalls 1 -WithSkill
    $viewSource = Get-AgentEvalSkillSource $viewProbe.context.skillPath
    [string] $duplicateViewRequest = [string]([ordered]@{
            category = 'view'
            arguments = [ordered]@{ path = $viewProbe.context.skillPath }
        } | ConvertTo-Json -Compress)
    $duplicateViewRequest = $duplicateViewRequest.Replace(
        '"path":',
        '"path":"duplicate","path":')
    $duplicateViewDecision = Invoke-TestRawLedgerRequest `
        -PipeName $viewProbe.executionPolicy.ledgerPipeName `
        -RequestText $duplicateViewRequest
    Assert-True ($duplicateViewDecision.allowed -eq $false) `
        'Parent ledger accepted duplicate view argument members.'
    [string] $duplicateLedgerRoot = [string]([ordered]@{
            category = 'command'
            command = $viewProbe.command
        } | ConvertTo-Json -Compress)
    $duplicateLedgerRoot = $duplicateLedgerRoot.Replace(
        '"category":"command"',
        '"category":"help","category":"command"')
    $duplicateLedgerRootDecision = Invoke-TestRawLedgerRequest `
        -PipeName $viewProbe.executionPolicy.ledgerPipeName `
        -RequestText $duplicateLedgerRoot
    Assert-True ($duplicateLedgerRootDecision.allowed -eq $false) `
        'Parent ledger accepted duplicate request members.'
    $directViewBypass = Invoke-TestLedgerRequest `
        -PipeName $viewProbe.executionPolicy.ledgerPipeName `
        -Request ([ordered]@{
            category = 'view'
            arguments = [ordered]@{ path = $viewProbe.context.fixturePath }
        })
    Assert-True ($directViewBypass.allowed -eq $false) `
        'A direct client consumed a view allowance for a non-skill path.'
    $firstView = Invoke-TestPolicyHook `
        -Hook $viewProbe.preToolHook `
        -SessionId $viewProbe.context.runId `
        -WorkingDirectory $viewProbe.context.workspace `
        -ToolName view `
        -ToolArguments ([ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 2) })
    Assert-True ($firstView.permissionDecision -eq 'allow') 'First bounded SKILL.md view was denied.'
    $caseVariantView = Invoke-TestPolicyHook `
        -Hook $viewProbe.preToolHook `
        -SessionId $viewProbe.context.runId `
        -WorkingDirectory $viewProbe.context.workspace `
        -ToolName View `
        -ToolArguments ([ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 2) })
    Assert-True ($caseVariantView.permissionDecision -eq 'deny') `
        'Case-variant view tool name was allowed before execution.'
    $viewState = Get-CopilotEvalExecutionPolicyState $viewProbe.executionPolicy
    Assert-True ($viewState.callCount -eq 0 -and @($viewState.viewRequests).Count -eq 1) `
        'First SKILL.md page did not consume only one view allowance.'
    $expectedFirstRequest = Get-AgentEvalSkillViewRequest `
        -Arguments ([pscustomobject]@{ path = $viewProbe.context.skillPath; view_range = @(1, 2) }) `
        -Source $viewSource
    Assert-True `
        ($viewState.viewRequests[0].requestHash -eq $expectedFirstRequest.hash) `
        'View ledger did not retain the exact first argument hash.'
    $defaultView = Invoke-TestPolicyHook `
        -Hook $viewProbe.preToolHook `
        -SessionId $viewProbe.context.runId `
        -WorkingDirectory $viewProbe.context.workspace `
        -ToolName view `
        -ToolArguments ([ordered]@{ path = $viewProbe.context.skillPath })
    Assert-True ($defaultView.permissionDecision -eq 'allow') 'Path-only full-range SKILL.md view was denied.'
    foreach ($range in @(@(6, 6), @(5, 7))) {
        $boundedView = Invoke-TestPolicyHook `
            -Hook $viewProbe.preToolHook `
            -SessionId $viewProbe.context.runId `
            -WorkingDirectory $viewProbe.context.workspace `
            -ToolName view `
            -ToolArguments ([ordered]@{ path = $viewProbe.context.skillPath; view_range = $range })
        Assert-True ($boundedView.permissionDecision -eq 'allow') 'A bounded SKILL.md continuation was denied.'
    }
    $excessView = Invoke-TestPolicyHook `
        -Hook $viewProbe.preToolHook `
        -SessionId $viewProbe.context.runId `
        -WorkingDirectory $viewProbe.context.workspace `
        -ToolName view `
        -ToolArguments ([ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 1) })
    Assert-True ($excessView.permissionDecision -eq 'deny') 'A fifth SKILL.md view was not denied.'
    $boundedViewState = Get-CopilotEvalExecutionPolicyState $viewProbe.executionPolicy
    Assert-True (@($boundedViewState.viewRequests).Count -eq 4) `
        'View policy did not stop at exactly four requests.'
    $atEofRequest = Get-AgentEvalSkillViewRequest `
        -Arguments ([pscustomobject]@{ path = $viewProbe.context.skillPath; view_range = @(5, 6) }) `
        -Source $viewSource
    $beyondEofRequest = Get-AgentEvalSkillViewRequest `
        -Arguments ([pscustomobject]@{ path = $viewProbe.context.skillPath; view_range = @(5, 7) }) `
        -Source $viewSource
    Assert-True ($atEofRequest.requestedBytes -eq $beyondEofRequest.requestedBytes -and
        $atEofRequest.hash -ne $beyondEofRequest.hash -and
        $beyondEofRequest.endLine -eq 7 -and $beyondEofRequest.clampedEndLine -eq 6) `
        'Beyond-EOF view did not clamp bytes while preserving its exact requested argument hash.'
    Assert-True (@($boundedViewState.viewRequests[3].arguments.view_range)[1] -eq 7) `
        'View policy ledger normalized the requested end line instead of preserving it.'
    $relatedViewProbe = New-TestExecutionPolicy -Name 'related view policy hook contract' -MaxCalls 1 -WithSkill
    [string] $relatedViewPath = Join-Path (Split-Path -Parent $relatedViewProbe.context.skillPath) 'reference.md'
    $relatedView = Invoke-TestPolicyHook `
        -Hook $relatedViewProbe.preToolHook `
        -SessionId $relatedViewProbe.context.runId `
        -WorkingDirectory $relatedViewProbe.context.workspace `
        -ToolName view `
        -ToolArguments ([ordered]@{ path = $relatedViewPath })
    Assert-True ($relatedView.permissionDecision -eq 'allow') 'Attested related skill file view was denied.'
    [string] $otherViewPath = Join-Path (Split-Path -Parent $viewProbe.context.skillPath) 'unlisted.md'
    [System.IO.File]::WriteAllText($otherViewPath, 'not the selected skill entry point')
    $otherView = Invoke-TestPolicyHook `
        -Hook $viewProbe.preToolHook `
        -SessionId $viewProbe.context.runId `
        -WorkingDirectory $viewProbe.context.workspace `
        -ToolName view `
        -ToolArguments ([ordered]@{ path = $otherViewPath })
    Assert-True ($otherView.permissionDecision -eq 'deny') 'Non-SKILL.md view was not denied.'
    $invalidViewProbe = New-TestExecutionPolicy -Name 'invalid view policy hook contract' -MaxCalls 1 -WithSkill
    [string] $invalidViewPath = $invalidViewProbe.context.skillPath
    [object[]] $invalidViewArguments = @(
        [ordered]@{ Path = $invalidViewPath }
        [ordered]@{ path = $invalidViewPath; View_Range = @(1, 2) }
        [ordered]@{ path = $invalidViewPath; view_range = 1 }
        [ordered]@{ path = $invalidViewPath; view_range = @() }
        [ordered]@{ path = $invalidViewPath; view_range = @(1) }
        [ordered]@{ path = $invalidViewPath; view_range = @(1, 2, 3) }
        [ordered]@{ path = $invalidViewPath; view_range = @('1', 2) }
        [ordered]@{ path = $invalidViewPath; view_range = @($true, 2) }
        [ordered]@{ path = $invalidViewPath; view_range = @(0, 1) }
        [ordered]@{ path = $invalidViewPath; view_range = @(1, 0) }
        [ordered]@{ path = $invalidViewPath; view_range = @(2, 1) }
        [ordered]@{ path = $invalidViewPath; view_range = @(7, 7) }
        [ordered]@{ path = $invalidViewPath; view_range = @(1, 519) }
        [ordered]@{ path = $invalidViewPath; view_range = @(1, [long]::MaxValue) }
        [ordered]@{ path = $invalidViewPath; view_range = @(1, -2) }
        [ordered]@{ path = $invalidViewPath; view_range = @(1, 2); extra = $true })
    foreach ($invalidViewArgument in $invalidViewArguments) {
        [bool] $viewEvidenceRejected = $false
        try {
            [void](Get-AgentEvalSkillViewRequest `
                    -Arguments $invalidViewArgument `
                    -Source (Get-AgentEvalSkillSource $invalidViewProbe.context.skillPath))
        }
        catch { $viewEvidenceRejected = $true }
        Assert-True $viewEvidenceRejected 'Transcript validation accepted malformed view arguments.'
        $invalidView = Invoke-TestPolicyHook `
            -Hook $invalidViewProbe.preToolHook `
            -SessionId $invalidViewProbe.context.runId `
            -WorkingDirectory $invalidViewProbe.context.workspace `
            -ToolName view `
            -ToolArguments $invalidViewArgument
        Assert-True ($invalidView.permissionDecision -eq 'deny') `
            "Malformed view arguments were not denied: $($invalidViewArgument | ConvertTo-Json -Depth 4 -Compress)"
    }

    [int] $truncatedLength = $viewSource.text.IndexOf('é', [StringComparison]::Ordinal) + 1
    [int] $continuationOffset = $truncatedLength
    [int] $continuationIndex = [Array]::BinarySearch([int[]]$viewSource.lineStarts, $continuationOffset)
    [int] $continuationLine = if ($continuationIndex -ge 0) { $continuationIndex + 1 } else { -bnot $continuationIndex }
    [int] $lastObservedIndex = [Array]::BinarySearch([int[]]$viewSource.lineStarts, $truncatedLength - 1)
    [int] $warningLine = $(if ($lastObservedIndex -ge 0) { $lastObservedIndex + 2 } else { (-bnot $lastObservedIndex) + 1 })
    [string] $truncationWarning = "[Output truncated. Use view_range=[$warningLine, ...] to continue reading. In your next response, you may batch this with other view calls. File has at least $($viewSource.renderedLineCount) lines.]"
    $firstPageArguments = [pscustomobject]@{ path = $viewProbe.context.skillPath; view_range = @(1, -1) }
    $continuationArguments = [pscustomobject]@{ path = $viewProbe.context.skillPath; view_range = @($continuationLine, -1) }
    [string] $continuationText = $viewSource.text.Substring($viewSource.lineStarts[$continuationLine - 1])
    [object[]] $completeSkillReads = @(
        [pscustomobject]@{
            callId = 'page-1'; arguments = $firstPageArguments; succeeded = $true
            result = [pscustomobject]@{
                content = $viewSource.text.Substring(0, $truncatedLength) + "`n`n" + $truncationWarning
                detailedContent = 'display-only forged diff'
            }
        }
        [pscustomobject]@{
            callId = 'page-2'; arguments = $continuationArguments; succeeded = $true
            result = [pscustomobject]@{ content = $continuationText; detailedContent = 'another display rendering' }
        })
    $completeSkillEvidence = Complete-AgentEvalSkillEvidence `
        -SourcePath $viewProbe.context.skillPath `
        -Reads $completeSkillReads
    Assert-True $completeSkillEvidence.verified 'Overlapping truncated CRLF/Unicode skill pages did not verify.'
    Assert-True ($completeSkillEvidence.reads[0].protocol -eq 'truncated-v1') `
        'Truncated skill page did not retain its protocol.'
    Assert-True ($completeSkillEvidence.reads[0].continuationLine -eq $warningLine -and
        $completeSkillEvidence.reads[0].coverageContinuationLine -eq $continuationLine) `
        'Skill proof conflated the host line hint with the overlap needed for complete coverage.'
    Assert-True ($completeSkillEvidence.sourceTerminalNewline -and
        $completeSkillEvidence.sourceBytes -gt $completeSkillEvidence.sourceChars) `
        'Skill proof did not preserve CRLF and multibyte source distinctions.'
    $missingPageEvidence = Complete-AgentEvalSkillEvidence `
        -SourcePath $viewProbe.context.skillPath `
        -Reads @($completeSkillReads[0])
    Assert-True (-not $missingPageEvidence.verified -and
        $missingPageEvidence.failure.Contains('complete source', [StringComparison]::Ordinal)) `
        'Missing skill continuation did not remain unverified.'

    foreach ($lineEndingCase in @(
            [pscustomobject]@{ name = 'CRLF terminal'; newline = "`r`n"; terminal = $true; omissions = 2; omittedChars = 4 }
            [pscustomobject]@{ name = 'LF terminal'; newline = "`n"; terminal = $true; omissions = 2; omittedChars = 2 }
            [pscustomobject]@{ name = 'CRLF no terminal'; newline = "`r`n"; terminal = $false; omissions = 1; omittedChars = 2 }
        )) {
        [string] $knownSourcePath = Join-Path $temporaryRoot "known 400 $($lineEndingCase.name).md"
        [string[]] $knownLines = @(1..400 | ForEach-Object {
                if ($_ -eq 1) { 'café β' }
                elseif ($_ -eq 2) { '' }
                elseif ($_ -eq 400) { 'last line' }
                else { "line $_" }
            })
        [string] $knownText = $knownLines -join $lineEndingCase.newline
        if ($lineEndingCase.terminal) { $knownText += $lineEndingCase.newline }
        [System.IO.File]::WriteAllText($knownSourcePath, $knownText, [System.Text.UTF8Encoding]::new($false))
        $knownSource = Get-AgentEvalSkillSource $knownSourcePath
        $knownArguments = @(
            [pscustomobject]@{ path = $knownSourcePath; view_range = @(1, 250) }
            [pscustomobject]@{ path = $knownSourcePath; view_range = @(251, 500) })
        [System.Collections.Generic.List[object]] $knownReads = [System.Collections.Generic.List[object]]::new()
        for ($knownIndex = 0; $knownIndex -lt $knownArguments.Count; $knownIndex++) {
            $knownRequest = Get-AgentEvalSkillViewRequest $knownArguments[$knownIndex] $knownSource
            [string] $knownContent = $knownRequest.requestedText
            if ($knownContent.EndsWith("`r`n", [StringComparison]::Ordinal)) {
                $knownContent = $knownContent.Substring(0, $knownContent.Length - 2)
            }
            elseif ($knownContent.EndsWith("`n", [StringComparison]::Ordinal)) {
                $knownContent = $knownContent.Substring(0, $knownContent.Length - 1)
            }
            $knownReads.Add([pscustomobject]@{
                    callId = "known-$knownIndex"
                    arguments = $knownArguments[$knownIndex]
                    succeeded = $true
                    result = [pscustomobject]@{ content = $knownContent; detailedContent = 'display only' }
                })
        }
        $knownEvidence = Complete-AgentEvalSkillEvidence -SourcePath $knownSourcePath -Reads $knownReads.ToArray()
        Assert-True ($knownEvidence.verified -and
            $knownEvidence.sourceLineCount -eq 400 -and
            $knownEvidence.sourceTerminalNewline -eq $lineEndingCase.terminal -and
            $knownEvidence.terminalNewlineOmissions -eq $lineEndingCase.omissions -and
            $knownEvidence.returnedPayloadChars -eq ($knownEvidence.sourceChars - $lineEndingCase.omittedChars) -and
            $knownEvidence.observedChars -eq $knownEvidence.sourceChars -and
            $knownEvidence.observedTextSha256 -eq $knownEvidence.sourceTextSha256 -and
            $knownEvidence.textNormalization -eq 'exact-text-with-single-view-terminal-newline-restoration-v1') `
            "Known $($lineEndingCase.name) source did not preserve exact logical text and explicit normalization evidence."
    }

    foreach ($mode in @(
            'answer-only', 'answer-before-analysis', 'completion-before-start', 'missing-tool', 'failed-completion', 'wrong-cli-path', 'wrong-answer', 'host-failure',
            'decoy-command', 'duplicate-call-id', 'missing-completion', 'unexpected-tool',
            'denied-unknown-tool', 'powershell-tool-case',
            'model-after-analysis', 'mismatched-operation', 'unknown-cli-schema', 'fractional-cli-schema',
            'scalar-cli-result', 'empty-cli-result', 'duplicate-cli-root-member',
            'duplicate-cli-context-member', 'duplicate-cli-result-member', 'malformed-shell-wrapper',
            'nonzero-shell-wrapper', 'mismatched-shell-content', 'command-member-case',
            'description-member-case', 'mode-member-case', 'missing-command-argument',
            'missing-description-argument', 'invalid-command-type', 'invalid-mode-type', 'async-mode',
            'repl-mode', 'invalid-initial-wait-type', 'zero-initial-wait', 'unbounded-initial-wait',
            'shell-sandbox-flag')) {
        $negative = Invoke-FakeRun -Name "cli $mode" -Mode $mode
        Assert-True ($negative.iterations[0].success -eq $false) "Fake CLI mode '$mode' unexpectedly passed."
    }
    $unexecuted = Invoke-FakeRun -Name 'cli allowed-no-execution' -Mode allowed-no-execution
    Assert-True ($unexecuted.iterations[0].success -eq $false -and
        $unexecuted.iterations[0].calls -eq 0 -and
        $unexecuted.iterations[0].execution.isolation.executionPolicyCallCount -eq 1) `
        'Conservatively consumed but unexecuted command did not invalidate evidence.'
    $fallback = Invoke-FakeRun -Name 'cli no-hook-fallback' -Mode no-hook-fallback
    Assert-True ($fallback.iterations[0].success -eq $false -and
        $fallback.iterations[0].calls -eq 0 -and
        $fallback.iterations[0].execution.isolation.executionPolicyCallCount -eq 0 -and
        $fallback.iterations[0].execution.isolation.shellDefaultDenied -eq $true) `
        'Missing-hook fallback did not remain unexecuted under the normal shell deny.'
    Assert-FakeParserFailureArtifacts `
        -Name 'malformed jsonl' `
        -Mode malformed-jsonl `
        -ExpectedMessage 'malformed JSONL' `
        -ExpectedRawText "{`r`n"
    Assert-FakeParserFailureArtifacts `
        -Name 'unknown event' `
        -Mode unknown-event `
        -ExpectedMessage 'unknown event type' `
        -ExpectedRawText '"type":"future.event"'
    Assert-FakeParserFailureArtifacts `
        -Name 'case variant event' `
        -Mode case-variant-event `
        -ExpectedMessage 'unknown event type' `
        -ExpectedRawText '"type":"RESULT"'
    foreach ($parserCase in @(
            [pscustomobject]@{ mode = 'event-root-extra-member'; raw = '"extra":true'; arm = 'cli' }
            [pscustomobject]@{ mode = 'duplicate-event-member'; raw = '"toolCallId":"call-1","toolCallId"'; arm = 'cli' }
            [pscustomobject]@{ mode = 'event-data-extra-member'; raw = '"extra":true'; arm = 'cli' }
            [pscustomobject]@{ mode = 'event-data-member-case'; raw = '"Data":'; arm = 'cli' }
            [pscustomobject]@{ mode = 'tool-call-id-member-case'; raw = '"ToolCallId":'; arm = 'cli' }
            [pscustomobject]@{ mode = 'completion-result-member-case'; raw = '"Result":'; arm = 'cli' }
            [pscustomobject]@{ mode = 'completion-success-member-case'; raw = '"Success":'; arm = 'cli' }
            [pscustomobject]@{ mode = 'answer-content-member-case'; raw = '"Content":'; arm = 'cli' }
            [pscustomobject]@{ mode = 'non-string-answer-content'; raw = '"content":7'; arm = 'cli' }
            [pscustomobject]@{ mode = 'result-exit-code-member-case'; raw = '"ExitCode":'; arm = 'cli' }
            [pscustomobject]@{ mode = 'model-member-case'; raw = '"Model":'; arm = 'cli' }
            [pscustomobject]@{ mode = 'scalar-model-data'; raw = '"data":"expected-model"'; arm = 'cli' }
            [pscustomobject]@{ mode = 'missing-model-value'; raw = '"data":{}'; arm = 'cli' }
            [pscustomobject]@{ mode = 'non-string-model-value'; raw = '"model":1'; arm = 'cli' }
            [pscustomobject]@{ mode = 'missing-call-id'; raw = '"toolCallId":""'; arm = 'cli' }
            [pscustomobject]@{ mode = 'string-success'; raw = '"success":"true"'; arm = 'cli' }
            [pscustomobject]@{ mode = 'event-after-result'; raw = '"type":"assistant.idle"'; arm = 'cli' }
            [pscustomobject]@{ mode = 'scalar-skill-inventory'; raw = '"skills":{'; arm = 'cli-skill' }
        )) {
        Assert-FakeParserFailureArtifacts `
            -Name "parser $($parserCase.mode)" `
            -Mode $parserCase.mode `
            -ExpectedMessage 'Copilot host emitted' `
            -ExpectedRawText $parserCase.raw `
            -Arm $parserCase.arm
    }
    $lockedBundle = Invoke-FakeRun -Name 'locked bundle' -Mode mutated-bundle
    Assert-True ($lockedBundle.iterations[0].success -eq $true) `
        'The host could rewrite the owned CLI bundle before authorization.'
    $lockedFixture = Invoke-FakeRun -Name 'locked fixture' -Mode mutated-fixture
    Assert-True ($lockedFixture.iterations[0].success -eq $true) `
        'The host could rewrite the owned fixture before authorization.'
    $lockedPolicy = Invoke-FakeRun -Name 'locked execution policy' -Mode mutated-execution-policy
    Assert-True ($lockedPolicy.iterations[0].success -eq $true) `
        'The host could rewrite the execution policy before tool authorization.'
    $lockedHookConfiguration = Invoke-FakeRun `
        -Name 'locked hook configuration' `
        -Mode mutated-hook-configuration
    Assert-True ($lockedHookConfiguration.iterations[0].success -eq $true) `
        'The host could rewrite the hook configuration before tool authorization.'
    $lockedImmutableGrowth = Invoke-FakeRun -Name 'locked immutable growth' -Mode oversized-immutable
    Assert-True ($lockedImmutableGrowth.iterations[0].success -eq $true) `
        'The host could grow the owned CLI bundle before authorization.'
    $wrongModel = Invoke-FakeRun -Name 'cli wrong-model' -Mode wrong-model
    Assert-True ($wrongModel.iterations[0].success -eq $false) 'Wrong observed model unexpectedly passed.'
    Assert-True ($wrongModel.model.requested -eq 'expected-model') 'Requested model was not retained.'
    Assert-True ($wrongModel.model.observed -eq 'other-model') 'Wrong observed model was replaced by the requested model.'
    Assert-True ($wrongModel.model.verified -eq $false) 'Wrong observed model was marked verified.'
    $wrongModelCase = Invoke-FakeRun -Name 'cli wrong-model-case' -Mode wrong-model-case
    Assert-True ($wrongModelCase.iterations[0].success -eq $false) 'Case-only observed model mismatch unexpectedly passed.'
    Assert-True ($wrongModelCase.model.verified -eq $false) 'Case-only observed model mismatch was marked verified.'
    $missingModel = Invoke-FakeRun -Name 'cli missing-model' -Mode missing-model
    Assert-True ($missingModel.iterations[0].success -eq $false) 'Missing observed model unexpectedly passed.'
    Assert-True ($null -eq $missingModel.model.observed) 'Missing observed model was replaced by another identity.'
    Assert-True (@($missingModel.model.observedDistinct).Count -eq 0) 'Missing observed model produced a distinct identity.'
    $modelVariant = Invoke-FakeRun -Name 'cli model-variant' -Mode model-variant
    Assert-True ($modelVariant.iterations[0].success -eq $false) 'Multiple host model variants unexpectedly passed.'
    Assert-True ($modelVariant.model.verified -eq $false) 'Multiple host model variants were marked verified.'
    $modelCaseVariant = Invoke-FakeRun -Name 'cli model-case-variant' -Mode model-case-variant
    Assert-True ($modelCaseVariant.iterations[0].success -eq $false) 'Case-distinct host model variants unexpectedly passed.'
    Assert-True ($modelCaseVariant.model.observedDistinct.Count -eq 2) `
        'Case-distinct host model variants were collapsed.'

    $skillSuccess = Invoke-FakeRun -Name 'skill success' -Mode success -Arm cli-skill
    Assert-True ($skillSuccess.iterations[0].success -eq $true) "Verified skill run failed: $($skillSuccess.iterations[0].note)"
    Assert-True ($skillSuccess.iterations[0].skill.provided -eq $true) 'Skill provision was not recorded.'
    Assert-True ($skillSuccess.iterations[0].skill.observed -eq $true) 'Skill read was not observed.'
    Assert-True ($skillSuccess.iterations[0].skill.verified -eq $true) 'Skill read hash was not verified.'
    Assert-True ($skillSuccess.iterations[0].skill.observedTextSha256 -eq
        $skillSuccess.iterations[0].skill.sourceContextSha256) `
        'Observed skill context hash did not match the normalized source body hash.'
    Assert-True ($skillSuccess.iterations[0].skill.sourceByteSha256 -eq
        $skillSuccess.iterations[0].skill.sha256) `
        'Skill byte attestation was not kept separate and intact.'
    Assert-True ($skillSuccess.iterations[0].skill.discovery.source -eq 'project' -and
        $skillSuccess.iterations[0].skill.discovery.enabled -eq $true) `
        'Skill discovery metadata did not identify the enabled project skill.'
    Assert-True (@($skillSuccess.iterations[0].skill.evidenceCallIds).Count -eq 1) `
        'Skill evidence did not retain exactly one native skill invocation.'
    Assert-True (@($skillSuccess.iterations[0].execution.isolation.executionPolicyViewRequests |
            Where-Object { $null -ne $_ }).Count -eq 0) `
        'Genuine skill discovery unexpectedly used prompt-directed file views.'
    Assert-True ($skillSuccess.iterations[0].execution.isolation.noCustomInstructions -eq $false) `
        'Skill arm unexpectedly disabled project customizations.'
    Assert-True (@($skillSuccess.iterations[0].execution.isolation.availableTools) -contains 'skill') `
        'Skill arm did not expose the native skill tool.'
    Assert-True (@($skillSuccess.iterations[0].execution.isolation.availableTools) -contains 'view') `
        'Skill arm did not expose bounded related-file views.'
    $skillViewSuccess = Invoke-FakeRun -Name 'skill view success' -Mode skill-view-success -Arm cli-skill
    Assert-True ($skillViewSuccess.iterations[0].success -eq $true -and
        @($skillViewSuccess.iterations[0].transcript | Where-Object { $_.kind -eq 'skill-read' }).Count -eq 1) `
        'An exact allowed related-file view did not remain verified.'
    $skillViewEvidence = @($skillViewSuccess.iterations[0].skill.reads)
    [string] $guidePath = Join-Path $root '.agents/skills/filtrace/references/guide.md'
    [string] $expectedGuideText = [System.IO.File]::ReadAllText($guidePath)
    Assert-True ($skillViewEvidence.Count -eq 1 -and
        $skillViewEvidence[0].path.EndsWith('references\guide.md', [StringComparison]::OrdinalIgnoreCase) -and
        $skillViewEvidence[0].contentSha256 -eq (Get-AgentEvalTextHash $expectedGuideText) -and
        $skillViewEvidence[0].sourceByteSha256 -eq (Get-AgentEvalFileHash $guidePath) -and
        $skillViewEvidence[0].requestedBytes -gt 0 -and
        $skillViewSuccess.iterations[0].skill.requestedBytes -eq $skillViewEvidence[0].requestedBytes -and
        $skillViewSuccess.iterations[0].skill.observedChars -eq $skillViewEvidence[0].logicalPayloadChars -and
        $skillViewSuccess.iterations[0].skill.returnedPayloadChars -eq $skillViewEvidence[0].payloadChars) `
        'A validated related-file view did not retain its source, request, and returned-content evidence.'
    $lockedSkillView = Invoke-FakeRun `
        -Name 'locked skill view file' `
        -Mode mutated-skill-view-file `
        -Arm cli-skill
    Assert-True ($lockedSkillView.iterations[0].success -eq $true) `
        'The host could rewrite a viewable skill file before authorization.'
    $sourceSkillFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.agents/skills/filtrace') -File -Recurse)
    Assert-True `
        (@($skillSuccess.iterations[0].skill.inventory).Count -eq $sourceSkillFiles.Count) `
        'Installed skill inventory did not contain every shipped file.'
    foreach ($mode in @(
            'missing-skill-read', 'missing-skill-discovery', 'wrong-skill-hash',
            'failed-skill-load', 'missing-skill-context', 'skill-extra-context',
            'skill-ledger-mismatch', 'skill-tool-case', 'skill-argument-name-case',
            'skill-argument-value-case', 'skill-discovery-name-case',
            'skill-discovery-source-case', 'skill-context-role-case', 'skill-view-altered',
            'answer-before-skill-context', 'skill-context-before-completion',
            'skill-discovery-after-invocation')) {
        $negative = Invoke-FakeRun -Name "skill $mode" -Mode $mode -Arm cli-skill
        Assert-True ($negative.iterations[0].success -eq $false) "Fake skill mode '$mode' unexpectedly passed."
        if ($mode -eq 'missing-skill-read') {
            Assert-True ($negative.iterations[0].skill.observed -eq $true -and
                @($negative.iterations[0].skill.evidenceCallIds).Count -eq 0) `
                'Discovery metadata was not kept distinct from actual skill invocation.'
        }
    }

    Assert-FakeRunThrows -Name 'timeout' -Mode timeout -ExpectedMessage 'did not finish within 1 seconds' -TimeoutSeconds 1
    $closedStreamStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Assert-FakeRunThrows `
        -Name 'closed stream timeout' `
        -Mode closed-stream-timeout `
        -ExpectedMessage 'did not finish within 3 seconds' `
        -TimeoutSeconds 3
    $closedStreamStopwatch.Stop()
    Assert-True ($closedStreamStopwatch.Elapsed.TotalSeconds -lt 5) `
        'Closed host streams bypassed the configured process deadline.'
    $closedStreamArtifactStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Assert-FakeRunThrows `
        -Name 'closed stream artifact' `
        -Mode closed-stream-artifact `
        -ExpectedMessage 'artifacts exceeded 16777216 bytes' `
        -TimeoutSeconds 10
    $closedStreamArtifactStopwatch.Stop()
    Assert-True ($closedStreamArtifactStopwatch.Elapsed.TotalSeconds -lt 4) `
        'Closed host streams suspended periodic artifact enforcement.'
    [string] $outputBoundDirectory = Join-Path $temporaryRoot 'output bound'
    Assert-FakeRunThrows `
        -Name 'output bound' `
        -Mode oversized-output `
        -ExpectedMessage 'output exceeded 1024 bytes' `
        -OutDir $outputBoundDirectory `
        -MaxOutputBytes 1024
    [System.IO.FileInfo[]] $failureOutputFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $outputBoundDirectory 'failures') -File -Recurse)
    [long] $failureOutputBytes = [long](
        $failureOutputFiles | Measure-Object -Property Length -Sum).Sum
    Assert-True ($failureOutputFiles.Count -eq 2 -and
        $failureOutputBytes -gt 0 -and $failureOutputBytes -le 8192) `
        'Output-limit failure did not retain a nonempty evaluator-owned prefix within 8192 bytes.'
    Assert-FakeRunThrows -Name 'artifact bound' -Mode oversized-artifact -ExpectedMessage 'artifacts exceeded 16777216 bytes'

    $descendantStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $descendantRun = Invoke-FakeRun -Name 'orphan descendant' -Mode orphan-descendant
    $descendantStopwatch.Stop()
    Assert-True ($descendantStopwatch.Elapsed.TotalSeconds -lt 4) `
        'Inherited descendant output handles delayed contained host completion.'
    [string] $descendantPidPath = Join-Path $descendantRun.iterations[0].execution.workspace 'descendant.pid'
    Assert-True (Test-Path -LiteralPath $descendantPidPath -PathType Leaf) `
        'Fake host did not record its descendant process id.'
    [int] $descendantPid = [int][System.IO.File]::ReadAllText($descendantPidPath)
    [System.Diagnostics.Process] $descendantProcess = Get-Process -Id $descendantPid -ErrorAction SilentlyContinue
    if ($null -ne $descendantProcess) {
        try { [void]$descendantProcess.WaitForExit(3000) } finally { $descendantProcess.Dispose() }
    }
    Assert-True ($null -eq (Get-Process -Id $descendantPid -ErrorAction SilentlyContinue)) `
        'A descendant survived the contained Copilot host process.'

    $runtimeCache = Invoke-FakeRun -Name 'host runtime cache' -Mode host-runtime-cache
    Assert-True ($runtimeCache.iterations[0].success -eq $true) 'Bounded host runtime cache run failed.'
    Assert-True ($runtimeCache.iterations[0].execution.hostRuntimeBytes -gt 0) 'Host runtime cache bytes were not recorded.'
    Assert-True `
        ($runtimeCache.iterations[0].execution.artifactBytes -lt 16777216) `
        'Host runtime cache bytes were charged to the dynamic artifact budget.'
    Assert-True `
        ($runtimeCache.iterations[0].execution.isolation.hostRuntimeMaxBytes -eq 256MB) `
        'Host runtime aggregate limit was not retained.'
    Assert-True `
        ($runtimeCache.iterations[0].execution.isolation.hostRuntimeMaxFileBytes -eq 128MB) `
        'Host runtime per-file limit was not retained.'
    Assert-True `
        ($runtimeCache.iterations[0].execution.isolation.hostRuntimeMaxEntries -eq 1024) `
        'Host runtime entry limit was not retained.'
    Assert-FakeRunThrows `
        -Name 'host runtime outside cache' `
        -Mode host-runtime-outside-cache `
        -ExpectedMessage 'artifacts exceeded 16777216 bytes'

    $usageRun = Join-Path $temporaryRoot 'runtime usage'
    $usageHome = Join-Path $usageRun 'home'
    $runtimeUsageDirectory = Join-Path $usageHome 'AppData/Local/copilot/pkg'
    [System.IO.Directory]::CreateDirectory($runtimeUsageDirectory) | Out-Null
    $runtimeUsageFile = Join-Path $runtimeUsageDirectory 'runtime.node'
    [System.IO.FileStream] $runtimeUsageStream = [System.IO.File]::Create($runtimeUsageFile)
    try { $runtimeUsageStream.SetLength(1024) } finally { $runtimeUsageStream.Dispose() }
    $usageContext = [pscustomobject]@{
        runDirectory = $usageRun
        home = $usageHome
        isolateHome = $true
        immutableFiles = @()
    }

    Remove-Item -LiteralPath $runtimeUsageFile -Force
    $script:strictAgentEvalDirectoryUsage = ${function:Get-AgentEvalDirectoryUsage}
    $script:runtimeRaceSource = $null
    $script:runtimeRaceDestination = $null
    $script:runtimeRaceCalls = 0
    function Get-AgentEvalDirectoryUsage {
        param(
            [Parameter(Mandatory)][string] $Path,
            [AllowNull()][string[]] $ExcludedPaths = @(),
            [AllowNull()][string[]] $ExcludedDirectories = @(),
            [ValidateRange(1, 1024)][int] $MaxEntries = 512,
            [long] $MaxBytes = [long]::MaxValue,
            [long] $MaxFileBytes = [long]::MaxValue,
            [string] $Scope = 'Copilot host artifacts'
        )

        if ($Scope -eq 'Copilot host runtime cache') {
            $script:runtimeRaceCalls++
            if ($script:runtimeRaceCalls -eq 1) {
                Move-Item -LiteralPath $script:runtimeRaceSource -Destination $script:runtimeRaceDestination
                throw [System.IO.DirectoryNotFoundException]::new(
                    "Could not find a part of the path '$script:runtimeRaceSource'.")
            }
        }
        return & $script:strictAgentEvalDirectoryUsage @PSBoundParameters
    }
    try {
        $script:runtimeRaceSource = Join-Path $runtimeUsageDirectory '.extracting-exact'
        $script:runtimeRaceDestination = Join-Path $runtimeUsageDirectory 'current-exact'
        [System.IO.Directory]::CreateDirectory($script:runtimeRaceSource) | Out-Null
        [System.IO.File]::WriteAllBytes(
            (Join-Path $script:runtimeRaceSource 'runtime.node'),
            [byte[]]::new(1024))
        $renamedRuntimeUsage = Get-AgentEvalCopilotUsage `
            -Context $usageContext `
            -MaxArtifactBytes 16777216 `
            -MaxHostRuntimeBytes 1024
        Assert-True ($script:runtimeRaceCalls -eq 2) 'Disappeared runtime directory did not cause one fresh rescan.'
        Assert-True ($renamedRuntimeUsage.hostRuntime.bytes -eq 1024) `
            'Fresh runtime rescan did not record the atomically renamed tree.'

        Remove-Item -LiteralPath $script:runtimeRaceDestination -Recurse -Force
        $script:runtimeRaceSource = Join-Path $runtimeUsageDirectory '.extracting-over-cap'
        $script:runtimeRaceDestination = Join-Path $runtimeUsageDirectory 'current-over-cap'
        $script:runtimeRaceCalls = 0
        [System.IO.Directory]::CreateDirectory($script:runtimeRaceSource) | Out-Null
        [System.IO.File]::WriteAllBytes(
            (Join-Path $script:runtimeRaceSource 'runtime.node'),
            [byte[]]::new(1025))
        [bool] $renamedRuntimeBytesRejected = $false
        try {
            [void](Get-AgentEvalCopilotUsage `
                    -Context $usageContext `
                    -MaxArtifactBytes 16777216 `
                    -MaxHostRuntimeBytes 1024)
        }
        catch {
            $renamedRuntimeBytesRejected = $_.Exception.Message.Contains(
                'runtime cache exceeded 1024 bytes', [StringComparison]::Ordinal)
        }
        Assert-True ($script:runtimeRaceCalls -eq 2) 'Runtime rename race retried more than once.'
        Assert-True $renamedRuntimeBytesRejected 'Fresh runtime rescan did not enforce the aggregate byte limit.'
    }
    finally {
        Set-Item -LiteralPath function:Get-AgentEvalDirectoryUsage -Value $script:strictAgentEvalDirectoryUsage
        if ($script:runtimeRaceDestination -and (Test-Path -LiteralPath $script:runtimeRaceDestination)) {
            Remove-Item -LiteralPath $script:runtimeRaceDestination -Recurse -Force
        }
        Remove-Variable strictAgentEvalDirectoryUsage, runtimeRaceSource, runtimeRaceDestination, runtimeRaceCalls `
            -Scope Script
    }

    [System.IO.File]::WriteAllBytes($runtimeUsageFile, [byte[]]::new(1024))
    $exactRuntimeUsage = Get-AgentEvalCopilotUsage `
        -Context $usageContext `
        -MaxArtifactBytes 16777216 `
        -MaxHostRuntimeBytes 1024
    Assert-True ($exactRuntimeUsage.hostRuntime.bytes -eq 1024) 'Exact host runtime byte limit was not accepted.'
    Assert-True ($exactRuntimeUsage.artifacts.bytes -eq 0) 'Empty-input context charged runtime bytes as artifacts.'
    $runtimeUsageStream = [System.IO.File]::OpenWrite($runtimeUsageFile)
    try { $runtimeUsageStream.SetLength(1025) } finally { $runtimeUsageStream.Dispose() }
    [bool] $runtimeBytesRejected = $false
    try {
        [void](Get-AgentEvalCopilotUsage `
                -Context $usageContext `
                -MaxArtifactBytes 16777216 `
                -MaxHostRuntimeBytes 1024)
    }
    catch { $runtimeBytesRejected = $_.Exception.Message.Contains('runtime cache exceeded 1024 bytes', [StringComparison]::Ordinal) }
    Assert-True $runtimeBytesRejected 'Host runtime aggregate byte limit was not enforced.'

    $runtimeUsageStream = [System.IO.File]::OpenWrite($runtimeUsageFile)
    try { $runtimeUsageStream.SetLength(128MB) } finally { $runtimeUsageStream.Dispose() }
    $exactRuntimeFileUsage = Get-AgentEvalCopilotUsage `
        -Context $usageContext `
        -MaxArtifactBytes 16777216 `
        -MaxHostRuntimeBytes 256MB
    Assert-True ($exactRuntimeFileUsage.hostRuntime.bytes -eq 128MB) 'Exact host runtime file limit was not accepted.'
    $runtimeUsageStream = [System.IO.File]::OpenWrite($runtimeUsageFile)
    try { $runtimeUsageStream.SetLength(128MB + 1) } finally { $runtimeUsageStream.Dispose() }
    [bool] $runtimeFileRejected = $false
    try {
        [void](Get-AgentEvalCopilotUsage `
                -Context $usageContext `
                -MaxArtifactBytes 16777216 `
                -MaxHostRuntimeBytes 256MB)
    }
    catch { $runtimeFileRejected = $_.Exception.Message.Contains('file', [StringComparison]::Ordinal) -and
            $_.Exception.Message.Contains('exceeded 134217728 bytes', [StringComparison]::Ordinal) }
    Assert-True $runtimeFileRejected 'Host runtime per-file byte limit was not enforced.'

    Remove-Item -LiteralPath $runtimeUsageFile -Force
    foreach ($index in 1..1024) {
        [System.IO.Directory]::CreateDirectory((Join-Path $runtimeUsageDirectory "empty-$index")) | Out-Null
    }
    $exactRuntimeEntries = Get-AgentEvalCopilotUsage `
        -Context $usageContext `
        -MaxArtifactBytes 16777216 `
        -MaxHostRuntimeBytes 256MB
    Assert-True ($exactRuntimeEntries.hostRuntime.entries -eq 1024) 'Exact host runtime entry limit was not accepted.'
    [System.IO.Directory]::CreateDirectory((Join-Path $runtimeUsageDirectory 'empty-1025')) | Out-Null
    [bool] $runtimeEntriesRejected = $false
    try {
        [void](Get-AgentEvalCopilotUsage `
                -Context $usageContext `
                -MaxArtifactBytes 16777216 `
                -MaxHostRuntimeBytes 256MB)
    }
    catch { $runtimeEntriesRejected = $_.Exception.Message.Contains('runtime cache exceeded 1024 entries', [StringComparison]::Ordinal) }
    Assert-True $runtimeEntriesRejected 'Host runtime total-entry limit did not count empty directories.'

    $nullExcludedDirectory = Join-Path $temporaryRoot 'null excluded usage'
    [System.IO.Directory]::CreateDirectory($nullExcludedDirectory) | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $nullExcludedDirectory 'payload.bin'), [byte[]]::new(32))
    $nullExcludedUsage = Get-AgentEvalDirectoryUsage `
        -Path $nullExcludedDirectory `
        -ExcludedPaths @($null) `
        -MaxEntries 2 `
        -MaxBytes 1024 `
        -MaxFileBytes 1024
    Assert-True ($nullExcludedUsage.bytes -eq 32) 'Null excluded path skipped ordinary usage measurement.'

    $fixtureRepository = Join-Path $temporaryRoot 'fixture repository'
    $fixtureDirectory = Join-Path $fixtureRepository 'tests/Filtrace.Core.Tests/Fixtures'
    [System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
    [string] $untrackedFixture = Join-Path $fixtureDirectory 'untracked.nettrace'
    [System.IO.File]::WriteAllText($untrackedFixture, 'untracked')
    [string] $gitPath = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
    & $gitPath -C $fixtureRepository init --quiet
    Assert-True ($LASTEXITCODE -eq 0) 'Temporary fixture repository was not initialized.'
    $nativeErrorPreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    [bool] $nativeErrorPreferenceWasDefined = $null -ne $nativeErrorPreference
    [object] $nativeErrorPreferenceValue = if ($nativeErrorPreferenceWasDefined) {
        $nativeErrorPreference.Value
    }
    else {
        $null
    }
    [bool] $untrackedFixtureRejected = $false
    try {
        $script:PSNativeCommandUseErrorActionPreference = $true
        try { [void](Assert-AgentEvalTrackedFixture -Root $fixtureRepository -FixturePath $untrackedFixture) }
        catch {
            $untrackedFixtureRejected = $_.Exception.Message.Contains(
                'is not a tracked repository file', [StringComparison]::Ordinal)
        }
    }
    finally {
        if ($nativeErrorPreferenceWasDefined) {
            $script:PSNativeCommandUseErrorActionPreference = $nativeErrorPreferenceValue
        }
        else {
            Remove-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Script -ErrorAction SilentlyContinue
        }
    }
    Assert-True $untrackedFixtureRejected `
        'Native-error preference bypassed the tracked-fixture diagnostic.'

    $copySource = Join-Path $temporaryRoot 'copy source.bin'
    $copyDestinationRoot = Join-Path $temporaryRoot 'copy destination'
    [System.IO.Directory]::CreateDirectory($copyDestinationRoot) | Out-Null
    [System.IO.File]::WriteAllText($copySource, 'source')
    [System.IO.File]::WriteAllText((Join-Path $copyDestinationRoot 'copy.bin'), 'existing')
    $copyFile = [pscustomobject]@{
        sourcePath = $copySource
        relativePath = 'copy.bin'
        bytes = (Get-Item -LiteralPath $copySource).Length
        sha256 = Get-AgentEvalFileHash $copySource
    }
    [bool] $copyDestinationRejected = $false
    try { [void](Copy-AgentEvalAttestedFile -File $copyFile -DestinationRoot $copyDestinationRoot) }
    catch { $copyDestinationRejected = $true }
    Assert-True $copyDestinationRejected 'Attested copy unexpectedly replaced an existing destination.'
    [System.IO.FileStream] $exclusiveSourceStream = $null
    try {
        $exclusiveSourceStream = [System.IO.FileStream]::new(
            $copySource,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    finally {
        if ($null -ne $exclusiveSourceStream) { $exclusiveSourceStream.Dispose() }
    }

    $boundedSource = Join-Path $temporaryRoot 'bounded source'
    [System.IO.Directory]::CreateDirectory($boundedSource) | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $boundedSource 'one.bin'), [byte[]]::new(768))
    [System.IO.File]::WriteAllBytes((Join-Path $boundedSource 'two.bin'), [byte[]]::new(768))
    [bool] $fileCountRejected = $false
    try {
        [void](Get-AgentEvalFileInventory -SourceDirectory $boundedSource -DestinationPrefix '' -MaxFiles 1 -MaxEntries 4 -MaxBytes 4096)
    }
    catch { $fileCountRejected = $_.Exception.Message.Contains('exceeds 1 files', [StringComparison]::Ordinal) }
    Assert-True $fileCountRejected 'Prelaunch input file-count bound was not enforced.'
    [bool] $byteCountRejected = $false
    try {
        [void](Get-AgentEvalFileInventory -SourceDirectory $boundedSource -DestinationPrefix '' -MaxFiles 4 -MaxEntries 4 -MaxBytes 1024)
    }
    catch { $byteCountRejected = $_.Exception.Message.Contains('exceeds 1024 bytes', [StringComparison]::Ordinal) }
    Assert-True $byteCountRejected 'Prelaunch input byte bound was not enforced.'

    $reparseTarget = Join-Path $temporaryRoot 'reparse target'
    $reparseRoot = Join-Path $temporaryRoot 'reparse source'
    [System.IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
    [System.IO.Directory]::CreateDirectory($reparseRoot) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $reparseTarget 'outside.txt'), 'outside')
    $linkType = if ([System.OperatingSystem]::IsWindows()) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path (Join-Path $reparseRoot 'linked') -Target $reparseTarget | Out-Null
    [bool] $reparseRejected = $false
    try {
        [void](Get-AgentEvalFileInventory -SourceDirectory $reparseRoot -DestinationPrefix '' -Recurse -MaxFiles 4 -MaxEntries 4 -MaxBytes 4096)
    }
    catch { $reparseRejected = $_.Exception.Message.Contains('reparse point', [StringComparison]::Ordinal) }
    Assert-True $reparseRejected 'Prelaunch reparse-point input was not rejected.'
    [bool] $reparseAncestorRejected = $false
    try { Assert-AgentEvalNoReparseAncestor -Path (Join-Path $reparseRoot 'linked/future/run') }
    catch { $reparseAncestorRejected = $_.Exception.Message.Contains('reparse point', [StringComparison]::Ordinal) }
    Assert-True $reparseAncestorRejected 'Strict workspace ancestry accepted a pre-existing reparse point.'
    [bool] $usageDescendantReparseRejected = $false
    try {
        [void](Get-AgentEvalDirectoryUsage `
                -Path $reparseRoot `
                -MaxEntries 4 `
                -MaxBytes 4096 `
                -MaxFileBytes 4096 `
                -Scope 'Copilot host runtime cache')
    }
    catch { $usageDescendantReparseRejected = $_.Exception.Message.Contains('reparse point', [StringComparison]::Ordinal) }
    Assert-True $usageDescendantReparseRejected 'Runtime usage accepted a descendant reparse point.'
    [bool] $usageRootReparseRejected = $false
    try {
        [void](Get-AgentEvalDirectoryUsage `
                -Path (Join-Path $reparseRoot 'linked') `
                -MaxEntries 4 `
                -MaxBytes 4096 `
                -MaxFileBytes 4096 `
                -Scope 'Copilot host runtime cache')
    }
    catch { $usageRootReparseRejected = $_.Exception.Message.Contains('reparse point', [StringComparison]::Ordinal) }
    Assert-True $usageRootReparseRejected 'Runtime usage accepted a root reparse point.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $temporaryRoot 'prelaunch-child-counter'))) `
        'Prelaunch input rejection unexpectedly reached a child-process counter.'

    $launchDirectory = Join-Path $temporaryRoot 'launch failure'
    $launchHome = Join-Path $launchDirectory 'home'
    [System.IO.Directory]::CreateDirectory($launchDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($launchHome) | Out-Null
    $invalidExecutable = Join-Path $launchDirectory 'not-an-application.exe'
    [System.IO.File]::WriteAllText($invalidExecutable, 'not an executable')
    $launchContext = [pscustomobject]@{
        workspace = $launchDirectory
        runDirectory = $launchDirectory
        home = $launchHome
        isolateHome = $true
        immutableFiles = @()
    }
    [bool] $launchFailurePreserved = $false
    try {
        [void](Invoke-BoundedCopilotProcess -FilePath $invalidExecutable -Arguments @('--version') -Context $launchContext -TimeoutSeconds 1)
    }
    catch {
        $launchFailurePreserved = $_.Exception.Message.Contains('failed after 0 captured bytes', [StringComparison]::Ordinal) -and
            -not $_.Exception.ToString().Contains('No process is associated', [StringComparison]::Ordinal)
    }
    Assert-True $launchFailurePreserved 'Native process acquisition failure was masked by unstarted-process cleanup.'

    [bool] $missingExpectedModelRejected = $false
    try {
        & $runner `
            -AgentHost copilot `
            -Arm cli `
            -Model expected-model `
            -Tasks gc-report `
            -N 1 `
            -OutDir (Join-Path $temporaryRoot 'missing expected model') `
            -CopilotPath $pwshPath `
            -CopilotAdapterPath $fakeHost
    }
    catch {
        $missingExpectedModelRejected = $_.Exception.Message.Contains('-ExpectedModel is required', [StringComparison]::Ordinal)
    }
    Assert-True $missingExpectedModelRejected 'Strict CLI arm accepted a run without -ExpectedModel.'

    [bool] $unsupportedDefaultTasksRejected = $false
    try {
        & $runner `
            -AgentHost copilot `
            -Arm cli `
            -Model expected-model `
            -ExpectedModel expected-model `
            -N 1 `
            -OutDir (Join-Path $temporaryRoot 'unsupported default tasks') `
            -CopilotPath $pwshPath `
            -CopilotAdapterPath $fakeHost
    }
    catch {
        $unsupportedDefaultTasksRejected = $_.Exception.Message.Contains(
            'cannot run the selected task set', [StringComparison]::Ordinal)
    }
    Assert-True $unsupportedDefaultTasksRejected `
        'Strict CLI arm did not reject unsupported default tasks before host launch.'

    [bool] $manifestTaskRejected = $false
    try {
        & $runner `
            -AgentHost copilot `
            -Arm cli `
            -Model expected-model `
            -ExpectedModel expected-model `
            -Tasks manifest-batch `
            -N 1 `
            -OutDir (Join-Path $temporaryRoot 'manifest task') `
            -CopilotPath $pwshPath `
            -CopilotAdapterPath $fakeHost
    }
    catch {
        $manifestTaskRejected = $_.Exception.Message.Contains(
            'manifest-backed', [StringComparison]::Ordinal)
    }
    Assert-True $manifestTaskRejected 'Strict CLI arm accepted a manifest without its dependency closure.'

    [bool] $strictRunCapRejected = $false
    try {
        & $runner `
            -AgentHost copilot `
            -Arm cli `
            -Model expected-model `
            -ExpectedModel expected-model `
            -Tasks gc-report `
            -N 65 `
            -OutDir (Join-Path $temporaryRoot 'strict run cap') `
            -CopilotPath $pwshPath `
            -CopilotAdapterPath $fakeHost
    }
    catch {
        $strictRunCapRejected = $_.Exception.Message.Contains(
            'limited to 64 total task iterations', [StringComparison]::Ordinal)
    }
    Assert-True $strictRunCapRejected 'Strict CLI arm accepted more than 64 total task iterations.'

    [bool] $strictRunBytesRejected = $false
    try {
        & $runner `
            -AgentHost copilot `
            -Arm cli `
            -Model expected-model `
            -ExpectedModel expected-model `
            -Tasks gc-report `
            -N 8 `
            -OutDir (Join-Path $temporaryRoot 'strict run bytes') `
            -CopilotPath $pwshPath `
            -CopilotAdapterPath $fakeHost
    }
    catch {
        $strictRunBytesRejected = $_.Exception.Message.Contains(
            'exceeding the run-wide 2147483648-byte limit', [StringComparison]::Ordinal)
    }
    Assert-True $strictRunBytesRejected 'Strict CLI arm accepted an over-budget retained-byte projection.'

    $comparisonDirectory = Join-Path $temporaryRoot 'comparison'
    [void](Invoke-FakeRun -Name 'comparison baseline' -Mode success -Label baseline -OutDir $comparisonDirectory)
    [void](Invoke-FakeRun -Name 'comparison candidate' -Mode success -Label candidate -OutDir $comparisonDirectory)
    [System.IO.File]::WriteAllText((Join-Path $comparisonDirectory 'unrelated.json'), '{')
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $comparisonDirectory
    Assert-True ($LASTEXITCODE -eq 0) 'Identical fake records did not compare neutral.'
    & $pwshPath -NoProfile -File $compareRunner `
        -Baseline baseline `
        -Candidate candidate `
        -ResultsDir $comparisonDirectory `
        -TokenGrowthTolerance ([double]::NaN)
    Assert-True ($LASTEXITCODE -eq 1) 'A NaN token-growth tolerance was accepted.'
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate baseline -ResultsDir $comparisonDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'A comparison using the same label for both sides was accepted.'

    $zeroTokenDirectory = Join-Path $temporaryRoot 'zero-token baseline comparison'
    [System.IO.Directory]::CreateDirectory($zeroTokenDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $zeroToken = Read-TestResult $resultPath.FullName
        if ($zeroToken.label -ceq 'baseline') {
            foreach ($iteration in @($zeroToken.iterations)) { $iteration.tokens = 0 }
            $zeroToken.summary[0].MedTokens = 0
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $zeroTokenDirectory $resultPath.Name),
            (($zeroToken | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $zeroTokenDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'Positive candidate tokens compared neutral against a zero-token baseline.'

    $filenameLabelDirectory = Join-Path $temporaryRoot 'filename label comparison'
    [System.IO.Directory]::CreateDirectory($filenameLabelDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        Copy-Item -LiteralPath $resultPath.FullName -Destination $filenameLabelDirectory
    }
    $candidatePath = Get-ChildItem -LiteralPath $filenameLabelDirectory -Filter '*-candidate-*.json' | Select-Object -First 1
    $mislabeled = Read-TestResult $candidatePath.FullName
    $mislabeled.timestamp = '9999-12-31T23:59:59.0000000+00:00'
    [System.IO.File]::WriteAllText(
        (Join-Path $filenameLabelDirectory 'copilot-expected-model-baseline-99991231-235959-999.json'),
        (($mislabeled | ConvertTo-Json -Depth 20) + "`n"),
        [System.Text.UTF8Encoding]::new($false))
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $filenameLabelDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'A filename label that disagreed with its payload label was accepted.'

    $defaultMcpDirectory = Join-Path $temporaryRoot 'default mcp comparison'
    [System.IO.Directory]::CreateDirectory($defaultMcpDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $defaultMcp = Read-TestResult $resultPath.FullName
        $defaultMcp.arm = 'mcp'
        $defaultMcp.model.requested = $null
        $defaultMcp.model.expected = $null
        $defaultMcp.strictRunBudget = $null
        [System.IO.File]::WriteAllText(
            (Join-Path $defaultMcpDirectory $resultPath.Name),
            (($defaultMcp | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $defaultMcpDirectory
    Assert-True ($LASTEXITCODE -eq 0) 'Verified default-model Copilot MCP records did not compare neutral.'

    $mediatedV3Directory = Join-Path $temporaryRoot 'mediated schema-v3 comparison'
    [System.IO.Directory]::CreateDirectory($mediatedV3Directory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $mediatedV3 = Read-TestResult $resultPath.FullName
        $mediatedV3.host = 'ollama'
        $mediatedV3.arm = 'cli'
        $mediatedV3.model.expected = $null
        $mediatedV3.strictRunBudget = $null
        $mediatedV3.mcpDll = $null
        $mediatedV3.transport = @()
        foreach ($iteration in @($mediatedV3.iterations)) {
            $iteration.hostUsage = $null
            $iteration.hostUsageFile = $null
            $iteration.execution = $null
            $iteration.skill = $null
            $iteration.transcript = @($iteration.transcript | ForEach-Object {
                    [pscustomobject]@{
                        kind = $_.kind
                        operation = $_.operation
                        cmd = $_.cmd
                        ok = $_.ok
                        textTokens = $_.textTokens
                        info = $_.info
                    }
                })
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $mediatedV3Directory $resultPath.Name),
            (($mediatedV3 | ConvertTo-Json -Depth 32) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $mediatedV3Directory
    Assert-True ($LASTEXITCODE -eq 0) 'Validated mediated-host schema-v3 records did not compare neutral.'

    $legacyV3Directory = Join-Path $temporaryRoot 'legacy schema-v3 comparison'
    [System.IO.Directory]::CreateDirectory($legacyV3Directory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $legacyV3 = Read-TestResult $resultPath.FullName
        $legacyV3.summary[0].PSObject.Properties.Remove('SuccessCount')
        $legacyV3.PSObject.Properties.Remove('configuration')
        $legacyV3.PSObject.Properties.Remove('authorizationSha256')
        foreach ($iteration in @($legacyV3.iterations)) {
            $iteration.execution.PSObject.Properties.Remove('nativeTimeoutSeconds')
            $iteration.execution.PSObject.Properties.Remove('hostOutputMaxBytes')
            $iteration.execution.isolation.PSObject.Properties.Remove('maxAiCredits')
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $legacyV3Directory $resultPath.Name),
            (($legacyV3 | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $legacyV3Directory
    Assert-True ($LASTEXITCODE -eq 0) 'Older schema-v3 records without persisted SuccessCount did not compare neutral.'

    $caseDistinctDirectory = Join-Path $temporaryRoot 'case-distinct model comparison'
    [System.IO.Directory]::CreateDirectory($caseDistinctDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $caseDistinct = Read-TestResult $resultPath.FullName
        if ($caseDistinct.label -ceq 'candidate') {
            $caseDistinct.model.requested = 'Expected-Model'
            $caseDistinct.model.expected = 'Expected-Model'
            $caseDistinct.model.observed = 'Expected-Model'
            $caseDistinct.model.observedDistinct = @('Expected-Model')
            foreach ($iteration in @($caseDistinct.iterations)) {
                $iteration.observedModel = 'Expected-Model'
                $iteration.observedModels = @('Expected-Model')
            }
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $caseDistinctDirectory $resultPath.Name),
            (($caseDistinct | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $caseDistinctDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'Case-distinct model identities were paired as one run.'

    $maxStepsDirectory = Join-Path $temporaryRoot 'max-steps comparison'
    [System.IO.Directory]::CreateDirectory($maxStepsDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $maxStepsRecord = Read-TestResult $resultPath.FullName
        if ($maxStepsRecord.label -ceq 'candidate') { $maxStepsRecord.maxSteps = 64 }
        [System.IO.File]::WriteAllText(
            (Join-Path $maxStepsDirectory $resultPath.Name),
            (($maxStepsRecord | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $maxStepsDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'Runs with different maxSteps budgets were paired.'

    $configurationDirectory = Join-Path $temporaryRoot 'configuration comparison'
    [System.IO.Directory]::CreateDirectory($configurationDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $configurationRecord = Read-TestResult $resultPath.FullName
        if ($configurationRecord.label -ceq 'candidate') { $configurationRecord.configuration = 'Debug' }
        [System.IO.File]::WriteAllText(
            (Join-Path $configurationDirectory $resultPath.Name),
            (($configurationRecord | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $configurationDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'Runs with different build configurations were paired.'

    $authorizationDirectory = Join-Path $temporaryRoot 'authorization comparison'
    [System.IO.Directory]::CreateDirectory($authorizationDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $authorizationRecord = Read-TestResult $resultPath.FullName
        $authorizationRecord.authorizationSha256 = if ($authorizationRecord.label -ceq 'baseline') { '0' * 64 } else { '1' * 64 }
        [System.IO.File]::WriteAllText(
            (Join-Path $authorizationDirectory $resultPath.Name),
            (($authorizationRecord | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $authorizationDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'Runs with different authorization hashes were paired.'

    $authorizationPresenceDirectory = Join-Path $temporaryRoot 'authorization presence comparison'
    [System.IO.Directory]::CreateDirectory($authorizationPresenceDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $authorizationPresenceRecord = Read-TestResult $resultPath.FullName
        if ($authorizationPresenceRecord.label -ceq 'baseline') {
            $authorizationPresenceRecord.PSObject.Properties.Remove('authorizationSha256')
        }
        else {
            $authorizationPresenceRecord.authorizationSha256 = '0' * 64
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $authorizationPresenceDirectory $resultPath.Name),
            (($authorizationPresenceRecord | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $authorizationPresenceDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'A hash-bound run was paired with a run lacking authorization evidence.'

    $inputIdentityDirectory = Join-Path $temporaryRoot 'input identity comparison'
    [System.IO.Directory]::CreateDirectory($inputIdentityDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $inputIdentityRecord = Read-TestResult $resultPath.FullName
        if ($inputIdentityRecord.label -ceq 'candidate') {
            $inputIdentityRecord.inputIdentity[0].inputClosureSha256 = '0' * 64
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $inputIdentityDirectory $resultPath.Name),
            (($inputIdentityRecord | ConvertTo-Json -Depth 32) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $inputIdentityDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'Runs with different task input closures were paired.'

    $roundedSuccessDirectory = Join-Path $temporaryRoot 'rounded success comparison'
    [System.IO.Directory]::CreateDirectory($roundedSuccessDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $roundedSuccess = Read-TestResult $resultPath.FullName
        $seed = $roundedSuccess.iterations[0]
        $roundedSuccess.n = 1000
        $roundedSuccess.iterations = @(for ($iteration = 1; $iteration -le 1000; $iteration++) {
                [pscustomobject]@{
                    task = $seed.task
                    iteration = $iteration
                    success = -not ($roundedSuccess.label -ceq 'candidate' -and $iteration -eq 1000)
                    calls = $seed.calls
                    helpCalls = $seed.helpCalls
                    tokens = $seed.tokens
                    wallMs = $seed.wallMs
                    observedModel = $seed.observedModel
                    observedModels = $seed.observedModels
                }
            })
        $roundedSuccess.summary[0].SuccessCount = if ($roundedSuccess.label -ceq 'candidate') { 999 } else { 1000 }
        $roundedSuccess.summary[0].'Success%' = 100
        [System.IO.File]::WriteAllText(
            (Join-Path $roundedSuccessDirectory $resultPath.Name),
            (($roundedSuccess | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $roundedSuccessDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'A one-in-1000 exact success regression was hidden by rounded percentages.'

    $legacyDirectory = Join-Path $temporaryRoot 'legacy comparison'
    [System.IO.Directory]::CreateDirectory($legacyDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $legacy = Read-TestResult $resultPath.FullName
        $legacy.schemaVersion = 2
        $legacy.arm = 'mcp'
        $legacy.model = 'expected-model'
        $legacy.PSObject.Properties.Remove('configuration')
        $legacy.PSObject.Properties.Remove('n')
        $legacy.PSObject.Properties.Remove('maxSteps')
        $legacy.PSObject.Properties.Remove('authorizationSha256')
        $legacy.PSObject.Properties.Remove('inputIdentity')
        $legacy.PSObject.Properties.Remove('iterations')
        [System.IO.File]::WriteAllText(
            (Join-Path $legacyDirectory $resultPath.Name),
            (($legacy | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $legacyDirectory
    Assert-True ($LASTEXITCODE -eq 0) 'Validated schema-v2 records did not compare neutral.'

        foreach ($case in @(
            'malformed', 'duplicate-root-member', 'oversized-result', 'invalid-timestamp',
            'empty-summary', 'duplicate-task', 'wrong-field-type', 'unknown-root-member',
            'missing-token-accounting', 'missing-mcp-dll', 'missing-strict-run-budget',
            'wrong-token-accounting-type', 'wrong-mcp-dll-type', 'scalar-warnings',
            'summary-success-mismatch', 'summary-median-mismatch', 'med-help-member-case', 'unknown-summary-member',
            'wrong-transport-type', 'negative-transport', 'unknown-transport-task',
            'missing-strict-expected-model', 'unknown-model-member',
            'wrong-success-count', 'wrapped-schema-version', 'oversized-n', 'oversized-max-steps',
            'scalar-summary', 'scalar-iterations', 'scalar-observed-distinct',
            'scalar-observed-models', 'missing-input-identity', 'scalar-input-identity',
            'malformed-input-hash', 'iteration-fixture-mismatch', 'unknown-iteration-member',
            'unknown-execution-member', 'unknown-skill-member', 'strict-schema-v2')) {
        $caseDirectory = Join-Path $temporaryRoot "comparison $case"
        [System.IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
        foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
            Copy-Item -LiteralPath $resultPath.FullName -Destination $caseDirectory
        }
        $casePath = Get-ChildItem -LiteralPath $caseDirectory -Filter '*-baseline-*.json' | Select-Object -First 1
        if ($case -eq 'malformed') {
            [System.IO.File]::WriteAllText($casePath.FullName, '{')
        }
        elseif ($case -eq 'duplicate-root-member') {
            [string] $duplicateJson = [System.IO.File]::ReadAllText($casePath.FullName)
            [string] $duplicateNeedle = '"schemaVersion": 3,'
            Assert-True $duplicateJson.Contains($duplicateNeedle, [StringComparison]::Ordinal) `
                'Duplicate-member mutation did not find schemaVersion.'
            $duplicateJson = $duplicateJson.Replace(
                $duplicateNeedle,
                '"schemaVersion": 3,"schemaVersion": 3,')
            [System.IO.File]::WriteAllText(
                $casePath.FullName,
                $duplicateJson,
                [System.Text.UTF8Encoding]::new($false))
        }
        elseif ($case -eq 'oversized-result') {
            [System.IO.FileStream] $oversizedResultStream = [System.IO.File]::OpenWrite($casePath.FullName)
            try { $oversizedResultStream.SetLength(256MB + 1) }
            finally { $oversizedResultStream.Dispose() }
        }
        else {
            $invalid = Read-TestResult $casePath.FullName
            switch ($case) {
                'invalid-timestamp' { $invalid.timestamp = '09/14/2026 03:18:11' }
                'empty-summary' { $invalid.summary = @() }
                'duplicate-task' { $invalid.summary = @($invalid.summary[0], $invalid.summary[0]) }
                'wrong-field-type' { $invalid.summary[0].MedCalls = 'one' }
                'unknown-root-member' {
                    $invalid | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
                }
                'missing-token-accounting' { $invalid.PSObject.Properties.Remove('tokenAccounting') }
                'missing-mcp-dll' { $invalid.PSObject.Properties.Remove('mcpDll') }
                'missing-strict-run-budget' { $invalid.PSObject.Properties.Remove('strictRunBudget') }
                'wrong-token-accounting-type' { $invalid.tokenAccounting = @{} }
                'wrong-mcp-dll-type' { $invalid.mcpDll = @{} }
                'scalar-warnings' { $invalid.warnings = 'warning' }
                'summary-success-mismatch' { $invalid.summary[0].'Success%' = 0 }
                'summary-median-mismatch' { $invalid.summary[0].MedCalls = [int]$invalid.summary[0].MedCalls + 1 }
                'med-help-member-case' {
                    $medHelpCalls = $invalid.summary[0].MedHelpCalls
                    $invalid.summary[0].PSObject.Properties.Remove('MedHelpCalls')
                    $invalid.summary[0] | Add-Member -NotePropertyName medhelpcalls -NotePropertyValue $medHelpCalls
                }
                'unknown-summary-member' {
                    $invalid.summary[0] | Add-Member -NotePropertyName UnexpectedMetric -NotePropertyValue 1
                }
                'wrong-transport-type' { $invalid.transport[0].MedText = 'one' }
                'negative-transport' { $invalid.transport[0].MedWire = -1 }
                'unknown-transport-task' { $invalid.transport[0].Task = 'other-task' }
                'missing-strict-expected-model' { $invalid.model.expected = $null }
                'unknown-model-member' {
                    $invalid.model | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
                }
                'wrong-success-count' { $invalid.summary[0].SuccessCount = 0 }
                'wrapped-schema-version' { $invalid.schemaVersion = 4294967299L }
                'oversized-n' { $invalid.n = 1001 }
                'oversized-max-steps' { $invalid.maxSteps = 65 }
                'scalar-summary' { $invalid.summary = $invalid.summary[0] }
                'scalar-iterations' { $invalid.iterations = $invalid.iterations[0] }
                'scalar-observed-distinct' { $invalid.model.observedDistinct = $invalid.model.observed }
                'scalar-observed-models' { $invalid.iterations[0].observedModels = $invalid.iterations[0].observedModel }
                'missing-input-identity' { $invalid.PSObject.Properties.Remove('inputIdentity') }
                'scalar-input-identity' { $invalid.inputIdentity = $invalid.inputIdentity[0] }
                'malformed-input-hash' { $invalid.inputIdentity[0].taskSha256 = 'A' * 64 }
                'iteration-fixture-mismatch' { $invalid.iterations[0].execution.fixture.sha256 = '0' * 64 }
                'unknown-iteration-member' {
                    $invalid.iterations[0] | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
                }
                'unknown-execution-member' {
                    $invalid.iterations[0].execution | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
                }
                'unknown-skill-member' {
                    $invalid.iterations[0].skill | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
                }
                'strict-schema-v2' {
                    $invalid.schemaVersion = 2
                    $invalid.model = 'expected-model'
                    $invalid.PSObject.Properties.Remove('n')
                    $invalid.PSObject.Properties.Remove('maxSteps')
                    $invalid.PSObject.Properties.Remove('inputIdentity')
                    $invalid.PSObject.Properties.Remove('iterations')
                }
            }
            [System.IO.File]::WriteAllText(
                $casePath.FullName,
                (($invalid | ConvertTo-Json -Depth 20) + "`n"),
                [System.Text.UTF8Encoding]::new($false))
        }
        & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $caseDirectory
        Assert-True ($LASTEXITCODE -eq 1) "Invalid comparison case '$case' was accepted."
    }

    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json') {
        if ($resultPath.Name -eq 'unrelated.json') { continue }
        $unverified = Read-TestResult $resultPath.FullName
        $unverified.model.observed = $null
        $unverified.model.observedDistinct = @()
        $unverified.model.verified = $false
        [System.IO.File]::WriteAllText(
            $resultPath.FullName,
            (($unverified | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $comparisonDirectory
    Assert-True ($LASTEXITCODE -eq 1) 'Unverified schema-v3 A/A records compared neutral.'

    Write-Host 'Agent eval fake-host contract passed.' -ForegroundColor Green
    exit 0
}
finally {
    foreach ($policyLedger in $policyLedgers) { $policyLedger.Dispose() }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}