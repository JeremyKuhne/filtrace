// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Rejects managed paths that escape an expected root or traverse a file-system link.
/// </summary>
internal static class ManagedPathGuard
{
    /// <summary>
    ///  Verifies that a candidate stays beneath the expected root and that each existing path component is link-free.
    /// </summary>
    /// <param name="root">The directory that must contain the candidate.</param>
    /// <param name="candidate">The path whose existing components are inspected.</param>
    public static void EnsureNoLinks(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Managed path is outside its expected root: '{candidate}'.");
        }

        string[] components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        string current = root;
        for (int index = 0; index < components.Length; index++)
        {
            current = Path.Join(current, components[index]);
            if (!TryGetAttributes(current, out FileAttributes attributes))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) is not 0)
            {
                throw new InvalidDataException($"Managed path must not contain links: '{current}'.");
            }

            if (index < components.Length - 1
                && (attributes & FileAttributes.Directory) is 0)
            {
                throw new InvalidDataException(
                    $"Managed path ancestor is not a directory: '{current}'.");
            }
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
