// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Tracing.Readers;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing;

[TestClass]
public sealed class NativeSymbolResolutionTests
{
    private static string EtwFixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "etw.etl");

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
        (string moduleName, string pdbName) = FindUnresolvedNativeModule();

        using TemporaryDirectory symbols = new();
        // Any bytes under the expected PDB name: the file exists, so the only thing that
        // can disagree is the recorded identity (signature and age).
        File.WriteAllText(Path.Combine(symbols.Path, pdbName), "not a real pdb");
        EtlReader reader = new();

        TraceReadResult result = reader.Read(EtwFixture, symbols.Path);

        NativeSymbolInfo native = result.NativeSymbols!;
        native.IdentityMismatchModules.Should().Contain(
            module => module.StartsWith(moduleName, StringComparison.OrdinalIgnoreCase));
        native.MissingSymbolModules.Should().NotContain(
            module => module.StartsWith(moduleName, StringComparison.OrdinalIgnoreCase));
    }

    // The heaviest unresolved module in the fixture, with the PDB name the trace recorded
    // for it. Read from the trace rather than hard-coded so the test pins the behavior
    // rather than a particular machine's kernel build.
    private static (string ModuleName, string PdbName) FindUnresolvedNativeModule()
    {
        using TraceLog traceLog = TraceLog.OpenOrConvert(EtwFixture);
        TraceModuleFile? heaviest = null;
        int heaviestAddresses = 0;
        foreach (TraceModuleFile moduleFile in traceLog.ModuleFiles)
        {
            if (string.IsNullOrEmpty(moduleFile.Name)
                || string.IsNullOrEmpty(moduleFile.PdbName)
                || moduleFile.CodeAddressesInModule <= heaviestAddresses)
            {
                continue;
            }

            heaviest = moduleFile;
            heaviestAddresses = moduleFile.CodeAddressesInModule;
        }

        heaviest.Should().NotBeNull("the ETW fixture carries native modules with recorded PDB names");
        return (heaviest!.Name, Path.GetFileName(heaviest.PdbName));
    }

    private static int ParseUnresolvedFrames(string entry)
    {
        // Entries read "module (1234 unresolved, 47%)".
        int open = entry.LastIndexOf(" (", StringComparison.Ordinal);
        string count = entry[(open + 2)..entry.IndexOf(' ', open + 2)];
        return int.Parse(count, CultureInfo.InvariantCulture);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"filtrace-native-symbols-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
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
