// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Filtrace.LocalTesting;

/// <summary>
///  Holds a bounded, link-free inventory of a directory for verified backup copying.
/// </summary>
internal sealed class DirectorySnapshot
{
    private readonly DirectorySnapshotEntry[] _entries;

    private DirectorySnapshot(DirectorySnapshotEntry[] entries, string fingerprint)
    {
        _entries = entries;
        Fingerprint = fingerprint;
    }

    /// <summary>
    ///  Gets a deterministic SHA-256 fingerprint of entry paths, kinds, lengths, and file contents.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    ///  Inventories a directory while enforcing entry-count, byte-size, and link-safety limits.
    /// </summary>
    /// <param name="root">The directory to inventory.</param>
    /// <param name="maxEntries">The maximum number of files and directories to retain.</param>
    /// <param name="maxBytes">The maximum aggregate file length in bytes.</param>
    /// <returns>An immutable snapshot ordered by relative path.</returns>
    public static DirectorySnapshot Create(string root, int maxEntries, long maxBytes)
    {
        List<DirectorySnapshotEntry> entries = new();
        Stack<string> pending = new();
        pending.Push(root);
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (FileSystemInfo item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                item.Refresh();
                if ((item.Attributes & FileAttributes.ReparsePoint) is not 0
                    || item.LinkTarget is not null)
                {
                    throw new InvalidDataException(
                        $"Skill destination must not contain links: '{item.FullName}'.");
                }

                string relativePath = Path.GetRelativePath(root, item.FullName).Replace(
                    Path.DirectorySeparatorChar,
                    '/');

                if (item is DirectoryInfo child)
                {
                    entries.Add(new(relativePath, IsDirectory: true, Length: 0, Sha256: null));
                    pending.Push(child.FullName);
                }
                else if (item is FileInfo file)
                {
                    if (!RegularFileGuard.Exists(file.FullName, "Skill destination entry"))
                    {
                        throw new IOException(
                            $"Skill destination entry disappeared: '{file.FullName}'.");
                    }

                    totalBytes = checked(totalBytes + file.Length);
                    if (totalBytes > maxBytes)
                    {
                        throw new InvalidDataException(
                            $"Skill destination exceeds the {maxBytes} byte safety limit: '{root}'.");
                    }

                    using FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                    entries.Add(new(
                        relativePath,
                        IsDirectory: false,
                        file.Length,
                        SHA256.HashData(stream)));
                }
                else
                {
                    throw new InvalidDataException(
                        $"Skill destination contains an unsupported entry: '{item.FullName}'.");
                }

                if (entries.Count > maxEntries)
                {
                    throw new InvalidDataException(
                        $"Skill destination exceeds the {maxEntries} entry safety limit: '{root}'.");
                }
            }
        }

        DirectorySnapshotEntry[] ordered = [.. entries.OrderBy(
            entry => entry.RelativePath,
            StringComparer.Ordinal)];

        return new(ordered, ComputeFingerprint(ordered));
    }

    /// <summary>
    ///  Copies the inventoried directory tree without overwriting any destination file.
    /// </summary>
    /// <param name="sourceRoot">The directory from which snapshot entries are read.</param>
    /// <param name="destinationRoot">The new directory that receives the snapshot entries.</param>
    public void CopyTo(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (DirectorySnapshotEntry entry in _entries.Where(entry => entry.IsDirectory))
        {
            Directory.CreateDirectory(Path.Join(
                destinationRoot,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        foreach (DirectorySnapshotEntry entry in _entries.Where(entry => !entry.IsDirectory))
        {
            string relativePath = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            File.Copy(
                Path.Join(sourceRoot, relativePath),
                Path.Join(destinationRoot, relativePath),
                overwrite: false);
        }
    }

    private static string ComputeFingerprint(DirectorySnapshotEntry[] entries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[sizeof(long)];
        foreach (DirectorySnapshotEntry entry in entries)
        {
            hash.AppendData(entry.IsDirectory ? [1] : [2]);
            byte[] path = Encoding.UTF8.GetBytes(entry.RelativePath);
            BinaryPrimitives.WriteInt32LittleEndian(number, path.Length);
            hash.AppendData(number[..sizeof(int)]);
            hash.AppendData(path);
            if (!entry.IsDirectory)
            {
                BinaryPrimitives.WriteInt64LittleEndian(number, entry.Length);
                hash.AppendData(number);
                hash.AppendData(entry.Sha256!);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
