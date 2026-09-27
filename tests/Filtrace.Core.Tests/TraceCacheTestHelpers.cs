// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

internal static class TraceCacheTestHelpers
{
    internal static void CorruptCacheWithoutChangingIdentity(string cachePath)
    {
        EtlxFileIdentity original = EtlxFileIdentity.ReadExisting(cachePath);
        using (FileStream stream = new(cachePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Write("not an ETLX cache"u8);
        }

        File.SetLastWriteTimeUtc(
            cachePath,
            new DateTime(original.LastWriteTimeUtcTicks, DateTimeKind.Utc));

        EtlxFileIdentity.ReadExisting(cachePath).Should().Be(original);
    }
}
