#!/usr/bin/env pwsh
#Requires -Version 7.2
# Copyright (c) Jeremy W Kuhne and contributors
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Exercises release-package validation and installed-server adapters with fakes.
.DESCRIPTION
  Requires PowerShell 7.2 or newer on Windows or Linux. No build, tool install,
  network, capture, elevation, or LLM is used. Fabricated archives test package
  rejection, and real child PowerShell processes test MCP failure and cleanup
  boundaries. A missing-dotnet child tests the smoke entry point's owned cleanup.
.PARAMETER OutputDirectory
  Local parent for a uniquely owned contract run, removed in finally.
#>
[CmdletBinding()]
param([string] $OutputDirectory = 'artifacts/release-package-contracts')

$ErrorActionPreference = 'Stop'
if (-not ($IsWindows -or $IsLinux)) { throw 'This contract runs on Windows or Linux only.' }
[string] $root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'eval\CopilotEval.Helpers.ps1')
[string] $smokeScript = Join-Path $PSScriptRoot 'Test-ReleasePackages.ps1'
[System.Management.Automation.Language.Token[]] $tokens = $null
[System.Management.Automation.Language.ParseError[]] $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($smokeScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Release package script does not parse.' }
Import-Module (Join-Path $PSScriptRoot 'ReleaseReadiness.psm1') -Force
$versionValidation = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Get-ReleaseVersion'
}, $true)
if ($null -eq $versionValidation) { throw 'Package smoke does not reuse release-version validation.' }
foreach ($version in @('0.10.0', '1.2.3-beta.1', '1.2.3--', '1.2.3-01', '1.2.3+build')) {
    & {
        [string] $ExpectedVersion = $version
        [bool] $accepted = $true
        try { . ([scriptblock]::Create($versionValidation.Extent.Text)) | Out-Null }
        catch { $accepted = $false }
        if ($accepted -ne ($version -notin @('1.2.3-01', '1.2.3+build'))) {
            throw "Package smoke disagrees with the release-stage version grammar for '$version'."
        }
    }
}
$applicationAssignment = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
    $node.Left.VariablePath.UserPath -ceq 'dotnetCommand'
}, $true)
if ($null -eq $applicationAssignment) { throw 'Could not locate native SDK resolution.' }
& {
    function Get-Command {
        param([string] $Name, $CommandType, $ErrorAction)
        [pscustomobject]@{ Source = "$Name-first" }
        [pscustomobject]@{ Source = "$Name-second" }
    }
    . ([scriptblock]::Create($applicationAssignment.Extent.Text))
    if ($dotnetCommand.Source -cne 'dotnet-first') {
        throw 'Multiple SDK applications were joined instead of selecting the first path.'
    }
}
# Load the actual pure functions without invoking the installation entry point.
foreach ($name in @('Get-ReleaseLocalPath', 'Read-ReleaseZipText', 'ConvertFrom-ReleaseXml',
        'Get-ReleasePackageIdentity', 'Assert-ReleaseProcessResult', 'Assert-ReleaseAnalysis')) {
    $function = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $false))
    if ($function.Count -ne 1) { throw "Could not locate production function '$name'." }
    . ([scriptblock]::Create($function[0].Extent.Text))
}

function Assert-Contract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Rejected([scriptblock] $Action, [string] $Name) {
    [bool] $rejected = $false
    try { & $Action | Out-Null }
    catch { $rejected = $true }
    Assert-Contract $rejected "Contract '$Name' accepted invalid input."
}
function New-FakeReleasePackage([string] $Path, [string] $Id, [string] $Mode = 'valid') {
    [string] $command = if ($Id.EndsWith('.Mcp')) { 'filtrace-mcp' } else { 'filtrace' }
    [string] $version = if ($Mode -eq 'wrong-version') { '0.9.0' } else { '0.10.0' }
    [string] $nuspecId = if ($Mode -eq 'wrong-id') { 'Other.Package' } else { $Id }
    [string] $nuspec = "<package><metadata><id>$nuspecId</id><version>$version</version></metadata></package>"
    if ($Mode -eq 'dtd') { $nuspec = '<!DOCTYPE package [<!ENTITY test "bad">]>' + $nuspec }
    if ($Mode -eq 'oversized-metadata') { $nuspec = (' ' * 70KB) + $nuspec }
    [string] $toolCommand = if ($Mode -eq 'wrong-command') { 'other-command' } else { $command }
    [string] $settings = '<DotNetCliTool><Commands><Command Name="' + $toolCommand +
        '" EntryPoint="tool.dll" Runner="dotnet" /></Commands></DotNetCliTool>'
    [hashtable] $manifest = @{
        name = 'io.github.JeremyKuhne/filtrace'; version = '0.10.0'
        packages = @(@{
            identifier = $Id; version = '0.10.0'; registryType = 'nuget'
            transport = @{ type = 'stdio' }
        })
    }
    if ($Mode -eq 'sentinel') { $manifest.version = '0.0.0-local' }
    if ($Mode -eq 'manifest-version') { $manifest.packages[0].version = '0.9.0' }
    [System.Collections.Specialized.OrderedDictionary] $content = [ordered]@{
        "$Id.nuspec" = $nuspec
        'tools/net10.0/any/DotnetToolSettings.xml' = $settings
        'tools/net10.0/any/tool.dll' = 'not an executable; never installed'
    }
    if ($Mode -eq 'missing-tool') { $content.Remove('tools/net10.0/any/DotnetToolSettings.xml') }
    if ($Mode -eq 'missing-entry') { $content.Remove('tools/net10.0/any/tool.dll') }
    if ($Mode -eq 'unsafe-entry') { $content['../escape.dll'] = 'invalid' }
    if ($Id.EndsWith('.Mcp') -and $Mode -ne 'missing-manifest') {
        $content['.mcp/server.json'] = if ($Mode -eq 'invalid-manifest') { 'not-json' } else {
            $manifest | ConvertTo-Json -Depth 8 -Compress
        }
    }
    $archive = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $content.GetEnumerator()) {
            $entry = $archive.CreateEntry([string]$item.Key)
            $stream = $entry.Open()
            try {
                [byte[]] $bytes = [System.Text.Encoding]::UTF8.GetBytes([string]$item.Value)
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
        if ($Mode -eq 'duplicate-entry') { [void]$archive.CreateEntry("$Id.nuspec") }
    }
    finally { $archive.Dispose() }
}

[string] $output = Get-ReleaseLocalPath $OutputDirectory
[string] $run = Join-Path $output ("contract-" + [Guid]::NewGuid().ToString('N'))
[string] $evidence = Join-Path $run 'logs'
[string] $hostPath = (Get-Process -Id $PID).Path
[System.Text.UTF8Encoding] $utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.Directory]::CreateDirectory($evidence) | Out-Null
$context = [pscustomobject]@{
    isolateHome = $false; workspace = $run; runDirectory = $evidence
    immutableFiles = @(); failureLogDirectory = Join-Path (Join-Path $evidence 'failures') 'native'
}
try {
    [string] $id = 'KlutzyNinja.Filtrace.Mcp'
    foreach ($mode in @('valid', 'wrong-id', 'wrong-version', 'dtd', 'oversized-metadata',
            'wrong-command', 'missing-tool', 'missing-entry', 'unsafe-entry', 'sentinel',
            'manifest-version', 'missing-manifest', 'invalid-manifest', 'duplicate-entry')) {
        [string] $path = Join-Path $run "$mode [package].nupkg"
        New-FakeReleasePackage $path $id $mode
        if ($mode -eq 'valid') {
            $identity = Get-ReleasePackageIdentity $path $id '0.10.0' 'filtrace-mcp'
            Assert-Contract ($identity.version -ceq '0.10.0' -and $identity.mcpManifest.version -ceq '0.10.0') 'Valid archive failed.'
        }
        else { Assert-Rejected { Get-ReleasePackageIdentity $path $id '0.10.0' 'filtrace-mcp' } "archive $mode" }
    }
    [string] $corrupt = Join-Path $run 'corrupt.nupkg'
    [System.IO.File]::WriteAllText($corrupt, 'not a zip', $utf8)
    Assert-Rejected { Get-ReleasePackageIdentity $corrupt $id '0.10.0' 'filtrace-mcp' } 'corrupt zip'
    Assert-Rejected { Get-ReleasePackageIdentity (Join-Path $run 'absent.nupkg') $id '0.10.0' 'filtrace-mcp' } 'absent package'
    foreach ($path in @('\\server\share', '//server/share', 'https://example.invalid/packages')) {
        Assert-Rejected { Get-ReleaseLocalPath $path } 'remote path'
    }

    $validResult = [pscustomobject]@{ exitCode = 0; stdoutText = 'data'; stderrText = '' }
    Assert-ReleaseProcessResult $validResult 'valid native' -RequireStdout
    foreach ($result in @($null, [pscustomobject]@{}, [pscustomobject]@{
            exitCode = 0; stdoutText = $null; stderrText = ''
        }, [pscustomobject]@{ exitCode = '0'; stdoutText = 'data'; stderrText = '' },
        [pscustomobject]@{ exitCode = 7; stdoutText = 'data'; stderrText = 'failed' })) {
        Assert-Rejected { Assert-ReleaseProcessResult $result 'fake native' -RequireStdout } 'native absent/nonzero/malformed'
    }
    Assert-Rejected { Assert-ReleaseProcessResult ([pscustomobject]@{
        exitCode = 0; stdoutText = ''; stderrText = ''
    }) 'empty' -RequireStdout } 'native empty'
    Assert-ReleaseProcessResult ([pscustomobject]@{ exitCode = 1; stdoutText = ''; stderrText = 'usage' }) 'usage' 1 -EmptyStdout
    Assert-Rejected { Assert-ReleaseProcessResult ([pscustomobject]@{
        exitCode = 1; stdoutText = 'pollution'; stderrText = 'usage'
    }) 'usage' 1 -EmptyStdout } 'usage stdout'
    Assert-ReleaseAnalysis '{"schemaVersion":18,"context":{"operation":"info"},"result":{}}' 'info' | Out-Null
    foreach ($json in @('', 'not-json', 'null', '[]', '{}',
            '{"schemaVersion":"18","context":{"operation":"info"},"result":{}}',
            '{"schemaVersion":18,"context":{"operation":"rank"},"result":{}}')) {
        Assert-Rejected { Assert-ReleaseAnalysis $json 'info' } 'malformed analysis'
    }

    [string] $fake = Join-Path $run 'fake mcp.ps1'
    [string] $fakeSource = @'
param([string] $Mode, [string] $PidPath)
$ErrorActionPreference = 'Stop'
[IO.File]::WriteAllText($PidPath, [string]$PID)
if ($Mode -eq 'hang') { Start-Sleep -Seconds 60; exit 0 }
if ($Mode -eq 'flood-stdout') { [Console]::Out.Write('x' * 100000); Start-Sleep -Seconds 60; exit 0 }
if ($Mode -eq 'flood-stderr') { [Console]::Error.Write('x' * 100000); Start-Sleep -Seconds 60; exit 0 }
if ($Mode -eq 'pollution') { [Console]::Out.WriteLine('not JSON-RPC') }
while ($null -ne ($line = [Console]::In.ReadLine())) {
    $request = $line | ConvertFrom-Json
    if ($request.method -eq 'notifications/initialized') { continue }
    $result = $null
    switch ($request.method) {
        'initialize' {
            if ($Mode -eq 'missing-initialize') { continue }
            $version = if ($Mode -eq 'wrong-version') { '0.9.0' } else { '0.10.0' }
            $result = @{ protocolVersion = '2024-11-05'; capabilities = @{}; serverInfo = @{ name = 'filtrace'; version = $version } }
        }
        'tools/list' {
            $tools = @('info','rank','callers','lines','heatmap','diff','batch','gc','timeline',
                'threadpool','diskio','lifecycle','query_events','export','processes','tree','classify','jit') |
                ForEach-Object {
                    @{ name = "trace_$_"; description = 'Uses trace-recorded sample intervals or raw sample counts.';
                       inputSchema = @{ type = 'object'; properties = @{} } }
                }
            $result = @{ tools = @($tools) }
            if ($Mode -eq 'wrong-id') { $request.id = 20 }
        }
        'tools/call' {
            if ($Mode -eq 'missing-call') { break }
            $text = @{ schemaVersion = 18; result = @{ path = $request.params.arguments.path } } | ConvertTo-Json -Compress -Depth 6
            $result = @{ content = @(@{ type = 'text'; text = $text }) }
        }
    }
    if ($null -ne $result) {
        [Console]::Out.WriteLine((@{ jsonrpc = '2.0'; id = $request.id; result = $result } | ConvertTo-Json -Compress -Depth 12))
    }
    if ($request.method -eq 'tools/call') { break }
}
if ($Mode -eq 'respond-then-hang') { Start-Sleep -Seconds 60 }
if ($Mode -eq 'nonzero') { exit 7 }
exit 0
'@
    [System.IO.File]::WriteAllText($fake, $fakeSource, $utf8)
    [string] $invoke = @'
& $env:RELEASE_MCP_CHECK -ServerExecutable $env:RELEASE_HOST -ServerArguments @(
    '-NoLogo','-NoProfile','-NonInteractive','-File',$env:RELEASE_FAKE,
    '-Mode',$env:RELEASE_MODE,'-PidPath',$env:RELEASE_PID
) -ExpectedVersion '0.10.0' -SchemaReportPath '' -TimeoutSeconds 1 -MaxOutputBytes 32768
exit $LASTEXITCODE
'@
    foreach ($mode in @('valid', 'wrong-version', 'missing-initialize', 'wrong-id', 'missing-call',
            'pollution', 'nonzero', 'flood-stdout', 'flood-stderr', 'hang', 'respond-then-hang')) {
        [string] $pidPath = Join-Path $run "$mode.pid"
        $result = Invoke-BoundedCopilotProcess -FilePath $hostPath -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand',
            [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($invoke))) `
            -Context $context -TimeoutSeconds 20 -MaxOutputBytes 64KB -MaxArtifactBytes 1MB -Environment @{
                RELEASE_MCP_CHECK = (Join-Path $PSScriptRoot 'Test-McpServer.ps1')
                RELEASE_HOST = $hostPath; RELEASE_FAKE = $fake; RELEASE_MODE = $mode; RELEASE_PID = $pidPath
            }
        Assert-Contract (($mode -eq 'valid' -and $result.exitCode -eq 0) -or
            ($mode -ne 'valid' -and $result.exitCode -ne 0)) "MCP adapter $mode failed: $($result.stdoutText) $($result.stderrText)"
        Assert-Contract (Test-Path -LiteralPath $pidPath -PathType Leaf) "MCP fake $mode never started."
        [int] $childId = [int][System.IO.File]::ReadAllText($pidPath)
        Assert-Contract ($null -eq (Get-Process -Id $childId -ErrorAction SilentlyContinue)) "MCP fake $mode leaked PID $childId."
    }

    [string] $feed = Join-Path $run 'local feed [owned]'
    [string] $smokeOutput = Join-Path $run 'smoke evidence'
    [System.IO.Directory]::CreateDirectory($feed) | Out-Null
    New-FakeReleasePackage (Join-Path $feed 'KlutzyNinja.Filtrace.0.10.0.nupkg') 'KlutzyNinja.Filtrace'
    New-FakeReleasePackage (Join-Path $feed 'KlutzyNinja.Filtrace.Mcp.0.10.0.nupkg') 'KlutzyNinja.Filtrace.Mcp'
    $result = Invoke-BoundedCopilotProcess -FilePath $hostPath -Arguments @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $smokeScript,
        '-PackagesDirectory', $feed, '-ExpectedVersion', '0.10.0', '-OutputDirectory', $smokeOutput
    ) -Context $context -Environment @{ PATH = '' } -TimeoutSeconds 20 -MaxOutputBytes 64KB -MaxArtifactBytes 1MB
    Assert-Contract ($result.exitCode -ne 0) 'Missing dotnet unexpectedly succeeded.'
    $smokeRuns = @([System.IO.Directory]::EnumerateDirectories($smokeOutput))
    Assert-Contract ($smokeRuns.Count -eq 1) 'Missing dotnet did not preserve one owned evidence run.'
    $report = Get-Content -LiteralPath (Join-Path $smokeRuns[0] 'smoke.json') -Raw | ConvertFrom-Json
    Assert-Contract ($report.status -ceq 'failed' -and $report.workRemoved -eq $true) 'Missing dotnet did not fail and remove owned work.'
    Assert-Contract ((Test-Path -LiteralPath $feed -PathType Container) -and
        -not (Test-Path -LiteralPath (Join-Path $smokeRuns[0] 'work'))) 'Smoke cleanup removed input feed or retained owned work.'
    Assert-Contract ($result.stderrText -match 'dotnet') 'Missing dotnet lost its failure diagnostic.'

    [string] $rejectedOutput = Join-Path $run 'rejected-output'
    $result = Invoke-BoundedCopilotProcess -FilePath $hostPath -Arguments @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $smokeScript,
        '-PackagesDirectory', $feed, '-ExpectedVersion', '0.10.1', '-OutputDirectory', $rejectedOutput
    ) -Context $context -Environment @{ PATH = '' } -TimeoutSeconds 20 -MaxOutputBytes 64KB -MaxArtifactBytes 1MB
    Assert-Contract ($result.exitCode -ne 0 -and $result.stderrText -match '0.10.1' -and
        -not (Test-Path -LiteralPath $rejectedOutput)) 'Stale package selection reached installation or created output.'
    if ($IsWindows) {
        [string] $longOutput = Join-Path $run ('long-output-' + ('x' * 180))
        $result = Invoke-BoundedCopilotProcess -FilePath $hostPath -Arguments @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $smokeScript,
            '-PackagesDirectory', $feed, '-ExpectedVersion', '0.10.0', '-OutputDirectory', $longOutput
        ) -Context $context -Environment @{ PATH = '' } -TimeoutSeconds 20 -MaxOutputBytes 64KB -MaxArtifactBytes 1MB
        Assert-Contract ($result.exitCode -ne 0 -and $result.stderrText -match '260 characters' -and
            -not (Test-Path -LiteralPath $longOutput)) 'Overlong Windows launch paths did not fail before writing.'
    }
    [System.IO.File]::Copy((Join-Path $feed 'KlutzyNinja.Filtrace.0.10.0.nupkg'),
        (Join-Path $feed 'unrelated.nupkg'), $false)
    $result = Invoke-BoundedCopilotProcess -FilePath $hostPath -Arguments @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $smokeScript,
        '-PackagesDirectory', $feed, '-ExpectedVersion', '0.10.0', '-OutputDirectory', $rejectedOutput
    ) -Context $context -Environment @{ PATH = '' } -TimeoutSeconds 20 -MaxOutputBytes 64KB -MaxArtifactBytes 1MB
    Assert-Contract ($result.exitCode -ne 0 -and $result.stderrText -match 'exactly the two' -and
        -not (Test-Path -LiteralPath $rejectedOutput)) 'Extra package selection reached installation or created output.'
    Write-Host 'Release-package contracts passed: archives, native results, MCP protocol failures/bounds/cleanup, and missing SDK.'
}
finally {
    Assert-AgentEvalNoReparsePoint -Path $run -Boundary $output
    Remove-Item -LiteralPath $run -Recurse -Force
}
