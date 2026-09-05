// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Runtime.InteropServices;

namespace Filtrace.LocalTesting;

/// <summary>
///  Distinguishes regular files from links, directories, and special Unix file types.
/// </summary>
internal static partial class RegularFileGuard
{
    private const int FileTypeMask = 0xF000, RegularFile = 0x8000;

    /// <summary>
    ///  Determines whether a path names an existing regular file, rejecting links and special files.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <param name="description">A human-readable resource name used in validation errors.</param>
    /// <returns>
    ///  <see langword="true"/> for an existing regular file; <see langword="false"/> when the path is absent.
    /// </returns>
    public static bool Exists(string path, string description)
    {
        FileInfo file = new(path);
        file.Refresh();
        if (file.LinkTarget is not null)
        {
            throw new InvalidDataException($"{description} must not be a link: '{path}'.");
        }

        if (!file.Exists)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        if (LStat(path, out NativeFileStatus status) is not 0)
        {
            throw new IOException($"Could not inspect {description}: '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }

        if ((status.Mode & FileTypeMask) is not RegularFile)
        {
            throw new InvalidDataException($"{description} must be a regular file: '{path}'.");
        }

        return true;
    }

    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out NativeFileStatus status);
}
