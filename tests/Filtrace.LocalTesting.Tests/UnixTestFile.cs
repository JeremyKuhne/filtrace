// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Filtrace.LocalTesting.Tests;

internal static class UnixTestFile
{
    public static void CreateFifo(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (MkFifo(path, 0x180) is not 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [DllImport(
        "libc",
        EntryPoint = "mkfifo",
        SetLastError = true)]
    private static extern int MkFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int mode);
}
