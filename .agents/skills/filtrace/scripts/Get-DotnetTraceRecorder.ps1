# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

function Get-DotnetTraceRecorder(
    [string]$CommandPath,
    [ValidateSet('cpu', 'alloc')]
    [string]$MetricName,
    [scriptblock]$InvokeText = $null) {
    if ($null -eq $InvokeText) {
        $versionOutput = (& $CommandPath --version 2>&1 | Out-String).Trim()
        $versionExitCode = $LASTEXITCODE
        if ($versionExitCode -ne 0) {
            throw "dotnet-trace --version failed (exit $versionExitCode)."
        }
    }
    else {
        $versionOutput = (& $InvokeText $CommandPath ([string[]]@('--version')) 'dotnet-trace --version').Trim()
    }

    $versionMatch = [regex]::Match(
        $versionOutput,
        '(?<!\d)\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?')
    if (-not $versionMatch.Success) {
        throw 'dotnet-trace --version returned no semantic version.'
    }

    if ($null -eq $InvokeText) {
        $profileOutput = & $CommandPath list-profiles 2>&1 | Out-String
        $profileExitCode = $LASTEXITCODE
        if ($profileExitCode -ne 0) {
            throw "dotnet-trace list-profiles failed (exit $profileExitCode)."
        }
    }
    else {
        $profileOutput = & $InvokeText $CommandPath ([string[]]@('list-profiles')) 'dotnet-trace list-profiles'
    }

    $profiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in ($profileOutput -split "`r?`n")) {
        $match = [regex]::Match(
            $line,
            '^\s*([A-Za-z0-9][A-Za-z0-9._-]{0,127})(?:\s+\(([^)]+)\))?\s+-\s')
        if (-not $match.Success) { continue }

        $appliesTo = $match.Groups[2].Value
        if ($appliesTo -and -not [string]::Equals($appliesTo, 'collect', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        [void]$profiles.Add($match.Groups[1].Value)
        if ($profiles.Count -gt 128) {
            throw 'dotnet-trace list-profiles returned more than 128 collect profiles.'
        }
    }

    $availableProfiles = @($profiles | Sort-Object)
    if ($availableProfiles.Count -eq 0) {
        throw 'dotnet-trace list-profiles returned no profiles that apply to collect.'
    }

    $effectiveProfiles = if ($MetricName -eq 'alloc') {
        if (-not $profiles.Contains('gc-verbose')) {
            throw "dotnet-trace does not advertise the required gc-verbose collect profile. Available: $($availableProfiles -join ', ')."
        }

        @('gc-verbose')
    }
    elseif ($profiles.Contains('dotnet-common') -and $profiles.Contains('dotnet-sampled-thread-time')) {
        @('dotnet-common', 'dotnet-sampled-thread-time')
    }
    elseif ($profiles.Contains('cpu-sampling')) {
        @('cpu-sampling')
    }
    else {
        throw "dotnet-trace does not advertise a supported CPU collect profile. Available: $($availableProfiles -join ', ')."
    }

    return [pscustomobject]@{
        Command = $CommandPath
        Version = $versionMatch.Value
        ProfileArgument = $effectiveProfiles -join ','
        Metadata = [ordered]@{
            name = 'dotnet-trace'
            version = $versionMatch.Value
            profiles = @($effectiveProfiles)
        }
    }
}