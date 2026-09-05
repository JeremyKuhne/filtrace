// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal sealed partial class CliManifestCorpus
{
    /// <summary>
    ///  Carries the schema marker and ordered cases written to one manifest file.
    /// </summary>
    /// <param name="SchemaVersion">The capture-manifest schema version.</param>
    /// <param name="Cases">The cases available to batch analysis or manifest pairing.</param>
    private sealed record ManifestFile(int SchemaVersion, IReadOnlyList<ManifestCase> Cases);
}
