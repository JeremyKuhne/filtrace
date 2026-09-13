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
. $helpers

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function New-TestExecutionPolicy([string] $Name, [int] $MaxCalls, [switch] $WithSkill) {
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
        skillInventory = if ($skillPath) { @([pscustomobject]@{ path = 'SKILL.md' }) } else { @() }
        immutableFiles = [System.Collections.Generic.List[object]]::new()
    }
    $task = Get-Content -LiteralPath (Join-Path $root 'eval/tasks/04-gc-report.json') -Raw | ConvertFrom-Json
    $task | Add-Member -NotePropertyName maxCalls -NotePropertyValue $null
    $executionPolicy = Initialize-CopilotEvalExecutionPolicy `
        -Context $context `
        -Task $task `
        -AllowedVerbs @('report') `
        -MaxCalls $MaxCalls `
        -HookSourcePath $policyHook
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
        [int] $TimeoutSeconds = 30,
        [int] $MaxOutputBytes = 10485760,
        [int] $MaxArtifactBytes = 16777216
    )

    [bool] $threw = $false
    try {
        [void](Invoke-FakeRun `
                -Name $Name `
                -Mode $Mode `
                -TimeoutSeconds $TimeoutSeconds `
                -MaxOutputBytes $MaxOutputBytes `
                -MaxArtifactBytes $MaxArtifactBytes)
    }
    catch {
        $threw = $_.Exception.Message.Contains($ExpectedMessage, [StringComparison]::Ordinal)
    }
    Assert-True $threw "Fake case '$Name' did not fail with '$ExpectedMessage'."
}

function Assert-FakeParserFailureArtifacts {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Mode,
        [Parameter(Mandatory)][string] $ExpectedMessage,
        [Parameter(Mandatory)][string] $ExpectedRawText
    )

    [string] $outDirectory = Join-Path $temporaryRoot $Name
    [bool] $threw = $false
    try {
        [void](Invoke-FakeRun -Name $Name -Mode $Mode -OutDir $outDirectory)
    }
    catch {
        $threw = $_.Exception.Message.Contains($ExpectedMessage, [StringComparison]::Ordinal)
    }
    Assert-True $threw "Fake parser case '$Name' did not fail with '$ExpectedMessage'."
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

try {
    [System.Management.Automation.Language.Token[]] $runnerTokens = $null
    [System.Management.Automation.Language.ParseError[]] $runnerErrors = $null
    $runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $runner, [ref]$runnerTokens, [ref]$runnerErrors)
    Assert-True ($runnerErrors.Count -eq 0) 'Invoke-AgentEval.ps1 did not parse for isolated function tests.'
    foreach ($functionName in @(
            'ConvertFrom-AgentEvalJsonLines', 'Write-AgentEvalNewFile', 'Save-AgentEvalHostOutput',
            'ConvertTo-AgentEvalResultJson')) {
        $functionAst = $runnerAst.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
            }, $true)
        Assert-True ($null -ne $functionAst) "Could not extract '$functionName' from Invoke-AgentEval.ps1."
        Invoke-Expression $functionAst.Extent.Text
    }

    [string[]] $recordedEventTypes = @(
        'assistant.idle', 'assistant.message', 'assistant.message_delta', 'assistant.message_start',
        'assistant.reasoning',
        'assistant.turn_end', 'assistant.turn_start', 'hook.end', 'hook.start', 'model.call_finished',
        'model.call_start', 'model.captured_assignment_context', 'model.message', 'model.messages_snapshot',
        'model.model_call_started', 'model.model_call_success', 'model.response', 'model.turn_ended',
        'model.turn_started', 'result', 'session.info', 'session.managed_settings_resolved',
        'session.mcp_servers_loaded', 'session.skills_loaded', 'session.shutdown',
        'session.start', 'session.tools_updated',
        'session.usage_checkpoint', 'system.message', 'tool.execution_complete',
        'tool.execution_start', 'user.message')
    [string[]] $recordedLines = @($recordedEventTypes | ForEach-Object {
            if ($_ -eq 'result') {
                [ordered]@{ type = $_; timestamp = 1L; sessionId = 'recorded'; exitCode = 0; usage = [ordered]@{} }
            }
            else {
                [ordered]@{
                    type = $_; data = [ordered]@{}; ephemeral = $true
                    id = 'recorded'; timestamp = 1L; parentId = $null
                }
            }
        } | ForEach-Object { $_ | ConvertTo-Json -Depth 4 -Compress })
    $recordedEvents = @(ConvertFrom-AgentEvalJsonLines $recordedLines)
    Assert-True ($recordedEvents.Count -eq $recordedEventTypes.Count) `
        'The isolated JSONL parser did not accept every recorded event shape.'
    [bool] $unknownRecordedEventRejected = $false
    try { [void](ConvertFrom-AgentEvalJsonLines @('{"type":"future.event","data":{}}')) }
    catch { $unknownRecordedEventRejected = $_.Exception.Message.Contains('unknown event type', [StringComparison]::Ordinal) }
    Assert-True $unknownRecordedEventRejected 'The isolated JSONL parser accepted an unrecorded event type.'

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

    $success = Invoke-FakeRun -Name 'cli success' -Mode success
    Assert-True ($success.arm -eq 'cli') "Expected cli arm, got '$($success.arm)'."
    Assert-True ($success.model.observed -eq 'expected-model') 'The observed model was not retained.'
    Assert-True ($success.model.verified -eq $true) 'The observed model was not verified.'
    Assert-True ($success.iterations[0].success -eq $true) "Grounded CLI iteration failed: $($success.iterations[0].note)"
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
    Assert-True (@($success.model.observedDistinct) -notcontains 'gpt-5.6-sol') `
        'Provider-level model metadata was used as strict host identity.'
    Assert-True ($success.iterations[0].hostUsageFile.available -eq $true -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.input.tokenCount -eq 20 -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.cache_read.tokenCount -eq 5 -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.cache_write.tokenCount -eq 15 -and
        $success.iterations[0].hostUsageFile.value.tokenDetails.output.tokenCount -eq 10) `
        'Detailed host token accounting was not retained from the usage output file.'

    $missingUsage = Invoke-FakeRun -Name 'cli missing usage output' -Mode missing-usage-output
    Assert-True ($missingUsage.iterations[0].success -eq $true -and
        $missingUsage.iterations[0].hostUsageFile.available -eq $false -and
        -not [string]::IsNullOrWhiteSpace([string]$missingUsage.iterations[0].hostUsageFile.reason)) `
        'Missing host usage output was not retained explicitly as unavailable.'
    Assert-FakeRunThrows `
        -Name 'malformed usage output' `
        -Mode malformed-usage-output `
        -ExpectedMessage 'Copilot usage output was malformed.'

    $legacyToolArguments = Invoke-FakeRun -Name 'cli legacy tool arguments' -Mode legacy-tool-arguments
    Assert-True ($legacyToolArguments.iterations[0].success -eq $true) `
        'The established two-member PowerShell argument form no longer passed.'
    $helpSuccess = Invoke-FakeRun -Name 'cli help success' -Mode help-success
    Assert-True ($helpSuccess.iterations[0].success -eq $true -and
        $helpSuccess.iterations[0].calls -eq 1 -and
        $helpSuccess.iterations[0].helpCalls -eq 1) `
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
    $malformedInput = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -RawInput '{'
    Assert-True ($malformedInput.permissionDecision -eq 'deny') 'Malformed hook input was not denied.'
    $unknownTool = Invoke-TestPolicyHook `
        -Hook $policyProbe.preToolHook `
        -SessionId $policySessionId `
        -WorkingDirectory $policyWorkspace `
        -ToolName web_fetch `
        -ToolArguments ([ordered]@{ url = 'https://example.com' })
    Assert-True ($unknownTool.permissionDecision -eq 'deny') 'Unknown tool was not denied before execution.'
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
    $firstView = Invoke-TestPolicyHook `
        -Hook $viewProbe.preToolHook `
        -SessionId $viewProbe.context.runId `
        -WorkingDirectory $viewProbe.context.workspace `
        -ToolName view `
        -ToolArguments ([ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 2) })
    Assert-True ($firstView.permissionDecision -eq 'allow') 'First bounded SKILL.md view was denied.'
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
    [string] $otherViewPath = Join-Path (Split-Path -Parent $viewProbe.context.skillPath) 'reference.md'
    [System.IO.File]::WriteAllText($otherViewPath, 'not the selected skill entry point')
    $otherView = Invoke-TestPolicyHook `
        -Hook $viewProbe.preToolHook `
        -SessionId $viewProbe.context.runId `
        -WorkingDirectory $viewProbe.context.workspace `
        -ToolName view `
        -ToolArguments ([ordered]@{ path = $otherViewPath })
    Assert-True ($otherView.permissionDecision -eq 'deny') 'Non-SKILL.md view was not denied.'
    [object[]] $invalidViewArguments = @(
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = 1 }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @() }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 2, 3) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @('1', 2) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @($true, 2) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(0, 1) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 0) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(2, 1) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(7, 7) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 519) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, [long]::MaxValue) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, -2) }
        [ordered]@{ path = $viewProbe.context.skillPath; view_range = @(1, 2); extra = $true })
    $invalidViewProbe = New-TestExecutionPolicy -Name 'invalid view policy hook contract' -MaxCalls 1 -WithSkill
    foreach ($invalidViewArgument in $invalidViewArguments) {
        $invalidViewArgument.path = $invalidViewProbe.context.skillPath
        $invalidView = Invoke-TestPolicyHook `
            -Hook $invalidViewProbe.preToolHook `
            -SessionId $invalidViewProbe.context.runId `
            -WorkingDirectory $invalidViewProbe.context.workspace `
            -ToolName view `
            -ToolArguments $invalidViewArgument
        Assert-True ($invalidView.permissionDecision -eq 'deny') 'Malformed view_range was not denied.'
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
            'answer-only', 'missing-tool', 'failed-completion', 'wrong-cli-path', 'wrong-answer', 'host-failure',
            'decoy-command', 'missing-call-id', 'duplicate-call-id', 'missing-completion', 'unexpected-tool',
            'string-success', 'mismatched-operation', 'unknown-cli-schema', 'malformed-shell-wrapper',
            'nonzero-shell-wrapper', 'mismatched-shell-content', 'missing-command-argument',
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
    Assert-FakeRunThrows -Name 'mutated bundle' -Mode mutated-bundle -ExpectedMessage 'input attestation failed'
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
    $sourceSkillFiles = @(Get-ChildItem -LiteralPath (Join-Path $root '.agents/skills/filtrace') -File -Recurse)
    Assert-True `
        (@($skillSuccess.iterations[0].skill.inventory).Count -eq $sourceSkillFiles.Count) `
        'Installed skill inventory did not contain every shipped file.'
    foreach ($mode in @(
            'missing-skill-read', 'missing-skill-discovery', 'wrong-skill-hash',
            'failed-skill-load', 'missing-skill-context', 'skill-ledger-mismatch')) {
        $negative = Invoke-FakeRun -Name "skill $mode" -Mode $mode -Arm cli-skill
        Assert-True ($negative.iterations[0].success -eq $false) "Fake skill mode '$mode' unexpectedly passed."
        if ($mode -eq 'missing-skill-read') {
            Assert-True ($negative.iterations[0].skill.observed -eq $true -and
                @($negative.iterations[0].skill.evidenceCallIds).Count -eq 0) `
                'Discovery metadata was not kept distinct from actual skill invocation.'
        }
    }

    Assert-FakeRunThrows -Name 'timeout' -Mode timeout -ExpectedMessage 'did not finish within 1 seconds' -TimeoutSeconds 1
    Assert-FakeRunThrows -Name 'output bound' -Mode oversized-output -ExpectedMessage 'output exceeded 1024 bytes' -MaxOutputBytes 1024
    Assert-FakeRunThrows -Name 'artifact bound' -Mode oversized-artifact -ExpectedMessage 'artifacts exceeded 16777216 bytes'

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

    $comparisonDirectory = Join-Path $temporaryRoot 'comparison'
    [void](Invoke-FakeRun -Name 'comparison baseline' -Mode success -Label baseline -OutDir $comparisonDirectory)
    [void](Invoke-FakeRun -Name 'comparison candidate' -Mode success -Label candidate -OutDir $comparisonDirectory)
    [System.IO.File]::WriteAllText((Join-Path $comparisonDirectory 'unrelated.json'), '{')
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $comparisonDirectory
    Assert-True ($LASTEXITCODE -eq 0) 'Identical fake records did not compare neutral.'

    $legacyDirectory = Join-Path $temporaryRoot 'legacy comparison'
    [System.IO.Directory]::CreateDirectory($legacyDirectory) | Out-Null
    foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
        $legacy = Get-Content -LiteralPath $resultPath.FullName -Raw | ConvertFrom-Json
        $legacy.schemaVersion = 2
        $legacy.model = 'expected-model'
        $legacy.PSObject.Properties.Remove('n')
        $legacy.PSObject.Properties.Remove('maxSteps')
        $legacy.PSObject.Properties.Remove('iterations')
        [System.IO.File]::WriteAllText(
            (Join-Path $legacyDirectory $resultPath.Name),
            (($legacy | ConvertTo-Json -Depth 20) + "`n"),
            [System.Text.UTF8Encoding]::new($false))
    }
    & $pwshPath -NoProfile -File $compareRunner -Baseline baseline -Candidate candidate -ResultsDir $legacyDirectory
    Assert-True ($LASTEXITCODE -eq 0) 'Validated schema-v2 records did not compare neutral.'

    foreach ($case in @('malformed', 'empty-summary', 'duplicate-task', 'wrong-field-type')) {
        $caseDirectory = Join-Path $temporaryRoot "comparison $case"
        [System.IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
        foreach ($resultPath in Get-ChildItem -LiteralPath $comparisonDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'unrelated.json' }) {
            Copy-Item -LiteralPath $resultPath.FullName -Destination $caseDirectory
        }
        $casePath = Get-ChildItem -LiteralPath $caseDirectory -Filter '*-baseline-*.json' | Select-Object -First 1
        if ($case -eq 'malformed') {
            [System.IO.File]::WriteAllText($casePath.FullName, '{')
        }
        else {
            $invalid = Get-Content -LiteralPath $casePath.FullName -Raw | ConvertFrom-Json
            switch ($case) {
                'empty-summary' { $invalid.summary = @() }
                'duplicate-task' { $invalid.summary = @($invalid.summary[0], $invalid.summary[0]) }
                'wrong-field-type' { $invalid.summary[0].MedCalls = 'one' }
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
        $unverified = Get-Content -LiteralPath $resultPath.FullName -Raw | ConvertFrom-Json
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
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}