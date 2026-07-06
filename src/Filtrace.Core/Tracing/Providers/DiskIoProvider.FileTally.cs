// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

public sealed partial class DiskIoProvider
{
    // A mutable per-file accumulator used only while tallying; the immutable
    // DiskIoFileRecord is built from it at the end.
    private struct FileTally
    {
        public long ReadBytes;
        public long WriteBytes;
        public int ReadCount;
        public int WriteCount;
        public double TotalDiskMs;
    }
}
