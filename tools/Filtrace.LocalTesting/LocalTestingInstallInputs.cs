// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Holds source-built artifacts that were validated before target mutation begins.
/// </summary>
internal sealed record LocalTestingInstallInputs
{
    private LocalTestingInstallInputs(
        string sourceCheckout,
        string cliPackagePath,
        string dotnetPath,
        string mcpDllPath,
        string skillDirectory)
    {
        SourceCheckout = sourceCheckout;
        CliPackagePath = cliPackagePath;
        DotnetPath = dotnetPath;
        McpDllPath = mcpDllPath;
        SkillDirectory = skillDirectory;
    }

    /// <summary>
    ///  Gets the normalized Filtrace source checkout that produced the artifacts.
    /// </summary>
    public string SourceCheckout { get; }

    /// <summary>
    ///  Gets the validated, canonically named CLI package path.
    /// </summary>
    public string CliPackagePath { get; }

    /// <summary>
    ///  Gets the <c>dotnet</c> host path or command name used for private tool installation.
    /// </summary>
    public string DotnetPath { get; }

    /// <summary>
    ///  Gets the validated local MCP server assembly path.
    /// </summary>
    public string McpDllPath { get; }

    /// <summary>
    ///  Gets the validated local skill source directory.
    /// </summary>
    public string SkillDirectory { get; }

    /// <summary>
    ///  Validates and normalizes all source-built inputs before a coordinator acquires the target lock.
    /// </summary>
    /// <param name="sourceCheckout">The Filtrace source checkout that produced the artifacts.</param>
    /// <param name="cliPackagePath">The canonically named Filtrace CLI package.</param>
    /// <param name="dotnetPath">The <c>dotnet</c> host path or command name.</param>
    /// <param name="mcpDllPath">The locally built Filtrace MCP server assembly.</param>
    /// <param name="skillDirectory">The locally built Filtrace skill directory.</param>
    /// <returns>Normalized inputs safe to pass to the installation coordinator.</returns>
    public static LocalTestingInstallInputs Create(
        string sourceCheckout,
        string cliPackagePath,
        string dotnetPath,
        string mcpDllPath,
        string skillDirectory)
    {
        string source = ReadDirectory(
            sourceCheckout,
            nameof(sourceCheckout),
            "Filtrace source checkout",
            inspectContents: false);

        LocalTestingCliPackage package = LocalTestingCliPackage.Read(cliPackagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);

        ArgumentException.ThrowIfNullOrWhiteSpace(mcpDllPath);
        string mcp = Path.GetFullPath(mcpDllPath);
        if (Directory.Exists(mcp) || !RegularFileGuard.Exists(mcp, "Filtrace MCP server"))
        {
            throw new FileNotFoundException("Filtrace MCP server does not exist.", mcp);
        }

        string skill = ReadDirectory(
            skillDirectory,
            nameof(skillDirectory),
            "Filtrace skill source",
            inspectContents: true);

        return new(source, package.Path, dotnetPath, mcp, skill);
    }

    private static string ReadDirectory(
        string path,
        string parameterName,
        string description,
        bool inspectContents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        DirectoryInfo directory = new(fullPath);
        directory.Refresh();
        if (inspectContents
            && (directory.LinkTarget is not null
                || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) is not 0)))
        {
            throw new InvalidDataException($"{description} must not be a link: '{fullPath}'.");
        }

        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"{description} does not exist: '{fullPath}'.");
        }

        if (!inspectContents)
        {
            return Path.TrimEndingDirectorySeparator(ResolveDirectory(directory));
        }

        _ = DirectorySnapshot.Create(
            fullPath,
            description,
            LocalTestingBaselineCapturer.MaxSkillEntries,
            LocalTestingBaselineCapturer.MaxSkillBytes);

        return fullPath;
    }

    private static string ResolveDirectory(DirectoryInfo directory)
    {
        FileSystemInfo resolved = directory.ResolveLinkTarget(returnFinalTarget: true) ?? directory;
        return Path.GetFullPath(resolved.FullName);
    }
}
