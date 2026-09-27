// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Cheap filesystem identity used to bind a conversion without hashing a large trace.
/// </summary>
/// <param name="Length">The file size in bytes.</param>
/// <param name="LastWriteTimeUtcTicks">The UTC last-write time in ticks.</param>
internal readonly record struct EtlxFileIdentity(long Length, long LastWriteTimeUtcTicks)
{
    /// <summary>
    ///  Reads the file size and UTC write time, failing if the file disappeared.
    /// </summary>
    /// <param name="path">The source trace or ETLX cache path.</param>
    /// <returns>The current file identity.</returns>
    internal static EtlxFileIdentity ReadExisting(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"ETLX cache or source file not found: {path}", path);
        }

        return new EtlxFileIdentity(file.Length, file.LastWriteTimeUtc.Ticks);
    }
}
