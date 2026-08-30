// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingCliPackageTests
{
    [TestMethod]
    public void Read_ValidPackage_ReturnsExactIdentityAndHash()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreatePackage(directory.Path, LocalTestingCliPackage.PackageId, "1.2.3");
        string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)))
            .ToLowerInvariant();

        LocalTestingCliPackage package = LocalTestingCliPackage.Read(packagePath);

        package.Path.Should().Be(Path.GetFullPath(packagePath));
        package.Version.Should().Be("1.2.3");
        package.Sha256.Should().Be(expectedHash);
    }

    [TestMethod]
    public void Read_WrongPackageId_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreatePackage(directory.Path, "Other.Tool", "1.2.3");

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*Other.Tool*expected*KlutzyNinja.Filtrace*");
    }

    [TestMethod]
    public void Read_MultipleRootNuspecs_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreatePackage(directory.Path, LocalTestingCliPackage.PackageId, "1.2.3");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            WriteNuspec(archive, "second.nuspec", LocalTestingCliPackage.PackageId, "1.2.3");
        }

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*exactly one root nuspec; found 2*");
    }

    [TestMethod]
    public void Read_NonPackageExtension_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Join(directory.Path, "package.zip");
        File.WriteAllText(packagePath, "not a package");

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*must have a .nupkg extension*");
    }

    [TestMethod]
    public void Read_OversizedNuspec_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Join(directory.Path, "package.nupkg");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("package.nuspec");
            using Stream stream = entry.Open();
            stream.Write(new byte[(1024 * 1024) + 1]);
        }

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*nuspec exceeds the 1048576 byte safety limit*");
    }

    [TestMethod]
    public void ValidatePackageLength_AtLimit_DoesNotThrow()
    {
        Action validate = () => LocalTestingCliPackage.ValidatePackageLength(
            LocalTestingCliPackage.MaxPackageBytes,
            "package.nupkg");

        validate.Should().NotThrow();
    }

    [TestMethod]
    public void ValidatePackageLength_OverLimit_Throws()
    {
        Action validate = () => LocalTestingCliPackage.ValidatePackageLength(
            LocalTestingCliPackage.MaxPackageBytes + 1,
            "package.nupkg");

        validate.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds the 33554432 byte safety limit*");
    }

    [TestMethod]
    public void Read_OverPackageSizeLimit_ThrowsBeforeArchiveValidation()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Join(directory.Path, "package.nupkg");
        using (FileStream stream = File.Create(packagePath))
        {
            stream.SetLength(LocalTestingCliPackage.MaxPackageBytes + 1);
        }

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*CLI package exceeds the 33554432 byte safety limit*");
    }

    [TestMethod]
    public void ValidateArchiveLimits_AtLimits_DoesNotThrow()
    {
        long[] entryLengths = new long[LocalTestingCliPackage.MaxArchiveEntries];
        entryLengths[0] = LocalTestingCliPackage.MaxExpandedBytes;
        Action validate = () => LocalTestingCliPackage.ValidateArchiveLimits(
            entryLengths.Length,
            entryLengths,
            "package.nupkg");

        validate.Should().NotThrow();
    }

    [TestMethod]
    public void ValidateArchiveLimits_OverEntryLimit_Throws()
    {
        Action validate = () => LocalTestingCliPackage.ValidateArchiveLimits(
            LocalTestingCliPackage.MaxArchiveEntries + 1,
            [],
            "package.nupkg");

        validate.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds the 1024 entry safety limit*");
    }

    [TestMethod]
    public void ValidateArchiveLimits_OverExpandedSizeLimit_Throws()
    {
        Action validate = () => LocalTestingCliPackage.ValidateArchiveLimits(
            1,
            [LocalTestingCliPackage.MaxExpandedBytes + 1],
            "package.nupkg");

        validate.Should().Throw<InvalidDataException>()
            .WithMessage("*expands beyond the 134217728 byte safety limit*");
    }

    [TestMethod]
    public void Read_MalformedNuspec_ThrowsPackageError()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Join(directory.Path, "package.nupkg");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("package.nuspec");
            using StreamWriter writer = new(entry.Open());
            writer.Write("<package><metadata>");
        }

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*nuspec is not valid XML*")
            .WithInnerException<XmlException>();
    }

    [TestMethod]
    public void Read_NuspecWithExternalEntity_ThrowsPackageError()
    {
        using TemporaryDirectory directory = new();
        string secretPath = Path.Join(directory.Path, "secret.txt");
        File.WriteAllText(secretPath, "must not be read");
        string packagePath = Path.Join(directory.Path, "package.nupkg");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("package.nuspec");
            using StreamWriter writer = new(entry.Open());
            writer.Write(
                $"<!DOCTYPE package [<!ENTITY secret SYSTEM \"{new Uri(secretPath).AbsoluteUri}\">]>"
                + "<package><metadata><id>&secret;</id><version>1.2.3</version></metadata></package>");
        }

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*nuspec is not valid XML*")
            .WithInnerException<XmlException>();
    }

    private static string CreatePackage(string directory, string id, string version)
    {
        string packagePath = Path.Join(directory, "package.nupkg");
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteNuspec(archive, "package.nuspec", id, version);
        return packagePath;
    }

    private static void WriteNuspec(ZipArchive archive, string name, string id, string version)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write($"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
    }
}
