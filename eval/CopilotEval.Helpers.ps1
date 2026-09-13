#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

function Get-AgentEvalFileHash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-AgentEvalTextHash([string] $Text) {
    [byte[]] $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-AgentEvalResultText($Result) {
    if ($Result -is [string]) { return [string]$Result }
    if ($null -eq $Result) { return $null }

    $members = @($Result.PSObject.Properties.Name)
    if ($members -contains 'text' -and $Result.text -is [string]) {
        return [string]$Result.text
    }
    if ($members -contains 'content' -and $Result.content -is [string]) {
        return [string]$Result.content
    }
    return $null
}

function Get-AgentEvalSkillSource([string] $Path) {
    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path
    if ($file.Length -gt 16MB) { throw "Skill entry point '$Path' exceeds 16777216 bytes." }

    [byte[]] $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    [int] $preambleLength = if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    [System.Text.UTF8Encoding] $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    [string] $text = $utf8.GetString($bytes, $preambleLength, $bytes.Length - $preambleLength)
    [System.Collections.Generic.List[int]] $lineStarts = [System.Collections.Generic.List[int]]::new()
    if ($text.Length -gt 0) {
        $lineStarts.Add(0)
        for ($index = 0; $index -lt ($text.Length - 1); $index++) {
            if ($text[$index] -eq "`n") { $lineStarts.Add($index + 1) }
        }
    }
    [bool] $terminalNewline = $text.EndsWith("`n", [StringComparison]::Ordinal)
    return [pscustomobject]@{
        path = $file.FullName
        bytes = $bytes.Length
        byteSha256 = Get-AgentEvalFileHash $file.FullName
        text = $text
        textSha256 = Get-AgentEvalTextHash $text
        lineStarts = $lineStarts.ToArray()
        lineCount = $lineStarts.Count
        renderedLineCount = $lineStarts.Count + $(if ($terminalNewline) { 1 } else { 0 })
        terminalNewline = $terminalNewline
    }
}

function Get-AgentEvalSkillViewRequest($Arguments, $Source) {
    if ($null -eq $Arguments -or $Arguments -is [string] -or $Arguments -is [ValueType]) {
        throw 'Skill view arguments were not an object.'
    }
    [string[]] $members = @($Arguments.PSObject.Properties.Name)
    if ($members.Count -lt 1 -or $members.Count -gt 2 -or
        $members -notcontains 'path' -or
        @($members | Where-Object { $_ -notin @('path', 'view_range') }).Count -ne 0 -or
        $Arguments.path -isnot [string] -or
        -not [System.IO.Path]::IsPathFullyQualified([string]$Arguments.path) -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath([string]$Arguments.path),
            [string]$Source.path,
            [StringComparison]::Ordinal)) {
        throw 'Skill view path was outside protocol.'
    }

    [int] $startLine = 1
    [int] $endLine = -1
    [long] $maximumEndLine = [long]$Source.lineCount + 512
    [bool] $explicitRange = $members -contains 'view_range'
    if ($explicitRange) {
        if ($null -eq $Arguments.view_range -or $Arguments.view_range -is [string] -or
            $Arguments.view_range -is [ValueType]) {
            throw 'Skill view range was not a two-element array.'
        }
        [object[]] $range = @($Arguments.view_range)
        if ($range.Count -ne 2 -or
            ($range[0] -isnot [int] -and $range[0] -isnot [long]) -or
            ($range[1] -isnot [int] -and $range[1] -isnot [long]) -or
            [long]$range[0] -lt 1 -or [long]$range[0] -gt [int]::MaxValue -or
            [long]$range[1] -lt -1 -or [long]$range[1] -gt $maximumEndLine) {
            throw 'Skill view range was outside protocol.'
        }
        $startLine = [int][long]$range[0]
        $endLine = [int][long]$range[1]
    }
    if ($Source.lineCount -lt 1 -or $startLine -gt [int]$Source.lineCount -or
        ($endLine -ne -1 -and $endLine -lt $startLine)) {
        throw 'Skill view range was outside the source line bounds.'
    }

    [int] $clampedEndLine = if ($endLine -eq -1) { [int]$Source.lineCount } else {
        [Math]::Min($endLine, [int]$Source.lineCount)
    }
    [int] $startOffset = [int]$Source.lineStarts[$startLine - 1]
    [int] $endOffset = if ($clampedEndLine -eq [int]$Source.lineCount) {
        $Source.text.Length
    }
    else {
        [int]$Source.lineStarts[$clampedEndLine]
    }
    $canonicalArguments = [ordered]@{ path = [string]$Arguments.path }
    if ($explicitRange) { $canonicalArguments.view_range = @($startLine, $endLine) }
    [string] $canonicalJson = $canonicalArguments | ConvertTo-Json -Depth 3 -Compress
    [string] $requestedText = $Source.text.Substring($startOffset, $endOffset - $startOffset)
    return [pscustomobject]@{
        hash = Get-AgentEvalTextHash $canonicalJson
        path = [string]$Arguments.path
        explicitRange = $explicitRange
        startLine = $startLine
        endLine = $endLine
        clampedEndLine = $clampedEndLine
        startOffset = $startOffset
        endOffset = $endOffset
        requestedBytes = [System.Text.Encoding]::UTF8.GetByteCount($requestedText)
        requestedText = $requestedText
    }
}

function Get-AgentEvalSkillViewPayload($Result, $Request, $Source) {
    if ($null -eq $Result -or $Result -is [string] -or $Result -is [ValueType]) {
        throw 'Skill view result was not the observed object shape.'
    }
    [string[]] $members = @($Result.PSObject.Properties.Name)
    if ($members.Count -ne 2 -or $members -notcontains 'content' -or
        $members -notcontains 'detailedContent' -or $Result.content -isnot [string] -or
        $Result.detailedContent -isnot [string]) {
        throw 'Skill view result was not the observed content/detailedContent shape.'
    }

    [string] $content = [string]$Result.content
    [string] $payload = $content
    [string] $logicalPayload = $payload
    [string] $protocol = 'complete-v1'
    [bool] $terminalNewlineOmitted = $false
    [string[]] $warnings = @()
    [int] $continuationLine = 0
    if (-not [string]::Equals($content, [string]$Request.requestedText, [StringComparison]::Ordinal)) {
        [string] $terminalNewline = if ($Request.requestedText.EndsWith("`r`n", [StringComparison]::Ordinal)) {
            "`r`n"
        }
        elseif ($Request.requestedText.EndsWith("`n", [StringComparison]::Ordinal)) {
            "`n"
        }
        else {
            ''
        }
        [string] $withoutTerminalNewline = if ($terminalNewline) {
            $Request.requestedText.Substring(0, $Request.requestedText.Length - $terminalNewline.Length)
        }
        else {
            ''
        }
        if ($terminalNewline -and
            [string]::Equals($content, $withoutTerminalNewline, [StringComparison]::Ordinal)) {
            $logicalPayload = $Request.requestedText
            $terminalNewlineOmitted = $true
            $protocol = 'complete-terminal-newline-omitted-v1'
        }
        else {
        [string] $markerPrefix = "`n`n[Output truncated. Use view_range=["
        [int] $markerIndex = $content.LastIndexOf($markerPrefix, [StringComparison]::Ordinal)
        if ($markerIndex -lt 0) { throw 'Skill view content neither completed its range nor had a known truncation wrapper.' }
        [string] $warning = $content.Substring($markerIndex + 2)
        [System.Text.RegularExpressions.Regex] $warningPattern = [System.Text.RegularExpressions.Regex]::new(
            '\A\[Output truncated\. Use view_range=\[(?<next>[1-9][0-9]*), \.\.\.\] to continue reading\. In your next response, you may batch this with other view calls\. File has at least (?<lines>[1-9][0-9]*) lines\.\]\z',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant,
            [TimeSpan]::FromMilliseconds(100))
        [System.Text.RegularExpressions.Match] $warningMatch = $warningPattern.Match($warning)
        if (-not $warningMatch.Success) { throw 'Skill view truncation wrapper was malformed.' }
        $payload = $content.Substring(0, $markerIndex)
        if ($payload.Length -eq 0 -or $payload.Length -ge [int]$Request.requestedText.Length -or
            -not $Request.requestedText.StartsWith($payload, [StringComparison]::Ordinal)) {
            throw 'Skill view truncated payload did not match the requested source prefix.'
        }
        [int] $absoluteEnd = [int]$Request.startOffset + $payload.Length
        [int] $lastObservedIndex = [Array]::BinarySearch([int[]]$Source.lineStarts, $absoluteEnd - 1)
        [int] $lastObservedLine = if ($lastObservedIndex -ge 0) { $lastObservedIndex + 1 } else { -bnot $lastObservedIndex }
        $continuationLine = $lastObservedLine + 1
        if ([int]$warningMatch.Groups['next'].Value -ne $continuationLine -or
            [int]$warningMatch.Groups['lines'].Value -ne [int]$Source.renderedLineCount) {
            throw 'Skill view truncation wrapper did not describe the observed source position.'
        }
        $protocol = 'truncated-v1'
        $warnings = @($warning)
        $logicalPayload = $payload
        }
    }
    elseif (-not $Request.requestedText.StartsWith($payload, [StringComparison]::Ordinal)) {
        throw 'Skill view payload did not match its requested source range.'
    }

    [int] $absoluteStart = [int]$Request.startOffset
    [int] $absoluteEnd = $absoluteStart + $logicalPayload.Length
    [int] $lastOffset = [Math]::Max($absoluteStart, $absoluteEnd - 1)
    [int] $lastLineIndex = [Array]::BinarySearch([int[]]$Source.lineStarts, $lastOffset)
    [int] $lastLine = if ($lastLineIndex -ge 0) { $lastLineIndex + 1 } else { -bnot $lastLineIndex }
    [int] $coverageIndex = [Array]::BinarySearch([int[]]$Source.lineStarts, $absoluteEnd)
    [int] $coverageContinuationLine = if ($coverageIndex -ge 0) { $coverageIndex + 1 } else { -bnot $coverageIndex }
    return [pscustomobject]@{
        content = $content
        contentSha256 = Get-AgentEvalTextHash $content
        payload = $payload
        payloadSha256 = Get-AgentEvalTextHash $payload
        logicalPayload = $logicalPayload
        logicalPayloadSha256 = Get-AgentEvalTextHash $logicalPayload
        payloadStartOffset = $absoluteStart
        payloadEndOffsetExclusive = $absoluteEnd
        payloadFirstLine = [int]$Request.startLine
        payloadLastLine = $lastLine
        protocol = $protocol
        terminalNewlineOmitted = $terminalNewlineOmitted
        continuationLine = $continuationLine
        coverageContinuationLine = $coverageContinuationLine
        warnings = $warnings
    }
}

function Complete-AgentEvalSkillEvidence([string] $SourcePath, [object[]] $Reads) {
    $source = Get-AgentEvalSkillSource $SourcePath
    [System.Text.StringBuilder] $observed = [System.Text.StringBuilder]::new()
    [System.Collections.Generic.List[object]] $readEvidence = [System.Collections.Generic.List[object]]::new()
    [long] $requestedBytes = 0
    [long] $returnedPayloadChars = 0
    [int] $terminalNewlineOmissions = 0
    [int] $coveredThrough = 0
    [int] $previousStart = -1
    $failure = $null
    try {
        if ($Reads.Count -gt 4) { throw 'Skill view count exceeded four.' }
        foreach ($read in $Reads) {
            if ($null -eq $read -or -not $read.succeeded) { throw 'Skill view completion was not successful.' }
            $request = Get-AgentEvalSkillViewRequest -Arguments $read.arguments -Source $source
            $requestedBytes += [long]$request.requestedBytes
            if ($requestedBytes -gt 64MB) { throw 'Skill view requests exceeded the cumulative byte limit.' }
            if ($request.startOffset -le $previousStart) { throw 'Skill view ranges were reordered or did not advance.' }
            $payload = Get-AgentEvalSkillViewPayload -Result $read.result -Request $request -Source $source
            $returnedPayloadChars += $payload.payload.Length
            if ($payload.terminalNewlineOmitted) { $terminalNewlineOmissions++ }
            if ($payload.payloadStartOffset -gt $coveredThrough) { throw 'Skill view ranges left an unobserved hole.' }
            [int] $overlap = $coveredThrough - $payload.payloadStartOffset
            if ($overlap -ge $payload.logicalPayload.Length) { throw 'Skill view payload did not extend observed coverage.' }
            [void]$observed.Append(
                $payload.logicalPayload,
                $overlap,
                $payload.logicalPayload.Length - $overlap)
            $coveredThrough = $payload.payloadEndOffsetExclusive
            $previousStart = $request.startOffset
            $readEvidence.Add([pscustomobject]@{
                    callId = [string]$read.callId
                    requestHash = $request.hash
                    startLine = $request.startLine
                    endLine = $request.endLine
                    clampedEndLine = $request.clampedEndLine
                    requestedBytes = $request.requestedBytes
                    contentChars = $payload.content.Length
                    contentSha256 = $payload.contentSha256
                    payloadChars = $payload.payload.Length
                    payloadSha256 = $payload.payloadSha256
                    logicalPayloadChars = $payload.logicalPayload.Length
                    logicalPayloadSha256 = $payload.logicalPayloadSha256
                    payloadStartOffset = $payload.payloadStartOffset
                    payloadEndOffsetExclusive = $payload.payloadEndOffsetExclusive
                    payloadFirstLine = $payload.payloadFirstLine
                    payloadLastLine = $payload.payloadLastLine
                    protocol = $payload.protocol
                    terminalNewlineOmitted = $payload.terminalNewlineOmitted
                    continuationLine = $payload.continuationLine
                    coverageContinuationLine = $payload.coverageContinuationLine
                    warnings = $payload.warnings
                })
        }
        if ($coveredThrough -ne $source.text.Length) { throw 'Skill view ranges did not cover the complete source.' }
    }
    catch {
        $failure = $_.Exception.Message
    }

    [string] $observedText = $observed.ToString()
    [bool] $verified = $null -eq $failure -and
        [string]::Equals($observedText, [string]$source.text, [StringComparison]::Ordinal)
    return [pscustomobject]@{
        observed = $Reads.Count -gt 0
        verified = $verified
        failure = $failure
        sourceByteSha256 = $source.byteSha256
        sourceTextSha256 = $source.textSha256
        observedTextSha256 = if ($observedText.Length -gt 0) { Get-AgentEvalTextHash $observedText } else { $null }
        textNormalization = 'exact-text-with-single-view-terminal-newline-restoration-v1'
        sourceBytes = $source.bytes
        sourceChars = $source.text.Length
        sourceLineCount = $source.lineCount
        sourceTerminalNewline = $source.terminalNewline
        observedChars = $observedText.Length
        returnedPayloadChars = $returnedPayloadChars
        terminalNewlineOmissions = $terminalNewlineOmissions
        requestedBytes = $requestedBytes
        reads = $readEvidence
    }
}

function Complete-AgentEvalDiscoveredSkillEvidence {
    param(
        [Parameter(Mandatory)][string] $SourcePath,
        [Parameter(Mandatory)][object[]] $Events
    )

    $source = Get-AgentEvalSkillSource $SourcePath
    $failure = $null
    $discovery = $null
    $toolCallId = $null
    $observedText = $null
    $expectedBody = $null
    try {
        $skillEvents = @($Events | Where-Object {
                $_.type -eq 'session.skills_loaded' -and
                $_.data.PSObject.Properties.Name -contains 'skills'
            })
        if ($skillEvents.Count -ne 1) { throw 'Host did not report exactly one skill inventory.' }
        $matches = @($skillEvents[0].data.skills | Where-Object { $_.name -eq 'filtrace' })
        if ($matches.Count -ne 1) { throw 'Host did not discover exactly one filtrace skill.' }
        $discovery = $matches[0]
        [string] $discoveredPath = [System.IO.Path]::GetFullPath([string]$discovery.path)
        if ($discovery.source -ne 'project' -or $discovery.enabled -isnot [bool] -or
            -not $discovery.enabled -or
            -not [string]::Equals($discoveredPath, [System.IO.Path]::GetFullPath($SourcePath), [StringComparison]::Ordinal)) {
            throw 'Filtrace skill discovery metadata did not match the owned project skill.'
        }

        $starts = @($Events | Where-Object {
                $_.type -eq 'tool.execution_start' -and $_.data.toolName -eq 'skill'
            })
        if ($starts.Count -ne 1) { throw 'Host did not invoke the skill tool exactly once.' }
        $arguments = $starts[0].data.arguments
        [string[]] $argumentMembers = @($arguments.PSObject.Properties.Name)
        if ($argumentMembers.Count -ne 1 -or $argumentMembers[0] -ne 'skill' -or
            $arguments.skill -isnot [string] -or $arguments.skill -ne 'filtrace') {
            throw 'Skill invocation did not select filtrace exactly.'
        }
        $toolCallId = [string]$starts[0].data.toolCallId
        $completions = @($Events | Where-Object {
                $_.type -eq 'tool.execution_complete' -and $_.data.toolCallId -eq $toolCallId
            })
        if ($completions.Count -ne 1 -or $completions[0].data.success -isnot [bool] -or
            -not $completions[0].data.success) {
            throw 'Filtrace skill invocation did not complete successfully.'
        }

        $contexts = @($Events | Where-Object {
                $_.type -eq 'model.message' -and $_.data.message.role -eq 'user' -and
                $_.data.message.content -is [string] -and
                ([string]$_.data.message.content).StartsWith('<skill-context name="filtrace">', [StringComparison]::Ordinal)
            })
        if ($contexts.Count -ne 1) { throw 'Host did not inject exactly one filtrace skill context.' }
        [string] $context = [string]$contexts[0].data.message.content
        [string] $normalizedSource = $source.text.Replace("`r`n", "`n")
        if (-not $normalizedSource.StartsWith("---`n", [StringComparison]::Ordinal)) {
            throw 'Filtrace skill source did not have frontmatter.'
        }
        [int] $frontmatterEnd = $normalizedSource.IndexOf("`n---`n", 4, [StringComparison]::Ordinal)
        if ($frontmatterEnd -lt 0) { throw 'Filtrace skill frontmatter was not terminated.' }
        [string] $sourceBody = $normalizedSource.Substring($frontmatterEnd + 5)
        if (-not $sourceBody.StartsWith("`n", [StringComparison]::Ordinal)) {
            throw 'Filtrace skill frontmatter was not followed by a blank separator.'
        }
        $expectedBody = $sourceBody.Substring(1)
        [string] $skillDirectory = [System.IO.Path]::GetDirectoryName($SourcePath)
        [string[]] $relatedPaths = @(Get-ChildItem -LiteralPath $skillDirectory -File -Recurse |
            Where-Object { -not [string]::Equals($_.FullName, $SourcePath, [StringComparison]::Ordinal) } |
            Sort-Object { [System.IO.Path]::GetRelativePath($skillDirectory, $_.FullName) } |
            ForEach-Object { $_.FullName })
        [string] $relatedSection = if ($relatedPaths.Count -gt 0) {
            "`n`nRelated files (use view tool to read):`n" +
                (($relatedPaths | ForEach-Object { "  - $_" }) -join "`n")
        }
        else { '' }
        [string] $expectedContext = "<skill-context name=`"filtrace`">`n" +
            "Base directory for this skill: $skillDirectory$relatedSection`n`n" +
            "$expectedBody`n</skill-context>"
        if (-not [string]::Equals($context, $expectedContext, [StringComparison]::Ordinal)) {
            throw 'Injected filtrace skill context did not exactly match the expected wrapper and source body.'
        }
        $observedText = $expectedBody
    }
    catch {
        $failure = $_.Exception.Message
    }

    return [pscustomobject]@{
        observed = $null -ne $discovery -or $null -ne $toolCallId -or $null -ne $observedText
        verified = $null -eq $failure
        failure = $failure
        sourceByteSha256 = $source.byteSha256
        sourceTextSha256 = $source.textSha256
        sourceContextSha256 = if ($null -ne $expectedBody) { Get-AgentEvalTextHash $expectedBody } else { $null }
        observedTextSha256 = if ($null -ne $observedText) { Get-AgentEvalTextHash $observedText } else { $null }
        textNormalization = 'frontmatter-and-leading-separator-removed-crlf-to-lf-v2'
        sourceBytes = $source.bytes
        sourceChars = $source.text.Length
        sourceLineCount = $source.lineCount
        sourceTerminalNewline = $source.terminalNewline
        discovery = $discovery
        toolCallId = $toolCallId
    }
}

function Get-AgentEvalPowerShellResultText($Result) {
    if ($null -eq $Result -or $Result -is [string]) { return $null }
    [string[]] $members = @($Result.PSObject.Properties.Name)
    if ($members.Count -ne 2 -or $members -notcontains 'content' -or $members -notcontains 'detailedContent' -or
        $Result.content -isnot [string] -or $Result.detailedContent -isnot [string] -or
        -not [string]::Equals([string]$Result.content, [string]$Result.detailedContent, [StringComparison]::Ordinal)) {
        return $null
    }
    [System.Text.RegularExpressions.Match] $match = [regex]::Match(
        [string]$Result.content,
        '\A(?<payload>.+)\r?\n<shellId: (?<shellId>[0-9]+) completed with exit code 0>\z',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) { return $null }
    return $match.Groups['payload'].Value
}

function Get-AgentEvalAllowedEnvironmentNames {
    return @(
        'PATH', 'PATHEXT', 'SystemRoot', 'WINDIR', 'ComSpec', 'TEMP', 'TMP', 'TMPDIR',
        'OS', 'NUMBER_OF_PROCESSORS', 'PROCESSOR_ARCHITECTURE', 'PROCESSOR_IDENTIFIER',
        'PROCESSOR_LEVEL', 'PROCESSOR_REVISION', 'DOTNET_ROOT', 'DOTNET_ROOT_X64',
        'LANG', 'LC_ALL', 'LC_CTYPE')
}

function Test-AgentEvalPathContained([string] $Path, [string] $Root) {
    [string] $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    [string] $canonicalRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    [StringComparison] $comparison = if ([System.OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    return [string]::Equals($canonicalPath.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar), $canonicalRoot, $comparison) -or
        $canonicalPath.StartsWith($canonicalRoot + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Assert-AgentEvalNoAncestorInstructions([string] $Workspace) {
    [System.IO.DirectoryInfo] $directory = Get-Item -LiteralPath $Workspace
    while ($null -ne $directory) {
        [string] $instructionsPath = Join-Path $directory.FullName 'AGENTS.md'
        if (Test-Path -LiteralPath $instructionsPath -PathType Leaf) {
            throw "Strict Copilot workspace '$Workspace' has ancestor instructions at '$instructionsPath'."
        }
        $directory = $directory.Parent
    }
}

function Assert-AgentEvalNoReparsePoint([string] $Path, [string] $Boundary) {
    [System.IO.FileSystemInfo] $current = Get-Item -LiteralPath $Path -Force
    [string] $canonicalBoundary = [System.IO.Path]::GetFullPath($Boundary).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    [StringComparison] $comparison = if ([System.OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Eval input '$($current.FullName)' is a reparse point."
        }
        if ([string]::Equals($current.FullName.TrimEnd('\', '/'), $canonicalBoundary, $comparison)) { return }
        $current = if ($current -is [System.IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }
    throw "Eval input '$Path' was not beneath '$Boundary'."
}

function Assert-AgentEvalTrackedFixture([string] $Root, [string] $FixturePath) {
    if ($FixturePath.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw "Eval fixture '$FixturePath' must not be a UNC path."
    }
    [string] $fixtureRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $Root 'tests/Filtrace.Core.Tests/Fixtures'))
    [string] $canonicalFixture = [System.IO.Path]::GetFullPath($FixturePath)
    if (-not (Test-AgentEvalPathContained -Path $canonicalFixture -Root $fixtureRoot) -or
        -not (Test-Path -LiteralPath $canonicalFixture -PathType Leaf)) {
        throw "Eval fixture '$FixturePath' must be a file beneath '$fixtureRoot'."
    }
    Assert-AgentEvalNoReparsePoint -Path $canonicalFixture -Boundary $fixtureRoot
    [System.IO.FileInfo] $fixtureInfo = Get-Item -LiteralPath $canonicalFixture
    if ($fixtureInfo.Length -gt 512MB) { throw "Eval fixture '$FixturePath' exceeds 536870912 bytes." }

    [string] $relativePath = [System.IO.Path]::GetRelativePath($Root, $canonicalFixture).Replace('\', '/')
    [string] $gitPath = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
    [string[]] $tracked = @(& $gitPath -C $Root ls-files --error-unmatch -- $relativePath 2>$null)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -ne 1 -or
        -not [string]::Equals($tracked[0], $relativePath, [StringComparison]::Ordinal)) {
        throw "Eval fixture '$relativePath' is not a tracked repository file."
    }
    & $gitPath -C $Root diff --quiet HEAD -- $relativePath
    if ($LASTEXITCODE -ne 0) { throw "Eval fixture '$relativePath' differs from HEAD." }
    return $canonicalFixture
}

function Get-AgentEvalFileInventory {
    param(
        [Parameter(Mandatory)][string] $SourceDirectory,
        [Parameter(Mandatory)][AllowEmptyString()][string] $DestinationPrefix,
        [switch] $Recurse,
        [string[]] $ExcludedExtensions = @(),
        [ValidateRange(1, 256)][int] $MaxFiles,
        [ValidateRange(1, 512)][int] $MaxEntries,
        [ValidateRange(1, 536870912)][long] $MaxBytes
    )

    [System.Collections.Generic.List[object]] $files = [System.Collections.Generic.List[object]]::new()
    [System.Collections.Generic.Stack[System.IO.DirectoryInfo]] $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    [System.IO.DirectoryInfo] $sourceRoot = Get-Item -LiteralPath $SourceDirectory
    if (($sourceRoot.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Eval input '$SourceDirectory' is a reparse point."
    }
    $pending.Push($sourceRoot)
    [long] $bytes = 0
    [int] $entries = 0
    while ($pending.Count -gt 0) {
        [System.IO.DirectoryInfo] $directory = $pending.Pop()
        foreach ($entry in $directory.EnumerateFileSystemInfos()) {
            if (-not $Recurse -and $entry -is [System.IO.DirectoryInfo]) { continue }
            $entries++
            if ($entries -gt $MaxEntries) { throw "Eval input '$SourceDirectory' exceeds $MaxEntries entries." }
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Eval input '$($entry.FullName)' is a reparse point."
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Push($entry)
                continue
            }
            if ($ExcludedExtensions -contains $entry.Extension) { continue }
            if ($files.Count -ge $MaxFiles) { throw "Eval input '$SourceDirectory' exceeds $MaxFiles files." }
            if ($entry.Length -gt ($MaxBytes - $bytes)) { throw "Eval input '$SourceDirectory' exceeds $MaxBytes bytes." }
            [long] $newBytes = $bytes + $entry.Length
            [string] $relative = [System.IO.Path]::GetRelativePath($SourceDirectory, $entry.FullName)
            if ($relative.Length -gt 240) { throw "Eval input relative path '$relative' exceeds 240 characters." }
            [string] $destinationRelativePath = if ($DestinationPrefix) {
                Join-Path $DestinationPrefix $relative
            }
            else {
                $relative
            }
            $files.Add([pscustomobject]@{
                    sourcePath = $entry.FullName
                    relativePath = $destinationRelativePath
                    bytes = $entry.Length
                    sha256 = Get-AgentEvalFileHash $entry.FullName
                })
            $bytes = $newBytes
        }
    }
    return [pscustomobject]@{ files = $files; bytes = $bytes; entries = $entries }
}

function Copy-AgentEvalAttestedFile($File, [string] $DestinationRoot) {
    [string] $destination = Join-Path $DestinationRoot $File.relativePath
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    [System.IO.FileStream] $sourceStream = [System.IO.FileStream]::new(
        $File.sourcePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    [System.IO.FileStream] $destinationStream = [System.IO.FileStream]::new(
        $destination, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    [System.Security.Cryptography.IncrementalHash] $actualHash =
        [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    [byte[]] $buffer = [byte[]]::new(1MB)
    [long] $copied = 0
    [string] $actualReadSha256 = $null
    try {
        while (($read = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($read -gt ([long]$File.bytes - $copied)) { throw "Eval input '$($File.sourcePath)' grew while it was copied." }
            $copied += $read
            $actualHash.AppendData($buffer, 0, $read)
            $destinationStream.Write($buffer, 0, $read)
        }
        $actualReadSha256 = [Convert]::ToHexString($actualHash.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $actualHash.Dispose()
        $destinationStream.Dispose()
        $sourceStream.Dispose()
    }
    if ($copied -ne [long]$File.bytes) { throw "Eval input '$($File.sourcePath)' changed length while it was copied." }
    [string] $actualSha256 = Get-AgentEvalFileHash $destination
    [string] $sourceAfterSha256 = Get-AgentEvalFileHash $File.sourcePath
    if ($actualReadSha256 -ne $File.sha256 -or $actualSha256 -ne $File.sha256 -or
        $sourceAfterSha256 -ne $File.sha256) {
        throw "Eval input '$($File.sourcePath)' changed while it was copied."
    }
    return [pscustomobject]@{
        sourcePath = $File.sourcePath
        path = $destination
        relativePath = $File.relativePath.Replace('\', '/')
        bytes = $copied
        sha256 = $actualSha256
    }
}

function New-CopilotEvalContext {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $OutDir,
        [Parameter(Mandatory)][string] $Arm,
        [Parameter(Mandatory)][string] $FixturePath,
        [Parameter(Mandatory)][string] $Configuration,
        [switch] $StrictCliArm
    )

    [string] $runId = [Guid]::NewGuid().ToString('D')
    [string] $strictBase = if ($StrictCliArm -and (Test-AgentEvalPathContained -Path $OutDir -Root $Root)) {
        Join-Path ([System.IO.Path]::GetTempPath()) 'filtrace agent eval'
    }
    else {
        $OutDir
    }
    [string] $runDirectory = Join-Path $strictBase "runs/$runId"
    [string] $workspace = if ($StrictCliArm) { Join-Path $runDirectory 'workspace' } else { $Root }
    [string] $isolatedHome = if ($StrictCliArm) { Join-Path $runDirectory 'home' } else { $null }
    [string] $logDirectory = Join-Path $runDirectory 'logs'
    foreach ($directory in @($runDirectory, $workspace, $isolatedHome, $logDirectory) | Where-Object { $_ }) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    if ($StrictCliArm) {
        if (Test-AgentEvalPathContained -Path $workspace -Root $Root) {
            throw "Strict Copilot workspace '$workspace' must be outside the repository."
        }
        Assert-AgentEvalNoAncestorInstructions -Workspace $workspace
    }

    [System.Collections.Generic.List[object]] $immutableFiles = [System.Collections.Generic.List[object]]::new()
    [string] $ownedFixture = $FixturePath
    [string] $sourceFixtureHash = $null
    if ($StrictCliArm) {
        $FixturePath = Assert-AgentEvalTrackedFixture -Root $Root -FixturePath $FixturePath
        [System.IO.FileInfo] $fixtureInfo = Get-Item -LiteralPath $FixturePath
        $sourceFixtureHash = Get-AgentEvalFileHash $FixturePath
        $fixtureRecord = Copy-AgentEvalAttestedFile -File ([pscustomobject]@{
                sourcePath = $FixturePath
                relativePath = (Join-Path 'input' $fixtureInfo.Name)
                bytes = $fixtureInfo.Length
                sha256 = $sourceFixtureHash
            }) -DestinationRoot $workspace
        $immutableFiles.Add($fixtureRecord)
        $ownedFixture = $fixtureRecord.path
    }
    else {
        $sourceFixtureHash = Get-AgentEvalFileHash $FixturePath
    }
    [string] $ownedFixtureHash = Get-AgentEvalFileHash $ownedFixture

    [string] $appHostName = if ([System.OperatingSystem]::IsWindows()) { 'filtrace.exe' } else { 'filtrace' }
    [string] $cliSourceDirectory = Join-Path $Root "src/Filtrace/bin/$Configuration/net10.0"
    [string] $cliSourcePath = Join-Path $cliSourceDirectory $appHostName
    if (-not (Test-Path -LiteralPath $cliSourcePath -PathType Leaf)) {
        throw "Current-checkout filtrace apphost not found at '$cliSourcePath'. Build src/Filtrace first."
    }
    [string] $cliSourceHash = Get-AgentEvalFileHash $cliSourcePath
    [string] $cliPath = $cliSourcePath
    [System.Collections.Generic.List[object]] $cliInventory = [System.Collections.Generic.List[object]]::new()
    if ($StrictCliArm) {
        [string] $ownedCliDirectory = Join-Path $workspace 'tools/filtrace'
        [System.IO.Directory]::CreateDirectory($ownedCliDirectory) | Out-Null
        $runtimeInventory = Get-AgentEvalFileInventory `
            -SourceDirectory $cliSourceDirectory `
            -DestinationPrefix '' `
            -ExcludedExtensions @('.pdb', '.xml') `
            -MaxFiles 256 `
            -MaxEntries 512 `
            -MaxBytes 512MB
        [string] $architectureDirectoryName = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
            ([System.Runtime.InteropServices.Architecture]::X64) { 'amd64' }
            ([System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
            default { throw "Unsupported eval process architecture '$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)'." }
        }
        [string] $architectureSource = Join-Path $cliSourceDirectory $architectureDirectoryName
        $architectureInventory = Get-AgentEvalFileInventory `
            -SourceDirectory $architectureSource `
            -DestinationPrefix $architectureDirectoryName `
            -Recurse `
            -MaxFiles 256 `
            -MaxEntries 512 `
            -MaxBytes 512MB
        [object[]] $selectedRuntimeFiles = @($runtimeInventory.files) + @($architectureInventory.files)
        [long] $selectedRuntimeBytes = [long]$runtimeInventory.bytes + [long]$architectureInventory.bytes
        [int] $selectedRuntimeEntries = [int]$runtimeInventory.entries + [int]$architectureInventory.entries
        if ($selectedRuntimeFiles.Count -gt 256 -or $selectedRuntimeEntries -gt 512 -or
            $selectedRuntimeBytes -gt 512MB) {
            throw 'Selected filtrace runtime bundle exceeds 256 files, 512 entries, or 536870912 bytes.'
        }
        foreach ($runtimeFile in $selectedRuntimeFiles) {
            $copiedRuntimeFile = Copy-AgentEvalAttestedFile -File $runtimeFile -DestinationRoot $ownedCliDirectory
            $cliInventory.Add($copiedRuntimeFile)
            $immutableFiles.Add($copiedRuntimeFile)
        }
        $cliPath = Join-Path $ownedCliDirectory $appHostName
        if ($cliSourceHash -ne (Get-AgentEvalFileHash $cliPath)) {
            throw "Owned filtrace apphost hash did not match '$cliSourcePath'."
        }
    }

    [System.Collections.Generic.List[object]] $skillInventory = [System.Collections.Generic.List[object]]::new()
    [string] $skillPath = $null
    [string] $skillHash = $null
    if ($Arm -eq 'cli-skill') {
        [string] $skillSource = Join-Path $Root '.agents/skills/filtrace'
        [string] $skillDestination = Join-Path $workspace '.agents/skills/filtrace'
        $boundedSkill = Get-AgentEvalFileInventory `
            -SourceDirectory $skillSource `
            -DestinationPrefix '' `
            -Recurse `
            -MaxFiles 64 `
            -MaxEntries 128 `
            -MaxBytes 16MB
        foreach ($sourceFile in @($boundedSkill.files | Sort-Object relativePath)) {
            $copiedSkillFile = Copy-AgentEvalAttestedFile -File $sourceFile -DestinationRoot $skillDestination
            $immutableFiles.Add($copiedSkillFile)
            $skillInventory.Add([pscustomobject]@{
                    path = $copiedSkillFile.relativePath
                    sha256 = $copiedSkillFile.sha256
                    bytes = $copiedSkillFile.bytes
                })
        }

        $skillPath = Join-Path $skillDestination 'SKILL.md'
        $skillHash = Get-AgentEvalFileHash $skillPath
    }

    return [pscustomobject]@{
        runId = $runId
        runDirectory = $runDirectory
        workspace = $workspace
        home = $isolatedHome
        isolateHome = [bool]$StrictCliArm
        logDirectory = $logDirectory
        usagePath = (Join-Path $runDirectory 'usage.json')
        fixturePath = $ownedFixture
        fixtureSha256 = $ownedFixtureHash
        cliSourcePath = $cliSourcePath
        cliSourceSha256 = $cliSourceHash
        cliPath = $cliPath
        cliSha256 = $cliSourceHash
        cliInventory = $cliInventory
        skillPath = $skillPath
        skillSha256 = $skillHash
        skillInventory = $skillInventory
        immutableFiles = $immutableFiles
        isolation = [pscustomobject]@{
            workspaceOutsideRepository = [bool]($StrictCliArm -and -not (Test-AgentEvalPathContained $workspace $Root))
            inheritedEnvironment = @(Get-AgentEvalAllowedEnvironmentNames)
            ownedEnvironment = @('HOME', 'USERPROFILE', 'XDG_CONFIG_HOME', 'APPDATA', 'LOCALAPPDATA', 'COPILOT_HOME')
        }
        inputPolicy = [pscustomobject]@{
            fixtureTrackedAndClean = [bool]$StrictCliArm
            fixtureMaxBytes = 512MB
            cliMaxFiles = 256
            cliMaxEntries = 512
            cliMaxBytes = 512MB
            cliFiles = $cliInventory.Count
            cliEntries = if ($StrictCliArm) { $selectedRuntimeEntries } else { 0 }
            cliBytes = if ($StrictCliArm) { $selectedRuntimeBytes } else { 0 }
            skillMaxFiles = 64
            skillMaxEntries = 128
            skillMaxBytes = 16MB
            skillMaxViewCalls = 4
            skillMaxRequestedBytes = 64MB
        }
    }
}

function Get-AgentEvalTaskCommandFamilies {
    param(
        [Parameter(Mandatory)][object] $Task,
        [Parameter(Mandatory)][string[]] $AllowedVerbs
    )

    [System.Collections.Generic.List[object]] $families = [System.Collections.Generic.List[object]]::new()
    [System.Collections.Generic.HashSet[string]] $seenVerbs =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($step in @($Task.steps)) {
        [string[]] $stepMembers = @($step.PSObject.Properties.Name)
        if ($stepMembers -notcontains 'args') { throw "Task '$($Task.id)' has a step without args." }
        [object[]] $arguments = @($step.args)
        if ($arguments.Count -lt 2 -or $arguments.Count -gt 30 -or
            @($arguments | Where-Object { $_ -isnot [string] }).Count -ne 0) {
            throw "Task '$($Task.id)' has an unsupported command shape."
        }
        [string] $verb = [string]$arguments[0]
        if ($AllowedVerbs -notcontains $verb -or -not $seenVerbs.Add($verb)) {
            throw "Task '$($Task.id)' cannot derive one bounded policy for verb '$verb'."
        }
        if (-not [string]::Equals([string]$arguments[1], '{fixture}', [StringComparison]::Ordinal)) {
            throw "Task '$($Task.id)' command '$verb' does not use its owned fixture as the first argument."
        }

        [System.Collections.Generic.List[object]] $options = [System.Collections.Generic.List[object]]::new()
        [System.Collections.Generic.HashSet[string]] $seenOptions =
            [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        [int] $positionals = 0
        for ($index = 2; $index -lt $arguments.Count; $index++) {
            [string] $argument = [string]$arguments[$index]
            if (-not $argument.StartsWith('--', [StringComparison]::Ordinal)) {
                $positionals++
                continue
            }
            if (-not $seenOptions.Add($argument)) {
                throw "Task '$($Task.id)' repeats option '$argument'."
            }
            if ($argument -eq '--callees') {
                $options.Add([pscustomobject]@{ name = $argument; kind = 'switch' })
                continue
            }
            $index++
            if ($index -ge $arguments.Count -or ([string]$arguments[$index]).StartsWith('--', [StringComparison]::Ordinal)) {
                throw "Task '$($Task.id)' option '$argument' has no value."
            }
            [string] $canonicalValue = [string]$arguments[$index]
            $optionPolicy = switch ($argument) {
                { $_ -in @('--metric', '--measure', '--kind', '--lanes', '--mode') } {
                    [pscustomobject]@{ name = $argument; kind = 'enum'; values = @($canonicalValue) }
                    break
                }
                '--top' { [pscustomobject]@{ name = $argument; kind = 'integer'; minimum = 1; maximum = 100 }; break }
                '--take' { [pscustomobject]@{ name = $argument; kind = 'integer'; minimum = 0; maximum = 100 }; break }
                { $_ -in @('--at', '--window') } {
                    [pscustomobject]@{ name = $argument; kind = 'decimal'; minimum = 0; maximum = 86400000 }
                    break
                }
                { $_ -in @('--case-id', '--name', '--payload', '--process') } {
                    [pscustomobject]@{ name = $argument; kind = 'text' }
                    break
                }
                default { throw "Task '$($Task.id)' option '$argument' is not in the bounded CLI policy vocabulary." }
            }
            $options.Add($optionPolicy)
        }
        $families.Add([pscustomobject]@{
                verb = $verb
                maxPositionals = $positionals
                options = $options
            })
    }
    if ($families.Count -eq 0) { throw "Task '$($Task.id)' has no command steps." }
    return $families
}

function Add-AgentEvalImmutableFile([object] $Context, [string] $Path, [string] $SourcePath) {
    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path
    $Context.immutableFiles.Add([pscustomobject]@{
            sourcePath = $SourcePath
            path = $file.FullName
            relativePath = [System.IO.Path]::GetRelativePath($Context.runDirectory, $file.FullName).Replace('\', '/')
            bytes = $file.Length
            sha256 = Get-AgentEvalFileHash $file.FullName
        })
}

function Initialize-CopilotEvalExecutionPolicy {
    param(
        [Parameter(Mandatory)][object] $Context,
        [Parameter(Mandatory)][object] $Task,
        [Parameter(Mandatory)][string[]] $AllowedVerbs,
        [ValidateRange(1, 64)][int] $MaxCalls,
        [Parameter(Mandatory)][string] $HookSourcePath
    )

    if (-not [System.OperatingSystem]::IsWindows()) {
        throw 'The Copilot CLI pre-execution policy currently supports only the Windows powershell tool schema.'
    }
    if (-not $Context.isolateHome) { throw 'The Copilot CLI pre-execution policy requires an isolated home.' }
    [int] $effectiveMaxCalls = $MaxCalls
    if ($null -ne $Task.maxCalls) {
        [int] $taskMaxCalls = [int]$Task.maxCalls
        if ($taskMaxCalls -lt 1) { throw "Task '$($Task.id)' has an invalid maxCalls value." }
        $effectiveMaxCalls = [Math]::Min($effectiveMaxCalls, $taskMaxCalls)
    }
    [object[]] $commandFamilies = @(Get-AgentEvalTaskCommandFamilies -Task $Task -AllowedVerbs $AllowedVerbs)
    [int] $maxHelpCalls = $commandFamilies.Count + 1

    [string] $policyDirectory = Join-Path $Context.runDirectory 'policy'
    [string] $hooksDirectory = Join-Path $Context.home 'copilot/hooks'
    foreach ($directory in @($policyDirectory, $hooksDirectory)) {
        if (-not (Test-AgentEvalPathContained -Path $directory -Root $Context.runDirectory)) {
            throw "Pre-execution policy directory '$directory' escaped the owned run directory."
        }
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    [System.IO.FileInfo] $hookSource = Get-Item -LiteralPath $HookSourcePath
    if (($hookSource.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Pre-execution hook '$HookSourcePath' is a reparse point."
    }
    $hookCopy = Copy-AgentEvalAttestedFile -File ([pscustomobject]@{
            sourcePath = $hookSource.FullName
            relativePath = 'CopilotEval.PolicyHook.ps1'
            bytes = $hookSource.Length
            sha256 = Get-AgentEvalFileHash $hookSource.FullName
        }) -DestinationRoot $policyDirectory
    $Context.immutableFiles.Add($hookCopy)

    $skillSource = if ($Context.skillPath) { Get-AgentEvalSkillSource $Context.skillPath } else { $null }
    [string] $viewPath = if ($skillSource) { [string]$skillSource.path } else { $null }
    [string] $policyPath = Join-Path $policyDirectory 'execution-policy.json'
    [string] $statePath = Join-Path $policyDirectory 'execution-state.json'
    [string] $lockPath = Join-Path $policyDirectory 'execution-state.lock'
    $policy = [ordered]@{
        schemaVersion = 4
        sessionId = $Context.runId
        workspace = $Context.workspace
        cliPath = $Context.cliPath
        fixturePath = $Context.fixturePath
        maxCalls = $effectiveMaxCalls
        maxHelpCalls = $maxHelpCalls
        statePath = $statePath
        lockPath = $lockPath
        viewPath = $viewPath
        viewLineCount = if ($skillSource) { [int]$skillSource.lineCount } else { 0 }
        maxViewCalls = 4
        maxViewBytes = 64MB
        commandFamilies = $commandFamilies
    }
    [System.IO.File]::WriteAllText(
        $policyPath,
        ($policy | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    Add-AgentEvalImmutableFile -Context $Context -Path $policyPath -SourcePath $policyPath

    [string] $hookHostPath = (Get-Process -Id $PID).Path
    if ([System.IO.Path]::GetFileNameWithoutExtension($hookHostPath) -ne 'pwsh') {
        throw "The Copilot CLI pre-execution hook requires PowerShell 7; current host is '$hookHostPath'."
    }
    $baseHook = [ordered]@{
        type = 'command'
        exec = $hookHostPath
        cwd = $Context.workspace
        timeoutSec = 5
    }
    $preToolHook = [ordered]@{} + $baseHook
    $preToolHook.matcher = 'powershell|view'
    $preToolHook.args = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $hookCopy.path,
        '-PolicyPath', $policyPath)
    [string] $hookConfigurationPath = Join-Path $hooksDirectory 'filtrace-eval-policy.json'
    $hookConfiguration = [ordered]@{
        version = 1
        hooks = [ordered]@{
            preToolUse = @($preToolHook)
        }
    }
    [System.IO.File]::WriteAllText(
        $hookConfigurationPath,
        ($hookConfiguration | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    Add-AgentEvalImmutableFile -Context $Context -Path $hookConfigurationPath -SourcePath $hookConfigurationPath

    return [pscustomobject]@{
        maxCalls = $effectiveMaxCalls
        maxHelpCalls = $maxHelpCalls
        policyPath = $policyPath
        statePath = $statePath
        hookConfigurationPath = $hookConfigurationPath
        hookPath = $hookCopy.path
        commandFamilies = $commandFamilies
        viewPath = $viewPath
        maxViewCalls = 4
        maxViewBytes = 64MB
        viewLineCount = if ($skillSource) { [int]$skillSource.lineCount } else { 0 }
    }
}

function Get-CopilotEvalExecutionPolicyState([object] $ExecutionPolicy) {
    if (-not (Test-Path -LiteralPath $ExecutionPolicy.statePath -PathType Leaf)) {
        return [pscustomobject]@{
            callCount = 0
            commandHashes = @()
            helpCallCount = 0
            helpCommandHashes = @()
            viewRequests = @()
        }
    }
    [System.IO.FileInfo] $stateFile = Get-Item -LiteralPath $ExecutionPolicy.statePath
    if (($stateFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $stateFile.Length -gt 65536) {
        throw 'Copilot pre-execution policy state was not a bounded ordinary file.'
    }
    try { $state = [System.IO.File]::ReadAllText($stateFile.FullName) | ConvertFrom-Json }
    catch { throw 'Copilot pre-execution policy state was malformed.' }
    [string[]] $stateMembers = @($state.PSObject.Properties.Name)
    if ($stateMembers.Count -ne 3 -or
        $stateMembers -notcontains 'commandHashes' -or
        $stateMembers -notcontains 'helpCommandHashes' -or
        $stateMembers -notcontains 'viewRequests') {
        throw 'Copilot pre-execution policy state shape was malformed.'
    }
    [object[]] $commandHashes = @($state.commandHashes)
    if ($commandHashes.Count -gt [int]$ExecutionPolicy.maxCalls -or
        @($commandHashes | Where-Object { $_ -isnot [string] -or $_ -notmatch '^[0-9a-f]{64}$' }).Count -ne 0) {
        throw 'Copilot pre-execution policy command hashes were malformed.'
    }
    [object[]] $helpCommandHashes = @($state.helpCommandHashes)
    if ($helpCommandHashes.Count -gt [int]$ExecutionPolicy.maxHelpCalls -or
        @($helpCommandHashes | Where-Object { $_ -isnot [string] -or $_ -notmatch '^[0-9a-f]{64}$' }).Count -ne 0) {
        throw 'Copilot pre-execution policy help command hashes were malformed.'
    }
    [object[]] $viewRequests = @($state.viewRequests)
    if ($viewRequests.Count -gt [int]$ExecutionPolicy.maxViewCalls) {
        throw 'Copilot pre-execution policy view requests were malformed.'
    }
    [long] $requestedBytes = 0
    $skillSource = if ($ExecutionPolicy.viewPath) { Get-AgentEvalSkillSource $ExecutionPolicy.viewPath } else { $null }
    foreach ($viewRequest in $viewRequests) {
        [string[]] $requestMembers = @($viewRequest.PSObject.Properties.Name)
        if ($requestMembers.Count -ne 3 -or $requestMembers -notcontains 'requestHash' -or
            $requestMembers -notcontains 'arguments' -or $requestMembers -notcontains 'requestedBytes' -or
            $viewRequest.requestHash -isnot [string] -or $viewRequest.requestHash -notmatch '^[0-9a-f]{64}$' -or
            ($viewRequest.requestedBytes -isnot [int] -and $viewRequest.requestedBytes -isnot [long]) -or
            $null -eq $skillSource) {
            throw 'Copilot pre-execution policy view requests were malformed.'
        }
        $parsedRequest = Get-AgentEvalSkillViewRequest -Arguments $viewRequest.arguments -Source $skillSource
        if (-not [string]::Equals($parsedRequest.hash, [string]$viewRequest.requestHash, [StringComparison]::Ordinal) -or
            [long]$parsedRequest.requestedBytes -ne [long]$viewRequest.requestedBytes) {
            throw 'Copilot pre-execution policy view request hash was malformed.'
        }
        $requestedBytes += [long]$parsedRequest.requestedBytes
        if ($requestedBytes -gt [long]$ExecutionPolicy.maxViewBytes) {
            throw 'Copilot pre-execution policy view request bytes were malformed.'
        }
    }
    return [pscustomobject]@{
        callCount = $commandHashes.Count
        commandHashes = [string[]]$commandHashes
        helpCallCount = $helpCommandHashes.Count
        helpCommandHashes = [string[]]$helpCommandHashes
        viewRequests = $viewRequests
    }
}

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

    [StringComparer] $pathComparer = if ([System.OperatingSystem]::IsWindows()) {
        [StringComparer]::OrdinalIgnoreCase
    }
    else {
        [StringComparer]::Ordinal
    }
    [System.Collections.Generic.HashSet[string]] $excluded =
        [System.Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($excludedPath in $ExcludedPaths) {
        if (-not [string]::IsNullOrEmpty($excludedPath)) {
            [void]$excluded.Add([System.IO.Path]::GetFullPath($excludedPath))
        }
    }
    [System.Collections.Generic.HashSet[string]] $excludedDirectorySet =
        [System.Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($excludedDirectory in $ExcludedDirectories) {
        if (-not [string]::IsNullOrEmpty($excludedDirectory)) {
            [void]$excludedDirectorySet.Add([System.IO.Path]::GetFullPath($excludedDirectory))
        }
    }
    [long] $total = 0
    [int] $files = 0
    [int] $entries = 0
    [System.Collections.Generic.Stack[System.IO.DirectoryInfo]] $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    [System.IO.DirectoryInfo] $root = Get-Item -LiteralPath $Path
    if (($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Scope root '$($root.FullName)' is a reparse point."
    }
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        foreach ($entry in $pending.Pop().EnumerateFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Scope entry '$($entry.FullName)' is a reparse point."
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                if ($excludedDirectorySet.Contains($entry.FullName)) { continue }
                $entries++
                if ($entries -gt $MaxEntries) { throw "$Scope exceeded $MaxEntries entries." }
                $pending.Push($entry)
                continue
            }
            if ($excluded.Contains($entry.FullName)) { continue }
            $entries++
            if ($entries -gt $MaxEntries) { throw "$Scope exceeded $MaxEntries entries." }
            $files++
            if ($entry.Length -gt ($MaxBytes - $total)) { throw "$Scope exceeded $MaxBytes bytes." }
            if ($entry.Length -gt $MaxFileBytes) {
                throw "$Scope file '$($entry.FullName)' exceeded $MaxFileBytes bytes."
            }
            $total += $entry.Length
        }
    }
    return [pscustomobject]@{ bytes = $total; files = $files; entries = $entries }
}

function Get-AgentEvalMutableDirectoryUsage {
    param(
        [Parameter(Mandatory)][string] $Path,
        [ValidateRange(1, 1024)][int] $MaxEntries,
        [long] $MaxBytes,
        [long] $MaxFileBytes,
        [string] $Scope
    )

    foreach ($attempt in 0..1) {
        try {
            return Get-AgentEvalDirectoryUsage `
                -Path $Path `
                -MaxEntries $MaxEntries `
                -MaxBytes $MaxBytes `
                -MaxFileBytes $MaxFileBytes `
                -Scope $Scope
        }
        catch {
            [bool] $directoryDisappeared = $false
            [System.Exception] $exception = $_.Exception
            while ($null -ne $exception) {
                if ($exception -is [System.IO.DirectoryNotFoundException]) {
                    $directoryDisappeared = $true
                    break
                }
                $exception = $exception.InnerException
            }
            if (-not $directoryDisappeared -or $attempt -ne 0) { throw }
        }
    }
}

function Get-AgentEvalHostRuntimePath($Context) {
    if (-not [System.OperatingSystem]::IsWindows() -or -not $Context.isolateHome) { return $null }
    if ([string]::IsNullOrEmpty([string]$Context.home) -or
        -not (Test-AgentEvalPathContained -Path $Context.home -Root $Context.runDirectory)) {
        throw 'The isolated Copilot home is not beneath the owned run directory.'
    }
    [string] $runtimePath = Join-Path $Context.home 'AppData/Local/copilot/pkg'
    if (-not (Test-AgentEvalPathContained -Path $runtimePath -Root $Context.home)) {
        throw 'The Copilot host runtime cache path is not beneath the isolated home.'
    }
    return $runtimePath
}

function Get-AgentEvalCopilotUsage {
    param(
        [Parameter(Mandatory)][object] $Context,
        [ValidateRange(1024, 104857600)][long] $MaxArtifactBytes,
        [ValidateRange(1024, 536870912)][long] $MaxHostRuntimeBytes = 256MB
    )

    [string] $runtimePath = Get-AgentEvalHostRuntimePath $Context
    $runtimeUsage = [pscustomobject]@{ bytes = 0L; files = 0; entries = 0 }
    if ($runtimePath -and (Test-Path -LiteralPath $runtimePath -PathType Container)) {
        $runtimeUsage = Get-AgentEvalMutableDirectoryUsage `
            -Path $runtimePath `
            -MaxEntries 1024 `
            -MaxBytes $MaxHostRuntimeBytes `
            -MaxFileBytes 128MB `
            -Scope 'Copilot host runtime cache'
    }
    [System.Collections.Generic.List[string]] $excludedPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($immutableFile in @($Context.immutableFiles)) {
        if (-not (Test-AgentEvalPathContained -Path $immutableFile.path -Root $Context.runDirectory) -or
            -not (Test-Path -LiteralPath $immutableFile.path -PathType Leaf)) {
            throw "Eval input attestation failed for '$($immutableFile.relativePath)'."
        }
        [System.IO.FileInfo] $file = Get-Item -LiteralPath $immutableFile.path -Force
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $file.Length -ne [long]$immutableFile.bytes) {
            throw "Eval input attestation failed for '$($immutableFile.relativePath)'."
        }
        Assert-AgentEvalNoReparsePoint -Path $file.FullName -Boundary $Context.runDirectory
        $excludedPaths.Add($file.FullName)
    }
    $artifactUsage = Get-AgentEvalDirectoryUsage `
        -Path $Context.runDirectory `
        -ExcludedPaths $excludedPaths.ToArray() `
        -ExcludedDirectories @($runtimePath) `
        -MaxEntries 512 `
        -MaxBytes $MaxArtifactBytes `
        -MaxFileBytes $MaxArtifactBytes `
        -Scope 'Copilot host artifacts'
    return [pscustomobject]@{
        artifacts = $artifactUsage
        hostRuntime = $runtimeUsage
    }
}

function Assert-AgentEvalContextIntegrity($Context) {
    foreach ($file in @($Context.immutableFiles)) {
        if (-not (Test-Path -LiteralPath $file.path -PathType Leaf) -or
            (Get-Item -LiteralPath $file.path).Length -ne [long]$file.bytes -or
            (Get-AgentEvalFileHash $file.path) -ne $file.sha256 -or
            (Get-AgentEvalFileHash $file.sourcePath) -ne $file.sha256) {
            throw "Eval input attestation failed for '$($file.relativePath)'."
        }
    }
}

function Get-AgentEvalHostUsageFile($Context) {
    [string] $path = [System.IO.Path]::GetFullPath([string]$Context.usagePath)
    if (-not (Test-AgentEvalPathContained -Path $path -Root $Context.runDirectory)) {
        throw 'Copilot usage output path escaped the owned run directory.'
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{
            available = $false
            reason = 'Host did not write the requested usage output file.'
            path = $path
            bytes = $null
            sha256 = $null
            value = $null
        }
    }

    [System.IO.FileInfo] $file = Get-Item -LiteralPath $path -Force
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -gt 1MB) {
        throw 'Copilot usage output was not a bounded ordinary file.'
    }
    try { $value = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json }
    catch { throw 'Copilot usage output was malformed.' }
    if ($null -eq $value -or $value -is [string] -or $value -is [ValueType]) {
        throw 'Copilot usage output was not an object.'
    }
    [string[]] $usageMembers = @($value.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($member in @('totalPremiumRequestCost', 'totalUserRequests', 'tokenDetails', 'currentModel')) {
        if ($usageMembers -notcontains $member) { throw 'Copilot usage output schema was malformed.' }
    }
    [bool] $premiumCostValid = $value.totalPremiumRequestCost -is [byte] -or
        $value.totalPremiumRequestCost -is [sbyte] -or
        $value.totalPremiumRequestCost -is [short] -or
        $value.totalPremiumRequestCost -is [ushort] -or
        $value.totalPremiumRequestCost -is [int] -or
        $value.totalPremiumRequestCost -is [uint] -or
        $value.totalPremiumRequestCost -is [long] -or
        $value.totalPremiumRequestCost -is [ulong] -or
        $value.totalPremiumRequestCost -is [float] -or
        $value.totalPremiumRequestCost -is [double] -or
        $value.totalPremiumRequestCost -is [decimal]
    [double] $premiumCost = if ($premiumCostValid) { [double]$value.totalPremiumRequestCost } else { -1 }
    if (-not $premiumCostValid -or $premiumCost -lt 0 -or
        [double]::IsNaN($premiumCost) -or [double]::IsInfinity($premiumCost) -or
        ($value.totalUserRequests -isnot [int] -and $value.totalUserRequests -isnot [long]) -or
        [long]$value.totalUserRequests -lt 0 -or
        $value.currentModel -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string]$value.currentModel) -or
        $null -eq $value.tokenDetails -or $value.tokenDetails -is [string] -or
        $value.tokenDetails -is [ValueType]) {
        throw 'Copilot usage output schema was malformed.'
    }
    [string[]] $tokenDetailMembers = @($value.tokenDetails.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($tokenKind in @('input', 'cache_read', 'cache_write', 'output')) {
        if ($tokenDetailMembers -notcontains $tokenKind) { throw 'Copilot usage output schema was malformed.' }
        $tokenDetail = $value.tokenDetails.$tokenKind
        if ($null -eq $tokenDetail -or $tokenDetail -is [string] -or $tokenDetail -is [ValueType]) {
            throw 'Copilot usage output schema was malformed.'
        }
        [string[]] $tokenMembers = @($tokenDetail.PSObject.Properties | ForEach-Object { $_.Name })
        if ($tokenMembers -notcontains 'tokenCount' -or
            ($tokenDetail.tokenCount -isnot [int] -and $tokenDetail.tokenCount -isnot [long]) -or
            [long]$tokenDetail.tokenCount -lt 0) {
            throw 'Copilot usage output schema was malformed.'
        }
    }

    return [pscustomobject]@{
        available = $true
        reason = $null
        path = $path
        bytes = $file.Length
        sha256 = Get-AgentEvalFileHash $path
        value = $value
    }
}

function Stop-AgentEvalProcess([System.Diagnostics.Process] $Process, [bool] $Started) {
    if (-not $Started) { return }
    try { if ($Process.HasExited) { return } } catch { return }
    try { $Process.Kill($true) } catch {}
    try { [void]$Process.WaitForExit(5000) } catch {}
}

function Invoke-BoundedCopilotProcess {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][object] $Context,
        [ValidateRange(1, 600)][int] $TimeoutSeconds = 600,
        [ValidateRange(1024, 104857600)][int] $MaxOutputBytes = 10485760,
        [ValidateRange(1024, 104857600)][int] $MaxArtifactBytes = 16777216,
        [ValidateRange(1024, 536870912)][long] $MaxHostRuntimeBytes = 256MB,
        [hashtable] $Environment = @{}
    )

    [System.Diagnostics.ProcessStartInfo] $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $Context.workspace
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }

    if ($Context.isolateHome) {
        $startInfo.Environment.Clear()
        foreach ($name in Get-AgentEvalAllowedEnvironmentNames) {
            [string] $value = [System.Environment]::GetEnvironmentVariable($name)
            if (-not [string]::IsNullOrEmpty($value)) { $startInfo.Environment[$name] = $value }
        }
        [string] $appData = Join-Path $Context.home 'AppData/Roaming'
        [string] $localAppData = Join-Path $Context.home 'AppData/Local'
        [string] $xdgConfigHome = Join-Path $Context.home '.config'
        [string] $copilotHome = Join-Path $Context.home 'copilot'
        foreach ($directory in @($appData, $localAppData, $xdgConfigHome, $copilotHome)) {
            [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        }
        $startInfo.Environment['HOME'] = $Context.home
        $startInfo.Environment['USERPROFILE'] = $Context.home
        $startInfo.Environment['XDG_CONFIG_HOME'] = $xdgConfigHome
        $startInfo.Environment['APPDATA'] = $appData
        $startInfo.Environment['LOCALAPPDATA'] = $localAppData
        $startInfo.Environment['COPILOT_HOME'] = $copilotHome
    }
    $startInfo.Environment['COPILOT_AUTO_UPDATE'] = 'false'
    foreach ($entry in $Environment.GetEnumerator()) {
        if ($null -eq $entry.Value) { [void]$startInfo.Environment.Remove([string]$entry.Key) }
        else { $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value }
    }

    [System.Diagnostics.Process] $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [System.Text.StringBuilder] $stdout = [System.Text.StringBuilder]::new()
    [System.Text.StringBuilder] $stderr = [System.Text.StringBuilder]::new()
    [System.Text.Encoding] $utf8 = [System.Text.Encoding]::UTF8
    [long] $capturedBytes = 0
    [System.Diagnostics.Stopwatch] $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    [bool] $started = $false

    try {
        if (-not $process.Start()) { throw "Failed to start '$FilePath'." }
        $started = $true
        [char[]] $stdoutBuffer = [char[]]::new(4096)
        [char[]] $stderrBuffer = [char[]]::new(4096)
        [System.Threading.Tasks.Task[int]] $stdoutTask = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        [System.Threading.Tasks.Task[int]] $stderrTask = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
        [bool] $stdoutClosed = $false
        [bool] $stderrClosed = $false
        [long] $nextArtifactCheck = 0

        while (-not ($stdoutClosed -and $stderrClosed)) {
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                Stop-AgentEvalProcess $process $started
                throw "Copilot host did not finish within $TimeoutSeconds seconds."
            }

            if ($stopwatch.ElapsedMilliseconds -ge $nextArtifactCheck) {
                $nextArtifactCheck = $stopwatch.ElapsedMilliseconds + 250
                [void](Get-AgentEvalCopilotUsage `
                        -Context $Context `
                        -MaxArtifactBytes $MaxArtifactBytes `
                        -MaxHostRuntimeBytes $MaxHostRuntimeBytes)
            }

            [System.Collections.Generic.List[System.Threading.Tasks.Task]] $pending = [System.Collections.Generic.List[System.Threading.Tasks.Task]]::new()
            if (-not $stdoutClosed) { $pending.Add($stdoutTask) }
            if (-not $stderrClosed) { $pending.Add($stderrTask) }
            if ($pending.Count -eq 0) {
                [void]$process.WaitForExit(50)
                continue
            }

            [int] $completedIndex = [System.Threading.Tasks.Task]::WaitAny($pending.ToArray(), 100)
            if ($completedIndex -lt 0) { continue }
            [System.Threading.Tasks.Task] $completed = $pending[$completedIndex]
            [bool] $isStdout = -not $stdoutClosed -and [object]::ReferenceEquals($completed, $stdoutTask)
            [int] $characterCount = if ($isStdout) {
                $stdoutTask.GetAwaiter().GetResult()
            }
            else {
                $stderrTask.GetAwaiter().GetResult()
            }

            if ($characterCount -eq 0) {
                if ($isStdout) { $stdoutClosed = $true } else { $stderrClosed = $true }
                continue
            }

            [char[]] $buffer = if ($isStdout) { $stdoutBuffer } else { $stderrBuffer }
            $capturedBytes += $utf8.GetByteCount($buffer, 0, $characterCount)
            if ($capturedBytes -gt $MaxOutputBytes) {
                Stop-AgentEvalProcess $process $started
                throw "Copilot host output exceeded $MaxOutputBytes bytes."
            }

            if ($isStdout) {
                [void]$stdout.Append($stdoutBuffer, 0, $characterCount)
                $stdoutTask = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
            }
            else {
                [void]$stderr.Append($stderrBuffer, 0, $characterCount)
                $stderrTask = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
            }
        }

        while (-not $process.HasExited) {
            [double] $remainingMilliseconds = ($TimeoutSeconds * 1000.0) - $stopwatch.Elapsed.TotalMilliseconds
            if ($remainingMilliseconds -le 0) {
                Stop-AgentEvalProcess $process $started
                throw "Copilot host did not finish within $TimeoutSeconds seconds."
            }
            if ($stopwatch.ElapsedMilliseconds -ge $nextArtifactCheck) {
                $nextArtifactCheck = $stopwatch.ElapsedMilliseconds + 250
                [void](Get-AgentEvalCopilotUsage `
                        -Context $Context `
                        -MaxArtifactBytes $MaxArtifactBytes `
                        -MaxHostRuntimeBytes $MaxHostRuntimeBytes)
            }
            [void]$process.WaitForExit([Math]::Min(100, [int][Math]::Ceiling($remainingMilliseconds)))
        }
        $finalUsage = Get-AgentEvalCopilotUsage `
            -Context $Context `
            -MaxArtifactBytes $MaxArtifactBytes `
            -MaxHostRuntimeBytes $MaxHostRuntimeBytes
        Assert-AgentEvalContextIntegrity $Context
        [string] $stdoutText = $stdout.ToString()
        [string] $stderrText = $stderr.ToString()
        return [pscustomobject]@{
            exitCode = $process.ExitCode
            stdout = @($stdoutText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
            stderr = @($stderrText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
            stdoutText = $stdoutText
            stderrText = $stderrText
            wallMs = [int]$stopwatch.ElapsedMilliseconds
            capturedBytes = $capturedBytes
            artifactBytes = $finalUsage.artifacts.bytes
            hostRuntimeBytes = $finalUsage.hostRuntime.bytes
            hostRuntimeFiles = $finalUsage.hostRuntime.files
            hostRuntimeEntries = $finalUsage.hostRuntime.entries
            hostRuntimeMaxBytes = $MaxHostRuntimeBytes
            hostRuntimeMaxFileBytes = 128MB
            hostRuntimeMaxEntries = 1024
        }
    }
    catch {
        throw [System.InvalidOperationException]::new(
            "Copilot host '$FilePath' failed after $capturedBytes captured bytes: $($_.Exception.Message)",
            $_.Exception)
    }
    finally {
        Stop-AgentEvalProcess $process $started
        $process.Dispose()
        $stopwatch.Stop()
    }
}