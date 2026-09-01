// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Filtrace.LocalTesting;

/// <summary>
///  Represents a structurally validated Filtrace CLI package and its content identity.
/// </summary>
/// <param name="Path">The absolute package path.</param>
/// <param name="Version">The version read from the package manifest.</param>
/// <param name="Sha256">The lowercase SHA-256 hash of the package bytes.</param>
internal sealed record LocalTestingCliPackage(string Path, string Version, string Sha256)
{
    /// <summary>
    ///  The NuGet package id required by local CLI installation.
    /// </summary>
    public const string PackageId = "KlutzyNinja.Filtrace";

    /// <summary>
    ///  The maximum number of ZIP entries accepted from a package.
    /// </summary>
    internal const int MaxArchiveEntries = 1024;

    /// <summary>
    ///  The maximum aggregate uncompressed ZIP-entry length, in bytes.
    /// </summary>
    internal const long MaxExpandedBytes = 128L * 1024 * 1024;

    /// <summary>
    ///  The maximum package-file length, in bytes.
    /// </summary>
    internal const long MaxPackageBytes = 32L * 1024 * 1024;
    private const int MaxNuspecBytes = 1024 * 1024;

    /// <summary>
    ///  Validates a prepared package, including its canonical <c>id.version.nupkg</c> file name.
    /// </summary>
    /// <param name="packagePath">The package path to validate.</param>
    /// <returns>The validated absolute path, manifest version, and package hash.</returns>
    public static LocalTestingCliPackage Read(string packagePath)
    {
        return Read(packagePath, requireCanonicalName: true);
    }

    /// <summary>
    ///  Validates a package found in the installed tool store without requiring its cached file name to be canonical.
    /// </summary>
    /// <param name="packagePath">The installed package path to validate.</param>
    /// <returns>The validated absolute path, manifest version, and package hash.</returns>
    internal static LocalTestingCliPackage ReadInstalled(string packagePath)
    {
        return Read(packagePath, requireCanonicalName: false);
    }

    private static LocalTestingCliPackage Read(string packagePath, bool requireCanonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        string fullPath = System.IO.Path.GetFullPath(packagePath);
        if (!System.IO.Path.GetExtension(fullPath).Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"CLI package must have a .nupkg extension: '{fullPath}'.");
        }

        if (!RegularFileGuard.Exists(fullPath, "CLI package"))
        {
            throw new FileNotFoundException("CLI package does not exist.", fullPath);
        }

        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidatePackageLength(stream.Length, fullPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ValidateArchiveLimits(
            archive.Entries.Count,
            archive.Entries.Select(entry => entry.Length),
            fullPath);

        static bool IsRootNuspec(ZipArchiveEntry entry)
        {
            return entry.Name.Equals(entry.FullName, StringComparison.Ordinal)
                && entry.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase);
        }

        ZipArchiveEntry[] nuspecs = [.. archive.Entries.Where(IsRootNuspec)];

        if (nuspecs.Length is not 1)
        {
            throw new InvalidDataException(
                $"CLI package must contain exactly one root nuspec; found {nuspecs.Length}: '{fullPath}'.");
        }

        (string id, string version) = ReadIdentity(nuspecs[0], fullPath);
        if (!id.Equals(PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"CLI package id is '{id}'; expected '{PackageId}': '{fullPath}'.");
        }

        string expectedName = $"{PackageId}.{version}.nupkg";
        if (requireCanonicalName && !System.IO.Path.GetFileName(fullPath).Equals(
            expectedName,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"CLI package must be named '{expectedName}': '{fullPath}'.");
        }

        return new(fullPath, version, sha256);
    }

    private static (string Id, string Version) ReadIdentity(
        ZipArchiveEntry nuspec,
        string packagePath)
    {
        if (nuspec.Length > MaxNuspecBytes)
        {
            throw new InvalidDataException(
                $"CLI package nuspec exceeds the {MaxNuspecBytes} byte safety limit: '{packagePath}'.");
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxNuspecBytes,
            XmlResolver = null
        };

        using Stream stream = nuspec.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document;
        try
        {
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"CLI package nuspec is not valid XML: '{packagePath}'.",
                exception);
        }

        XElement package = document.Root is { Name.LocalName: "package" } root
            ? root
            : throw new InvalidDataException($"CLI package nuspec has no package root: '{packagePath}'.");

        XElement metadata = ReadSingleElement(package, "metadata", packagePath);
        string id = ReadSingleElement(metadata, "id", packagePath).Value.Trim();
        string version = ReadSingleElement(metadata, "version", packagePath).Value.Trim();
        if (id.Length is 0 || version.Length is 0)
        {
            throw new InvalidDataException($"CLI package nuspec identity is incomplete: '{packagePath}'.");
        }

        return (id, version);
    }

    /// <summary>
    ///  Rejects a package file whose on-disk length exceeds the bounded read budget.
    /// </summary>
    /// <param name="length">The package-file length in bytes.</param>
    /// <param name="packagePath">The path included in a validation error.</param>
    internal static void ValidatePackageLength(long length, string packagePath)
    {
        if (length > MaxPackageBytes)
        {
            throw new InvalidDataException(
                $"CLI package exceeds the {MaxPackageBytes} byte safety limit: '{packagePath}'.");
        }
    }

    /// <summary>
    ///  Rejects excessive ZIP entry counts, expanded size, and expanded-length overflow.
    /// </summary>
    /// <param name="entryCount">The package's ZIP entry count.</param>
    /// <param name="entryLengths">The uncompressed length of each entry.</param>
    /// <param name="packagePath">The path included in a validation error.</param>
    internal static void ValidateArchiveLimits(
        int entryCount,
        IEnumerable<long> entryLengths,
        string packagePath)
    {
        if (entryCount > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"CLI package exceeds the {MaxArchiveEntries} entry safety limit: '{packagePath}'.");
        }

        long totalLength = 0;
        try
        {
            foreach (long length in entryLengths)
            {
                totalLength = checked(totalLength + length);
                if (totalLength > MaxExpandedBytes)
                {
                    throw new InvalidDataException(
                        $"CLI package expands beyond the {MaxExpandedBytes} byte safety limit: '{packagePath}'.");
                }
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"CLI package expanded size is invalid: '{packagePath}'.",
                exception);
        }
    }

    private static XElement ReadSingleElement(XElement parent, string name, string packagePath)
    {
        XElement[] matches = [.. parent.Elements().Where(
            element => element.Name.LocalName.Equals(name, StringComparison.Ordinal))];

        return matches.Length is 1
            ? matches[0]
            : throw new InvalidDataException(
                $"CLI package nuspec must contain exactly one '{name}' element: '{packagePath}'.");
    }
}
