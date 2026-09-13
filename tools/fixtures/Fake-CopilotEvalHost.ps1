#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param(
    [Alias('C')]
    [string] $WorkingDirectory,
    [Alias('p')]
    [string] $Prompt,
    [Alias('output-format')]
    [string] $OutputFormat,
    [Alias('no-custom-instructions')]
    [switch] $NoCustomInstructions,
    [Alias('disable-builtin-mcps')]
    [switch] $DisableBuiltinMcps,
    [Alias('no-remote-export')]
    [switch] $NoRemoteExport,
    [Alias('no-remote')]
    [switch] $NoRemote,
    [Alias('no-auto-update')]
    [switch] $NoAutoUpdate,
    [Alias('no-bash-env')]
    [switch] $NoBashEnvironment,
    [Alias('no-ask-user')]
    [switch] $NoAskUser,
    [Alias('disallow-temp-dir')]
    [switch] $DisallowTempDirectory,
    [Alias('session-id')]
    [string] $SessionId,
    [Alias('usage-output-file')]
    [string] $UsageOutputFile,
    [Alias('log-dir')]
    [string] $LogDirectory,
    [Alias('available-tools')]
    [string] $AvailableTools,
    [Alias('excluded-tools')]
    [string] $ExcludedTools,
    [Alias('allow-tool')]
    [string[]] $AllowedTools,
    [Alias('deny-tool')]
    [string[]] $DeniedTools,
    [Alias('max-ai-credits')]
    [int] $MaxAiCredits,
    [string] $Model
)

if ($Prompt -notmatch 'trace at (.+?) and answer') {
    throw 'Fake Copilot host could not recover the fixture path from the prompt.'
}

$fixture = $Matches[1]
if ($Prompt -notmatch 'local filtrace CLI at (.+?), analyze') {
    throw 'Fake Copilot host could not recover the owned filtrace path from the prompt.'
}
$filtracePath = $Matches[1]
$appHostName = [System.IO.Path]::GetFileName($filtracePath)
$skillPath = if (-not $NoCustomInstructions) {
    Join-Path $WorkingDirectory '.agents/skills/filtrace/SKILL.md'
}
else { $null }
if ($skillPath -and ($Prompt.Contains($skillPath, [StringComparison]::OrdinalIgnoreCase) -or
        $Prompt.Contains('skill at ', [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Fake Copilot host received a prompt-directed skill path.'
}
$expectedTools = if ($skillPath) { 'powershell,skill' } else { 'powershell' }
$observedBuiltinTools = @(
    'powershell', 'read_powershell', 'stop_powershell', 'list_powershell', 'apply_patch', 'view',
    'web_fetch', 'fetch_copilot_cli_documentation', 'skill', 'sql', 'session_store_sql',
    'read_agent', 'list_agents', 'write_agent', 'rg', 'glob', 'task')
$expectedExcludedTools = @($observedBuiltinTools | Where-Object { $expectedTools.Split(',') -notcontains $_ }) -join ','
$deniedToolNames = @($DeniedTools | ForEach-Object { $_ -split ',' })
if ($AvailableTools -ne $expectedTools -or $ExcludedTools -ne $expectedExcludedTools -or
    $AllowedTools.Count -ne 0 -or $deniedToolNames -notcontains 'read' -or $deniedToolNames -notcontains 'shell' -or
    $deniedToolNames -notcontains 'write' -or $deniedToolNames -notcontains 'url' -or $MaxAiCredits -ne 30) {
    throw 'Fake Copilot host did not receive the strict owned-apphost tool policy.'
}
$hookConfigurationPath = Join-Path $env:COPILOT_HOME 'hooks/filtrace-eval-policy.json'
if (-not (Test-Path -LiteralPath $hookConfigurationPath -PathType Leaf)) {
    throw 'Fake Copilot host did not receive the isolated pre-execution hook configuration.'
}
$hookConfiguration = Get-Content -LiteralPath $hookConfigurationPath -Raw | ConvertFrom-Json
$preToolHook = @($hookConfiguration.hooks.preToolUse)
if (@($hookConfiguration.hooks.PSObject.Properties.Name).Count -ne 1 -or
    @($hookConfiguration.hooks.PSObject.Properties.Name)[0] -ne 'preToolUse' -or
    $preToolHook.Count -ne 1 -or
    $preToolHook[0].exec -ne (Get-Process -Id $PID).Path -or
    $preToolHook[0].args -contains '-Mode') {
    throw 'Fake Copilot host received a malformed pre-execution hook configuration.'
}

function Invoke-FakePolicyHook($Hook, [string] $ToolName, $ToolArguments) {
    $hookInput = [ordered]@{
        sessionId = $SessionId
        timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        cwd = $WorkingDirectory
        toolName = $ToolName
        toolArgs = $ToolArguments
    } | ConvertTo-Json -Depth 8 -Compress
    [string[]] $hookOutput = @($hookInput | & ([string]$Hook.exec) @($Hook.args))
    if ($LASTEXITCODE -ne 0 -or $hookOutput.Count -ne 1) {
        throw "Fake Copilot host policy hook failed for '$ToolName'."
    }
    return $hookOutput[0] | ConvertFrom-Json
}
$mode = if ($env:FILTRACE_AGENT_EVAL_FAKE_MODE) { $env:FILTRACE_AGENT_EVAL_FAKE_MODE } else { 'success' }
$requiredSwitches = @(
    $DisableBuiltinMcps, $NoRemoteExport, $NoRemote,
    $NoAutoUpdate, $NoBashEnvironment, $NoAskUser, $DisallowTempDirectory)
if ($requiredSwitches -contains $false -or [bool]$NoCustomInstructions -eq [bool]$skillPath -or
    $Model -ne 'expected-model' -or $OutputFormat -ne 'json' -or
    -not $SessionId -or -not $UsageOutputFile -or -not $LogDirectory) {
    throw 'Fake Copilot host did not receive the required isolation switches.'
}
if (-not $WorkingDirectory.Contains(' ', [StringComparison]::Ordinal)) {
    throw 'Fake Copilot host expected an isolated working directory containing a space.'
}
$expectedHome = Join-Path ([System.IO.Path]::GetDirectoryName($WorkingDirectory)) 'home'
if ($env:HOME -ne $expectedHome -or $env:USERPROFILE -ne $expectedHome -or
    $env:XDG_CONFIG_HOME -ne (Join-Path $expectedHome '.config') -or
    $env:APPDATA -ne (Join-Path $expectedHome 'AppData/Roaming') -or
    $env:LOCALAPPDATA -ne (Join-Path $expectedHome 'AppData/Local') -or
    $env:COPILOT_HOME -ne (Join-Path $expectedHome 'copilot')) {
    throw 'Fake Copilot host did not receive the isolated per-process home environment.'
}
foreach ($name in @('GITHUB_TOKEN', 'GH_TOKEN', 'COPILOT_ALLOW_ALL', 'COPILOT_MODEL', 'COPILOT_PROVIDER',
        'COPILOT_CUSTOM_INSTRUCTIONS_DIRS', 'OTEL_EXPORTER_OTLP_ENDPOINT', 'FILTRACE_AGENT_EVAL_SECRET')) {
    if (-not [string]::IsNullOrEmpty([System.Environment]::GetEnvironmentVariable($name))) {
        throw "Fake Copilot host inherited forbidden environment variable '$name'."
    }
}
for ($directory = Get-Item -LiteralPath $WorkingDirectory; $null -ne $directory; $directory = $directory.Parent) {
    if (Test-Path -LiteralPath (Join-Path $directory.FullName 'AGENTS.md')) {
        throw 'Fake Copilot host workspace can discover an ancestor AGENTS.md.'
    }
}
if ($mode -eq 'timeout') {
    [System.Threading.Thread]::Sleep(5000)
}
if ($mode -in @('closed-stream-timeout', 'closed-stream-artifact')) {
    Add-Type -Namespace AgentEval -Name NativeMethods -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern System.IntPtr GetStdHandle(int handle);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern bool CloseHandle(System.IntPtr handle);
'@
    [void][AgentEval.NativeMethods]::CloseHandle([AgentEval.NativeMethods]::GetStdHandle(-11))
    [void][AgentEval.NativeMethods]::CloseHandle([AgentEval.NativeMethods]::GetStdHandle(-12))
    if ($mode -eq 'closed-stream-artifact') {
        [System.IO.FileStream] $closedStreamArtifact =
            [System.IO.File]::Create((Join-Path $WorkingDirectory 'closed-stream-oversized.bin'))
        try { $closedStreamArtifact.SetLength(20MB) } finally { $closedStreamArtifact.Dispose() }
    }
    [System.Threading.Thread]::Sleep(10000)
    exit 0
}
if ($mode -eq 'oversized-output') {
    [Console]::Out.WriteLine(('x' * 4096))
}
if ($mode -eq 'oversized-artifact') {
    [string] $oversizedPath = Join-Path $WorkingDirectory 'oversized.bin'
    [System.IO.FileStream] $stream = [System.IO.File]::Create($oversizedPath)
    try { $stream.SetLength(20MB) } finally { $stream.Dispose() }
}
if ($mode -eq 'host-runtime-cache') {
    [string] $runtimeDirectory = Join-Path $env:LOCALAPPDATA 'copilot/pkg/win32-x64/test-runtime'
    [System.IO.Directory]::CreateDirectory($runtimeDirectory) | Out-Null
    [System.IO.FileStream] $stream = [System.IO.File]::Create((Join-Path $runtimeDirectory 'runtime.node'))
    try { $stream.SetLength(20MB) } finally { $stream.Dispose() }
}
if ($mode -eq 'host-runtime-outside-cache') {
    [string] $runtimeDirectory = Join-Path $env:LOCALAPPDATA 'copilot/not-pkg'
    [System.IO.Directory]::CreateDirectory($runtimeDirectory) | Out-Null
    [System.IO.FileStream] $stream = [System.IO.File]::Create((Join-Path $runtimeDirectory 'runtime.node'))
    try { $stream.SetLength(20MB) } finally { $stream.Dispose() }
}
if ($mode -eq 'mutated-bundle') {
    [System.IO.File]::AppendAllText((Join-Path (Split-Path -Parent $filtracePath) 'Filtrace.Core.dll'), 'changed')
}
if ($mode -eq 'oversized-immutable') {
    [string] $immutablePath = Join-Path (Split-Path -Parent $filtracePath) 'Filtrace.Core.dll'
    [System.IO.FileStream] $immutableStream = [System.IO.File]::OpenWrite($immutablePath)
    try { $immutableStream.SetLength($immutableStream.Length + 20MB) } finally { $immutableStream.Dispose() }
    [System.Threading.Thread]::Sleep(5000)
}
if ($mode -eq 'orphan-descendant') {
    [System.Diagnostics.ProcessStartInfo] $descendantStart = [System.Diagnostics.ProcessStartInfo]::new()
    $descendantStart.FileName = (Get-Process -Id $PID).Path
    $descendantStart.UseShellExecute = $false
    $descendantStart.RedirectStandardOutput = $true
    $descendantStart.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command',
            '[Threading.Thread]::Sleep(10000)')) {
        [void]$descendantStart.ArgumentList.Add($argument)
    }
    [System.Diagnostics.Process] $descendant = [System.Diagnostics.Process]::new()
    $descendant.StartInfo = $descendantStart
    if (-not $descendant.Start()) { throw 'Fake descendant did not start.' }
    [System.IO.File]::WriteAllText(
        (Join-Path $WorkingDirectory 'descendant.pid'),
        [string]$descendant.Id,
        [System.Text.UTF8Encoding]::new($false))
    $descendant.Dispose()
}
$reportedModel = if ($mode -eq 'wrong-model') {
    'other-model'
}
elseif ($mode -eq 'wrong-model-case') {
    'Expected-Model'
}
else {
    'expected-model'
}
$reportedFiltracePath = if ($mode -eq 'wrong-cli-path') { Join-Path $WorkingDirectory "other/$appHostName" } else { $filtracePath }
$completionSucceeded = $mode -ne 'failed-completion'
$answer = if ($mode -eq 'wrong-answer') { 'There were 6 garbage collections.' } else { 'There were 7 garbage collections.' }

$events = [System.Collections.Generic.List[object]]::new()
$reportedSkills = if ($skillPath -and $mode -ne 'missing-skill-discovery') {
    @([ordered]@{
            name = 'filtrace'
            commandName = 'filtrace'
            source = 'project'
            enabled = $true
            path = $skillPath
        })
}
else { @() }
$events.Add([ordered]@{
        type = 'session.skills_loaded'
        data = [ordered]@{ skills = $reportedSkills }
    })
$events.Add([ordered]@{
        type = 'model.messages_snapshot'
        data = [ordered]@{ messages = @() }
    })
$events.Add([ordered]@{
        type = 'model.model_call_started'
        data = [ordered]@{ model = 'gpt-5.6-sol'; provider = 'copilot' }
    })
if ($mode -ne 'missing-model') {
    $events.Add([ordered]@{
            type = 'session.tools_updated'
            data = [ordered]@{ model = $reportedModel }
        })
    if ($mode -in @('model-variant', 'model-case-variant')) {
        [string] $variantModel = if ($mode -eq 'model-case-variant') { 'Expected-Model' } else { 'expected-model-variant' }
        $events.Add([ordered]@{
                type = 'session.tools_updated'
                data = [ordered]@{ model = $variantModel }
            })
    }
}

if ($mode -in @('answer-before-analysis', 'answer-before-skill-context')) {
    $events.Add([ordered]@{
            type = 'assistant.message'
            data = [ordered]@{ content = $answer }
        })
}

if ($skillPath -and $mode -ne 'missing-skill-read') {
    [string] $skillText = [System.IO.File]::ReadAllText($skillPath).Replace("`r`n", "`n")
    [int] $frontmatterEnd = $skillText.IndexOf("`n---`n", 4, [StringComparison]::Ordinal)
    if ($frontmatterEnd -lt 0) { throw 'Fake skill frontmatter was malformed.' }
    [string] $sourceBody = $skillText.Substring($frontmatterEnd + 5)
    if (-not $sourceBody.StartsWith("`n", [StringComparison]::Ordinal)) {
        throw 'Fake skill frontmatter was not followed by a blank separator.'
    }
    [string] $skillBody = $sourceBody.Substring(1)
    [string] $skillDirectory = [System.IO.Path]::GetDirectoryName($skillPath)
    [string[]] $relatedPaths = @(Get-ChildItem -LiteralPath $skillDirectory -File -Recurse |
        Where-Object { -not [string]::Equals($_.FullName, $skillPath, [StringComparison]::Ordinal) } |
        Sort-Object { [System.IO.Path]::GetRelativePath($skillDirectory, $_.FullName) } |
        ForEach-Object { $_.FullName })
    [string] $relatedSection = if ($relatedPaths.Count -gt 0) {
        "`n`nRelated files (use view tool to read):`n" +
            (($relatedPaths | ForEach-Object { "  - $_" }) -join "`n")
    }
    else { '' }
    [string] $reportedSkillName = if ($mode -eq 'skill-ledger-mismatch') { 'other-skill' } else { 'filtrace' }
    [string] $contextBody = if ($mode -in @(
            'wrong-skill-hash', 'skill-missing-page', 'skill-hole', 'skill-reorder',
            'skill-overlap-conflict', 'skill-malformed-line-hint', 'skill-malformed-suffix',
            'skill-wrong-character', 'skill-forged-diff')) {
        '!' + $skillBody.Substring(1)
    }
    else { $skillBody }
    [string] $extraContext = if ($mode -eq 'skill-extra-context') { "`nInjected text outside the skill source." } else { '' }
    $events.Add([ordered]@{
            type = 'tool.execution_start'
            data = [ordered]@{
                toolCallId = 'skill-load'
                toolName = 'skill'
                arguments = [ordered]@{ skill = $reportedSkillName }
            }
        })
    $events.Add([ordered]@{
            type = 'tool.execution_complete'
            data = [ordered]@{
                toolCallId = 'skill-load'
                success = $mode -ne 'failed-skill-load'
                result = [ordered]@{
                    content = 'Skill "filtrace" loaded successfully. Follow the instructions in the skill context.'
                    detailedContent = "Skill loaded successfully ✅`n`n$skillBody"
                }
            }
        })
    if ($mode -ne 'missing-skill-context') {
        $events.Add([ordered]@{
                type = 'model.message'
                data = [ordered]@{
                    message = [ordered]@{
                        role = 'user'
                        content = "<skill-context name=`"filtrace`">`nBase directory for this skill: $skillDirectory$relatedSection$extraContext`n`n$contextBody`n</skill-context>"
                    }
                }
            })
    }
}

if ($mode -eq 'help-success') {
    [string] $helpCommand = "& '$filtracePath' '--help'"
    $helpArguments = [ordered]@{
        command = $helpCommand
        description = 'Show filtrace CLI help'
        mode = 'sync'
        initial_wait = 30
    }
    $helpDecision = Invoke-FakePolicyHook `
        -Hook $preToolHook[0] `
        -ToolName powershell `
        -ToolArguments $helpArguments
    if ($helpDecision.permissionDecision -ne 'allow') {
        throw 'Fake Copilot host policy denied bounded top-level help.'
    }
    $events.Add([ordered]@{
            type = 'tool.execution_start'
            data = [ordered]@{
                toolCallId = 'help-call'
                toolName = 'powershell'
                arguments = $helpArguments
            }
        })
    [string] $helpResult = "Usage: [command] [-h|--help]`n`nCommands:`n  report"
    $events.Add([ordered]@{
            type = 'tool.execution_complete'
            data = [ordered]@{
                toolCallId = 'help-call'
                success = $true
                result = [ordered]@{
                    content = "$helpResult`n<shellId: 0 completed with exit code 0>"
                    detailedContent = "$helpResult`n<shellId: 0 completed with exit code 0>"
                }
            }
        })
}

if ($mode -notin @('answer-only', 'missing-tool', 'no-hook-fallback')) {
    [string] $reportedOperation = if ($mode -eq 'mismatched-operation') { 'rank' } else { 'gc' }
    [string] $command = if ($mode -eq 'decoy-command') {
        "Write-Output '$reportedFiltracePath report $fixture --format json'"
    }
    else {
        [string[]] $commandParts = @($reportedFiltracePath, 'report', $fixture, '--kind', 'gc', '--format', 'json')
        '& ' + (($commandParts | ForEach-Object { "'$($_.Replace("'", "''"))'" }) -join ' ')
    }
    if ($mode -eq 'recorded-policy-denial') {
        $events.Add([ordered]@{
                type = 'tool.execution_start'
                data = [ordered]@{
                    toolCallId = 'denied-call'
                    toolName = 'powershell'
                    arguments = [ordered]@{
                        command = $command
                        description = 'Inspect trace format and analysis capabilities'
                        mode = 'sync'
                        initial_wait = 30
                    }
                }
            })
        $events.Add([ordered]@{
                type = 'tool.execution_complete'
                data = [ordered]@{
                    toolCallId = 'denied-call'
                    success = $false
                    error = [ordered]@{
                        message = 'Denied by preToolUse hook: Denied by the bounded filtrace evaluation policy.'
                        code = 'denied'
                    }
                }
            })
    }
    [string] $callId = if ($mode -eq 'missing-call-id') { '' } else { 'call-1' }
    [string] $toolName = if ($mode -eq 'unexpected-tool') { 'write' } else { 'powershell' }
    $toolArguments = [ordered]@{
        command = $command
        description = 'Analyze the owned trace with filtrace'
    }
    if ($mode -notin @('legacy-tool-arguments', 'recorded-policy-denial')) {
        $toolArguments.mode = 'sync'
        $toolArguments.initial_wait = 30
    }
    switch ($mode) {
        'missing-command-argument' { [void]$toolArguments.Remove('command') }
        'missing-description-argument' { [void]$toolArguments.Remove('description') }
        'invalid-command-type' { $toolArguments.command = @($command) }
        'invalid-mode-type' { $toolArguments.mode = $true }
        'async-mode' { $toolArguments.mode = 'async' }
        'repl-mode' { $toolArguments.mode = 'repl' }
        'invalid-initial-wait-type' { $toolArguments.initial_wait = '30' }
        'zero-initial-wait' { $toolArguments.initial_wait = 0 }
        'unbounded-initial-wait' { $toolArguments.initial_wait = 31 }
        'shell-sandbox-flag' { $toolArguments.sandbox = $true }
    }
    [string[]] $invalidToolArgumentModes = @(
        'missing-command-argument', 'missing-description-argument', 'invalid-command-type',
        'invalid-mode-type', 'async-mode', 'repl-mode', 'invalid-initial-wait-type',
        'zero-initial-wait', 'unbounded-initial-wait', 'shell-sandbox-flag')
    if ($mode -notin @('decoy-command', 'wrong-cli-path', 'unexpected-tool')) {
        $preToolDecision = Invoke-FakePolicyHook `
            -Hook $preToolHook[0] `
            -ToolName $toolName `
            -ToolArguments $toolArguments
        [string] $expectedDecision = if ($invalidToolArgumentModes -contains $mode) { 'deny' } else { 'allow' }
        if ($preToolDecision.permissionDecision -ne $expectedDecision) {
            throw "Fake Copilot host policy returned '$($preToolDecision.permissionDecision)' for '$mode'; expected '$expectedDecision'."
        }
    }
    if ($mode -eq 'allowed-no-execution') {
        $toolName = $null
    }
    if ($null -ne $toolName) {
    $events.Add([ordered]@{
            type = 'tool.execution_start'
            data = [ordered]@{
                toolCallId = $callId
                toolName = $toolName
                arguments = $toolArguments
            }
        })
    if ($mode -eq 'duplicate-call-id') { $events.Add($events[$events.Count - 1]) }
    if ($mode -ne 'missing-completion') {
    $events.Add([ordered]@{
            type = 'tool.execution_complete'
            data = [ordered]@{
                toolCallId = $callId
                success = if ($mode -eq 'string-success') { 'true' } else { $completionSucceeded }
                result = if ($completionSucceeded) {
                    [int] $schemaVersion = if ($mode -eq 'unknown-cli-schema') { 16 } else { 17 }
                    [string] $json = "{`"schemaVersion`":$schemaVersion,`"context`":{`"operation`":`"$reportedOperation`"},`"result`":{`"gcCount`":7}}"
                    [string] $content = switch ($mode) {
                        'malformed-shell-wrapper' { "prefix`n$json`n<shellId: 0 completed with exit code 0>"; break }
                        'nonzero-shell-wrapper' { "$json`n<shellId: 0 completed with exit code 1>"; break }
                        default { "$json`n<shellId: 0 completed with exit code 0>" }
                    }
                    [string] $detailedContent = if ($mode -eq 'mismatched-shell-content') { "$content changed" } else { $content }
                    [ordered]@{ content = $content; detailedContent = $detailedContent }
                }
                else { 'filtrace failed' }
            }
        })
    }
    }
}

if ($mode -notin @('answer-before-analysis', 'answer-before-skill-context')) {
    $events.Add([ordered]@{
            type = 'assistant.message'
            data = [ordered]@{ content = $answer }
        })
}
if ($mode -eq 'unknown-event') {
    $events.Add([ordered]@{ type = 'future.event'; data = [ordered]@{} })
}
$resultEvent = [ordered]@{
    type = 'result'
    exitCode = if ($mode -eq 'host-failure') { 1 } else { 0 }
}
if ($mode -ne 'missing-all-usage') {
    $resultEvent.usage = [ordered]@{
        premiumRequests = 1
        totalApiDurationMs = 20
        sessionDurationMs = 25
    }
}
$events.Add($resultEvent)

    if ($mode -notin @('missing-usage-output', 'missing-all-usage')) {
        [string] $usageJson = if ($mode -eq 'malformed-usage-output') {
            '{'
        }
        elseif ($mode -eq 'empty-usage-output') {
            '{}'
        }
        else {
            $usageValue = [ordered]@{
                    totalPremiumRequestCost = 1
                    totalUserRequests = 1
                    tokenDetails = [ordered]@{
                        input = [ordered]@{ tokenCount = 20 }
                        cache_read = [ordered]@{ tokenCount = 5 }
                        cache_write = [ordered]@{ tokenCount = 15 }
                        output = [ordered]@{ tokenCount = 10 }
                    }
                    currentModel = $reportedModel
                }
            switch ($mode) {
                'usage-missing-token-details' { [void]$usageValue.Remove('tokenDetails') }
                'usage-missing-token-count' { [void]$usageValue.tokenDetails.output.Remove('tokenCount') }
                'usage-missing-premium' { [void]$usageValue.Remove('totalPremiumRequestCost') }
                'usage-wrong-type' { $usageValue.tokenDetails.input.tokenCount = '20' }
                'usage-negative-token' { $usageValue.tokenDetails.cache_read.tokenCount = -1 }
                'usage-model-mismatch' { $usageValue.currentModel = 'other-model' }
            }
            [string]($usageValue | ConvertTo-Json -Depth 6)
        }
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($UsageOutputFile)) | Out-Null
        [System.IO.File]::WriteAllText($UsageOutputFile, $usageJson, [System.Text.UTF8Encoding]::new($false))
    }

if ($mode -in @('completion-before-start', 'skill-context-before-completion')) {
    [string] $targetCallId = if ($mode -eq 'completion-before-start') { 'call-1' } else { 'skill-load' }
    [int] $startIndex = -1
    [int] $completionIndex = -1
    [int] $contextIndex = -1
    for ($eventIndex = 0; $eventIndex -lt $events.Count; $eventIndex++) {
        $event = $events[$eventIndex]
        if ($event.type -eq 'tool.execution_start' -and $event.data.toolCallId -eq $targetCallId) {
            $startIndex = $eventIndex
        }
        elseif ($event.type -eq 'tool.execution_complete' -and $event.data.toolCallId -eq $targetCallId) {
            $completionIndex = $eventIndex
        }
        elseif ($mode -eq 'skill-context-before-completion' -and $event.type -eq 'model.message' -and
            $event.data.message.role -eq 'user' -and $event.data.message.content -is [string] -and
            ([string]$event.data.message.content).StartsWith(
                '<skill-context name="filtrace">', [StringComparison]::Ordinal)) {
            $contextIndex = $eventIndex
        }
    }
    if ($startIndex -lt 0 -or $completionIndex -lt 0) { throw 'Fake host could not reorder tool events.' }
    if ($mode -eq 'completion-before-start') {
        $completionEvent = $events[$completionIndex]
        $events.RemoveAt($completionIndex)
        $events.Insert($startIndex, $completionEvent)
    }
    else {
        if ($contextIndex -lt 0) { throw 'Fake host could not reorder skill context.' }
        $contextEvent = $events[$contextIndex]
        $events.RemoveAt($contextIndex)
        $events.Insert($completionIndex, $contextEvent)
    }
}

foreach ($hostEvent in $events) {
    if ($mode -eq 'malformed-jsonl') {
        [Console]::Out.WriteLine('{')
        $mode = 'malformed-jsonl-emitted'
    }
    $hostEvent | ConvertTo-Json -Depth 8 -Compress
}
