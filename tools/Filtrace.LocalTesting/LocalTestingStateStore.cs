// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.LocalTesting;

/// <summary>
///  Reads, validates, and atomically replaces the durable recovery state for local testing.
/// </summary>
internal sealed class LocalTestingStateStore
{
    /// <summary>
    ///  The maximum accepted state-document length, in bytes.
    /// </summary>
    internal const int MaxStateBytes = 1024 * 1024;

    /// <summary>
    ///  Reads and validates existing state without creating a file when none exists.
    /// </summary>
    /// <param name="statePath">The state JSON path.</param>
    /// <returns>The validated state, or <see langword="null"/> when the path does not exist.</returns>
    public LocalTestingState? Read(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        if (!File.Exists(statePath))
        {
            return null;
        }

        using FileStream stream = new(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        if (stream.Length > MaxStateBytes)
        {
            throw new InvalidDataException(
                $"Local-testing state exceeds the {MaxStateBytes} byte safety limit: '{statePath}'.");
        }

        LocalTestingState? state;
        try
        {
            state = JsonSerializer.Deserialize(
                stream,
                LocalTestingJsonContext.Default.LocalTestingState);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Local-testing state is not valid JSON: '{statePath}'.",
                exception);
        }

        if (state is null)
        {
            throw new InvalidDataException($"Local-testing state is empty: '{statePath}'.");
        }

        Validate(state);
        return state;
    }

    /// <summary>
    ///  Validates and writes state through a flushed sibling temporary file before replacing the target.
    /// </summary>
    /// <param name="statePath">The state JSON path in an existing directory.</param>
    /// <param name="state">The internally consistent recovery state to persist.</param>
    public void Write(string statePath, LocalTestingState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(statePath));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Local-testing state directory does not exist: '{directory}'.");
        }

        string temporaryPath = Path.Join(
            directory,
            $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (FileStream stream = new(temporaryPath, options))
            {
                JsonSerializer.Serialize(
                    stream,
                    state,
                    LocalTestingJsonContext.Default.LocalTestingState);

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void Validate(LocalTestingState state)
    {
        if (state.SchemaVersion != LocalTestingState.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported local-testing schema version '{state.SchemaVersion}'.");
        }

        if (state.Status is LocalTestingStatus.Unknown || !Enum.IsDefined(state.Status))
        {
            throw new InvalidDataException("Local-testing state has an unknown status.");
        }

        if (string.IsNullOrWhiteSpace(state.SourceCheckout)
            || !Path.IsPathFullyQualified(state.SourceCheckout))
        {
            throw new InvalidDataException("Local-testing source checkout must be an absolute path.");
        }

        ValidateBaseline(state.Baseline);
        if (state.Status is LocalTestingStatus.Active && state.Cli is null)
        {
            throw new InvalidDataException("Active local-testing state must record the CLI package.");
        }

        if (state.Cli is not null)
        {
            if (string.IsNullOrWhiteSpace(state.Cli.PackageVersion))
            {
                throw new InvalidDataException("CLI package version is missing.");
            }

            ValidateSha256(state.Cli.PackageSha256, "CLI package");
        }
    }

    private static void ValidateBaseline(LocalTestingBaseline? baseline)
    {
        if (baseline is null)
        {
            throw new InvalidDataException("Local-testing baseline is missing.");
        }

        ValidateMcpBaseline(baseline.Mcp);
        if (baseline.Skill is null)
        {
            throw new InvalidDataException("Local-testing skill baseline is missing.");
        }

        if (baseline.CreatedDirectories is null)
        {
            throw new InvalidDataException("Local-testing created-directory baseline is missing.");
        }

        if (baseline.Skill.Existed)
        {
            ValidateSha256(baseline.Skill.BackupSha256, "Skill backup");
        }
        else if (baseline.Skill.BackupSha256 is not null)
        {
            throw new InvalidDataException(
                "An absent skill baseline cannot contain a backup hash.");
        }

        if (baseline.CreatedDirectories.Agents && !baseline.CreatedDirectories.Skills)
        {
            throw new InvalidDataException(
                "A created .agents directory requires a created skills directory.");
        }
    }

    /// <summary>
    ///  Verifies that MCP existence flags and the retained server value describe a possible prior configuration.
    /// </summary>
    /// <param name="baseline">The baseline to validate.</param>
    internal static void ValidateMcpBaseline(McpBaseline? baseline)
    {
        if (baseline is null)
        {
            throw new InvalidDataException("Local-testing MCP baseline is missing.");
        }

        if (baseline.ServerExisted)
        {
            if (!baseline.FileExisted
                || !baseline.ServersExisted
                || baseline.Server is null
                || baseline.Server.Value.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "An existing MCP server baseline requires an existing file, an existing 'servers' object, and an object-valued 'filtrace' entry.");
            }
        }
        else if (baseline.Server is not null)
        {
            throw new InvalidDataException(
                "An absent MCP server baseline cannot contain a server value.");
        }

        if (baseline.ServersExisted && !baseline.FileExisted)
        {
            throw new InvalidDataException(
                "An existing MCP servers baseline requires an existing file.");
        }
    }

    private static void ValidateSha256(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{description} SHA-256 is not valid.");
        }
    }
}
