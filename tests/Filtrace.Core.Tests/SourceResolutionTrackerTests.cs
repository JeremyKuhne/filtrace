// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Reflection.PortableExecutable;
using Filtrace.Tracing.Readers;

namespace Filtrace.Tracing;

[TestClass]
public sealed class SourceResolutionTrackerTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void NormalizeSymbolsDirectory_NullOrEmpty_ReturnsNull(string? symbolsDirectory)
    {
        TraceLogReader.NormalizeSymbolsDirectory(symbolsDirectory).Should().BeNull();
    }

    [TestMethod]
    public void NormalizeSymbolsDirectory_ExistingDirectory_ReturnsCanonicalPath()
    {
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, AppContext.BaseDirectory);

        string? normalized = TraceLogReader.NormalizeSymbolsDirectory(relative);

        normalized.Should().Be(Path.GetFullPath(relative));
    }

    [TestMethod]
    [DataRow("srv*cache*https://evil.example/symbols")]
    [DataRow("cache*symbols")]
    [DataRow(@"\\server\share\build")]
    public void NormalizeSymbolsDirectory_SymbolPathSyntax_Throws(string symbolsDirectory)
    {
        Action act = () => TraceLogReader.NormalizeSymbolsDirectory(symbolsDirectory);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("symbolsDirectory")
            .WithMessage("*symbol-path syntax*");
    }

    [TestMethod]
    public void NormalizeSymbolsDirectory_MultipleEntries_Throws()
    {
        const string symbolsDirectory = "first;second";

        Action act = () => TraceLogReader.NormalizeSymbolsDirectory(symbolsDirectory);

        act.Should().Throw<ArgumentException>().WithParameterName("symbolsDirectory");
    }

    [TestMethod]
    public void NormalizeSymbolsDirectory_CanonicalPathWithSeparator_Throws()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"filtrace;symbols-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Action act = () => TraceLogReader.NormalizeSymbolsDirectory(directory);

            act.Should().Throw<ArgumentException>().WithParameterName("symbolsDirectory");
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void NormalizeSymbolsDirectory_MissingDirectory_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"filtrace-missing-{Guid.NewGuid():N}");

        Action act = () => TraceLogReader.NormalizeSymbolsDirectory(missing);

        act.Should().Throw<DirectoryNotFoundException>().WithMessage("Symbols directory*");
    }

    [TestMethod]
    public void HasMatchingPdb_ExactPortablePdbIdentity_ReturnsTrue()
    {
        string modulePath = typeof(SourceResolutionTrackerTests).Assembly.Location;
        string directory = Path.GetDirectoryName(modulePath)!;
        using FileStream stream = File.OpenRead(modulePath);
        using PEReader reader = new(stream);
        DebugDirectoryEntry codeView = reader.ReadDebugDirectory()
            .Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData data = reader.ReadCodeViewDebugDirectoryData(codeView);

        bool matched = SourceResolutionTracker.HasMatchingPdb(
            directory,
            data.Path,
            data.Guid,
            data.Age,
            modulePath);

        matched.Should().BeTrue();
        SourceResolutionTracker.HasMatchingPdb(
            directory,
            data.Path,
            Guid.NewGuid(),
            data.Age,
            modulePath).Should().BeFalse();
        SourceResolutionTracker.HasMatchingPdb(
            directory,
            data.Path,
            data.Guid,
            data.Age + 1,
            modulePath).Should().BeFalse();
    }

    [TestMethod]
    public void GetPdbMatchStatus_LocalCandidate_ClassifiesIdentity()
    {
        string modulePath = typeof(SourceResolutionTrackerTests).Assembly.Location;
        string directory = Path.GetDirectoryName(modulePath)!;
        using FileStream stream = File.OpenRead(modulePath);
        using PEReader reader = new(stream);
        DebugDirectoryEntry codeView = reader.ReadDebugDirectory()
            .Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData data = reader.ReadCodeViewDebugDirectoryData(codeView);

        SourceResolutionTracker.GetPdbMatchStatus(
            directory,
            directory,
            data.Path,
            data.Guid,
            data.Age,
            modulePath).Should().Be(SourceResolutionTracker.PdbMatchStatus.Matched);
        SourceResolutionTracker.GetPdbMatchStatus(
            directory,
            directory,
            data.Path,
            Guid.NewGuid(),
            data.Age,
            modulePath).Should().Be(SourceResolutionTracker.PdbMatchStatus.IdentityMismatch);
        SourceResolutionTracker.GetPdbMatchStatus(
            directory,
            directory,
            data.Path,
            data.Guid,
            data.Age + 1,
            modulePath).Should().Be(SourceResolutionTracker.PdbMatchStatus.IdentityMismatch);

        string emptyDirectory = Path.Combine(Path.GetTempPath(), $"filtrace-symbols-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDirectory);
        try
        {
            SourceResolutionTracker.GetPdbMatchStatus(
                emptyDirectory,
                emptyDirectory,
                data.Path,
                data.Guid,
                data.Age,
                modulePath).Should().Be(SourceResolutionTracker.PdbMatchStatus.NotFound);
        }
        finally
        {
            Directory.Delete(emptyDirectory);
        }
    }

    [TestMethod]
    [DataRow(@"C:\build\MyApp.pdb")]
    [DataRow("/build/MyApp.pdb")]
    [DataRow("MyApp.pdb")]
    public void GetPdbFileName_AnyTracePathStyle_ReturnsLeaf(string path)
    {
        SourceResolutionTracker.GetPdbFileName(path).Should().Be("MyApp.pdb");
    }

    [TestMethod]
    public void MergePdbMatchStatus_ExactMatchTakesPrecedence()
    {
        SourceResolutionTracker.MergePdbMatchStatus(
            SourceResolutionTracker.PdbMatchStatus.IdentityMismatch,
            SourceResolutionTracker.PdbMatchStatus.Matched)
            .Should().Be(SourceResolutionTracker.PdbMatchStatus.Matched);
        SourceResolutionTracker.MergePdbMatchStatus(
            SourceResolutionTracker.PdbMatchStatus.Matched,
            SourceResolutionTracker.PdbMatchStatus.IdentityMismatch)
            .Should().Be(SourceResolutionTracker.PdbMatchStatus.Matched);
        SourceResolutionTracker.MergePdbMatchStatus(
            SourceResolutionTracker.PdbMatchStatus.NotFound,
            SourceResolutionTracker.PdbMatchStatus.IdentityMismatch)
            .Should().Be(SourceResolutionTracker.PdbMatchStatus.IdentityMismatch);
    }

    [TestMethod]
    public void ObserveManagedFrame_ReportsSequencePointsAndNamedFramesWithoutSource()
    {
        SourceResolutionTracker tracker = new(null, null);
        tracker.ObserveManagedFrame(1, null, "ModuleA", "Mapped", sourceMapped: false);
        tracker.ObserveManagedFrame(1, null, "ModuleA", "Mapped", sourceMapped: true);
        tracker.ObserveManagedFrame(2, null, "ModuleA", "Unmapped", sourceMapped: false);
        tracker.ObserveManagedFrame(2, null, "ModuleA", "Unmapped", sourceMapped: false);
        tracker.ObserveManagedFrame(3, null, "ModuleA", null, sourceMapped: false);
        tracker.ObserveManagedFrame(4, null, "ModuleA", "Unmapped(int)", sourceMapped: false);

        SourceResolutionInfo source = tracker.CreateInfo();

        source.SampledManagedMethodCount.Should().Be(4);
        source.SourceMappedManagedMethodCount.Should().Be(1);
        source.UnmappedNamedManagedFrameCount.Should().Be(4);
        source.HighestUnmappedMethods.Should().Equal(
            "ModuleA!Unmapped (0/3 mapped)",
            "ModuleA!Mapped (1/2 mapped)");
    }

    [TestMethod]
    public void ObserveManagedFrame_TooManyMethods_MakesUniqueCountsUnavailable()
    {
        SourceResolutionTracker tracker = new(null, null);
        for (int methodKey = 0; methodKey <= SourceResolutionTracker.MaxTrackedMethods; methodKey++)
        {
            tracker.ObserveManagedFrame(
                methodKey,
                null,
                "ModuleA",
                "Run",
                sourceMapped: false);
        }

        SourceResolutionInfo source = tracker.CreateInfo();

        source.SampledManagedMethodCount.Should().BeNull();
        source.SourceMappedManagedMethodCount.Should().BeNull();
        source.HighestUnmappedMethods.Should().BeEmpty();
        source.UnmappedNamedManagedFrameCount.Should().Be(
            SourceResolutionTracker.MaxTrackedMethods + 1);
    }

    [TestMethod]
    public void Read_OuterSymbolsDirectory_SeparatesNamesFromSourceQuality()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "activity.nettrace");
        NetTraceReader reader = new();

        TraceReadResult result = reader.Read(path, AppContext.BaseDirectory);

        result.SymbolResolutionRate.Should().Be(1.0);
        SourceResolutionInfo source = result.SourceResolution!;
        source.SearchedDirectories.Should().Equal(AppContext.BaseDirectory);
        source.SampledManagedFrameCount.Should().BeGreaterThan(0);
        source.MappedManagedFrameCount.Should().BeLessThan(source.SampledManagedFrameCount);
        source.SampledManagedMethodCount.Should().BeGreaterThan(0);
        source.SourceMappedManagedMethodCount.Should().NotBeNull();
        source.SourceMappedManagedMethodCount.Value.Should().BeLessThanOrEqualTo(
            source.SampledManagedMethodCount.Value);
        source.UnmappedNamedManagedFrameCount.Should().BeGreaterThan(0);
        source.HighestUnmappedMethods.Should().NotBeEmpty();
        source.HighestUnmappedModules.Should().Contain(
            module => module.Contains("HotLoopBench", StringComparison.OrdinalIgnoreCase));
        source.PdbIdentityMismatchModules.Should().BeEmpty();
        string[] moduleNames =
        [
            .. source.HighestUnmappedModules
            .Select(static module => module.Split(" (", StringSplitOptions.None)[0])
        ];
        moduleNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().HaveCount(moduleNames.Length);
    }

    [TestMethod]
    public void Read_SameNamedWrongIdentityPdb_ReportsMismatch()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "activity.nettrace");
        string modulePath = typeof(SourceResolutionTrackerTests).Assembly.Location;
        string assemblyDirectory = Path.GetDirectoryName(modulePath)!;
        using FileStream stream = File.OpenRead(modulePath);
        using PEReader peReader = new(stream);
        DebugDirectoryEntry codeView = peReader.ReadDebugDirectory()
            .Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData data = peReader.ReadCodeViewDebugDirectoryData(codeView);
        string sourcePdb = Path.Combine(assemblyDirectory, Path.GetFileName(data.Path));
        string symbolsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"filtrace-wrong-pdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(symbolsDirectory);
        try
        {
            File.Copy(sourcePdb, Path.Combine(symbolsDirectory, "HotLoopBench.pdb"));
            NetTraceReader reader = new();

            TraceReadResult result = reader.Read(path, symbolsDirectory);

            result.SourceResolution!.PdbIdentityMismatchModules.Should().Contain(
                module => module.Contains("HotLoopBench", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(symbolsDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void NormalizeModuleName_ControlCharactersAndLongInput_ReturnsBoundedDisplayText()
    {
        string name = $"Hot\tLoop\0Bench{new string('x', 200)}";

        string normalized = SourceResolutionTracker.NormalizeModuleName(name);

        normalized.Should().HaveLength(120);
        normalized.Should().StartWith("Hot Loop Bench");
        normalized.Any(char.IsControl).Should().BeFalse();

        string method = SourceResolutionTracker.NormalizeMethodName(
            "Hot\tLoop",
            $"Run\0{new string('x', 200)}(class System.String)");
        method.Should().HaveLength(120);
        method.Should().StartWith("Hot Loop!Run ");
        method.Any(char.IsControl).Should().BeFalse();
    }

    [TestMethod]
    public void ObserveModule_WithoutMetadata_KeepsDistinctNormalizedNames()
    {
        SourceResolutionTracker tracker = new(null, null);
        tracker.ObserveModule(null, "ModuleA", sourceMapped: false);
        tracker.ObserveModule(null, "ModuleB", sourceMapped: false);
        tracker.ObserveModule(null, "modulea", sourceMapped: true);

        SourceResolutionInfo source = tracker.CreateInfo();

        source.SampledManagedFrameCount.Should().Be(3);
        source.MappedManagedFrameCount.Should().Be(1);
        source.MatchingPdbModules.Should().Equal("ModuleA");
        source.HighestUnmappedModules.Should().BeEquivalentTo(
            "ModuleA (1/2 mapped)",
            "ModuleB (0/1 mapped)");
    }
}