# Copyright (c) Jeremy W Kuhne and contributors
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ReleaseVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $TagName
    )

    [string] $number = '(0|[1-9][0-9]*)'
    [string] $identifier = '(0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
    [string] $pattern = "^v$number\.$number\.$number(?:-$identifier(?:\.$identifier)*)?\z"
    if ($TagName -cnotmatch $pattern) {
        throw "Release tag '$TagName' must be v followed by a canonical semantic version without build metadata."
    }

    return $TagName.Substring(1)
}

function Get-ReleaseMergeCandidate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $CommitSha,
        [Parameter(Mandatory)][string] $Repository,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $PullRequests
    )

    [object[]] $matches = @($PullRequests | Where-Object {
            $_.state -ceq 'closed' -and
            -not [string]::IsNullOrWhiteSpace([string]$_.merged_at) -and
            $_.merge_commit_sha -ceq $CommitSha -and
            $_.base.ref -ceq 'main' -and
            $_.base.repo.full_name -ceq $Repository
        })
    if ($matches.Count -ne 1) {
        throw "Release commit '$CommitSha' must identify exactly one merged pull request targeting '$Repository/main'."
    }

    [object] $candidate = $matches[0]
    if ($candidate.head.sha -cnotmatch '^[0-9a-f]{40}$' -or
        ($candidate.number -isnot [long] -and $candidate.number -isnot [int]) -or
        $candidate.number -le 0) {
        throw 'The merged pull request has malformed head identity or number.'
    }

    return $candidate
}

function Assert-ReleaseCiEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $HeadSha,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $CommitTree,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $HeadTree,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $CheckRuns
    )

    if ($CommitTree -cne $HeadTree) {
        throw 'The release tree differs from the reviewed pull-request head; its CI cannot authorize these bytes.'
    }

    foreach ($name in @('agent files', 'build (Linux ARM64)', 'build (Windows)', 'ci')) {
        [object[]] $matches = @($CheckRuns | Where-Object {
                $_.name -ceq $name -and $_.app.slug -ceq 'github-actions'
            })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one latest GitHub Actions '$name' check for the reviewed head."
        }

        [object] $check = $matches[0]
        if ($check.head_sha -cne $HeadSha -or
            $check.status -cne 'completed' -or
            $check.conclusion -cne 'success') {
            throw "Release check '$name' did not succeed on the exact reviewed head."
        }
    }
}

function Get-ReleaseReadinessEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $TagName,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $CommitSha,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
        [Parameter(Mandatory)][scriptblock] $Query
    )

    [string] $version = Get-ReleaseVersion -TagName $TagName
    [string] $actualCommit = & $Query 'git' @('rev-parse', 'HEAD') $false
    if ($actualCommit -cne $CommitSha) { throw 'The checked-out commit differs from the release candidate.' }
    [string] $dirty = & $Query 'git' @('status', '--porcelain', '--untracked-files=normal') $true
    if (-not [string]::IsNullOrWhiteSpace($dirty)) { throw 'Release verification requires a clean source worktree.' }

    [string] $comparison = & $Query 'gh' @(
        'api', "repos/$Repository/compare/$CommitSha...main",
        '--jq', '.merge_base_commit.sha') $false
    if ($comparison -cne $CommitSha) { throw 'The release candidate is not in main history.' }

    [string] $pullRequestJson = & $Query 'gh' @(
        'api', "repos/$Repository/commits/$CommitSha/pulls?per_page=100",
        '--jq', '[.[] | {number,state,merged_at,merge_commit_sha,head:{sha:.head.sha},base:{ref:.base.ref,repo:{full_name:.base.repo.full_name}}}]') $false
    [object[]] $pullRequests = @($pullRequestJson | ConvertFrom-Json -Depth 20)
    if ($pullRequests.Count -ge 100) { throw 'Associated PR evidence is incomplete at its page limit.' }
    [object] $candidate = Get-ReleaseMergeCandidate -CommitSha $CommitSha -Repository $Repository -PullRequests $pullRequests
    [string] $headSha = $candidate.head.sha

    [string] $headTree = & $Query 'gh' @(
        'api', "repos/$Repository/git/commits/$headSha", '--jq', '.tree.sha') $false
    [string] $commitTree = & $Query 'git' @('rev-parse', 'HEAD^{tree}') $false
    [string] $checkJson = & $Query 'gh' @(
        'api', "repos/$Repository/commits/$headSha/check-runs?filter=latest&per_page=100",
        '--jq', '{total_count,check_runs:[.check_runs[]|{name,head_sha,status,conclusion,app:{slug:.app.slug}}]}') $false
    [object] $checks = $checkJson | ConvertFrom-Json -Depth 12
    if (($checks.total_count -isnot [long] -and $checks.total_count -isnot [int]) -or
        $checks.total_count -ne @($checks.check_runs).Count) {
        throw 'Latest check-run evidence is malformed or incomplete.'
    }
    Assert-ReleaseCiEvidence -HeadSha $headSha -CommitTree $commitTree -HeadTree $headTree -CheckRuns @($checks.check_runs)

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        repository = $Repository
        tag = $TagName
        version = $version
        commit = $CommitSha
        tree = $commitTree
        pullRequest = $candidate.number
        reviewedHead = $headSha
        requiredChecks = @('agent files', 'build (Linux ARM64)', 'build (Windows)', 'ci')
    }
}

Export-ModuleMember -Function Get-ReleaseVersion, Get-ReleaseMergeCandidate, Assert-ReleaseCiEvidence, Get-ReleaseReadinessEvidence
