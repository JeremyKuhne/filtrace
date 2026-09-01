// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Describes one link-free entry retained in a bounded directory snapshot.
/// </summary>
/// <param name="RelativePath">The slash-delimited path relative to the snapshot root.</param>
/// <param name="IsDirectory">Whether the entry is a directory rather than a file.</param>
/// <param name="Length">The file length in bytes, or zero for a directory.</param>
/// <param name="Sha256">The file-content hash, or <see langword="null"/> for a directory.</param>
internal sealed record DirectorySnapshotEntry(string RelativePath, bool IsDirectory, long Length, byte[]? Sha256);
