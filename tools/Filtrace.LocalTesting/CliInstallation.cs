// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Identifies the exact Filtrace CLI package installed into the isolated tool directory.
/// </summary>
internal sealed record CliInstallation
{
    /// <summary>
    ///  Gets the installed NuGet package version.
    /// </summary>
    public required string PackageVersion { get; init; }

    /// <summary>
    ///  Gets the lowercase SHA-256 hash of the installed package bytes.
    /// </summary>
    public required string PackageSha256 { get; init; }
}
