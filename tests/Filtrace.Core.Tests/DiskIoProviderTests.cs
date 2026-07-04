// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class DiskIoProviderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // The disk I/O fixture is a trimmed ETW capture of a workload that writes several
    // files with write-through, so the trace carries the physical DiskIO write events
    // this provider reads, resolved to the workload's file names.
    private static DiskIoResult LoadDiskIo() =>
        new DiskIoProvider().Read(FixturePath("diskio.etl"));

    [TestMethod]
    public void Read_DiskIoFixture_ReportsWrites()
    {
        DiskIoResult result = LoadDiskIo();

        result.WriteCount.Should().BeGreaterThan(0, "the workload writes several files with write-through");
        result.TotalWriteBytes.Should().BeGreaterThan(0);
        result.TotalDiskMs.Should().BeGreaterThan(0.0);
        result.Files.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Read_DiskIoFixture_ResolvesTheWorkloadFileNames()
    {
        DiskIoResult result = LoadDiskIo();

        // The write-through workload writes files named block-N.bin; the trim keeps the
        // file-name rundown entries for the files its disk I/O touched, so the events
        // resolve to those names rather than to a raw device path.
        result.Files.Should().Contain(f => f.FileName.Contains("block", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Read_DiskIoFixture_PerFileTalliesSumToTheTotals()
    {
        DiskIoResult result = LoadDiskIo();

        result.Files.Sum(static f => f.ReadBytes).Should().Be(result.TotalReadBytes);
        result.Files.Sum(static f => f.WriteBytes).Should().Be(result.TotalWriteBytes);
        result.Files.Sum(static f => f.ReadCount).Should().Be(result.ReadCount);
        result.Files.Sum(static f => f.WriteCount).Should().Be(result.WriteCount);
        result.Files.Sum(static f => f.TotalDiskMs).Should().BeApproximately(result.TotalDiskMs, 0.001);
    }

    [TestMethod]
    public void Read_DiskIoFixture_RanksFilesByDiskTimeDescending()
    {
        DiskIoResult result = LoadDiskIo();

        for (int i = 1; i < result.Files.Count; i++)
        {
            result.Files[i].TotalDiskMs.Should().BeLessThanOrEqualTo(result.Files[i - 1].TotalDiskMs);
        }
    }

    [TestMethod]
    public void Read_DiskIoFixture_EveryRecordIsWellFormed()
    {
        DiskIoResult result = LoadDiskIo();

        result.Files.Should().OnlyContain(f => f.FileName.Length > 0);
        result.Files.Should().OnlyContain(f => f.ReadBytes >= 0 && f.WriteBytes >= 0);
        result.Files.Should().OnlyContain(f => f.ReadCount >= 0 && f.WriteCount >= 0);
        result.Files.Should().OnlyContain(f => f.TotalDiskMs >= 0.0);
        // A kept file has at least one operation.
        result.Files.Should().OnlyContain(f => f.ReadCount + f.WriteCount > 0);
    }

    [TestMethod]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        DiskIoProvider provider = new();

        Action act = () => provider.Read(FixturePath("does-not-exist.etl"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void Read_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        DiskIoProvider provider = new();

        Action act = () => provider.Read(path!);

        act.Should().Throw<ArgumentException>();
    }
}
