#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Contract checks for the bundled short-command capture helper.

.DESCRIPTION
  Runs Capture-CommandTrace.ps1 against a fake filtrace command. No traced
    workload, ETW session, elevation prompt, or native capture is started unless
    -WindowsNativeArgv is specified. That Windows-only mode uses owned native test
    apphosts to prove the structured argv encoding without starting ETW.

.PARAMETER WindowsNativeArgv
    Also run the Windows native fake-collector and argv-recorder contract. Build
    tests/Filtrace.LocalTesting.Tests in Release before using this switch.
#>
[CmdletBinding()]
param(
        [switch]$WindowsNativeArgv,
        [switch]$EnvironmentCleanupProbe
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$captureScript = Join-Path $root '.agents/skills/filtrace/scripts/Capture-CommandTrace.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ft-command-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
$environmentVariableNames = @(
    'FILTRACE_COMMAND_CALLS',
    'FILTRACE_TEST_SECRET',
    'DOTNET_ReadyToRun',
    'FILTRACE_COMMAND_MODE',
    'FILTRACE_CAPTURE_SCRIPT',
    'FILTRACE_CAPTURE_RUN',
    'FILTRACE_CAPTURE_TOOL',
    'FILTRACE_CAPTURE_COMMAND',
    'FILTRACE_PROCESS_MODE',
    'FILTRACE_START_PROCESS_CALL',
    'FILTRACE_WAIT_MS',
    'FILTRACE_PROCESS_DISPOSED',
    'FILTRACE_COMMAND_CAPTURE_PROBE_MODE',
    'FILTRACE_COMMAND_CAPTURE_PROBE_READINESS_PATH',
    'FILTRACE_COMMAND_CAPTURE_RECORD_DIRECTORY',
    'FILTRACE_COMMAND_CAPTURE_HOST_EDITION',
    'FILTRACE_COMMAND_CAPTURE_HOST_VERSION')
$originalEnvironmentVariables = [ordered]@{}
foreach ($environmentVariableName in $environmentVariableNames) {
    $environmentEntry = Get-Item -LiteralPath "Env:$environmentVariableName" -ErrorAction SilentlyContinue
    $originalEnvironmentVariables[$environmentVariableName] = [pscustomobject]@{
        Exists = $null -ne $environmentEntry
        Value = if ($null -ne $environmentEntry) { [string]$environmentEntry.Value } else { $null }
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-CaptureChild(
    [string]$ScriptPath,
    [string]$SpecPath,
    [string]$ExpectedSpecSha256 = '',
    [string]$CallerNativeArgumentPassing = '',
    [string]$CallerModeRecordPath = '') {
    if (-not $ExpectedSpecSha256) {
        $ExpectedSpecSha256 = (Get-FileHash -LiteralPath $SpecPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $text = if ($CallerNativeArgumentPassing) {
        & pwsh -NoProfile -File $nativeArgumentModeWrapper `
            -NativeArgumentPassing $CallerNativeArgumentPassing `
            -ModeRecordPath $CallerModeRecordPath `
            -ScriptPath $ScriptPath `
            -SpecPath $SpecPath `
            -ExpectedSpecSha256 $ExpectedSpecSha256 2>&1 | Out-String
    }
    else {
        & pwsh -NoProfile -File $ScriptPath -SpecPath $SpecPath `
            -ExpectedSpecSha256 $ExpectedSpecSha256 2>&1 | Out-String
    }
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = $text
    }
}

function Invoke-WindowsNativeArgvProof(
    [string]$ScriptPath,
    [string]$RunName,
    [object[]]$NativeScenarios,
    [string]$NativeRecorder,
    [string]$NativeCollector,
    [string]$CallerNativeArgumentPassing = '') {
    $runDirectory = Join-Path $temporaryRoot $RunName
    $recordDirectory = Join-Path $temporaryRoot "$RunName-records"
    $specPath = Join-Path $temporaryRoot "$RunName.json"
    New-Item -ItemType Directory -Path $recordDirectory | Out-Null
    $env:FILTRACE_COMMAND_CAPTURE_RECORD_DIRECTORY = $recordDirectory

    $spec = [ordered]@{
        scenarios = $NativeScenarios
        iterations = 1
        profile = 'startup'
        cpuSampleMSec = 1.0
        outputDirectory = $runDirectory
        workingDirectory = Split-Path -Parent $NativeRecorder
        filtracePath = $NativeCollector
    }
    [System.IO.File]::WriteAllText(
        $specPath,
        ($spec | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))

    $callerModeRecordPath = Join-Path $temporaryRoot "$RunName-caller-mode.txt"
    $captureResult = Invoke-CaptureChild $ScriptPath $specPath -CallerNativeArgumentPassing $CallerNativeArgumentPassing -CallerModeRecordPath $callerModeRecordPath
    Assert-True ($captureResult.ExitCode -eq 0) "Windows native argv capture failed with exit $($captureResult.ExitCode).`n$($captureResult.Output)"
    if ($CallerNativeArgumentPassing) {
        Assert-True ((Get-Content -LiteralPath $callerModeRecordPath -Raw) -ceq $CallerNativeArgumentPassing) "Windows native argv capture did not enter the production helper with caller mode '$CallerNativeArgumentPassing'."
    }

    $manifest = Get-Content -LiteralPath (Join-Path $runDirectory 'manifest.json') -Raw | ConvertFrom-Json
    $caseEvidence = [System.Collections.Generic.List[object]]::new()
    foreach ($nativeScenario in $NativeScenarios) {
        $caseName = [string]$nativeScenario.name
        $expectedArguments = @($nativeScenario.argumentList)
        $workloadRecord = Get-Content -LiteralPath (Join-Path $recordDirectory "$caseName.workload.json") -Raw | ConvertFrom-Json
        $collectorRecord = Get-Content -LiteralPath (Join-Path $recordDirectory "$caseName.collector.json") -Raw | ConvertFrom-Json
        $actualArguments = @($workloadRecord.Arguments)
        Assert-True ($actualArguments.Count -eq $expectedArguments.Count) "Native argv case '$caseName' changed argument count: expected $($expectedArguments.Count), observed $($actualArguments.Count)."
        for ($argumentIndex = 0; $argumentIndex -lt $expectedArguments.Count; $argumentIndex++) {
            Assert-True ([string]$actualArguments[$argumentIndex] -ceq [string]$expectedArguments[$argumentIndex]) "Native argv case '$caseName' changed token $argumentIndex."
        }

        $manifestCase = @($manifest.cases | Where-Object id -eq $caseName)[0]
        Assert-True ($null -ne $manifestCase) "Native argv case '$caseName' was missing from the manifest."
        Assert-True ($manifestCase.command.arguments.argumentList -is [array]) "Native argv case '$caseName' did not retain an argumentList JSON array."
        Assert-True (@($manifestCase.invocations).Count -eq 1) "Native argv case '$caseName' did not retain its single owned invocation root."
        Assert-True ([int]$manifestCase.invocations[0].processId -eq [int]$workloadRecord.ProcessId) "Native argv case '$caseName' did not retain the owned recorder PID."
        Assert-True ([DateTimeOffset]$manifestCase.invocations[0].startedUtc -le [DateTimeOffset]$manifestCase.invocations[0].stoppedUtc) "Native argv case '$caseName' retained reversed recorder timestamps."
        Assert-True ([System.IO.Path]::GetFullPath([string]$workloadRecord.ProcessPath) -eq [System.IO.Path]::GetFullPath($NativeRecorder)) "Native argv case '$caseName' did not execute the renamed native recorder apphost."
        Assert-True ([string]$workloadRecord.HostEdition -ceq [string]$PSVersionTable.PSEdition) "Native argv case '$caseName' changed the recorded PowerShell edition."
        Assert-True ([string]$workloadRecord.HostVersion -ceq $PSVersionTable.PSVersion.ToString()) "Native argv case '$caseName' changed the recorded PowerShell version."

        [string[]]$collectorArguments = @($collectorRecord.Arguments)
        $launchIndex = [Array]::IndexOf($collectorArguments, '--launch')
        $launchArgumentsIndex = [Array]::IndexOf($collectorArguments, '--launch-args')
        Assert-True ($launchIndex -ge 0 -and $collectorArguments[$launchIndex + 1] -eq $NativeRecorder) "Native argv case '$caseName' did not reach the collector as the exact --launch option token."
        Assert-True ([string]$collectorRecord.Launch -eq $NativeRecorder) "Native argv case '$caseName' changed the collector's parsed --launch value."
        Assert-True ([string]$collectorRecord.LaunchArguments -ceq [string]$manifestCase.command.arguments.commandLine) "Native argv case '$caseName' changed the collector's parsed --launch-args value."
        if ($expectedArguments.Count -eq 0) {
            Assert-True ($launchArgumentsIndex -lt 0) "Native argv case '$caseName' unexpectedly sent --launch-args for an empty argument list."
        }
        else {
            Assert-True ($launchArgumentsIndex -ge 0 -and $collectorArguments[$launchArgumentsIndex + 1] -ceq [string]$manifestCase.command.arguments.commandLine) "Native argv case '$caseName' did not reach the collector as the exact --launch-args option token."
        }

        $caseEvidence.Add([pscustomobject]@{
            Name = $caseName
            ArgumentCount = $actualArguments.Count
            EncodedLength = ([string]$collectorRecord.LaunchArguments).Length
            ProcessId = [int]$workloadRecord.ProcessId
        })
    }

    return $caseEvidence
}

if ($WindowsNativeArgv -and [System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw '-WindowsNativeArgv requires Windows.'
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    if ($EnvironmentCleanupProbe) {
        $env:FILTRACE_COMMAND_CALLS = 'probe-mutated'
        $env:FILTRACE_TEST_SECRET = 'probe-mutated'
        $env:DOTNET_ReadyToRun = 'probe-mutated'
        $env:FILTRACE_COMMAND_MODE = 'probe-mutated'
        throw 'Forced environment cleanup probe failure.'
    }

    $cleanupProbeWrapper = Join-Path $temporaryRoot 'Invoke-EnvironmentCleanupProbe.ps1'
    $cleanupProbeWrapperText = @'
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScriptPath)

$env:DOTNET_ReadyToRun = 'existing-dotnet-sentinel'
$env:FILTRACE_TEST_SECRET = 'existing-test-sentinel'
Remove-Item Env:FILTRACE_COMMAND_MODE -ErrorAction SilentlyContinue
$pathBefore = $env:PATH
$failure = $null
try {
    & $ScriptPath -EnvironmentCleanupProbe
}
catch {
    $failure = $_.Exception.Message
}

$result = [ordered]@{
    forcedFailureObserved = $failure -ceq 'Forced environment cleanup probe failure.'
    dotnetRestored = $env:DOTNET_ReadyToRun -ceq 'existing-dotnet-sentinel'
    testVariableRestored = $env:FILTRACE_TEST_SECRET -ceq 'existing-test-sentinel'
    absentVariableRestored = $null -eq [Environment]::GetEnvironmentVariable('FILTRACE_COMMAND_MODE')
    pathUnchanged = $env:PATH -ceq $pathBefore
}
$result | ConvertTo-Json -Compress
if (-not ($result.forcedFailureObserved -and
        $result.dotnetRestored -and
        $result.testVariableRestored -and
        $result.absentVariableRestored -and
        $result.pathUnchanged)) {
    exit 1
}
'@
    [System.IO.File]::WriteAllText(
        $cleanupProbeWrapper,
        $cleanupProbeWrapperText,
        [System.Text.UTF8Encoding]::new($false))
    $cleanupProbeOutput = & pwsh -NoProfile -File $cleanupProbeWrapper -ScriptPath $PSCommandPath 2>&1 | Out-String
    $cleanupProbeExitCode = $LASTEXITCODE
    Assert-True ($cleanupProbeExitCode -eq 0) "The forced-failure environment cleanup probe failed with exit $cleanupProbeExitCode."
    $cleanupProbeResult = $cleanupProbeOutput | ConvertFrom-Json
    Assert-True ($cleanupProbeResult.forcedFailureObserved) 'The environment cleanup probe did not observe its forced failure.'
    Assert-True ($cleanupProbeResult.dotnetRestored) 'The environment cleanup probe did not restore the original DOTNET_ReadyToRun value.'
    Assert-True ($cleanupProbeResult.testVariableRestored) 'The environment cleanup probe did not restore the original test-variable value.'
    Assert-True ($cleanupProbeResult.absentVariableRestored) 'The environment cleanup probe did not restore an originally absent test variable.'
    Assert-True ($cleanupProbeResult.pathUnchanged) 'The environment cleanup probe changed PATH.'

    $nativeArgumentModeWrapper = Join-Path $temporaryRoot 'Invoke-CaptureWithNativeArgumentMode.ps1'
    $nativeArgumentModeWrapperText = @'
#requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Legacy', 'Standard', 'Windows')][string]$NativeArgumentPassing,
    [Parameter(Mandatory)][string]$ModeRecordPath,
    [Parameter(Mandatory)][string]$ScriptPath,
    [Parameter(Mandatory)][string]$SpecPath,
    [Parameter(Mandatory)][string]$ExpectedSpecSha256)

$PSNativeCommandArgumentPassing = $NativeArgumentPassing
[System.IO.File]::WriteAllText($ModeRecordPath, [string]$PSNativeCommandArgumentPassing)
& $ScriptPath -SpecPath $SpecPath -ExpectedSpecSha256 $ExpectedSpecSha256
'@
    [System.IO.File]::WriteAllText(
        $nativeArgumentModeWrapper,
        $nativeArgumentModeWrapperText,
        [System.Text.UTF8Encoding]::new($false))

    $captureSource = Get-Content -LiteralPath $captureScript -Raw
    $tokens = $null
    $parseErrors = $null
    $captureAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $captureScript,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Command capture helper did not parse under PowerShell 7.'
    Assert-True ($captureAst.ScriptRequirements.RequiredPSVersion -eq [Version]'7.3') 'Command capture helper did not require exactly PowerShell 7.3.'

    $testElevatedDefinitions = @(
        $captureAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-Elevated' },
            $true)
    )
    Assert-True ($testElevatedDefinitions.Count -eq 1) 'Command capture helper did not contain exactly one Test-Elevated function.'
    $testElevatedDefinition = $testElevatedDefinitions[0]
    $boundedWriterDefinitions = @(
        $captureAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-BoundedUtf8File' },
            $true)
    )
    Assert-True ($boundedWriterDefinitions.Count -eq 1) 'Command capture helper did not contain exactly one bounded UTF-8 writer.'
    . ([scriptblock]::Create($boundedWriterDefinitions[0].Extent.Text))
    $maxSerializedBytes = 16 * 1024 * 1024
    $exactAsciiPath = Join-Path $temporaryRoot 'manifest-exact-ascii.json'
    $exactAsciiJson = '"' + ('a' * ($maxSerializedBytes - 3)) + '"'
    Write-BoundedUtf8File $exactAsciiPath $exactAsciiJson
    Assert-True ((Get-Item -LiteralPath $exactAsciiPath).Length -eq $maxSerializedBytes - 1) 'The largest accepted ASCII manifest boundary was rejected or changed.'
    Remove-Item -LiteralPath $exactAsciiPath
    $exactAsciiJson = $null

    $astralCharacter = [char]::ConvertFromUtf32(0x1F600)
    $exactUnicodePath = Join-Path $temporaryRoot 'manifest-exact-unicode.json'
    $exactUnicodeJson = '"' + ('u' * ($maxSerializedBytes - 7)) + $astralCharacter + '"'
    Write-BoundedUtf8File $exactUnicodePath $exactUnicodeJson
    Assert-True ((Get-Item -LiteralPath $exactUnicodePath).Length -eq $maxSerializedBytes - 1) 'The largest accepted Unicode manifest boundary was rejected or changed.'

    $oversizedManifestPath = Join-Path $temporaryRoot 'manifest-oversized.json'
    foreach ($suffix in @('', 'é')) {
        $oversizedUnicodeJson = '"' + ('u' * ($maxSerializedBytes - 6)) + $astralCharacter + $suffix + '"'
        $expectedBytes = [System.Text.Encoding]::UTF8.GetByteCount($oversizedUnicodeJson)
        $oversizedManifestFailure = $null
        try {
            Write-BoundedUtf8File $oversizedManifestPath $oversizedUnicodeJson
        }
        catch {
            $oversizedManifestFailure = $_.Exception.Message
        }
        Assert-True ($oversizedManifestFailure -match "$expectedBytes UTF-8 bytes") "A manifest at or above the limit did not report its exact byte count. Observed failure: $oversizedManifestFailure"
        Assert-True (-not (Test-Path -LiteralPath $oversizedManifestPath)) 'A manifest at or above the limit created a partial or invalid file.'
    }
    Remove-Item -LiteralPath $exactUnicodePath
    $exactUnicodeJson = $null
    $oversizedUnicodeJson = $null

    $testCaptureSource =
        $captureSource.Substring(0, $testElevatedDefinition.Extent.StartOffset) +
        'function Test-Elevated { return $true }' +
        $captureSource.Substring($testElevatedDefinition.Extent.EndOffset)
    $testCaptureSource = $testCaptureSource.Replace('if ($IsWindows -eq $false)', 'if ($false)')
    $testCaptureScript = Join-Path $temporaryRoot 'Capture-CommandTrace.ps1'
    [System.IO.File]::WriteAllText(
        $testCaptureScript,
        $testCaptureSource,
        [System.Text.UTF8Encoding]::new($false))

    $fakeFiltrace = Join-Path $temporaryRoot 'Fake-Filtrace.ps1'
    $fakeFiltraceText = @'
[System.IO.File]::AppendAllText(
    $env:FILTRACE_COMMAND_CALLS,
    (([ordered]@{
        arguments = @($args)
        workingDirectory = (Get-Location).Path
    } | ConvertTo-Json -Compress) + [Environment]::NewLine),
    [System.Text.UTF8Encoding]::new($false))
if ($args[0] -eq '--version') {
    if ($env:FILTRACE_COMMAND_MODE -eq 'version-nonzero') {
        Write-Output 'version probe failed'
        $global:LASTEXITCODE = 31
        return
    }
    if ($env:FILTRACE_COMMAND_MODE -eq 'version-empty') {
        $global:LASTEXITCODE = 0
        return
    }
    Write-Output '1.2.3-contract'
    $global:LASTEXITCODE = 0
    return
}
if ($args[0] -eq 'collect' -and $args[1] -eq '--help') {
    if ($env:FILTRACE_COMMAND_MODE -eq 'help-nonzero') {
        Write-Output 'help failed'
        $global:LASTEXITCODE = 32
        return
    }
    if ($env:FILTRACE_COMMAND_MODE -eq 'help-missing-capability') {
        Write-Output 'collect --format --launch'
        $global:LASTEXITCODE = 0
        return
    }
    Write-Output 'collect --iterations --format --launch --launch-args'
    $global:LASTEXITCODE = 0
    return
}
$outputIndex = [Array]::IndexOf($args, '--output')
$tracePath = $args[$outputIndex + 1]
if ([System.IO.Path]::GetFileName($tracePath) -eq 'failed.etl') {
    Write-Output 'bounded failure detail'
    $global:LASTEXITCODE = 23
    return
}
if ([System.IO.Path]::GetFileName($tracePath) -eq 'diagnostic-surrogate.etl') {
    Write-Output (('d' * 2047) + [char]::ConvertFromUtf32(0x1F600) + 'tail')
    $global:LASTEXITCODE = 23
    return
}
if ($env:FILTRACE_COMMAND_MODE -eq 'malformed-collect') {
    Write-Output 'workload emitted {"unrelated":true}'
    Write-Output '{not valid collect json'
    $global:LASTEXITCODE = 0
    return
}
[System.IO.File]::WriteAllText($tracePath, 'fake trace')
$iterationsIndex = [Array]::IndexOf($args, '--iterations')
$iterations = [int]$args[$iterationsIndex + 1]
$processIdBase = if ([System.IO.Path]::GetFileName($tracePath) -eq 'legacy.etl') { 4100 } else { 4200 }
$invocations = @(
    1..$iterations | ForEach-Object {
        [ordered]@{
            ordinal = $_
            processId = $processIdBase + $_ - 1
            exitCode = 0
            startedUtc = "2026-09-05T12:00:0$($_ - 1).0000000+00:00"
            stoppedUtc = "2026-09-05T12:00:0$($_ - 1).0100000+00:00"
        }
    }
)
switch ($env:FILTRACE_COMMAND_MODE) {
    'missing-invocation-field' { $invocations[0].Remove('stoppedUtc') }
    'noninteger-ordinal' { $invocations[0].ordinal = 'one' }
    'zero-process-id' { $invocations[0].processId = 0 }
    'short-invocation-count' { $invocations = @($invocations[0]) }
    'nonarray-invocations' { $invocations = $invocations[0] }
    'duplicate-ordinal' { $invocations[1].ordinal = $invocations[0].ordinal }
    'noninteger-exit-code' { $invocations[0].exitCode = 'zero' }
    'invalid-timestamp' { $invocations[0].startedUtc = 'not-a-timestamp' }
    'reversed-timestamps' { $invocations[0].stoppedUtc = '2026-09-05T11:59:59.0000000+00:00' }
}
Write-Output 'workload output before result'
[ordered]@{
    result = [ordered]@{
        cpuSample = [ordered]@{ effectiveMSec = 1.0; clamped = $false }
        invocations = $invocations
    }
} | ConvertTo-Json -Depth 6 -Compress
$global:LASTEXITCODE = 0
'@
    [System.IO.File]::WriteAllText(
        $fakeFiltrace,
        $fakeFiltraceText,
        [System.Text.UTF8Encoding]::new($false))

    $callsPath = Join-Path $temporaryRoot 'filtrace-calls.jsonl'
    $runDirectory = Join-Path $temporaryRoot 'run with spaces'
    $workingDirectory = Join-Path $temporaryRoot 'original working directory'
    New-Item -ItemType Directory -Path $workingDirectory | Out-Null
    $pwshPath = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
    $dotnetPath = @(Get-Command dotnet -CommandType Application -ErrorAction Stop)[0].Source
    $secretValue = 'must-not-enter-command-manifest'
    $env:FILTRACE_COMMAND_CALLS = $callsPath
    $env:FILTRACE_TEST_SECRET = $secretValue
    $env:DOTNET_ReadyToRun = '0'

    $specPath = Join-Path $temporaryRoot 'scenarios.json'
    $fakeFiltraceIdentity = [ordered]@{
        path = [System.IO.Path]::GetFullPath($fakeFiltrace)
        sha256 = (Get-FileHash -LiteralPath $fakeFiltrace -Algorithm SHA256).Hash.ToLowerInvariant()
        version = '1.2.3-contract'
    }

    $invalidDigestSpecPath = Join-Path $temporaryRoot 'spec-invalid-digest.json'
    [System.IO.File]::WriteAllBytes($invalidDigestSpecPath, [System.Text.Encoding]::UTF8.GetBytes('{"scenarios":[]}'))
    $missingDigestOutput = & pwsh -NoProfile -File $testCaptureScript -SpecPath $invalidDigestSpecPath 2>&1 | Out-String
    $missingDigestExitCode = $LASTEXITCODE
    Assert-True ($missingDigestExitCode -ne 0) 'SpecPath without ExpectedSpecSha256 was accepted.'
    Assert-True ($missingDigestOutput -match 'ExpectedSpecSha256 is required') 'A missing spec digest did not report the authenticated handoff requirement.'
    $malformedDigestOutput = & pwsh -NoProfile -File $testCaptureScript -SpecPath $invalidDigestSpecPath `
        -ExpectedSpecSha256 'not-a-sha256' 2>&1 | Out-String
    $malformedDigestExitCode = $LASTEXITCODE
    Assert-True ($malformedDigestExitCode -ne 0) 'A malformed spec digest was accepted.'
    Assert-True ($malformedDigestOutput -match 'ExpectedSpecSha256') 'A malformed spec digest did not fail parameter validation.'

    $validEmptySpecResult = Invoke-CaptureChild $testCaptureScript $invalidDigestSpecPath
    Assert-True ($validEmptySpecResult.ExitCode -ne 0) 'An authenticated empty scenario specification was accepted.'
    Assert-True ($validEmptySpecResult.Output -match 'Supply at least one -Scenario') 'An authenticated empty scenario specification did not reach scenario validation.'

    $completeSpecBytes = [System.Text.Encoding]::UTF8.GetBytes('{"scenarios":[]}')
    $completeSpecSha256 = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($completeSpecBytes)).ToLowerInvariant()
    $truncatedSpecPath = Join-Path $temporaryRoot 'spec-truncated.json'
    [System.IO.File]::WriteAllBytes($truncatedSpecPath, $completeSpecBytes[0..($completeSpecBytes.Length - 2)])
    $truncatedSpecResult = Invoke-CaptureChild $testCaptureScript $truncatedSpecPath $completeSpecSha256
    Assert-True ($truncatedSpecResult.ExitCode -ne 0) 'A truncated elevation spec was accepted with the complete payload digest.'
    Assert-True ($truncatedSpecResult.Output -match 'does not match the digest') 'A truncated elevation spec was parsed before its digest mismatch was reported.'
    Assert-True (-not (Test-Path -LiteralPath $callsPath)) 'An invalid authenticated specification reached the fake filtrace boundary.'

    $tamperedCallsPath = Join-Path $temporaryRoot 'tampered-spec-calls.jsonl'
    $tamperedRunDirectory = Join-Path $temporaryRoot 'run-tampered-spec'
    $tamperedSpecPath = Join-Path $temporaryRoot 'spec-tampered.json'
    $benignSpecBytes = [System.Text.Encoding]::UTF8.GetBytes('{"scenarios":[]}')
    [System.IO.File]::WriteAllBytes($tamperedSpecPath, $benignSpecBytes)
    $expectedBenignSpecSha256 = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($benignSpecBytes)).ToLowerInvariant()
    $tamperedSpec = [ordered]@{
        scenarios = @([ordered]@{
                name = 'tampered'
                requestedCommand = $pwshPath
                command = $fakeFiltrace
                executableSha256 = $fakeFiltraceIdentity.sha256
                argumentsKind = 'argumentList'
                argumentList = @()
            })
        iterations = 1
        profile = 'startup'
        cpuSampleMSec = 1.0
        outputDirectory = $tamperedRunDirectory
        workingDirectory = $workingDirectory
        filtracePath = $fakeFiltrace
        filtrace = $fakeFiltraceIdentity
    }
    [System.IO.File]::WriteAllText(
        $tamperedSpecPath,
        ($tamperedSpec | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))
    $env:FILTRACE_COMMAND_CALLS = $tamperedCallsPath
    $tamperedResult = Invoke-CaptureChild $testCaptureScript $tamperedSpecPath $expectedBenignSpecSha256
    Assert-True ($tamperedResult.ExitCode -ne 0) "A replaced elevation spec was accepted despite the benign digest $expectedBenignSpecSha256."
    Assert-True (-not (Test-Path -LiteralPath $tamperedCallsPath)) 'A replaced elevation spec reached the fake filtrace boundary.'
    Assert-True (-not (Test-Path -LiteralPath $tamperedRunDirectory)) 'A replaced elevation spec created its requested run directory.'

    $traversalRunDirectory = Join-Path $temporaryRoot 'run-traversal'
    $traversalSpecPath = Join-Path $temporaryRoot 'spec-traversal.json'
    $outsideSentinelPath = Join-Path $temporaryRoot 'outside.etl'
    $outsideSentinel = 'outside sentinel must remain unchanged'
    [System.IO.File]::WriteAllText($outsideSentinelPath, $outsideSentinel)
    $traversalSpec = [ordered]@{
        scenarios = @([ordered]@{ name = '../outside'; command = $pwshPath; argumentList = @() })
        iterations = 1
        profile = 'startup'
        cpuSampleMSec = 1.0
        outputDirectory = $traversalRunDirectory
        workingDirectory = $workingDirectory
        filtracePath = $fakeFiltrace
    }
    [System.IO.File]::WriteAllText(
        $traversalSpecPath,
        ($traversalSpec | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))

    $traversalResult = Invoke-CaptureChild $testCaptureScript $traversalSpecPath
    Assert-True ($traversalResult.ExitCode -ne 0) 'A traversal scenario name was accepted.'
    Assert-True (-not (Test-Path -LiteralPath $callsPath)) 'A traversal scenario reached the fake filtrace boundary.'
    Assert-True ((Get-Content -LiteralPath $outsideSentinelPath -Raw) -ceq $outsideSentinel) 'A traversal scenario replaced the outside sentinel.'

    $invalidNames = @(
        'bad/name',
        'bad\name',
        "bad`nname",
        'CON',
        'COM¹',
        'COM¹.txt',
        'COM²',
        'COM².txt',
        'COM³',
        'COM³.txt',
        'LPT¹',
        'LPT¹.log',
        'LPT²',
        'LPT².log',
        'LPT³',
        'LPT³.log',
        'trailing.',
        ('x' * 257))
    for ($invalidNameIndex = 0; $invalidNameIndex -lt $invalidNames.Count; $invalidNameIndex++) {
        $invalidName = $invalidNames[$invalidNameIndex]
        $invalidNameCallsPath = Join-Path $temporaryRoot "invalid-name-$invalidNameIndex.jsonl"
        $invalidNameSpecPath = Join-Path $temporaryRoot "invalid-name-$invalidNameIndex.json"
        $invalidNameSpec = [ordered]@{
            scenarios = @([ordered]@{ name = $invalidName; command = $pwshPath; argumentList = @() })
            iterations = 1
            profile = 'startup'
            cpuSampleMSec = 1.0
            outputDirectory = Join-Path $temporaryRoot "run-invalid-name-$invalidNameIndex"
            workingDirectory = $workingDirectory
            filtracePath = $fakeFiltrace
        }
        [System.IO.File]::WriteAllText(
            $invalidNameSpecPath,
            ($invalidNameSpec | ConvertTo-Json -Depth 6),
            [System.Text.UTF8Encoding]::new($false))
        $env:FILTRACE_COMMAND_CALLS = $invalidNameCallsPath
        $invalidNameResult = Invoke-CaptureChild $testCaptureScript $invalidNameSpecPath
        Assert-True ($invalidNameResult.ExitCode -ne 0) "Invalid scenario name at index $invalidNameIndex was accepted."
        Assert-True (-not (Test-Path -LiteralPath $invalidNameCallsPath)) "Invalid scenario name at index $invalidNameIndex reached the fake filtrace boundary."
    }

    $boundaryCallsPath = Join-Path $temporaryRoot 'boundary-calls.jsonl'
    $boundaryRunDirectory = Join-Path $temporaryRoot 'run-boundary'
    $boundarySpecPath = Join-Path $temporaryRoot 'spec-boundary.json'
    $boundaryScenarios = [System.Collections.Generic.List[object]]::new()
    $boundaryScenarios.Add([ordered]@{ name = ('x' * 256); command = $pwshPath; argumentList = @() })
    $boundaryScenarios.Add([ordered]@{ name = 'normal.dot space'; command = $pwshPath; argumentList = @() })
    $tracePrefixLength = 255 - '.etl'.Length - 16 - 1
    $surrogateBoundaryName = ('s' * ($tracePrefixLength - 1)) + $astralCharacter + ('t' * (256 - $tracePrefixLength - 1))
    $boundaryScenarios.Add([ordered]@{ name = $surrogateBoundaryName; command = $pwshPath; argumentList = @() })
    foreach ($boundaryIndex in 2..254) {
        $boundaryScenarios.Add([ordered]@{ name = "case-$boundaryIndex"; command = $pwshPath; argumentList = @() })
    }
    $boundarySpec = [ordered]@{
        scenarios = $boundaryScenarios
        iterations = 1
        profile = 'startup'
        cpuSampleMSec = 1.0
        outputDirectory = $boundaryRunDirectory + [System.IO.Path]::DirectorySeparatorChar
        workingDirectory = $workingDirectory
        filtracePath = $fakeFiltrace
    }
    [System.IO.File]::WriteAllText(
        $boundarySpecPath,
        ($boundarySpec | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))
    $env:FILTRACE_COMMAND_CALLS = $boundaryCallsPath
    $boundaryResult = Invoke-CaptureChild $testCaptureScript $boundarySpecPath
    Assert-True ($boundaryResult.ExitCode -eq 0) "The exact scenario count/name boundary was rejected.`n$($boundaryResult.Output)"
    $boundaryManifest = Get-Content -LiteralPath (Join-Path $boundaryRunDirectory 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True (@($boundaryManifest.cases).Count -eq 256) 'The 256-case reader boundary was not retained.'
    Assert-True ($boundaryManifest.cases[0].id.Length -eq 256) 'The 256-character case-id boundary was not retained.'
    Assert-True ([System.IO.Path]::GetFileName($boundaryManifest.cases[0].trace).Length -le 255) 'The boundary case trace filename exceeded the Windows component limit.'
    Assert-True (@($boundaryManifest.cases | Where-Object id -eq 'normal.dot space').Count -eq 1) 'An ordinary dotted and spaced scenario name was not retained.'
    $surrogateBoundaryCase = @($boundaryManifest.cases | Where-Object id -eq $surrogateBoundaryName)[0]
    $surrogateBoundaryHash = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($surrogateBoundaryName))).ToLowerInvariant().Substring(0, 16)
    $expectedSurrogateFileName = ('s' * ($tracePrefixLength - 1)) + "-$surrogateBoundaryHash.etl"
    Assert-True ([System.IO.Path]::GetFileName($surrogateBoundaryCase.trace) -ceq $expectedSurrogateFileName) 'Trace filename truncation split a surrogate pair or hashed a truncated scenario name.'
    Assert-True (Test-Path -LiteralPath $surrogateBoundaryCase.trace -PathType Leaf) 'The surrogate-boundary trace filename was not usable on the filesystem.'

    $tooManyCallsPath = Join-Path $temporaryRoot 'too-many-calls.jsonl'
    $tooManySpecPath = Join-Path $temporaryRoot 'spec-too-many.json'
    $tooManySpec = $boundarySpec.PSObject.Copy()
    $tooManySpec.outputDirectory = Join-Path $temporaryRoot 'run-too-many'
    $tooManySpec.scenarios = @($boundaryScenarios) + @([ordered]@{ name = 'case-256'; command = $pwshPath; argumentList = @() })
    [System.IO.File]::WriteAllText(
        $tooManySpecPath,
        ($tooManySpec | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))
    $env:FILTRACE_COMMAND_CALLS = $tooManyCallsPath
    $tooManyResult = Invoke-CaptureChild $testCaptureScript $tooManySpecPath
    Assert-True ($tooManyResult.ExitCode -ne 0) 'A 257-case command capture was accepted.'
    Assert-True (-not (Test-Path -LiteralPath $tooManyCallsPath)) 'A 257-case command capture reached the fake filtrace boundary.'

    $env:FILTRACE_COMMAND_CALLS = $callsPath

    $spec = [ordered]@{
        scenarios = @(
            [ordered]@{ name = 'legacy'; command = $pwshPath; arguments = '-NoProfile -Command "exit 0"' }
            [ordered]@{ name = 'structured'; command = $dotnetPath; argumentList = @('tool with spaces.dll', '', 'quote"inside', 'path with trailing\') }
            [ordered]@{ name = 'control-display'; command = $pwshPath; argumentList = @("line1`r`nline2", ('x' * 2048)) }
            [ordered]@{ name = 'failed'; command = $pwshPath; argumentList = @('-NoProfile', '-Command', 'exit 23') }
        )
        iterations = 2
        profile = 'startup'
        cpuSampleMSec = 1.0
        outputDirectory = $runDirectory
        workingDirectory = $workingDirectory
        filtracePath = $fakeFiltrace
        filtrace = $fakeFiltraceIdentity
    }
    [System.IO.File]::WriteAllText(
        $specPath,
        ($spec | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))

    $captureResult = Invoke-CaptureChild $testCaptureScript $specPath
    Assert-True ($captureResult.ExitCode -eq 0) "Fake command capture failed with exit $($captureResult.ExitCode).`n$($captureResult.Output)"

    $manifestPath = Join-Path $runDirectory 'manifest.json'
    Assert-True (Test-Path -LiteralPath $manifestPath) 'Partial command capture did not write a manifest.'
    $manifestText = Get-Content -LiteralPath $manifestPath -Raw
    $manifest = $manifestText | ConvertFrom-Json
    Assert-True ($manifest.schemaVersion -eq 2) 'Additive command provenance changed the manifest schema version.'
    Assert-True ($manifest.kind -eq 'command') 'Command manifest kind changed.'
    Assert-True (@($manifest.cases).Count -eq 3) 'The successful command cases were not retained after one failure.'
    Assert-True ($null -ne $manifest.failedCases -and @($manifest.failedCases).Count -eq 1) 'The failed scenario did not retain a structured failure record.'
    Assert-True ($manifest.failedCases[0].collectExitCode -eq 23) 'The failed scenario lost the native collect exit code.'
    Assert-True ($manifest.failedCases[0].diagnostic -eq 'bounded failure detail') 'The failed scenario lost its bounded diagnostic.'
    Assert-True ($manifest.workingDirectory -eq [System.IO.Path]::GetFullPath($workingDirectory)) 'The original working directory was not retained.'
    Assert-True ($manifest.filtrace.path -eq [System.IO.Path]::GetFullPath($fakeFiltrace)) 'The resolved filtrace path was not retained.'
    Assert-True ($manifest.filtrace.version -eq '1.2.3-contract') 'The verified filtrace version was not retained.'
    Assert-True ($manifest.filtrace.sha256 -eq (Get-FileHash -LiteralPath $fakeFiltrace -Algorithm SHA256).Hash.ToLowerInvariant()) 'The filtrace content identity was not retained.'
    Assert-True ($manifest.environment.variables.DOTNET_ReadyToRun -eq '0') 'The allowlisted environment was not retained.'
    Assert-True (-not $manifestText.Contains('FILTRACE_TEST_SECRET', [StringComparison]::Ordinal)) 'A non-allowlisted environment name entered the manifest.'
    Assert-True (-not $manifestText.Contains($secretValue, [StringComparison]::Ordinal)) 'A non-allowlisted environment value entered the manifest.'

    $legacyCase = @($manifest.cases | Where-Object id -eq 'legacy')[0]
    $structuredCase = @($manifest.cases | Where-Object id -eq 'structured')[0]
    $controlDisplayCase = @($manifest.cases | Where-Object id -eq 'control-display')[0]
    Assert-True ($legacyCase.command.arguments.kind -eq 'legacyCommandLine') 'Legacy arguments were presented as parsed argv.'
    Assert-True ($legacyCase.command.arguments.commandLine -eq '-NoProfile -Command "exit 0"') 'Legacy command-line text changed.'
    Assert-True ($null -eq $legacyCase.command.arguments.argumentList) 'Legacy command-line text fabricated structured argv.'
    Assert-True ($structuredCase.command.arguments.kind -eq 'argumentList') 'Structured arguments lost their provenance kind.'
    Assert-True (($structuredCase.command.arguments.argumentList | ConvertTo-Json -Compress) -ceq '["tool with spaces.dll","","quote\"inside","path with trailing\\"]') 'Structured argv did not round-trip exactly.'
    Assert-True ($controlDisplayCase.benchmarkDisplay -ceq 'control-display') 'Control or oversized command text entered benchmarkDisplay.'
    Assert-True ($controlDisplayCase.command.arguments.argumentList[0] -ceq "line1`r`nline2") 'CRLF changed in exact command provenance.'
    Assert-True ($controlDisplayCase.command.arguments.argumentList[1] -ceq ('x' * 2048)) 'The long argv token changed in exact command provenance.'
    Assert-True ($legacyCase.command.executable.path -eq [System.IO.Path]::GetFullPath($pwshPath)) 'The legacy case did not retain its resolved executable path.'
    Assert-True ($structuredCase.command.executable.path -eq [System.IO.Path]::GetFullPath($dotnetPath)) 'The mixed executable case did not retain its resolved executable path.'
    Assert-True (@($legacyCase.invocations).Count -eq 2 -and $legacyCase.invocations[0].processId -eq 4100) 'The exact legacy invocation roots were not retained.'
    Assert-True (@($structuredCase.invocations).Count -eq 2 -and $structuredCase.invocations[0].processId -eq 4200) 'The exact structured invocation roots were not retained.'
    Assert-True (-not ($manifest.warnings -match 'different executables|no manifest-wide process scope')) 'The stale mixed-executable warning remained.'

    $collectCalls = @(
        Get-Content -LiteralPath $callsPath |
            ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object { $_.arguments[0] -eq 'collect' -and $_.arguments[1] -ne '--help' }
    )
    Assert-True ($collectCalls.Count -eq 4) 'The fake filtrace boundary did not observe all scenarios.'
    foreach ($collectCall in $collectCalls) {
        Assert-True ($collectCall.workingDirectory -eq [System.IO.Path]::GetFullPath($workingDirectory)) 'A collect invocation did not run in the recorded working directory.'
    }
    $structuredCall = @($collectCalls | Where-Object { $_.arguments -contains (Join-Path $runDirectory 'structured.etl') })[0].arguments
    $launchArgsIndex = [Array]::IndexOf($structuredCall, '--launch-args')
    Assert-True ($launchArgsIndex -ge 0) 'Structured argv was not forwarded to the collector.'
    Assert-True ($structuredCall[$launchArgsIndex + 1] -eq '"tool with spaces.dll" "" "quote\"inside" "path with trailing\\"') 'Structured argv was not encoded at the collector boundary.'

    $diagnosticCallsPath = Join-Path $temporaryRoot 'diagnostic-surrogate-calls.jsonl'
    $diagnosticRunDirectory = Join-Path $temporaryRoot 'run-diagnostic-surrogate'
    $diagnosticSpecPath = Join-Path $temporaryRoot 'spec-diagnostic-surrogate.json'
    $diagnosticSpec = [ordered]@{
        scenarios = @([ordered]@{ name = 'diagnostic-surrogate'; command = $pwshPath; argumentList = @() })
        iterations = 1
        profile = 'startup'
        cpuSampleMSec = 1.0
        outputDirectory = $diagnosticRunDirectory
        workingDirectory = $workingDirectory
        filtracePath = $fakeFiltrace
    }
    [System.IO.File]::WriteAllText(
        $diagnosticSpecPath,
        ($diagnosticSpec | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))
    $env:FILTRACE_COMMAND_CALLS = $diagnosticCallsPath
    $diagnosticResult = Invoke-CaptureChild $testCaptureScript $diagnosticSpecPath
    Assert-True ($diagnosticResult.ExitCode -ne 0) 'The diagnostic-boundary failure unexpectedly succeeded.'
    $diagnosticManifest = Get-Content -LiteralPath (Join-Path $diagnosticRunDirectory 'manifest.json') -Raw | ConvertFrom-Json
    $expectedDiagnostic = ('d' * 2047) + '... [truncated]'
    Assert-True ($diagnosticManifest.failedCases[0].diagnostic -ceq $expectedDiagnostic) 'Diagnostic truncation split the surrogate pair at its 2048-character boundary.'

    if ($WindowsNativeArgv) {
        $probeBuildDirectory = Join-Path $root 'tests/Filtrace.LocalTesting.Tests/bin/Release/net10.0'
        $probeApphost = Join-Path $probeBuildDirectory 'Filtrace.LocalTesting.Tests.exe'
        if (-not (Test-Path -LiteralPath $probeApphost -PathType Leaf)) {
            throw "Windows native argv contract requires '$probeApphost'. Build tests/Filtrace.LocalTesting.Tests in Release first."
        }

        $nativeProbeDirectory = Join-Path $temporaryRoot 'native argv probe'
        New-Item -ItemType Directory -Path $nativeProbeDirectory | Out-Null
        Get-ChildItem -LiteralPath $probeBuildDirectory | Copy-Item -Destination $nativeProbeDirectory -Recurse
        $nativeCollector = Join-Path $nativeProbeDirectory 'fakefiltrace.exe'
        $nativeRecorder = Join-Path $nativeProbeDirectory 'nativeargvrecorder.exe'
        Copy-Item -LiteralPath $probeApphost -Destination $nativeCollector
        Copy-Item -LiteralPath $probeApphost -Destination $nativeRecorder
        $readinessPath = Join-Path $nativeProbeDirectory 'ready.marker'
        [System.IO.File]::WriteAllText($readinessPath, 'ready', [System.Text.UTF8Encoding]::new($false))

        $env:FILTRACE_COMMAND_CAPTURE_PROBE_MODE = 'collector'
        $env:FILTRACE_COMMAND_CAPTURE_PROBE_READINESS_PATH = $readinessPath
        $env:FILTRACE_COMMAND_CAPTURE_HOST_EDITION = [string]$PSVersionTable.PSEdition
        $env:FILTRACE_COMMAND_CAPTURE_HOST_VERSION = $PSVersionTable.PSVersion.ToString()

        $nativeScenarios = [System.Collections.Generic.List[object]]::new()
        $nativeScenarios.Add([ordered]@{
            name = 'native-empty-list'
            command = $nativeRecorder
            argumentList = [string[]]@()
        })
        $nativeScenarios.Add([ordered]@{
            name = 'native-single'
            command = $nativeRecorder
            argumentList = [string[]]@('--version')
        })
        $nativeScenarios.Add([ordered]@{
            name = 'native-roundtrip'
            command = $nativeRecorder
            argumentList = [string[]]@(
                '',
                'plain',
                'two words',
                'quote"inside',
                "line1`r`nline2",
                'path with trailing\',
                'alpha_日本語_é',
                ('x' * 2048))
        })

        $encoderGuard = '    if ($Value.Length -gt 0 -and $Value -notmatch ''[\s"]'') { return $Value }'
        $brokenEncoderGuard = "    if (`$Value.Length -eq 0) { return '' }`r`n$encoderGuard"
        $mutatedCaptureSource = $testCaptureSource.Replace($encoderGuard, $brokenEncoderGuard)
        Assert-True (-not $mutatedCaptureSource.Equals($testCaptureSource, [StringComparison]::Ordinal)) 'The native argv mutation did not change the temporary capture helper.'
        $mutatedCaptureScript = Join-Path $temporaryRoot 'Capture-CommandTrace-Mutated.ps1'
        [System.IO.File]::WriteAllText(
            $mutatedCaptureScript,
            $mutatedCaptureSource,
            [System.Text.UTF8Encoding]::new($false))

        $mutationFailure = $null
        try {
            $null = Invoke-WindowsNativeArgvProof $mutatedCaptureScript 'native-mutated' @($nativeScenarios) $nativeRecorder $nativeCollector
        }
        catch {
            $mutationFailure = $_.Exception.Message
        }
        Assert-True ($null -ne $mutationFailure -and $mutationFailure -match "Native argv case 'native-roundtrip' changed") "The broken empty-argument encoder was not rejected by the native argv proof. Observed failure: $mutationFailure"

        $standardModeAssignment = '$script:PSNativeCommandArgumentPassing = ''Standard'''
        $callerControlledCaptureSource = $testCaptureSource.Replace($standardModeAssignment, '')
        Assert-True (-not $callerControlledCaptureSource.Equals($testCaptureSource, [StringComparison]::Ordinal)) 'The caller Legacy-mode mutation did not remove the helper Standard-mode assignment.'
        $callerControlledCaptureScript = Join-Path $temporaryRoot 'Capture-CommandTrace-CallerControlled.ps1'
        [System.IO.File]::WriteAllText(
            $callerControlledCaptureScript,
            $callerControlledCaptureSource,
            [System.Text.UTF8Encoding]::new($false))

        $callerLegacyFailure = $null
        try {
            $null = Invoke-WindowsNativeArgvProof $callerControlledCaptureScript 'native-helper-inherits-legacy' @($nativeScenarios) $nativeRecorder $nativeCollector 'Legacy'
        }
        catch {
            $callerLegacyFailure = $_.Exception.Message
        }
        Assert-True ($null -ne $callerLegacyFailure -and $callerLegacyFailure -match "Native argv case 'native-roundtrip' changed") "Caller Legacy mode did not corrupt the old helper's native argv boundary. Observed failure: $callerLegacyFailure"

        $nativeEvidence = @(Invoke-WindowsNativeArgvProof $testCaptureScript 'native-caller-legacy' @($nativeScenarios) $nativeRecorder $nativeCollector 'Legacy')
        Assert-True ($nativeEvidence.Count -eq $nativeScenarios.Count) 'The Windows native argv proof did not return every case record.'
        $evidenceText = $nativeEvidence | ForEach-Object { "$($_.Name):$($_.ArgumentCount)args/$($_.EncodedLength)chars/pid=$($_.ProcessId)" }
        Write-Host "Windows native argv proof passed under $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion) with caller mode Legacy and helper mode Standard: $($evidenceText -join '; '). Encoder mutation rejected: $mutationFailure Caller-mode mutation rejected: $callerLegacyFailure" -ForegroundColor Green
    }

    foreach ($failureMode in @('version-nonzero', 'version-empty', 'help-nonzero', 'help-missing-capability')) {
        $modeRunDirectory = Join-Path $temporaryRoot "run-$failureMode"
        $modeSpecPath = Join-Path $temporaryRoot "spec-$failureMode.json"
        $modeSpec = [ordered]@{
            scenarios = @([ordered]@{ name = 'probe'; command = $pwshPath; argumentList = @() })
            iterations = 1
            profile = 'startup'
            cpuSampleMSec = 1.0
            outputDirectory = $modeRunDirectory
            workingDirectory = $workingDirectory
            filtracePath = $fakeFiltrace
        }
        [System.IO.File]::WriteAllText(
            $modeSpecPath,
            ($modeSpec | ConvertTo-Json -Depth 6),
            [System.Text.UTF8Encoding]::new($false))
        $env:FILTRACE_COMMAND_MODE = $failureMode
        $modeResult = Invoke-CaptureChild $testCaptureScript $modeSpecPath
        Assert-True ($modeResult.ExitCode -ne 0) "Native preflight mode '$failureMode' was accepted."
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $modeRunDirectory 'manifest.json'))) "Native preflight mode '$failureMode' wrote a manifest."
    }

    $invalidResultModes = @(
        'malformed-collect',
        'missing-invocation-field',
        'noninteger-ordinal',
        'zero-process-id',
        'short-invocation-count',
        'nonarray-invocations',
        'duplicate-ordinal',
        'noninteger-exit-code',
        'invalid-timestamp',
        'reversed-timestamps')
    foreach ($invalidResultMode in $invalidResultModes) {
        $invalidRunDirectory = Join-Path $temporaryRoot "run-$invalidResultMode"
        $invalidSpecPath = Join-Path $temporaryRoot "spec-$invalidResultMode.json"
        $invalidSpec = [ordered]@{
            scenarios = @([ordered]@{ name = $invalidResultMode; command = $pwshPath; argumentList = @() })
            iterations = if ($invalidResultMode -eq 'nonarray-invocations') { 1 } else { 2 }
            profile = 'startup'
            cpuSampleMSec = 1.0
            outputDirectory = $invalidRunDirectory
            workingDirectory = $workingDirectory
            filtracePath = $fakeFiltrace
        }
        [System.IO.File]::WriteAllText(
            $invalidSpecPath,
            ($invalidSpec | ConvertTo-Json -Depth 6),
            [System.Text.UTF8Encoding]::new($false))
        $env:FILTRACE_COMMAND_MODE = $invalidResultMode
        $invalidResult = Invoke-CaptureChild $testCaptureScript $invalidSpecPath
        Assert-True ($invalidResult.ExitCode -ne 0) "Invalid collect result mode '$invalidResultMode' was accepted as success."

        $invalidManifestPath = Join-Path $invalidRunDirectory 'manifest.json'
        Assert-True (Test-Path -LiteralPath $invalidManifestPath) "Invalid collect result mode '$invalidResultMode' lost its diagnostic manifest."
        $invalidManifest = Get-Content -LiteralPath $invalidManifestPath -Raw | ConvertFrom-Json
        Assert-True (@($invalidManifest.cases).Count -eq 0) "Invalid collect result mode '$invalidResultMode' fabricated a successful case."
        Assert-True (@($invalidManifest.failedCases).Count -eq 1) "Invalid collect result mode '$invalidResultMode' lost its failed-case diagnostic."
        Assert-True ($invalidManifest.failedCases[0].status -eq 'invalidResult') "Invalid collect result mode '$invalidResultMode' recorded the wrong failure status."
        Assert-True (-not [string]::IsNullOrWhiteSpace($invalidManifest.failedCases[0].diagnostic)) "Invalid collect result mode '$invalidResultMode' recorded an empty diagnostic."
        Assert-True ($null -eq $invalidManifest.failedCases[0].PSObject.Properties['invocations']) "Invalid collect result mode '$invalidResultMode' fabricated invocation roots."
    }
    Remove-Item Env:FILTRACE_COMMAND_MODE -ErrorAction SilentlyContinue

    $reusedRunDirectory = Join-Path $temporaryRoot 'reused-run'
    New-Item -ItemType Directory -Path $reusedRunDirectory | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $reusedRunDirectory 'stale.etl'), 'stale')
    $env:FILTRACE_CAPTURE_SCRIPT = $testCaptureScript
    $env:FILTRACE_CAPTURE_RUN = $reusedRunDirectory
    $env:FILTRACE_CAPTURE_TOOL = $fakeFiltrace
    $env:FILTRACE_CAPTURE_COMMAND = $pwshPath
    $reusedOutput = & pwsh -NoProfile -Command @'
& $env:FILTRACE_CAPTURE_SCRIPT `
    -Scenario @(@{ Name = 'reused'; Command = $env:FILTRACE_CAPTURE_COMMAND; ArgumentList = @() }) `
    -OutputDirectory $env:FILTRACE_CAPTURE_RUN `
    -FiltracePath $env:FILTRACE_CAPTURE_TOOL
'@ 2>&1 | Out-String
    $reusedExitCode = $LASTEXITCODE
    Assert-True ($reusedExitCode -ne 0) 'A nonempty command-capture run directory was reused.'
    Assert-True ($reusedOutput -match 'is not empty') 'Run-directory reuse did not report the stale-artifact boundary.'

    $falseElevatedDefinition = 'function Test-Elevated { return $false }'
    $fakeStartProcess = @'
function Start-Process {
    param(
        [string]$FilePath,
        [string]$Verb,
        [switch]$PassThru,
        [string]$WorkingDirectory,
        [object[]]$ArgumentList)
    if ($Verb -cne 'RunAs') { throw 'The elevation fake expected Verb RunAs.' }
    [System.IO.File]::WriteAllText(
        $env:FILTRACE_START_PROCESS_CALL,
        ([ordered]@{
            filePath = $FilePath
            workingDirectory = $WorkingDirectory
            argumentList = @($ArgumentList)
        } | ConvertTo-Json -Depth 4),
        [System.Text.UTF8Encoding]::new($false))
    if ($env:FILTRACE_PROCESS_MODE -eq 'null') { return $null }
    if ($env:FILTRACE_PROCESS_MODE -eq 'start-throw') { throw 'fake Start-Process failure' }
    $logIndex = [Array]::IndexOf($ArgumentList, '-LogFile')
    if ($logIndex -ge 0) {
        $logPath = ([string]$ArgumentList[$logIndex + 1]).Trim('"')
        [System.IO.File]::WriteAllLines($logPath, @(0..204 | ForEach-Object { "log-line-$_" }))
    }
    $process = [pscustomobject]@{}
    if ($env:FILTRACE_PROCESS_MODE -eq 'exitcode-throw') {
        $process | Add-Member ScriptProperty ExitCode { throw 'fake exit-code access failure' }
    }
    else {
        $exitCode = if ($env:FILTRACE_PROCESS_MODE -eq 'nonzero') { 17 } else { 0 }
        $process | Add-Member NoteProperty ExitCode $exitCode
    }
    $process | Add-Member ScriptMethod WaitForExit {
        param([int]$Milliseconds)
        [System.IO.File]::WriteAllText($env:FILTRACE_WAIT_MS, [string]$Milliseconds)
        if ($env:FILTRACE_PROCESS_MODE -eq 'wait-throw') { throw 'fake wait failure' }
        return $env:FILTRACE_PROCESS_MODE -notin @('timeout', 'wait-throw')
    }
    $process | Add-Member ScriptMethod Dispose {
        [System.IO.File]::WriteAllText($env:FILTRACE_PROCESS_DISPOSED, 'disposed')
    }
    return $process
}
'@
    $testParentSource =
        $captureSource.Substring(0, $testElevatedDefinition.Extent.StartOffset) +
        $falseElevatedDefinition + [Environment]::NewLine + $fakeStartProcess +
        $captureSource.Substring($testElevatedDefinition.Extent.EndOffset)
    $testParentSource = $testParentSource.Replace('if ($IsWindows -eq $false)', 'if ($false)')
    $testParentScript = Join-Path $temporaryRoot 'Capture-CommandTrace-Parent.ps1'
    [System.IO.File]::WriteAllText(
        $testParentScript,
        $testParentSource,
        [System.Text.UTF8Encoding]::new($false))

    foreach ($processMode in @('timeout', 'wait-throw', 'null', 'start-throw', 'exitcode-throw', 'nonzero')) {
        $parentRunDirectory = Join-Path $temporaryRoot "parent [case] $processMode run"
        $parentSpecPath = Join-Path $temporaryRoot "parent-$processMode.json"
        $parentSpec = [ordered]@{
            scenarios = @([ordered]@{ name = 'parent'; command = $pwshPath; argumentList = @() })
            iterations = 1
            profile = 'startup'
            cpuSampleMSec = 1.0
            outputDirectory = $parentRunDirectory
            workingDirectory = $workingDirectory
            filtracePath = $fakeFiltrace
        }
        [System.IO.File]::WriteAllText(
            $parentSpecPath,
            ($parentSpec | ConvertTo-Json -Depth 6),
            [System.Text.UTF8Encoding]::new($false))
        $env:FILTRACE_PROCESS_MODE = $processMode
        $env:FILTRACE_START_PROCESS_CALL = Join-Path $temporaryRoot "start-$processMode.json"
        $env:FILTRACE_WAIT_MS = Join-Path $temporaryRoot "wait-$processMode.txt"
        $env:FILTRACE_PROCESS_DISPOSED = Join-Path $temporaryRoot "disposed-$processMode.txt"
        $parentResult = Invoke-CaptureChild $testParentScript $parentSpecPath
        if ($processMode -eq 'nonzero') {
            Assert-True ($parentResult.ExitCode -eq 17) 'The elevated child nonzero exit code was not propagated.'
        }
        else {
            Assert-True ($parentResult.ExitCode -ne 0) "Fake elevation mode '$processMode' was accepted."
        }
        if ($processMode -in @('timeout', 'wait-throw', 'exitcode-throw', 'nonzero')) {
            Assert-True (Test-Path -LiteralPath $env:FILTRACE_PROCESS_DISPOSED) "Fake elevation mode '$processMode' did not dispose its process handle.`n$($parentResult.Output)"
        }
        if ($processMode -in @('timeout', 'wait-throw')) {
            Assert-True ((Get-Content -LiteralPath $env:FILTRACE_WAIT_MS -Raw) -eq '1800000') "Fake elevation mode '$processMode' did not use the bounded wait."
            Assert-True ($parentResult.Output -match 'child may still be running') "Fake elevation mode '$processMode' overstated child termination."
            $observedLogTail = @($parentResult.Output -split "`r?`n" | Where-Object { $_ -match '^log-line-\d+$' })
            Assert-True ($observedLogTail.Count -eq 200) "Fake elevation mode '$processMode' did not emit exactly the bounded 200-line log tail."
            Assert-True ($observedLogTail[0] -ceq 'log-line-5') "Fake elevation mode '$processMode' emitted content before the bounded log tail."
            Assert-True ($observedLogTail[-1] -ceq 'log-line-204') "Fake elevation mode '$processMode' did not surface the end of the log tail."
        }
        if ($processMode -ne 'start-throw') {
            $startCall = Get-Content -LiteralPath $env:FILTRACE_START_PROCESS_CALL -Raw | ConvertFrom-Json
            Assert-True ($startCall.filePath -eq (Get-Process -Id $PID).Path) "Fake elevation mode '$processMode' did not reuse the current host."
            Assert-True ($startCall.workingDirectory -eq [System.IO.Path]::GetFullPath($workingDirectory)) "Fake elevation mode '$processMode' lost the original working directory."
            $generatedSpecPath = Join-Path $parentRunDirectory 'scenarios.json'
            Assert-True (@($startCall.argumentList) -contains ('"' + $generatedSpecPath + '"')) "Fake elevation mode '$processMode' did not preserve the quoted spec path."
            $expectedDigestIndex = [Array]::IndexOf([object[]]$startCall.argumentList, '-ExpectedSpecSha256')
            Assert-True ($expectedDigestIndex -ge 0) "Fake elevation mode '$processMode' omitted the parent-generated spec digest."
            $generatedSpecSha256 = (Get-FileHash -LiteralPath $generatedSpecPath -Algorithm SHA256).Hash.ToLowerInvariant()
            Assert-True ([string]$startCall.argumentList[$expectedDigestIndex + 1] -ceq $generatedSpecSha256) "Fake elevation mode '$processMode' did not pass the exact generated spec-byte digest."
        }
    }

    $global:LASTEXITCODE = 0
    Write-Host 'Capture-CommandTrace contract checks passed.' -ForegroundColor Green
}
finally {
    foreach ($environmentVariableName in $environmentVariableNames) {
        $originalEnvironmentVariable = $originalEnvironmentVariables[$environmentVariableName]
        if ($originalEnvironmentVariable.Exists) {
            Set-Item -LiteralPath "Env:$environmentVariableName" -Value $originalEnvironmentVariable.Value
        }
        else {
            Remove-Item -LiteralPath "Env:$environmentVariableName" -ErrorAction SilentlyContinue
        }
    }
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}