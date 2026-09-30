#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) Jeremy W Kuhne and contributors
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseReadiness.psm1') -Force

function Assert-Rejected([scriptblock] $Action, [string] $Message) {
    [bool] $rejected = $false
    try { & $Action | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw $Message }
}

function Copy-Evidence([object] $Value) {
    return ($Value | ConvertTo-Json -Depth 12 | ConvertFrom-Json -Depth 12)
}

foreach ($version in @('0.10.0', '1.2.3-beta.1', '0.10.0-rc.1')) {
    if ((Get-ReleaseVersion "v$version") -cne $version) { throw 'A valid release tag did not retain its exact version.' }
}
foreach ($tag in @('main', 'v', 'v01.2.3', 'v0.10', 'v0.10.0+build', 'v0.10.0-01', "v0.10.0`n")) {
    Assert-Rejected { Get-ReleaseVersion $tag } "Invalid tag '$tag' was accepted."
}

[string] $commit = 'a' * 40
[string] $head = 'b' * 40
[string] $tree = 'c' * 40
[string] $repository = 'JeremyKuhne/filtrace'
[object] $pullRequest = [pscustomobject]@{
    number = 10
    state = 'closed'
    merged_at = '2026-09-30T00:00:00Z'
    merge_commit_sha = $commit
    head = [pscustomobject]@{ sha = $head }
    base = [pscustomobject]@{ ref = 'main'; repo = [pscustomobject]@{ full_name = $repository } }
}
if ((Get-ReleaseMergeCandidate $commit $repository @($pullRequest)).number -ne 10) {
    throw 'Valid squash-merge provenance was rejected.'
}
Assert-Rejected { Get-ReleaseMergeCandidate $commit $repository @() } 'An unassociated commit was accepted.'
Assert-Rejected { Get-ReleaseMergeCandidate $commit $repository @($pullRequest, $pullRequest) } 'Ambiguous PR identity was accepted.'
foreach ($mutation in @('open', 'unmerged', 'wrong-commit', 'wrong-base', 'wrong-repository', 'malformed-head', 'malformed-number')) {
    [object] $changed = Copy-Evidence $pullRequest
    switch ($mutation) {
        'open' { $changed.state = 'open' }
        'unmerged' { $changed.merged_at = $null }
        'wrong-commit' { $changed.merge_commit_sha = 'd' * 40 }
        'wrong-base' { $changed.base.ref = 'feature' }
        'wrong-repository' { $changed.base.repo.full_name = 'owner/fork' }
        'malformed-head' { $changed.head.sha = 'not-a-sha' }
        'malformed-number' { $changed.number = '10' }
    }
    Assert-Rejected { Get-ReleaseMergeCandidate $commit $repository @($changed) } "PR evidence '$mutation' was accepted."
}

[object[]] $checks = @('agent files', 'build (Linux ARM64)', 'build (Windows)', 'ci') | ForEach-Object {
    [pscustomobject]@{
        name = $_
        head_sha = $head
        status = 'completed'
        conclusion = 'success'
        app = [pscustomobject]@{ slug = 'github-actions' }
    }
}
Assert-ReleaseCiEvidence $head $tree $tree $checks
Assert-Rejected { Assert-ReleaseCiEvidence $head $tree ('d' * 40) $checks } 'A different release tree reused PR CI.'
Assert-Rejected { Assert-ReleaseCiEvidence $head $tree $tree @() } 'Missing CI was accepted.'
Assert-Rejected { Assert-ReleaseCiEvidence $head $tree $tree @($checks + $checks[3]) } 'Ambiguous latest CI was accepted.'
foreach ($index in 0..3) {
    foreach ($mutation in @('missing', 'wrong-head', 'pending', 'failure', 'cancelled', 'skipped', 'wrong-app', 'malformed')) {
        [object[]] $changed = Copy-Evidence $checks
        switch ($mutation) {
            'missing' { $changed = @($changed | Where-Object name -CNE $checks[$index].name) }
            'wrong-head' { $changed[$index].head_sha = 'd' * 40 }
            'pending' { $changed[$index].status = 'in_progress' }
            'failure' { $changed[$index].conclusion = 'failure' }
            'cancelled' { $changed[$index].conclusion = 'cancelled' }
            'skipped' { $changed[$index].conclusion = 'skipped' }
            'wrong-app' { $changed[$index].app.slug = 'other-app' }
            'malformed' { $changed[$index] = [pscustomobject]@{ name = $checks[$index].name } }
        }
        Assert-Rejected { Assert-ReleaseCiEvidence $head $tree $tree $changed } "CI evidence '$mutation' for '$($checks[$index].name)' was accepted."
    }
}

function New-ReleaseQueryWitness([string] $Mode) {
    [object] $prWitness = Copy-Evidence $pullRequest
    [object[]] $checkWitness = Copy-Evidence $checks
    [string] $commitWitness = $commit
    [string] $treeWitness = $tree
    return {
        param([string] $Tool, [string[]] $Arguments, [bool] $AllowEmpty)
        if ($Mode -ceq 'native-failure') { throw 'The read-only query failed.' }
        if ($Tool -ceq 'git') {
            if ($Arguments[0] -ceq 'status') {
                if (-not $AllowEmpty) { throw 'A valid empty status was not permitted.' }
                if ($Mode -ceq 'dirty') { return ' M changed.cs' }
                return ''
            }
            if ($Arguments[1] -ceq 'HEAD') {
                if ($Mode -ceq 'wrong-checkout') { return 'd' * 40 }
                return $commitWitness
            }
            if ($Arguments[1] -ceq 'HEAD^{tree}') { return $treeWitness }
            throw 'Unexpected git query.'
        }
        if ($Arguments[1].Contains('/compare/')) {
            if ($Mode -ceq 'outside-main') { return 'd' * 40 }
            return $commitWitness
        }
        if ($Arguments[1].Contains('/pulls?')) {
            if ($Mode -ceq 'malformed-pr') { return '{' }
            if ($Mode -ceq 'missing-pr') { return '[]' }
            return ConvertTo-Json -InputObject @($prWitness) -Depth 12 -Compress
        }
        if ($Arguments[1].Contains('/git/commits/')) {
            if ($Mode -ceq 'changed-tree') { return 'd' * 40 }
            return $treeWitness
        }
        if ($Arguments[1].Contains('/check-runs?')) {
            if ($Mode -ceq 'malformed-checks') { return '{' }
            [int] $total = if ($Mode -ceq 'incomplete-checks') { 5 } else { 4 }
            return [pscustomobject]@{ total_count = $total; check_runs = $checkWitness } |
                ConvertTo-Json -Depth 12 -Compress
        }
        throw 'Unexpected GitHub query.'
    }.GetNewClosure()
}

[object] $evidence = Get-ReleaseReadinessEvidence `
    -TagName v0.10.0 -CommitSha $commit -Repository $repository -Query (New-ReleaseQueryWitness valid)
if ($evidence.version -cne '0.10.0' -or $evidence.reviewedHead -cne $head -or $evidence.tree -cne $tree) {
    throw 'The positive release query witness did not retain exact version/head/tree evidence.'
}
foreach ($mode in @('native-failure', 'dirty', 'wrong-checkout', 'outside-main', 'malformed-pr',
        'missing-pr', 'changed-tree', 'malformed-checks', 'incomplete-checks')) {
    [scriptblock] $queryWitness = New-ReleaseQueryWitness $mode
    Assert-Rejected {
        Get-ReleaseReadinessEvidence -TagName v0.10.0 -CommitSha $commit -Repository $repository -Query $queryWitness
    } "Release query witness '$mode' was accepted."
}
Write-Host 'Release provenance and exact-tree CI contract passed.'
