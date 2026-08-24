#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Contract checks for the replayable Filtrace analysis-record helper.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$analysisScript = Join-Path $root '.agents/skills/filtrace/scripts/Invoke-FiltraceAnalysis.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "filtrace analysis $([Guid]::NewGuid().ToString('N').Substring(0, 10))"
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Write-Json([string] $Path, [System.Collections.IDictionary] $Value) {
    [System.IO.File]::WriteAllText(
        $Path,
        (ConvertTo-Json -InputObject $Value -Depth 12 -Compress),
        $utf8)
}

function Invoke-Analysis(
    [string] $Plan,
    [string] $OutputDirectory,
    [string] $Filtrace,
    [string] $ReplayFrom = '') {
    [System.Collections.Generic.List[string]] $arguments = @(
        '-NoProfile',
        '-File', $analysisScript,
        '-Plan', $Plan,
        '-OutputDirectory', $OutputDirectory,
        '-FiltracePath', $Filtrace,
        '-TimeoutSeconds', '120'
    )
    if ($ReplayFrom) {
        $arguments.Add('-ReplayFrom')
        $arguments.Add($ReplayFrom)
    }

    & (Get-Process -Id $PID).Path @arguments 2>&1 | Out-Host
    return $LASTEXITCODE
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $tokens = $null
    $parseErrors = $null
    $analysisAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $analysisScript,
        [ref] $tokens,
        [ref] $parseErrors)
    Assert-True ($parseErrors.Count -eq 0) 'Analysis-record helper did not parse.'
    $boundaryFunctionNames = @('Limit-Utf8Text', 'New-CaptureErrorJson')
    $boundaryDefinitions = @(
        $analysisAst.FindAll(
            { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $boundaryFunctionNames },
            $true) |
            Sort-Object { $_.Extent.StartOffset } |
            ForEach-Object { $_.Extent.Text }
    )
    Assert-True ($boundaryDefinitions.Count -eq $boundaryFunctionNames.Count) 'Analysis-record boundary functions could not be isolated.'
    . ([scriptblock]::Create(($boundaryDefinitions -join [Environment]::NewLine)))

    [string] $marker = "`n[filtrace analysis record truncated stderr]`n"
    [int] $markerBytes = $utf8.GetByteCount($marker)
    [bool] $truncated = $false
    [string] $longError = [string]::new([char] 'x', $markerBytes + 10)
    [string] $boundedError = Limit-Utf8Text $longError ($markerBytes + 1) ([ref] $truncated)
    Assert-True $truncated 'Stderr over the one-byte payload budget was not marked truncated.'
    Assert-True ($utf8.GetByteCount($boundedError) -le $markerBytes + 1) 'Stderr truncation exceeded its exact byte budget.'
    $markerOnlyRejected = $false
    try { $null = Limit-Utf8Text $longError $markerBytes ([ref] $truncated) }
    catch { $markerOnlyRejected = $true }
    Assert-True $markerOnlyRejected 'A stderr budget with no payload room was accepted.'

    $maxStdoutBytes = 128
    [string] $captureError = New-CaptureErrorJson ([string]::new([char] 'x', 1024))
    Assert-True ($utf8.GetByteCount($captureError) -le $maxStdoutBytes) 'Capture-error fallback exceeded its own byte limit.'
    $null = $captureError | ConvertFrom-Json

    & dotnet build (Join-Path $root 'src/Filtrace/Filtrace.csproj') -c $Configuration --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Filtrace build failed with exit code $LASTEXITCODE."
    }

    [string] $filtraceDirectory = Join-Path $root "src/Filtrace/bin/$Configuration/net10.0"
    [string] $filtrace = Join-Path $filtraceDirectory 'filtrace.exe'
    if (-not (Test-Path -LiteralPath $filtrace -PathType Leaf)) {
        $filtrace = Join-Path $filtraceDirectory 'filtrace'
    }
    Assert-True (Test-Path -LiteralPath $filtrace -PathType Leaf) 'Built Filtrace executable was not found.'

    [string] $inputDirectory = Join-Path $temporaryRoot 'inputs with spaces'
    New-Item -ItemType Directory -Path $inputDirectory | Out-Null
    [string] $cpuInput = Join-Path $inputDirectory 'cpu profile.speedscope.json'
    [string] $allocInput = Join-Path $inputDirectory 'allocation profile.nettrace'
    Copy-Item -LiteralPath (Join-Path $root 'tests/Filtrace.Core.Tests/Fixtures/folding.speedscope.json') -Destination $cpuInput
    Copy-Item -LiteralPath (Join-Path $root 'tests/Filtrace.Core.Tests/Fixtures/alloc.nettrace') -Destination $allocInput

    [string] $planPath = Join-Path $temporaryRoot 'analysis-plan.json'
    [System.Collections.IDictionary] $plan = [ordered] @{
        schemaVersion = 1
        inputs = @(
            [ordered] @{ id = 'cpu'; kind = 'trace'; path = 'inputs with spaces/cpu profile.speedscope.json' }
            [ordered] @{ id = 'alloc'; kind = 'trace'; path = 'inputs with spaces/allocation profile.nettrace' }
        )
        queries = @(
            [ordered] @{
                id = 'orientation'
                operation = 'info'
                inputIds = @('cpu')
                arguments = @('--strict', '--require-enabled', 'cpu', '--require-events', 'cpu')
            }
            [ordered] @{
                id = 'root-cpu'
                operation = 'rank'
                inputIds = @('cpu')
                arguments = @('--metric', 'cpu', '--root', 'MyApp.Work', '--top', '5')
            }
            [ordered] @{
                id = 'callers'
                operation = 'callers'
                inputIds = @('cpu')
                arguments = @('MyApp.Inner', '--root', 'MyApp.Work', '--top', '5')
            }
            [ordered] @{
                id = 'allocation'
                operation = 'rank'
                inputIds = @('alloc')
                arguments = @('--metric', 'alloc', '--top', '5')
            }
        )
    }
    Write-Json $planPath $plan

    [string] $completedDirectory = Join-Path $temporaryRoot 'completed record'
    [int] $completedExit = Invoke-Analysis $planPath $completedDirectory $filtrace
    Assert-True ($completedExit -eq 0) "Completed analysis exited $completedExit."
    [string] $completedRecordPath = Join-Path $completedDirectory 'run.json'
    [object] $completedRecord = Get-Content -LiteralPath $completedRecordPath -Raw | ConvertFrom-Json -Depth 32
    Assert-True ($completedRecord.status -ceq 'completed') 'Completed record did not report completed status.'
    Assert-True (@($completedRecord.queries).Count -eq 4) 'Completed record did not retain all four queries.'
    Assert-True ($completedRecord.inputs[0].path -ceq $cpuInput) 'Resolved input path was not retained.'
    Assert-True ($completedRecord.inputs[0].sha256 -ceq (Get-FileHash -LiteralPath $cpuInput -Algorithm SHA256).Hash) 'CPU input hash drifted.'
    Assert-True (
        [System.Linq.Enumerable]::SequenceEqual(
            [byte[]] [System.IO.File]::ReadAllBytes($planPath),
            [byte[]] [System.IO.File]::ReadAllBytes((Join-Path $completedDirectory 'plan.json')))) `
        'The retained plan bytes differ from the supplied plan.'

    foreach ($query in $completedRecord.queries) {
        Assert-True ($query.status -ceq 'completed') "Query '$($query.id)' did not complete."
        Assert-True ($query.arguments[1] -in @($cpuInput, $allocInput)) "Query '$($query.id)' did not retain its input as one exact argument."
        Assert-True ($query.arguments[-2] -ceq '--format' -and $query.arguments[-1] -ceq 'json') "Query '$($query.id)' did not force JSON output."
        [string] $stdoutPath = Join-Path $completedDirectory $query.stdout
        [string] $stderrPath = Join-Path $completedDirectory $query.stderr
        $null = Get-Content -LiteralPath $stdoutPath -Raw | ConvertFrom-Json -Depth 32
        Assert-True ($query.stdoutSha256 -ceq (Get-FileHash -LiteralPath $stdoutPath -Algorithm SHA256).Hash) "Query '$($query.id)' stdout hash drifted."
        Assert-True ($query.stderrSha256 -ceq (Get-FileHash -LiteralPath $stderrPath -Algorithm SHA256).Hash) "Query '$($query.id)' stderr hash drifted."
    }

    [string] $manifestPath = Join-Path $temporaryRoot 'capture manifest.json'
    Write-Json $manifestPath ([ordered] @{
        schemaVersion = 1
        cases = @([ordered] @{
            id = 'case-one'
            benchmark = 'Bench.Work'
            parameters = ''
            benchmarkDisplay = 'Bench.Work'
            speedscope = 'inputs with spaces/cpu profile.speedscope.json'
        })
    })
    [string] $manifestPlanPath = Join-Path $temporaryRoot 'manifest-plan.json'
    Write-Json $manifestPlanPath ([ordered] @{
        schemaVersion = 1
        inputs = @([ordered] @{
            id = 'case'
            kind = 'manifest'
            path = 'capture manifest.json'
            caseId = 'case-one'
        })
        queries = @([ordered] @{
            id = 'case-rank'
            operation = 'rank'
            inputIds = @('case')
            arguments = @('--root', 'MyApp.Work', '--top', '5')
        })
    })
    [string] $manifestDirectory = Join-Path $temporaryRoot 'manifest record'
    [int] $manifestExit = Invoke-Analysis $manifestPlanPath $manifestDirectory $filtrace
    Assert-True ($manifestExit -eq 0) "Manifest analysis exited $manifestExit."
    [object] $manifestRecord = Get-Content -LiteralPath (Join-Path $manifestDirectory 'run.json') -Raw | ConvertFrom-Json -Depth 32
    Assert-True ($manifestRecord.inputs[0].kind -ceq 'manifest') 'Manifest input kind was not retained.'
    Assert-True ($manifestRecord.inputs[0].caseId -ceq 'case-one') 'Selected manifest case id was not retained.'
    Assert-True ($manifestRecord.inputs[0].sha256 -ceq (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash) 'Manifest hash was not retained.'
    Assert-True ($manifestRecord.inputs[0].dependencies[0].caseId -ceq 'case-one') 'Manifest dependency case id was not retained.'
    Assert-True ($manifestRecord.inputs[0].dependencies[0].sha256 -ceq (Get-FileHash -LiteralPath $cpuInput -Algorithm SHA256).Hash) 'Manifest dependency trace hash was not retained.'
    Assert-True ($manifestRecord.queries[0].arguments -contains '--case-id') 'Case-addressed rank did not retain --case-id.'
    Assert-True (Test-Path -LiteralPath $completedRecordPath) 'A later analysis overwrote the first run record.'

    [string] $rejectedPlanPath = Join-Path $temporaryRoot 'rejected-plan.json'
    Write-Json $rejectedPlanPath ([ordered] @{
        schemaVersion = 1
        inputs = @([ordered] @{ id = 'cpu'; kind = 'trace'; path = 'inputs with spaces/cpu profile.speedscope.json' })
        queries = @([ordered] @{
            id = 'quality-gate'
            operation = 'info'
            inputIds = @('cpu')
            arguments = @('--require-enabled', 'alloc')
        })
    })
    [string] $rejectedDirectory = Join-Path $temporaryRoot 'rejected record'
    [int] $rejectedExit = Invoke-Analysis $rejectedPlanPath $rejectedDirectory $filtrace
    Assert-True ($rejectedExit -eq 3) "Quality rejection exited $rejectedExit instead of 3."
    [object] $rejectedRecord = Get-Content -LiteralPath (Join-Path $rejectedDirectory 'run.json') -Raw | ConvertFrom-Json -Depth 32
    Assert-True ($rejectedRecord.status -ceq 'rejected') 'Run did not retain rejected status.'
    Assert-True ($rejectedRecord.queries[0].status -ceq 'rejected') 'Query did not retain rejected status.'
    [object] $rejectedOutput = Get-Content -LiteralPath (Join-Path $rejectedDirectory $rejectedRecord.queries[0].stdout) -Raw | ConvertFrom-Json -Depth 32
    Assert-True ($rejectedOutput.warnings[0].code -ceq 'required_analysis_unsupported') 'Rejected output did not retain the quality diagnostic.'

    [string] $forbiddenPlanPath = Join-Path $temporaryRoot 'forbidden-plan.json'
    Write-Json $forbiddenPlanPath ([ordered] @{
        schemaVersion = 1
        inputs = @([ordered] @{ id = 'cpu'; kind = 'trace'; path = 'inputs with spaces/cpu profile.speedscope.json' })
        queries = @([ordered] @{ id = 'write'; operation = 'export'; inputIds = @('cpu'); arguments = @() })
    })
    [string] $forbiddenDirectory = Join-Path $temporaryRoot 'forbidden record'
    [int] $forbiddenExit = Invoke-Analysis $forbiddenPlanPath $forbiddenDirectory $filtrace
    Assert-True ($forbiddenExit -ne 0) 'Forbidden export analysis unexpectedly succeeded.'
    Assert-True (-not (Test-Path -LiteralPath $forbiddenDirectory)) 'Forbidden plan created an output directory before validation failed.'

    Add-Content -LiteralPath $cpuInput -Value 'mutation'
    [string] $replayDirectory = Join-Path $temporaryRoot 'replay record'
    [int] $replayExit = Invoke-Analysis $planPath $replayDirectory $filtrace $completedRecordPath
    Assert-True ($replayExit -ne 0) 'Replay unexpectedly accepted mutated trace bytes.'
    Assert-True (-not (Test-Path -LiteralPath $replayDirectory)) 'Replay created an output directory before input hashes were verified.'

    $global:LASTEXITCODE = 0
    Write-Host 'Filtrace analysis-record contract passed.' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}