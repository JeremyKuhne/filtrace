#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) Jeremy W Kuhne and contributors
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Smoke-tests the two packed filtrace tools without using published packages.

.DESCRIPTION
  Requires PowerShell 7.2 or newer on Windows or Linux, the .NET 10 SDK selected
  by this repository's global.json, the .NET 10 runtime, and the committed
  fixtures and test helpers.
  PackagesDirectory must contain exactly the CLI and MCP .nupkg files at
  ExpectedVersion; .snupkg files are ignored. Inspects their identities, tool
  entry points, and stamped MCP manifest before installation.

  Copies only those packages into a fresh local feed. All installations use
  --tool-path, that feed, an explicit source-clearing NuGet.Config, and per-child
  home/cache/scratch directories. Nothing is installed globally. Runs the
  installed CLI and the existing MCP wire-protocol check, not build outputs.
  Reads copied EventPipe and speedscope fixtures, and a copied ETL on Windows.
  Does not capture, elevate, opt into network symbols, or publish anything.

  Each invocation creates a uniquely owned run beneath OutputDirectory. Bounded
  logs, hashes, the MCP schema report, and smoke.json remain as evidence, including
  on failure. The feed, installed tools, homes, caches, and copied fixtures are
  removed in finally. Existing output directories and other runs are not removed.

.PARAMETER PackagesDirectory
  Local directory containing KlutzyNinja.Filtrace and KlutzyNinja.Filtrace.Mcp.

.PARAMETER ExpectedVersion
  Exact package and executable semantic version, without build metadata.

.PARAMETER OutputDirectory
  Local evidence parent directory. Relative paths use the caller's directory.
  On Windows, choose a short enough path for the installed native shims to remain
  below 260 characters; unsupported long launch paths are rejected before writing.

.PARAMETER ProcessTimeoutSeconds
  Per-child deadline, including installation. Defaults to 120 seconds.

.EXAMPLE
  ./tools/Test-ReleasePackages.ps1 -PackagesDirectory artifacts/packages `
    -ExpectedVersion 0.10.0 -OutputDirectory artifacts/release-smoke
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $PackagesDirectory,
    [Parameter(Mandatory)][ValidateLength(1, 128)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string] $ExpectedVersion,
    [ValidateNotNullOrEmpty()][string] $OutputDirectory = 'artifacts/release-smoke',
    [ValidateRange(1, 600)][int] $ProcessTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
if (-not ($IsWindows -or $IsLinux)) {
    throw 'Test-ReleasePackages.ps1 supports PowerShell Core on Windows and Linux only.'
}
[string] $root = Split-Path -Parent $PSScriptRoot
[System.Text.UTF8Encoding] $utf8 = [System.Text.UTF8Encoding]::new($false)

# Reuse the offline evaluator's bounded native runner and owned-path guards.
# No Copilot process, evaluation protocol, or LLM is involved.
. (Join-Path $root 'eval\CopilotEval.Helpers.ps1')

function Get-ReleaseLocalPath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('\\') -or
        $Path.StartsWith('//') -or $Path -match '^[a-zA-Z][a-zA-Z0-9+.-]*://') {
        throw "Release smoke path '$Path' must be a local filesystem path."
    }
    [string] $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    Assert-AgentEvalNoReparseAncestor $full
    return $full
}

function Read-ReleaseZipText([System.IO.Compression.ZipArchiveEntry] $Entry) {
    if ($Entry.Length -gt 64KB) { throw "Package metadata '$($Entry.FullName)' exceeds 64 KiB." }
    [System.IO.Stream] $stream = $Entry.Open()
    [System.IO.StreamReader] $reader = [System.IO.StreamReader]::new(
        $stream, [System.Text.UTF8Encoding]::new($false, $true), $true)
    try {
        [char[]] $buffer = [char[]]::new(65537)
        [int] $count = $reader.ReadBlock($buffer, 0, $buffer.Length)
        if ($count -gt 65536) { throw "Package metadata '$($Entry.FullName)' exceeds its read bound." }
        return [string]::new($buffer, 0, $count)
    }
    finally { $reader.Dispose() }
}

function ConvertFrom-ReleaseXml([string] $Text) {
    [System.Xml.XmlReaderSettings] $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 65536
    [System.IO.StringReader] $textReader = [System.IO.StringReader]::new($Text)
    [System.Xml.XmlReader] $reader = [System.Xml.XmlReader]::Create($textReader, $settings)
    try {
        [System.Xml.XmlDocument] $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    }
    finally { $reader.Dispose(); $textReader.Dispose() }
}

function Get-ReleasePackageIdentity(
    [string] $Path, [string] $PackageId, [string] $Version, [string] $CommandName) {
    [System.IO.FileInfo] $file = Get-Item -LiteralPath $Path -Force
    if ($file.Length -gt 256MB) { throw "Package '$Path' exceeds 256 MiB." }
    [System.IO.Compression.ZipArchive] $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -gt 4096) { throw "Package '$Path' has too many entries." }
        [System.Collections.Generic.Dictionary[string, object]] $entries =
            [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        [long] $total = 0
        foreach ($entry in $archive.Entries) {
            [string] $name = $entry.FullName.Replace('\', '/')
            if ($name.StartsWith('/') -or $name.Contains(':') -or
                @($name.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -ne 0 -or
                -not $entries.TryAdd($name, $entry)) {
                throw "Package '$Path' contains an unsafe or duplicate entry '$name'."
            }
            if ($entry.Length -gt 256MB -or $entry.Length -gt (512MB - $total)) {
                throw "Package '$Path' exceeds its expanded-size bounds."
            }
            $total += $entry.Length
        }
        [string[]] $nuspecs = @($entries.Keys | Where-Object { $_ -match '^[^/]+\.nuspec$' })
        if ($nuspecs.Count -ne 1) { throw "Package '$Path' must contain exactly one root nuspec." }
        [System.Xml.XmlDocument] $nuspec = ConvertFrom-ReleaseXml (Read-ReleaseZipText $entries[$nuspecs[0]])
        $ids = $nuspec.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']")
        $versions = $nuspec.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']")
        if ($ids.Count -ne 1 -or $versions.Count -ne 1 -or
            $ids[0].InnerText -cne $PackageId -or $versions[0].InnerText -cne $Version) {
            throw "Package '$Path' does not identify '$PackageId' at exact version '$Version'."
        }

        [string] $settingsPath = 'tools/net10.0/any/DotnetToolSettings.xml'
        if (-not $entries.ContainsKey($settingsPath)) { throw "Package '$Path' has no net10.0 tool settings." }
        [System.Xml.XmlDocument] $toolSettings = ConvertFrom-ReleaseXml (Read-ReleaseZipText $entries[$settingsPath])
        $commands = $toolSettings.SelectNodes('/DotNetCliTool/Commands/Command')
        if ($commands.Count -ne 1 -or $commands[0].GetAttribute('Name') -cne $CommandName -or
            $commands[0].GetAttribute('Runner') -cne 'dotnet') {
            throw "Package '$Path' does not expose the expected '$CommandName' dotnet tool."
        }
        [string] $entryPoint = $commands[0].GetAttribute('EntryPoint')
        if ([string]::IsNullOrWhiteSpace($entryPoint) -or $entryPoint.Contains('/') -or
            $entryPoint.Contains('\') -or -not $entries.ContainsKey("tools/net10.0/any/$entryPoint")) {
            throw "Package '$Path' has a missing or invalid tool entry point."
        }

        $manifest = $null
        if ($PackageId -ceq 'KlutzyNinja.Filtrace.Mcp') {
            if (-not $entries.ContainsKey('.mcp/server.json')) { throw "MCP package '$Path' has no .mcp/server.json." }
            [string] $manifestText = Read-ReleaseZipText $entries['.mcp/server.json']
            $manifest = $manifestText | ConvertFrom-Json -AsHashtable -Depth 16
            if ($manifest -isnot [System.Collections.IDictionary] -or
                $manifest.version -cne $Version -or $manifest.name -cne 'io.github.JeremyKuhne/filtrace' -or
                $manifestText.Contains('0.0.0-local') -or @($manifest.packages).Count -ne 1 -or
                $manifest.packages[0].identifier -cne $PackageId -or
                $manifest.packages[0].version -cne $Version -or
                $manifest.packages[0].registryType -cne 'nuget' -or
                $manifest.packages[0].transport.type -cne 'stdio') {
                throw "MCP package '$Path' has an unstamped or inconsistent server manifest."
            }
        }
        return [pscustomobject][ordered]@{
            id = $PackageId; version = $Version; command = $CommandName
            entryPoint = $entryPoint; bytes = $file.Length
            sha256 = Get-AgentEvalFileHash $Path
            mcpManifest = $manifest
        }
    }
    finally { $archive.Dispose() }
}

function Assert-ReleaseProcessResult(
    [object] $Result, [string] $Description, [int] $ExpectedExitCode = 0,
    [switch] $RequireStdout, [switch] $EmptyStdout) {
    if ($null -eq $Result -or $Result.exitCode -isnot [int] -or
        $Result.stdoutText -isnot [string] -or $Result.stderrText -isnot [string]) {
        throw "$Description returned an absent or malformed native result."
    }
    if ($Result.exitCode -ne $ExpectedExitCode) {
        throw "$Description exited with code $($Result.exitCode), expected $ExpectedExitCode. See the retained logs."
    }
    if ($RequireStdout -and [string]::IsNullOrWhiteSpace($Result.stdoutText)) {
        throw "$Description returned empty stdout."
    }
    if ($EmptyStdout -and $Result.stdoutText.Length -ne 0) {
        throw "$Description polluted stdout while reporting a usage error."
    }
}

function Invoke-ReleaseProcess([string] $Name, [string] $Executable, [string[]] $Arguments) {
    [object] $context = [pscustomobject]@{
        isolateHome = $false
        workspace = $work
        runDirectory = $evidence
        immutableFiles = @()
        failureLogDirectory = Join-Path (Join-Path $evidence 'failures') $Name
    }
    $command = [ordered]@{ name = $Name; executable = $Executable; arguments = @($Arguments); status = 'started' }
    $report.commands.Add($command)
    try {
        [object] $result = Invoke-BoundedCopilotProcess -FilePath $Executable -Arguments $Arguments `
            -Context $context -Environment $childEnvironment -TimeoutSeconds $ProcessTimeoutSeconds `
            -MaxOutputBytes 1MB -MaxArtifactBytes 16MB
    }
    catch {
        $command.status = 'failed'
        $command.failure = $_.Exception.Message
        throw
    }
    [string] $stdoutPath = Join-Path $evidence "$Name.stdout.log"
    [string] $stderrPath = Join-Path $evidence "$Name.stderr.log"
    [System.IO.File]::WriteAllText($stdoutPath, $result.stdoutText, $utf8)
    [System.IO.File]::WriteAllText($stderrPath, $result.stderrText, $utf8)
    $command.status = 'completed'
    $command.exitCode = $result.exitCode
    $command.wallMs = $result.wallMs
    $command.stdout = [System.IO.Path]::GetFileName($stdoutPath)
    $command.stderr = [System.IO.Path]::GetFileName($stderrPath)
    $command.stdoutSha256 = Get-AgentEvalFileHash $stdoutPath
    $command.stderrSha256 = Get-AgentEvalFileHash $stderrPath
    return $result
}

function Assert-ReleaseAnalysis([string] $Text, [string] $Operation) {
    try { $value = $Text | ConvertFrom-Json -AsHashtable -Depth 32 }
    catch { throw "Installed CLI '$Operation' returned malformed JSON." }
    if ($value -isnot [System.Collections.IDictionary] -or
        ($value.schemaVersion -isnot [long] -and $value.schemaVersion -isnot [int]) -or $value.schemaVersion -ne 18 -or
        $value.context -isnot [System.Collections.IDictionary] -or $value.context.operation -cne $Operation -or
        $value.result -isnot [System.Collections.IDictionary]) {
        throw "Installed CLI '$Operation' did not return the expected analysis envelope."
    }
    return $value
}

[string] $packages = Get-ReleaseLocalPath $PackagesDirectory
if (-not (Test-Path -LiteralPath $packages -PathType Container)) { throw "Package directory '$packages' is missing." }
[object[]] $inputs = @(
    [pscustomobject]@{ id = 'KlutzyNinja.Filtrace'; command = 'filtrace'; directory = 'cli' },
    [pscustomobject]@{ id = 'KlutzyNinja.Filtrace.Mcp'; command = 'filtrace-mcp'; directory = 'mcp' }
)
[string[]] $packageFiles = @([System.IO.Directory]::EnumerateFiles($packages) |
    Where-Object { $_.EndsWith('.nupkg', [StringComparison]::OrdinalIgnoreCase) })
if ($packageFiles.Count -ne 2) { throw "Package directory '$packages' must contain exactly the two release .nupkg files." }
foreach ($packageInput in $inputs) {
    $packageInput | Add-Member -NotePropertyName path -NotePropertyValue (Join-Path $packages "$($packageInput.id).$ExpectedVersion.nupkg")
    if (-not (Test-Path -LiteralPath $packageInput.path -PathType Leaf)) {
        throw "Expected package '$($packageInput.path)' is missing; stale or unrelated packages are not accepted."
    }
    Assert-AgentEvalNoReparsePoint -Path $packageInput.path -Boundary $packages
}

[string] $output = Get-ReleaseLocalPath $OutputDirectory
[string] $run = Join-Path $output ("release-packages-" + [Guid]::NewGuid().ToString('N'))
[string] $work = Join-Path $run 'work'
[string] $evidence = Join-Path $run 'evidence'
[string] $feed = Join-Path $work 'feed'
if ($IsWindows -and (Join-Path (Join-Path $work 'mcp') 'filtrace-mcp.exe').Length -ge 260) {
    throw 'The Windows native tool launch path would reach 260 characters. Choose a shorter OutputDirectory.'
}
[System.Collections.Specialized.OrderedDictionary] $report = [ordered]@{
    schemaVersion = 1; expectedVersion = $ExpectedVersion
    startedUtc = [DateTime]::UtcNow.ToString('o')
    platform = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    powershell = $PSVersionTable.PSVersion.ToString()
    packages = [System.Collections.Generic.List[object]]::new()
    commands = [System.Collections.Generic.List[object]]::new()
    fixtures = [System.Collections.Generic.List[object]]::new()
    etl = if ($IsWindows) { 'required' } else { 'skipped: ETL is Windows-only' }
    status = 'failed'; workRemoved = $false
}
[System.IO.Directory]::CreateDirectory($output) | Out-Null
if (Test-Path -LiteralPath $run) { throw "Owned run '$run' already exists." }
[System.IO.Directory]::CreateDirectory($run) | Out-Null
$claim = [System.IO.File]::Open((Join-Path $run 'owner'), [System.IO.FileMode]::CreateNew)
$claim.Dispose()
[int] $exitCode = 1
try {
    foreach ($directory in @($work, $evidence, $feed)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    # Keep SDK resolution independent of the location of the evidence directory.
    [string] $sdkPolicy = Join-Path $root 'global.json'
    Assert-AgentEvalNoReparsePoint -Path $sdkPolicy -Boundary $root
    [System.IO.File]::Copy($sdkPolicy, (Join-Path $work 'global.json'), $false)
    $report.sdkPolicySha256 = Get-AgentEvalFileHash $sdkPolicy
    foreach ($packageInput in $inputs) {
        [string] $copy = Join-Path $feed ([System.IO.Path]::GetFileName($packageInput.path))
        [System.IO.File]::Copy($packageInput.path, $copy, $false)
        $identity = Get-ReleasePackageIdentity $copy $packageInput.id $ExpectedVersion $packageInput.command
        if ($identity.sha256 -cne (Get-AgentEvalFileHash $packageInput.path)) { throw "Package '$($packageInput.path)' changed while copying." }
        $identity | Add-Member -NotePropertyName source -NotePropertyValue $packageInput.path
        $report.packages.Add($identity)
    }

    [hashtable] $childEnvironment = @{
        HOME = (Join-Path $work 'home'); USERPROFILE = (Join-Path $work 'home')
        DOTNET_CLI_HOME = (Join-Path $work 'dotnet-home')
        APPDATA = (Join-Path $work 'appdata'); LOCALAPPDATA = (Join-Path $work 'localappdata')
        XDG_CONFIG_HOME = (Join-Path $work 'xdg-config'); XDG_CACHE_HOME = (Join-Path $work 'xdg-cache')
        NUGET_PACKAGES = (Join-Path $work 'nuget-packages')
        NUGET_HTTP_CACHE_PATH = (Join-Path $work 'nuget-http')
        NUGET_PLUGINS_CACHE_PATH = (Join-Path $work 'nuget-plugins')
        NUGET_SCRATCH = (Join-Path $work 'scratch')
        TEMP = (Join-Path $work 'scratch'); TMP = (Join-Path $work 'scratch'); TMPDIR = (Join-Path $work 'scratch')
    }
    foreach ($directory in @($childEnvironment.Values | Sort-Object -Unique)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $childEnvironment.DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $childEnvironment.DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $childEnvironment.DOTNET_NOLOGO = '1'
    $childEnvironment.DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    $childEnvironment.NUGET_PLUGIN_PATHS = $null
    $childEnvironment.NUGET_CREDENTIALPROVIDERS_PATH = $null
    $childEnvironment._NT_SYMBOL_PATH = ''
    $childEnvironment._NT_ALT_SYMBOL_PATH = ''

    [string] $config = Join-Path $work 'NuGet.Config'
    [System.Xml.XmlWriterSettings] $xmlSettings = [System.Xml.XmlWriterSettings]::new()
    $xmlSettings.Encoding = $utf8
    $xmlSettings.Indent = $true
    [System.Xml.XmlWriter] $writer = [System.Xml.XmlWriter]::Create($config, $xmlSettings)
    try {
        $writer.WriteStartElement('configuration')
        foreach ($section in @('packageSources', 'auditSources')) {
            $writer.WriteStartElement($section)
            $writer.WriteElementString('clear', '')
            $writer.WriteStartElement('add')
            $writer.WriteAttributeString('key', 'release-smoke')
            $writer.WriteAttributeString('value', $feed)
            $writer.WriteEndElement(); $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
    }
    finally { $writer.Dispose() }

    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop
    [string] $dotnet = $dotnetCommand.Source
    $sdk = Invoke-ReleaseProcess 'sdk-version' $dotnet @('--version')
    Assert-ReleaseProcessResult $sdk 'SDK version' -RequireStdout
    if ($sdk.stdoutText.Trim() -notmatch '^10\.') { throw 'The repository-selected .NET 10 SDK is required.' }
    $report.sdk = $sdk.stdoutText.Trim()
    $runtimes = Invoke-ReleaseProcess 'runtimes' $dotnet @('--list-runtimes')
    Assert-ReleaseProcessResult $runtimes 'Runtime list' -RequireStdout
    if ($runtimes.stdoutText -notmatch '(?m)^Microsoft\.NETCore\.App 10\.') { throw 'The .NET 10 runtime is required.' }

    foreach ($packageInput in $inputs) {
        [string] $toolPath = Join-Path $work $packageInput.directory
        $install = Invoke-ReleaseProcess ("install-" + $packageInput.command) $dotnet @(
            'tool', 'install', $packageInput.id, '--version', $ExpectedVersion, '--tool-path', $toolPath,
            '--framework', 'net10.0', '--configfile', $config, '--source', $feed,
            '--no-http-cache', '--verbosity', 'minimal')
        Assert-ReleaseProcessResult $install "Installation of $($packageInput.id)" -RequireStdout
        [string] $shim = Join-Path $toolPath ($packageInput.command + $(if ($IsWindows) { '.exe' } else { '' }))
        if (-not (Test-Path -LiteralPath $shim -PathType Leaf)) { throw "Installation did not create '$shim'." }
        $packageInput | Add-Member -NotePropertyName executable -NotePropertyValue $shim
    }
    [string] $cli = $inputs[0].executable
    $version = Invoke-ReleaseProcess 'cli-version' $cli @('--version')
    Assert-ReleaseProcessResult $version 'Installed CLI version' -RequireStdout
    if ($version.stdoutText.Trim() -cne $ExpectedVersion) { throw 'Installed CLI version does not match the packages.' }
    $help = Invoke-ReleaseProcess 'cli-help' $cli @('--help')
    Assert-ReleaseProcessResult $help 'Installed CLI help' -RequireStdout
    if ($help.stdoutText -notmatch '(?m)^Usage:' -or $help.stdoutText -notmatch '(?m)^\s+info\s' -or
        $help.stdoutText -notmatch '(?m)^\s+rank\s' -or $help.stdoutText -match '(?m)^\s+cpu\s') {
        throw 'Installed CLI help is missing canonical commands or exposes removed cpu.'
    }

    [string] $fixtureDirectory = Join-Path $work 'fixtures'
    [System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
    [object[]] $fixtures = @(
        [pscustomobject]@{ name = 'folding.speedscope.json'; format = 'Speedscope' },
        [pscustomobject]@{ name = 'threadpool.nettrace'; format = 'NetTrace' }
    )
    if ($IsWindows) { $fixtures += [pscustomobject]@{ name = 'etw.etl'; format = 'Etl' } }
    foreach ($fixture in $fixtures) {
        [string] $source = Join-Path (Join-Path $root 'tests\Filtrace.Core.Tests\Fixtures') $fixture.name
        [string] $copy = Join-Path $fixtureDirectory $fixture.name
        Assert-AgentEvalNoReparsePoint -Path $source -Boundary $root
        [System.IO.File]::Copy($source, $copy, $false)
        [string] $hash = Get-AgentEvalFileHash $copy
        if ($hash -cne (Get-AgentEvalFileHash $source)) { throw "Fixture '$source' changed while copying." }
        $info = Invoke-ReleaseProcess ("info-" + $fixture.format) $cli @('info', $copy, '--format', 'json')
        Assert-ReleaseProcessResult $info "Installed CLI info ($($fixture.format))" -RequireStdout
        $infoValue = Assert-ReleaseAnalysis $info.stdoutText 'info'
        if ($infoValue.result.format -cne $fixture.format -or $infoValue.result.path -ne $copy -or
            $infoValue.result.sampleCount -le 0 -or @($infoValue.result.availableAnalyses).Count -eq 0) {
            throw "Installed CLI info did not analyze the copied $($fixture.format) fixture."
        }
        $rank = Invoke-ReleaseProcess ("rank-" + $fixture.format) $cli @('rank', $copy, '--top', '5', '--format', 'json')
        Assert-ReleaseProcessResult $rank "Installed CLI rank ($($fixture.format))" -RequireStdout
        $rankValue = Assert-ReleaseAnalysis $rank.stdoutText 'rank'
        if (@($rankValue.result.rows).Count -eq 0 -or $rankValue.result.scopeWeight -le 0 -or
            $rankValue.context.metric -cne 'cpu') { throw "Installed CLI rank returned no CPU ranking for $($fixture.format)." }
        if ((Get-AgentEvalFileHash $source) -cne $hash -or (Get-AgentEvalFileHash $copy) -cne $hash) {
            throw "Fixture bytes changed during analysis of '$source'."
        }
        $report.fixtures.Add([ordered]@{ source = $source; sha256 = $hash; format = $fixture.format; info = 'passed'; rank = 'passed' })
    }
    foreach ($arguments in @(@('cpu', (Join-Path $fixtureDirectory 'folding.speedscope.json')), @('cpu', '--help'))) {
        [string] $name = if ($arguments[1] -eq '--help') { 'removed-cpu-help' } else { 'removed-cpu' }
        $removed = Invoke-ReleaseProcess $name $cli $arguments
        Assert-ReleaseProcessResult $removed 'Removed cpu command' -ExpectedExitCode 1 -EmptyStdout
        if ($removed.stderrText -notmatch "Unknown command 'cpu'") { throw 'Removed cpu did not report its usage error on stderr.' }
    }

    [string] $schemaPath = Join-Path $evidence 'mcp-schema-tokens.json'
    [string] $hostPath = (Get-Process -Id $PID).Path
    $mcp = Invoke-ReleaseProcess 'installed-mcp' $hostPath @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'Test-McpServer.ps1'),
        '-ServerExecutable', $inputs[1].executable, '-ExpectedVersion', $ExpectedVersion,
        '-FixturePath', (Join-Path $fixtureDirectory 'folding.speedscope.json'), '-SchemaReportPath', $schemaPath)
    Assert-ReleaseProcessResult $mcp 'Installed MCP protocol check' -RequireStdout
    if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) { throw 'MCP protocol check did not write its schema evidence.' }
    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json -Depth 16
    [string[]] $expectedTools = @(
        'trace_info', 'trace_rank', 'trace_callers', 'trace_lines', 'trace_heatmap', 'trace_diff',
        'trace_batch', 'trace_gc', 'trace_timeline', 'trace_threadpool', 'trace_diskio', 'trace_lifecycle',
        'trace_query_events', 'trace_export', 'trace_processes', 'trace_tree', 'trace_classify', 'trace_jit'
    )
    [string[]] $actualTools = @($schema.tools | ForEach-Object { $_.name })
    if ($schema.serverVersion -cne $ExpectedVersion -or $schema.toolCount -ne 18 -or
        $actualTools.Count -ne 18 -or
        @(Compare-Object -ReferenceObject $expectedTools -DifferenceObject $actualTools -CaseSensitive).Count -ne 0) {
        throw 'Installed MCP version or exact 18-tool surface did not match the release contract.'
    }
    $report.mcp = [ordered]@{
        version = $schema.serverVersion; toolNames = $actualTools
        estimatedTokens = $schema.estimatedTokens; budgetTokens = $schema.budgetTokens
        initialize = 'passed'; toolsList = 'passed'; toolsCall = 'passed'; stdoutPurity = 'passed'
    }
    foreach ($package in $report.packages) {
        if ((Get-AgentEvalFileHash $package.source) -cne $package.sha256) { throw "Source package '$($package.source)' changed during smoke." }
    }
    $report.status = 'passed'
    $exitCode = 0
}
catch {
    $report.failure = $_.Exception.Message
    Write-Error -Message "Release package smoke failed: $($_.Exception.Message)" -ErrorAction Continue
}
finally {
    try {
        if (Test-Path -LiteralPath $work) {
            Assert-AgentEvalNoReparsePoint -Path $work -Boundary $run
            Remove-Item -LiteralPath $work -Recurse -Force
        }
        $report.workRemoved = -not (Test-Path -LiteralPath $work)
        if (-not $report.workRemoved) { throw "Owned work directory '$work' still exists." }
    }
    catch {
        $report.cleanupFailure = $_.Exception.Message
        $report.status = 'failed'
        $exitCode = 1
        Write-Error -Message "Owned release smoke cleanup failed: $($_.Exception.Message)" -ErrorAction Continue
    }
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    [string] $reportPath = Join-Path $run 'smoke.json'
    [System.IO.File]::WriteAllText($reportPath, (($report | ConvertTo-Json -Depth 16) + "`n"), $utf8)
    Write-Host "Release package smoke $($report.status). Evidence: $reportPath"
}
exit $exitCode
