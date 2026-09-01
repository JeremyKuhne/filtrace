// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Persists the recoverable state of a local-testing installation for one target checkout.
/// </summary>
internal sealed record LocalTestingState
{
    /// <summary>
    ///  The schema version written and understood by this implementation.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    ///  Gets the schema version of this state document.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    ///  Gets the last durable phase reached by the local-testing workflow.
    /// </summary>
    public required LocalTestingStatus Status { get; init; }

    /// <summary>
    ///  Gets the absolute source-checkout path from which local-testing assets were installed.
    /// </summary>
    public required string SourceCheckout { get; init; }

    /// <summary>
    ///  Gets the target state captured before installation.
    /// </summary>
    public required LocalTestingBaseline Baseline { get; init; }

    /// <summary>
    ///  Gets the installed CLI package identity, when installation reached that step.
    /// </summary>
    public CliInstallation? Cli { get; init; }
}
