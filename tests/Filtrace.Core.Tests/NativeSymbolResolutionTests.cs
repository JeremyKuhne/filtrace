// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Tracing.Readers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Touki;

namespace Filtrace.Tracing;

// Every case here reads an .etl, which TraceEvent can only convert on Windows.
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class NativeSymbolResolutionTests
{
    private static string EtwFixture => Path.Join(AppContext.BaseDirectory, "Fixtures", "etw.etl");

    // Resolution succeeding end to end is covered by tools/Test-NativeSymbolResolution.ps1
    // rather than by a committed capture. A filtrace capture records no PDB identity of
    // its own, so TraceEvent resolves a native module by reading the binary back from the
    // absolute path in the trace - which only exists on the machine that captured it.
    // These tests cover what a committed fixture can pin: how a miss is classified.

    [TestMethod]
    public void Read_NoSymbolsDirectory_SkipsTheLocalNativePass()
    {
        EtlReader reader = new();

        TraceReadResult result = reader.Read(EtwFixture);

        // The pass is what a caller opts into with --symbols; without one there is no
        // local path to search, so it must not run and must not report.
        result.NativeSymbols.Should().BeNull();
    }

    [TestMethod]
    public void Read_SymbolsDirectoryWithoutNativePdbs_ReportsMissingModulesByDescendingWeight()
    {
        using TemporaryDirectory symbols = new();
        EtlReader reader = new();

        TraceReadResult result = reader.Read(EtwFixture, symbols.Path);

        NativeSymbolInfo native = result.NativeSymbols!;
        native.UnresolvedFrameCount.Should().BeGreaterThan(0);
        native.ResolvedModules.Should().BeEmpty();
        native.MissingSymbolModules.Should().NotBeEmpty();
        native.IdentityMismatchModules.Should().BeEmpty();

        // Reported highest-impact first, so a caller reading only the head of the list
        // sees the modules that actually darken the profile.
        int[] unresolved = [.. native.MissingSymbolModules.Select(ParseUnresolvedFrames)];
        unresolved.Should().BeInDescendingOrder();
    }

    [TestMethod]
    public void Read_ManyUnresolvedModules_BoundsEachReportedCategory()
    {
        using TemporaryDirectory symbols = new();
        EtlReader reader = new();

        TraceReadResult result = reader.Read(EtwFixture, symbols.Path);

        // A pathological trace must not flood the output with a long tail of modules.
        result.NativeSymbols!.MissingSymbolModules.Count.Should().BeLessThanOrEqualTo(8);
    }

    [TestMethod]
    public void Read_SameNamedWrongIdentityNativePdb_ReportsMismatchRatherThanMissing()
    {
        EtlReader reader = new();

        // Let the pass itself pick the subject. Choosing independently risks naming a
        // module the pass never attempts - it ranks by unresolved sampled frames and skips
        // anything below a small share, which no separate ranking reproduces. The subject
        // also has to carry a recorded PDB identity, or there is nothing for a local file
        // to disagree with and the miss can only ever be "no symbol file".
        using TemporaryDirectory empty = new();
        IReadOnlyList<string> missing = reader.Read(EtwFixture, empty.Path).NativeSymbols!.MissingSymbolModules;

        string? moduleName = null;
        string? pdbName = null;
        foreach (string entry in missing)
        {
            string candidate = ParseModuleName(entry);
            pdbName = FindRecordedPdbName(candidate);
            if (pdbName is not null)
            {
                moduleName = candidate;
                break;
            }
        }

        moduleName.Should().NotBeNull("the ETW fixture has an unresolved module with a recorded PDB name");

        using TemporaryDirectory symbols = new();
        // Any bytes under the expected PDB name: the file exists, so the only thing that
        // can disagree is the recorded identity (signature and age).
        File.WriteAllText(Path.Join(symbols.Path, pdbName!), "not a real pdb");

        TraceReadResult result = reader.Read(EtwFixture, symbols.Path);

        // The same module has to move category, which is the whole distinction: present
        // but wrong is a different problem for the caller than absent.
        NativeSymbolInfo native = result.NativeSymbols!;
        native.IdentityMismatchModules.Should().Contain(
            module => module.StartsWith(moduleName, StringComparison.OrdinalIgnoreCase));
        native.MissingSymbolModules.Should().NotContain(
            module => module.StartsWith(moduleName, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CreateInfo_ReportsEveryOutcomeThatCanReachIt()
    {
        // A failed lookup must not vanish: with no category of its own it would leave
        // frames counted as unresolved and no module accounting for them.
        NativeModuleSymbolStatus[] statuses =
        [
            new("resolved", NativeSymbolStatus.Resolved, 40, 0.4),
            new("missing", NativeSymbolStatus.NoSymbolFile, 30, 0.3),
            new("mismatch", NativeSymbolStatus.IdentityMismatch, 20, 0.2),
            new("failed", NativeSymbolStatus.LookupFailed, 10, 0.1)
        ];

        NativeSymbolInfo info = NativeSymbolResolution.CreateInfo(statuses)!;

        info.ResolvedModules.Should().ContainSingle().Which.Should().StartWith("resolved");
        info.MissingSymbolModules.Should().ContainSingle().Which.Should().StartWith("missing");
        info.IdentityMismatchModules.Should().ContainSingle().Which.Should().StartWith("mismatch");
        info.LookupFailedModules.Should().ContainSingle().Which.Should().StartWith("failed");
        info.UnresolvedFrameCount.Should().Be(100);
    }

    // The PDB filename the trace recorded for a module, or null when it recorded none -
    // not every native module in a capture carries symbol identity.
    private static string? FindRecordedPdbName(string moduleName)
    {
        using TraceLog traceLog = TraceLog.OpenOrConvert(EtwFixture);
        foreach (TraceModuleFile moduleFile in traceLog.ModuleFiles)
        {
            if (string.Equals(moduleFile.Name, moduleName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(moduleFile.PdbName))
            {
                return Path.GetFileName(moduleFile.PdbName);
            }
        }

        return null;
    }

    // Entries read "module (1234 unresolved, 47%)".
    private static string ParseModuleName(string entry) =>
        entry[..entry.LastIndexOf(" (", StringComparison.Ordinal)];

    private static int ParseUnresolvedFrames(string entry)
    {
        // Entries read "module (1234 unresolved, 47%)".
        int open = entry.LastIndexOf(" (", StringComparison.Ordinal);
        string count = entry[(open + 2)..entry.IndexOf(' ', open + 2)];
        return int.Parse(count, CultureInfo.InvariantCulture);
    }

    private sealed class TemporaryDirectory : DisposableBase
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                $"filtrace-native-symbols-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory must not fail a passing test.
            }
        }
    }
}
