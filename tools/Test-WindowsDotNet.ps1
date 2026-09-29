#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Runs the complete Release suite without overlapping Windows ETW test hosts.

.DESCRIPTION
  Runs non-ETW test modules concurrently, then runs the CLI module alone.
  Resolves the test projects from the solution so a newly added test project
  cannot silently fall outside the Windows gate.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'filtrace.slnx'
$testsRoot = Join-Path $root 'tests'

[string[]] $projects = @(& dotnet sln $solution list)
if ($LASTEXITCODE -ne 0) { throw 'Could not list projects in filtrace.slnx.' }
[string[]] $testModules = @(
    foreach ($project in $projects) {
        if ($project -notmatch '^tests[\\/].+\.csproj$') { continue }
        [string] $name = [System.IO.Path]::GetFileNameWithoutExtension($project)
        if (-not $name.EndsWith('.Tests', [StringComparison]::Ordinal)) {
            throw "Unexpected test project name in solution: $project"
        }
        [string] $binary = Join-Path $root (
            "tests\$name\bin\Release\net10.0\$name.dll")
        if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw "Release test module was not built: $binary"
        }
        $name
    }
)
[string[]] $cliModules = @($testModules | Where-Object { $_ -ceq 'Filtrace.Cli.Tests' })
[string[]] $otherModules = @($testModules | Where-Object { $_ -cne 'Filtrace.Cli.Tests' })
if ($cliModules.Count -ne 1 -or $otherModules.Count -lt 1 -or
    $cliModules.Count + $otherModules.Count -ne $testModules.Count) {
    throw 'Could not partition the complete solution test modules around the CLI ETW host.'
}

function Invoke-TestModuleGroup(
    [string[]] $Names,
    [int] $Parallelism,
    [int] $MinimumExpectedTests) {
    [string] $selection = (@($Names | ForEach-Object {
                "*/bin/Release/net10.0/$_.dll"
            }) -join ';')
    [object[]] $lines = @()
    & dotnet test --test-modules $selection --root-directory $testsRoot `
        --max-parallel-test-modules $Parallelism `
        --minimum-expected-tests $MinimumExpectedTests --no-ansi 2>&1 |
        Tee-Object -Variable lines | Out-Host
    [int] $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Test modules '$($Names -join ', ')' failed with exit code $exitCode."
    }

    [string[]] $observed = @($lines | ForEach-Object {
            if ($_ -match '^Running tests from (.*?\.dll) \(') {
                [System.IO.Path]::GetFileNameWithoutExtension($Matches[1])
            }
        })
    if (@(Compare-Object ($Names | Sort-Object) ($observed | Sort-Object)).Count -ne 0) {
        throw "Test modules '$($Names -join ', ')' did not all execute: $($observed -join ', ')."
    }
}

# All test projects still run on Windows. Only the CLI test module starts ETW
# sessions; wait for the non-ETW hosts to exit before launching it.
Invoke-TestModuleGroup -Names $otherModules -Parallelism 4 -MinimumExpectedTests 1514
Invoke-TestModuleGroup -Names $cliModules -Parallelism 1 -MinimumExpectedTests 399
Write-Host "Windows Release test gate passed $($testModules.Count) modules."
