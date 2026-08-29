// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Filtrace.LocalTesting;

internal sealed class LocalTestingBaselineCapturer
{
    internal const int MaxMcpConfigurationBytes = 1024 * 1024;
    internal const int MaxSkillEntries = 2048;
    internal const long MaxSkillBytes = 16 * 1024 * 1024;

    public LocalTestingBaseline Capture(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureDirectory(plan.TargetRoot, "Target repository");
        EnsureDirectory(plan.GitDirectory, "Git directory");
        EnsureDirectory(plan.ArtifactsDirectory, "Local-testing artifacts directory");

        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.McpConfigurationPath);
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.SkillDestination);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.ArtifactsDirectory);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.SkillBackupPath);

        McpBaseline mcp = CaptureMcp(plan.McpConfigurationPath);
        SkillBaseline skill = CaptureSkill(plan.SkillDestination, plan.SkillBackupPath);

        return new()
        {
            Mcp = mcp,
            Skill = skill,
            CreatedDirectories = CaptureCreatedDirectories(plan)
        };
    }

    private static McpBaseline CaptureMcp(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                $"VS Code MCP configuration is a directory, not a file: '{path}'.");
        }
        if (!RegularFileGuard.Exists(path, "VS Code MCP configuration"))
        {
            return new();
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxMcpConfigurationBytes)
        {
            throw new InvalidDataException(
                $"VS Code MCP configuration exceeds the {MaxMcpConfigurationBytes} byte safety limit: '{path}'.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new()
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"VS Code MCP configuration root must be a JSON object: '{path}'.");
            }

            bool serversExisted = TryGetUniqueProperty(root, "servers", path, out JsonElement servers);
            if (!serversExisted)
            {
                return new() { FileExisted = true };
            }
            if (servers.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"VS Code MCP configuration property 'servers' must be a JSON object: '{path}'.");
            }

            bool serverExisted = TryGetUniqueProperty(
                servers,
                "filtrace",
                path,
                out JsonElement server);
            if (serverExisted && server.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"VS Code MCP server 'filtrace' must be a JSON object: '{path}'.");
            }

            return new()
            {
                FileExisted = true,
                ServersExisted = true,
                ServerExisted = serverExisted,
                Server = serverExisted ? server.Clone() : null
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"VS Code MCP configuration is not valid JSON: '{path}'.",
                exception);
        }
    }

    private static SkillBaseline CaptureSkill(string source, string backup)
    {
        if (File.Exists(source))
        {
            throw new InvalidDataException(
                $"Skill destination is a file, not a directory: '{source}'.");
        }
        if (!Directory.Exists(source))
        {
            return new();
        }
        if (File.Exists(backup) || Directory.Exists(backup))
        {
            throw new InvalidDataException($"Skill backup already exists: '{backup}'.");
        }

        DirectorySnapshot sourceSnapshot = DirectorySnapshot.Create(
            source,
            MaxSkillEntries,
            MaxSkillBytes);
        string staging = $"{backup}.{Guid.NewGuid():N}.tmp";
        try
        {
            sourceSnapshot.CopyTo(source, staging);
            DirectorySnapshot backupSnapshot = DirectorySnapshot.Create(
                staging,
                MaxSkillEntries,
                MaxSkillBytes);
            if (!sourceSnapshot.Fingerprint.Equals(
                backupSnapshot.Fingerprint,
                StringComparison.Ordinal))
            {
                throw new IOException("Skill backup did not match the source snapshot.");
            }

            Directory.Move(staging, backup);
            return new()
            {
                Existed = true,
                BackupSha256 = backupSnapshot.Fingerprint
            };
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static CreatedDirectoryBaseline CaptureCreatedDirectories(ResourcePlan plan)
    {
        string vscode = Path.GetDirectoryName(plan.McpConfigurationPath)
            ?? throw new InvalidDataException("MCP configuration has no parent directory.");
        string agents = Path.Join(plan.TargetRoot, ".agents");
        string skills = Path.Join(agents, "skills");

        return new()
        {
            Vscode = !Directory.Exists(vscode),
            Agents = !Directory.Exists(agents),
            Skills = !Directory.Exists(skills)
        };
    }

    private static bool TryGetUniqueProperty(
        JsonElement parent,
        string propertyName,
        string path,
        out JsonElement value)
    {
        bool found = false;
        value = default;
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName))
            {
                continue;
            }
            if (found)
            {
                throw new InvalidDataException(
                    $"VS Code MCP configuration contains duplicate '{propertyName}' properties: '{path}'.");
            }

            found = true;
            value = property.Value;
        }

        return found;
    }

    private static void EnsureDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{description} does not exist: '{path}'.");
        }
    }
}

internal static class ManagedPathGuard
{
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

internal sealed class DirectorySnapshot
{
    private readonly DirectorySnapshotEntry[] _entries;

    private DirectorySnapshot(DirectorySnapshotEntry[] entries, string fingerprint)
    {
        _entries = entries;
        Fingerprint = fingerprint;
    }

    public string Fingerprint { get; }

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

internal sealed record DirectorySnapshotEntry(string RelativePath, bool IsDirectory, long Length, byte[]? Sha256);

internal static class RegularFileGuard
{
    private const int FileTypeMask = 0xF000, RegularFile = 0x8000;

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

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct NativeFileStatus
    {
        [FieldOffset(4)] public int Mode;
    }
}
