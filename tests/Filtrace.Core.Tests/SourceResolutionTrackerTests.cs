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
        source.HighestUnmappedModules.Should().Contain(
            module => module.Contains("HotLoopBench", StringComparison.OrdinalIgnoreCase));
        string[] moduleNames =
        [
            .. source.HighestUnmappedModules
            .Select(static module => module.Split(" (", StringSplitOptions.None)[0])
        ];
        moduleNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().HaveCount(moduleNames.Length);
    }

    [TestMethod]
    public void NormalizeModuleName_ControlCharactersAndLongInput_ReturnsBoundedDisplayText()
    {
        string name = $"Hot\tLoop\0Bench{new string('x', 200)}";

        string normalized = SourceResolutionTracker.NormalizeModuleName(name);

        normalized.Should().HaveLength(120);
        normalized.Should().StartWith("Hot Loop Bench");
        normalized.Any(char.IsControl).Should().BeFalse();
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