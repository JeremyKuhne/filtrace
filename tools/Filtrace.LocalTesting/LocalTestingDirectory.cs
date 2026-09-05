// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Provides deletion behavior for fixed operation trees already validated and owned by local testing.
/// </summary>
internal static class LocalTestingDirectory
{
    /// <summary>
    ///  Recursively deletes an owned operation tree, clearing Windows read-only attributes first.
    /// </summary>
    /// <param name="path">The validated fixed or private operation path to delete.</param>
    public static void DeleteTree(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                path,
                "*",
                SearchOption.AllDirectories))
            {
                ClearReadOnly(entry);
            }

            ClearReadOnly(path);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnly(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) is not 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
