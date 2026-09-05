// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Runtime.InteropServices;

namespace Filtrace.LocalTesting;

internal static partial class RegularFileGuard
{
    /// <summary>
    ///  Receives the native file-mode field returned by <c>SystemNative_LStat</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct NativeFileStatus
    {
        /// <summary>
        ///  The Unix file mode, including its file-type bits.
        /// </summary>
        [FieldOffset(4)] public int Mode;
    }
}
