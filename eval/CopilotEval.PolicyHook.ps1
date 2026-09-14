#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PolicyPath,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedPolicySha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-PolicyDecision([bool] $Allowed, [string] $Reason) {
    $decision = if ($Allowed) {
        [ordered]@{ permissionDecision = 'allow' }
    }
    else {
        [ordered]@{ permissionDecision = 'deny'; permissionDecisionReason = $Reason }
    }
    [Console]::Out.WriteLine(($decision | ConvertTo-Json -Compress))
}

function Read-BoundedText([System.IO.TextReader] $Reader, [int] $MaxCharacters) {
    [System.Text.StringBuilder] $text = [System.Text.StringBuilder]::new()
    [char[]] $buffer = [char[]]::new(4096)
    while (($read = $Reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
        if ($read -gt ($MaxCharacters - $text.Length)) { throw 'Hook input exceeded its character limit.' }
        [void]$text.Append($buffer, 0, $read)
    }
    return $text.ToString()
}

function Get-ObjectMemberNames($Value) {
    if ($null -eq $Value) { return @() }
    return @($Value.PSObject.Properties.Name)
}

function Assert-ExactObjectMembers($Value, [string[]] $ExpectedMembers, [string] $Message) {
    if ($Value -isnot [pscustomobject]) { throw $Message }
    [string[]] $members = @(Get-ObjectMemberNames $Value)
    [System.Collections.Generic.HashSet[string]] $expected =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in $ExpectedMembers) { [void]$expected.Add($member) }
    if ($members.Count -ne $expected.Count -or
        @($members | Where-Object { -not $expected.Contains($_) }).Count -ne 0) {
        throw $Message
    }
}

function Assert-PolicyShape($Policy) {
    Assert-ExactObjectMembers `
        -Value $Policy `
        -ExpectedMembers @(
            'schemaVersion', 'sessionId', 'workspace', 'cliPath', 'fixturePath',
            'maxCalls', 'maxHelpCalls', 'statePath', 'lockPath', 'viewFiles',
            'maxViewCalls', 'maxViewBytes', 'commandFamilies') `
        -Message 'Policy shape was malformed.'
    if (($Policy.schemaVersion -isnot [int] -and $Policy.schemaVersion -isnot [long]) -or
        [long]$Policy.schemaVersion -ne 5 -or
        $Policy.sessionId -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Policy.sessionId) -or
        $Policy.workspace -isnot [string] -or
        $Policy.cliPath -isnot [string] -or
        $Policy.fixturePath -isnot [string] -or
        $Policy.statePath -isnot [string] -or
        $Policy.lockPath -isnot [string] -or
        $Policy.viewFiles -isnot [object[]] -or $Policy.viewFiles.Count -gt 64 -or
        ($Policy.maxCalls -isnot [int] -and $Policy.maxCalls -isnot [long]) -or
        [long]$Policy.maxCalls -lt 1 -or [long]$Policy.maxCalls -gt 64 -or
        ($Policy.maxHelpCalls -isnot [int] -and $Policy.maxHelpCalls -isnot [long]) -or
        [long]$Policy.maxHelpCalls -lt 1 -or [long]$Policy.maxHelpCalls -gt 64 -or
        ($Policy.maxViewCalls -isnot [int] -and $Policy.maxViewCalls -isnot [long]) -or
        [long]$Policy.maxViewCalls -ne 4 -or
        ($Policy.maxViewBytes -isnot [int] -and $Policy.maxViewBytes -isnot [long]) -or
        [long]$Policy.maxViewBytes -ne 64MB -or
        $Policy.commandFamilies -isnot [object[]] -or
        $Policy.commandFamilies.Count -lt 1 -or $Policy.commandFamilies.Count -gt 16) {
        throw 'Policy header was malformed.'
    }
    [System.Collections.Generic.HashSet[string]] $viewPaths =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($viewFile in $Policy.viewFiles) {
        Assert-ExactObjectMembers `
            -Value $viewFile `
            -ExpectedMembers @('path', 'lineCount', 'sha256') `
            -Message 'Policy view file was malformed.'
        if ($viewFile.path -isnot [string] -or
            -not [System.IO.Path]::IsPathFullyQualified([string]$viewFile.path) -or
            -not (Test-PathContained -Path ([string]$viewFile.path) -Root ([string]$Policy.workspace)) -or
            ($viewFile.lineCount -isnot [int] -and $viewFile.lineCount -isnot [long]) -or
            [long]$viewFile.lineCount -lt 0 -or [long]$viewFile.lineCount -gt [int]::MaxValue -or
            $viewFile.sha256 -isnot [string] -or $viewFile.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            -not $viewPaths.Add([System.IO.Path]::GetFullPath([string]$viewFile.path))) {
            throw 'Policy view file was malformed.'
        }
    }
    foreach ($family in $Policy.commandFamilies) {
        Assert-ExactObjectMembers `
            -Value $family `
            -ExpectedMembers @('verb', 'maxPositionals', 'options') `
            -Message 'Policy command family was malformed.'
        if ($family.verb -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$family.verb) -or
            ($family.maxPositionals -isnot [int] -and $family.maxPositionals -isnot [long]) -or
            [long]$family.maxPositionals -lt 0 -or [long]$family.maxPositionals -gt 30 -or
            $family.options -isnot [object[]]) {
            throw 'Policy command family was malformed.'
        }
        foreach ($option in $family.options) {
            if ($option -isnot [pscustomobject] -or $option.name -isnot [string] -or
                -not ([string]$option.name).StartsWith('--', [StringComparison]::Ordinal) -or
                $option.kind -isnot [string]) {
                throw 'Policy option was malformed.'
            }
            [string] $kind = [string]$option.kind
            if ([string]::Equals($kind, 'enum', [StringComparison]::Ordinal)) {
                Assert-ExactObjectMembers `
                    -Value $option `
                    -ExpectedMembers @('name', 'kind', 'values') `
                    -Message 'Policy option was malformed.'
                if ($option.values -isnot [object[]] -or $option.values.Count -lt 1 -or
                    @($option.values | Where-Object { $_ -isnot [string] }).Count -ne 0) {
                    throw 'Policy option was malformed.'
                }
            }
            elseif ([string]::Equals($kind, 'integer', [StringComparison]::Ordinal) -or
                [string]::Equals($kind, 'decimal', [StringComparison]::Ordinal)) {
                Assert-ExactObjectMembers `
                    -Value $option `
                    -ExpectedMembers @('name', 'kind', 'minimum', 'maximum') `
                    -Message 'Policy option was malformed.'
                if (($option.minimum -isnot [int] -and $option.minimum -isnot [long]) -or
                    ($option.maximum -isnot [int] -and $option.maximum -isnot [long]) -or
                    [long]$option.minimum -gt [long]$option.maximum) {
                    throw 'Policy option was malformed.'
                }
            }
            elseif ([string]::Equals($kind, 'text', [StringComparison]::Ordinal) -or
                [string]::Equals($kind, 'switch', [StringComparison]::Ordinal)) {
                Assert-ExactObjectMembers `
                    -Value $option `
                    -ExpectedMembers @('name', 'kind') `
                    -Message 'Policy option was malformed.'
            }
            else {
                throw 'Policy option was malformed.'
            }
        }
    }
}

function ConvertFrom-ToolArguments($Value) {
    if ($Value -is [string]) {
        if ($Value.Length -gt 16384) { throw 'Tool arguments exceeded their character limit.' }
        try { return $Value | ConvertFrom-Json }
        catch { throw 'Tool arguments were not valid JSON.' }
    }
    if ($null -eq $Value -or $Value -is [ValueType]) { throw 'Tool arguments were not an object.' }
    return $Value
}

function Test-PathContained([string] $Path, [string] $Root) {
    [string] $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    [string] $canonicalRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    return $canonicalPath.StartsWith($canonicalRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-OrdinaryFile([string] $Path, [string] $Boundary) {
    if (-not (Test-PathContained -Path $Path -Root $Boundary)) { throw 'Policy file path escaped its boundary.' }
    [System.IO.FileSystemInfo] $current = Get-Item -LiteralPath $Path -Force
    if ($current -isnot [System.IO.FileInfo]) { throw 'Policy file path was not an ordinary file.' }
    [string] $canonicalBoundary = [System.IO.Path]::GetFullPath($Boundary).TrimEnd('\', '/')
    while ($null -ne $current) {
        if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Policy file path contained a reparse point.'
        }
        if ([string]::Equals($current.FullName.TrimEnd('\', '/'), $canonicalBoundary,
                [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $current = if ($current -is [System.IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }
    throw 'Policy file path did not reach its boundary.'
}

function Get-TextHash([string] $Text) {
    [byte[]] $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-FileHash([string] $Path) {
    [byte[]] $bytes = [System.IO.File]::ReadAllBytes($Path)
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-SkillSource([string] $Path) {
    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path
    if ($file.Length -gt 16MB) { throw 'Skill entry point exceeded its byte limit.' }
    [byte[]] $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    [int] $preambleLength = if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    [string] $text = [System.Text.UTF8Encoding]::new($false, $true).GetString(
        $bytes, $preambleLength, $bytes.Length - $preambleLength)
    [System.Collections.Generic.List[int]] $lineStarts = [System.Collections.Generic.List[int]]::new()
    if ($text.Length -gt 0) {
        $lineStarts.Add(0)
        for ($index = 0; $index -lt ($text.Length - 1); $index++) {
            if ($text[$index] -eq "`n") { $lineStarts.Add($index + 1) }
        }
    }
    return [pscustomobject]@{
        path = $file.FullName
        text = $text
        lineStarts = $lineStarts.ToArray()
        lineCount = $lineStarts.Count
    }
}

function Get-ViewSource($Arguments, $Policy) {
    if ($null -eq $Arguments -or $Arguments -is [string] -or $Arguments -is [ValueType]) {
        throw 'View path was outside policy.'
    }
    [string[]] $members = @(Get-ObjectMemberNames $Arguments)
    if ($members -cnotcontains 'path' -or $Arguments.path -isnot [string] -or
        -not [System.IO.Path]::IsPathFullyQualified([string]$Arguments.path)) {
        throw 'View path was outside policy.'
    }
    [string] $canonicalPath = [System.IO.Path]::GetFullPath([string]$Arguments.path)
    [object[]] $matches = @($Policy.viewFiles | Where-Object {
            [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$_.path),
                $canonicalPath,
                [StringComparison]::Ordinal)
        })
    if ($matches.Count -ne 1) { throw 'View path was outside policy.' }
    $viewFile = $matches[0]
    Assert-OrdinaryFile -Path $canonicalPath -Boundary ([string]$Policy.workspace)
    $source = Get-SkillSource $canonicalPath
    if ([int]$source.lineCount -ne [int]$viewFile.lineCount -or
        -not [string]::Equals((Get-FileHash $canonicalPath), [string]$viewFile.sha256, [StringComparison]::Ordinal)) {
        throw 'Policy view source metadata was malformed.'
    }
    return $source
}

function Get-SkillViewRequest($Arguments, $Source) {
    [string[]] $members = @(Get-ObjectMemberNames $Arguments)
    if ($members.Count -lt 1 -or $members.Count -gt 2 -or
        $members -cnotcontains 'path' -or
        @($members | Where-Object {
                -not ([string]::Equals($_, 'path', [StringComparison]::Ordinal) -or
                    [string]::Equals($_, 'view_range', [StringComparison]::Ordinal))
            }).Count -ne 0 -or
        $Arguments.path -isnot [string] -or
        -not [System.IO.Path]::IsPathFullyQualified([string]$Arguments.path) -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath([string]$Arguments.path),
            [string]$Source.path,
            [StringComparison]::Ordinal)) {
        throw 'View path was outside policy.'
    }

    [int] $startLine = 1
    [int] $endLine = -1
    [long] $maximumEndLine = [long]$Source.lineCount + 512
    [bool] $explicitRange = $members -ccontains 'view_range'
    if ($explicitRange) {
        if ($null -eq $Arguments.view_range -or $Arguments.view_range -is [string] -or
            $Arguments.view_range -is [ValueType]) {
            throw 'View range was outside policy.'
        }
        [object[]] $range = @($Arguments.view_range)
        if ($range.Count -ne 2 -or
            ($range[0] -isnot [int] -and $range[0] -isnot [long]) -or
            ($range[1] -isnot [int] -and $range[1] -isnot [long]) -or
            [long]$range[0] -lt 1 -or [long]$range[0] -gt [int]::MaxValue -or
            [long]$range[1] -lt -1 -or [long]$range[1] -gt $maximumEndLine) {
            throw 'View range was outside policy.'
        }
        $startLine = [int][long]$range[0]
        $endLine = [int][long]$range[1]
    }
    if ($Source.lineCount -lt 1 -or $startLine -gt [int]$Source.lineCount -or
        ($endLine -ne -1 -and $endLine -lt $startLine)) {
        throw 'View range was outside source bounds.'
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
    return [pscustomobject]@{
        requestHash = Get-TextHash $canonicalJson
        arguments = [pscustomobject]$canonicalArguments
        requestedBytes = [System.Text.Encoding]::UTF8.GetByteCount(
            $Source.text.Substring($startOffset, $endOffset - $startOffset))
    }
}

function Assert-TextValue([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 256 -or $Value.StartsWith('--')) {
        throw 'Command text value was outside policy.'
    }
    foreach ($character in $Value.ToCharArray()) {
        if ([char]::IsControl($character)) { throw 'Command text value contained a control character.' }
    }
}

function Assert-OptionValue([string] $Value, $Option) {
    switch ([string]$Option.kind) {
        'enum' {
            [bool] $matched = $false
            foreach ($candidate in @($Option.values)) {
                if ([string]::Equals($Value, [string]$candidate, [StringComparison]::Ordinal)) {
                    $matched = $true
                    break
                }
            }
            if (-not $matched) { throw "Option '$($Option.name)' value was outside policy." }
        }
        'integer' {
            [long] $number = 0
            if (-not [long]::TryParse(
                    $Value,
                    [Globalization.NumberStyles]::None,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$number) -or
                $number -lt [long]$Option.minimum -or $number -gt [long]$Option.maximum) {
                throw "Option '$($Option.name)' value was outside policy."
            }
        }
        'decimal' {
            [decimal] $number = 0
            if (-not [decimal]::TryParse(
                    $Value,
                    [Globalization.NumberStyles]::AllowDecimalPoint,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$number) -or
                $number -lt [decimal]$Option.minimum -or $number -gt [decimal]$Option.maximum) {
                throw "Option '$($Option.name)' value was outside policy."
            }
        }
        'text' { Assert-TextValue -Value $Value }
        default { throw "Option '$($Option.name)' had an unknown policy kind." }
    }
}

function Assert-LiteralCommand($Arguments, $Policy) {
    [string[]] $argumentMembers = @(Get-ObjectMemberNames $Arguments)
    [System.Collections.Generic.HashSet[string]] $allowedArgumentMembers =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in @('command', 'description', 'mode', 'initial_wait')) {
        [void]$allowedArgumentMembers.Add($member)
    }
    [System.Collections.Generic.HashSet[string]] $actualArgumentMembers =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in $argumentMembers) { [void]$actualArgumentMembers.Add($member) }
    if ($argumentMembers.Count -lt 2 -or $argumentMembers.Count -gt $allowedArgumentMembers.Count -or
        @($argumentMembers | Where-Object { -not $allowedArgumentMembers.Contains($_) }).Count -ne 0 -or
        -not $actualArgumentMembers.Contains('command') -or
        -not $actualArgumentMembers.Contains('description') -or
        $Arguments.command -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string]$Arguments.command) -or
        $Arguments.command.Length -gt 8192) {
        throw 'PowerShell arguments were outside policy.'
    }
    if ($Arguments.description -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string]$Arguments.description) -or
        $Arguments.description.Length -gt 256 -or
        @($Arguments.description.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0) {
        throw 'PowerShell description was outside policy.'
    }
    if ($actualArgumentMembers.Contains('mode') -and
        ($Arguments.mode -isnot [string] -or
        -not [string]::Equals([string]$Arguments.mode, 'sync', [StringComparison]::Ordinal))) {
        throw 'PowerShell mode was outside policy.'
    }
    if ($actualArgumentMembers.Contains('initial_wait') -and
        (($Arguments.initial_wait -isnot [int] -and $Arguments.initial_wait -isnot [long]) -or
        [long]$Arguments.initial_wait -lt 1 -or [long]$Arguments.initial_wait -gt 30)) {
        throw 'PowerShell initial wait was outside policy.'
    }

    [System.Management.Automation.Language.Token[]] $tokens = $null
    [System.Management.Automation.Language.ParseError[]] $errors = $null
    [System.Management.Automation.Language.ScriptBlockAst] $ast =
        [System.Management.Automation.Language.Parser]::ParseInput(
            [string]$Arguments.command, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0 -or $null -eq $ast.EndBlock -or
        -not $ast.EndBlock.Unnamed -or $ast.EndBlock.Statements.Count -ne 1 -or
        $null -ne $ast.EndBlock.Traps -or $null -ne $ast.BeginBlock -or
        $null -ne $ast.ProcessBlock -or $null -ne $ast.CleanBlock -or
        $null -ne $ast.DynamicParamBlock -or $null -ne $ast.ParamBlock -or
        $null -ne $ast.ScriptRequirements -or $ast.UsingStatements.Count -ne 0 -or
        $ast.Attributes.Count -ne 0) {
        throw 'PowerShell command was not one statement.'
    }
    $statement = $ast.EndBlock.Statements[0]
    if ($statement -isnot [System.Management.Automation.Language.PipelineAst] -or
        $statement.PipelineElements.Count -ne 1) {
        throw 'PowerShell pipelines are not permitted.'
    }
    $command = $statement.PipelineElements[0]
    if ($command -isnot [System.Management.Automation.Language.CommandAst] -or
        $command.InvocationOperator -ne [System.Management.Automation.Language.TokenKind]::Ampersand -or
        $command.Redirections.Count -ne 0) {
        throw 'PowerShell command shape was outside policy.'
    }

    [System.Collections.Generic.List[string]] $values = [System.Collections.Generic.List[string]]::new()
    foreach ($element in $command.CommandElements) {
        if ($element -isnot [System.Management.Automation.Language.StringConstantExpressionAst] -or
            $element.StringConstantType -ne [System.Management.Automation.Language.StringConstantType]::SingleQuoted) {
            throw 'Every PowerShell command token must be single-quoted literal text.'
        }
        $values.Add([string]$element.Value)
    }
    if ($values.Count -lt 2 -or $values.Count -gt 32) { throw 'PowerShell command argument count was outside policy.' }
    if (-not [string]::Equals($values[0], [string]$Policy.cliPath, [StringComparison]::Ordinal)) {
        throw 'PowerShell command executable was outside policy.'
    }
    Assert-OrdinaryFile -Path ([string]$Policy.cliPath) -Boundary ([string]$Policy.workspace)

    if ($values.Count -eq 2 -and
        [string]::Equals($values[1], '--help', [StringComparison]::Ordinal)) {
        return [pscustomobject]@{ command = [string]$Arguments.command; isHelp = $true }
    }
    if ($values.Count -eq 3 -and
        [string]::Equals($values[2], '--help', [StringComparison]::Ordinal)) {
        $helpFamily = @($Policy.commandFamilies | Where-Object {
                [string]::Equals([string]$_.verb, $values[1], [StringComparison]::Ordinal)
            })
        if ($helpFamily.Count -eq 1) {
            return [pscustomobject]@{ command = [string]$Arguments.command; isHelp = $true }
        }
        throw "Help verb '$($values[1])' was outside policy."
    }

    if ($values.Count -lt 5 -or
        -not [string]::Equals($values[2], [string]$Policy.fixturePath, [StringComparison]::Ordinal) -or
        -not [string]::Equals($values[$values.Count - 2], '--format', [StringComparison]::Ordinal) -or
        -not [string]::Equals($values[$values.Count - 1], 'json', [StringComparison]::Ordinal)) {
        throw 'PowerShell command paths or output format were outside policy.'
    }
    Assert-OrdinaryFile -Path ([string]$Policy.fixturePath) -Boundary ([string]$Policy.workspace)

    $family = @($Policy.commandFamilies | Where-Object {
            [string]::Equals([string]$_.verb, $values[1], [StringComparison]::Ordinal)
        })
    if ($family.Count -ne 1) { throw "Verb '$($values[1])' was outside policy." }
    [System.Collections.Generic.Dictionary[string, object]] $options =
        [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($option in @($family[0].options)) {
        if (-not $options.TryAdd([string]$option.name, $option)) { throw 'Policy contained a duplicate option.' }
    }
    [System.Collections.Generic.HashSet[string]] $seenOptions =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [int] $positionals = 0
    for ($index = 3; $index -lt ($values.Count - 2); $index++) {
        [string] $value = $values[$index]
        if ($value.StartsWith('--', [StringComparison]::Ordinal)) {
            if (-not $options.ContainsKey($value) -or -not $seenOptions.Add($value)) {
                throw "Option '$value' was outside policy."
            }
            $option = $options[$value]
            if ([string]$option.kind -eq 'switch') { continue }
            $index++
            if ($index -ge ($values.Count - 2)) { throw "Option '$value' had no value." }
            Assert-OptionValue -Value $values[$index] -Option $option
            continue
        }
        $positionals++
        if ($positionals -gt [int]$family[0].maxPositionals) { throw 'Too many positional arguments.' }
        Assert-TextValue -Value $value
    }
    return [pscustomobject]@{ command = [string]$Arguments.command; isHelp = $false }
}

function Read-PolicyState([string] $StatePath) {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        return [pscustomobject]@{ commandHashes = @(); helpCommandHashes = @(); viewRequests = @() }
    }
    [System.IO.FileInfo] $stateFile = Get-Item -LiteralPath $StatePath
    if ($stateFile.Length -gt 65536) { throw 'Policy state exceeded its byte limit.' }
    try { $state = [System.IO.File]::ReadAllText($StatePath) | ConvertFrom-Json }
    catch { throw 'Policy state was malformed.' }
    if ($state -isnot [pscustomobject]) { throw 'Policy state shape was malformed.' }
    [string[]] $stateMembers = @(Get-ObjectMemberNames $state)
    if ($stateMembers.Count -ne 3 -or
        $stateMembers -cnotcontains 'commandHashes' -or
        $stateMembers -cnotcontains 'helpCommandHashes' -or
        $stateMembers -cnotcontains 'viewRequests' -or
        $state.commandHashes -isnot [object[]] -or
        $state.helpCommandHashes -isnot [object[]] -or
        $state.viewRequests -isnot [object[]]) {
        throw 'Policy state shape was malformed.'
    }
    return $state
}

function Open-PolicyLock([string] $LockPath) {
    [System.Diagnostics.Stopwatch] $wait = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try {
            return [System.IO.File]::Open(
                $LockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
        }
        catch [System.IO.IOException] {
            if ($wait.ElapsedMilliseconds -ge 2000) { throw }
            [System.Threading.Thread]::Sleep(10)
        }
    }
}

function Write-PolicyState(
    [string] $StatePath,
    [string[]] $CommandHashes,
    [string[]] $HelpCommandHashes,
    [object[]] $ViewRequests) {
    [string] $temporaryPath = "$StatePath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [string] $json = [string]([ordered]@{
            commandHashes = @($CommandHashes)
            helpCommandHashes = @($HelpCommandHashes)
            viewRequests = @($ViewRequests)
            } | ConvertTo-Json -Depth 6 -Compress)
        [System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::Move($temporaryPath, $StatePath, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

try {
    [System.IO.FileInfo] $policyFile = Get-Item -LiteralPath $PolicyPath
    if ($policyFile.Length -gt 65536) { throw 'Policy exceeded its byte limit.' }
    [byte[]] $policyBytes = [System.IO.File]::ReadAllBytes($policyFile.FullName)
    [string] $policySha256 = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($policyBytes)).ToLowerInvariant()
    if (-not [string]::Equals($policySha256, $ExpectedPolicySha256, [StringComparison]::Ordinal)) {
        throw 'Policy hash did not match the expected immutable value.'
    }
    $policy = [System.Text.UTF8Encoding]::new($false, $true).GetString($policyBytes) | ConvertFrom-Json
    Assert-PolicyShape $policy
    [string] $policyDirectory = $policyFile.DirectoryName
    foreach ($statePath in @([string]$policy.statePath, [string]$policy.lockPath)) {
        if (-not (Test-PathContained -Path $statePath -Root $policyDirectory)) {
            throw 'Policy state path escaped its boundary.'
        }
    }

    [string] $inputText = Read-BoundedText -Reader ([Console]::In) -MaxCharacters 65536
    try { $inputObject = $inputText | ConvertFrom-Json }
    catch { throw 'Hook input was not valid JSON.' }
    [string[]] $inputMembers = @(Get-ObjectMemberNames $inputObject)
    [System.Collections.Generic.HashSet[string]] $expectedInputMembers =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in @('sessionId', 'timestamp', 'cwd', 'toolName', 'toolArgs')) {
        [void]$expectedInputMembers.Add($member)
    }
    [System.Collections.Generic.HashSet[string]] $actualInputMembers =
        [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($member in $inputMembers) { [void]$actualInputMembers.Add($member) }
    if ($inputMembers.Count -ne $expectedInputMembers.Count -or
        $actualInputMembers.Count -ne $expectedInputMembers.Count -or
        @($expectedInputMembers | Where-Object { -not $actualInputMembers.Contains($_) }).Count -ne 0) {
        throw 'Hook input shape was outside policy.'
    }
    if (($inputObject.timestamp -isnot [long] -and $inputObject.timestamp -isnot [int]) -or
        [long]$inputObject.timestamp -lt 0 -or
        $inputObject.sessionId -isnot [string] -or
        -not [string]::Equals([string]$inputObject.sessionId, [string]$policy.sessionId, [StringComparison]::Ordinal) -or
        $inputObject.cwd -isnot [string] -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath([string]$inputObject.cwd),
            [System.IO.Path]::GetFullPath([string]$policy.workspace),
            [StringComparison]::OrdinalIgnoreCase) -or
        $inputObject.toolName -isnot [string]) {
        throw 'Hook session or working directory was outside policy.'
    }
    $toolArguments = ConvertFrom-ToolArguments $inputObject.toolArgs
    [string] $toolName = [string]$inputObject.toolName

    [System.IO.FileStream] $lock = $null
    try {
        $lock = Open-PolicyLock -LockPath ([string]$policy.lockPath)
        $state = Read-PolicyState -StatePath ([string]$policy.statePath)
        [System.Collections.Generic.List[string]] $commandHashes =
            [System.Collections.Generic.List[string]]::new()
        foreach ($commandHash in @($state.commandHashes)) {
            if ($commandHash -isnot [string] -or $commandHash -cnotmatch '^[0-9a-f]{64}$') {
                throw 'Policy command hash was malformed.'
            }
            $commandHashes.Add([string]$commandHash)
        }
        if ($commandHashes.Count -gt [int]$policy.maxCalls) { throw 'Policy command count exceeded its limit.' }
        [System.Collections.Generic.List[string]] $helpCommandHashes =
            [System.Collections.Generic.List[string]]::new()
        foreach ($commandHash in @($state.helpCommandHashes)) {
            if ($commandHash -isnot [string] -or $commandHash -cnotmatch '^[0-9a-f]{64}$') {
                throw 'Policy help command hash was malformed.'
            }
            $helpCommandHashes.Add([string]$commandHash)
        }
        if ($helpCommandHashes.Count -gt [int]$policy.maxHelpCalls) {
            throw 'Policy help command count exceeded its limit.'
        }
        [System.Collections.Generic.List[object]] $viewRequests =
            [System.Collections.Generic.List[object]]::new()
        [long] $requestedViewBytes = 0
        foreach ($usedViewRequest in $state.viewRequests) {
            if ($usedViewRequest -isnot [pscustomobject]) { throw 'Policy view request was malformed.' }
            [string[]] $requestMembers = @(Get-ObjectMemberNames $usedViewRequest)
            if ($requestMembers.Count -ne 3 -or $requestMembers -cnotcontains 'requestHash' -or
                $requestMembers -cnotcontains 'arguments' -or $requestMembers -cnotcontains 'requestedBytes' -or
                $usedViewRequest.requestHash -isnot [string] -or
                $usedViewRequest.requestHash -cnotmatch '^[0-9a-f]{64}$' -or
                ($usedViewRequest.requestedBytes -isnot [int] -and $usedViewRequest.requestedBytes -isnot [long])) {
                throw 'Policy view request was malformed.'
            }
            $skillSource = Get-ViewSource -Arguments $usedViewRequest.arguments -Policy $policy
            $parsedViewRequest = Get-SkillViewRequest -Arguments $usedViewRequest.arguments -Source $skillSource
            if (-not [string]::Equals(
                    [string]$usedViewRequest.requestHash,
                    [string]$parsedViewRequest.requestHash,
                    [StringComparison]::Ordinal) -or
                [long]$usedViewRequest.requestedBytes -ne [long]$parsedViewRequest.requestedBytes) {
                throw 'Policy view request hash was malformed.'
            }
            $requestedViewBytes += [long]$parsedViewRequest.requestedBytes
            $viewRequests.Add($usedViewRequest)
        }
        if ($viewRequests.Count -gt [int]$policy.maxViewCalls -or
            $requestedViewBytes -gt [long]$policy.maxViewBytes) {
            throw 'Policy view request limit was exceeded.'
        }

        if ([string]::Equals($toolName, 'view', [StringComparison]::Ordinal)) {
            $skillSource = Get-ViewSource -Arguments $toolArguments -Policy $policy
            $viewRequest = Get-SkillViewRequest -Arguments $toolArguments -Source $skillSource
            if ($viewRequests.Count -ge [int]$policy.maxViewCalls -or
                [long]$viewRequest.requestedBytes -gt ([long]$policy.maxViewBytes - $requestedViewBytes)) {
                throw 'View request limit was reached.'
            }
            $viewRequests.Add([pscustomobject]@{
                    requestHash = $viewRequest.requestHash
                    arguments = $viewRequest.arguments
                    requestedBytes = $viewRequest.requestedBytes
                })
        }
        elseif ([string]::Equals($toolName, 'powershell', [StringComparison]::Ordinal)) {
            $literalCommand = Assert-LiteralCommand -Arguments $toolArguments -Policy $policy
            if ($literalCommand.isHelp) {
                if ($helpCommandHashes.Count -ge [int]$policy.maxHelpCalls) {
                    throw "Filtrace help call limit $($policy.maxHelpCalls) was reached."
                }
            }
            elseif ($commandHashes.Count -ge [int]$policy.maxCalls) {
                throw "Filtrace analysis call limit $($policy.maxCalls) was reached."
            }
            [byte[]] $commandBytes = [System.Text.Encoding]::UTF8.GetBytes([string]$literalCommand.command)
            [string] $commandHash =
                [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($commandBytes)).ToLowerInvariant()
            if ($literalCommand.isHelp) { $helpCommandHashes.Add($commandHash) }
            else { $commandHashes.Add($commandHash) }
        }
        else {
            throw "Tool '$toolName' was outside policy."
        }
        Write-PolicyState `
            -StatePath ([string]$policy.statePath) `
            -CommandHashes $commandHashes.ToArray() `
            -HelpCommandHashes $helpCommandHashes.ToArray() `
            -ViewRequests $viewRequests.ToArray()
    }
    finally {
        if ($null -ne $lock) { $lock.Dispose() }
    }
    Write-PolicyDecision -Allowed $true -Reason ''
}
catch {
    Write-PolicyDecision -Allowed $false -Reason 'Denied by the bounded filtrace evaluation policy.'
}