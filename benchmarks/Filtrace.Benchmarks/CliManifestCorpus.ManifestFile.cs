// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal sealed partial class CliManifestCorpus
{
    /// <summary>
    ///  Defines the serialized benchmark manifest root.
    /// </summary>
    private sealed record ManifestFile(int SchemaVersion, IReadOnlyList<ManifestCase> Cases);
}