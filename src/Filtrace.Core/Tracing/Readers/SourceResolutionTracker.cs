// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Aggregates managed frame, method, module, source-line, and local PDB identity coverage for one trace read.
/// </summary>
internal sealed partial class SourceResolutionTracker
{
    private const int MaxTrackedModules = 1024;

    /// <summary>
    ///  The unique-method ceiling after which method-level counts become unavailable instead of growing without bound.
    /// </summary>
    internal const int MaxTrackedMethods = 16384;
    private const int MaxReportedMatchingModules = 16;
    private const int MaxReportedUnmappedModules = 8;
    private const int MaxReportedMismatchModules = 8;
    private const int MaxReportedUnmappedMethods = 5;
    private const int MaxModuleNameLength = 120;
    private const int MaxMethodNameLength = 120;

    private readonly string? _localSymbolPath;
    private readonly string[] _searchedDirectories;
    private readonly Dictionary<int, ModuleResolution> _modules = [];
    private readonly Dictionary<string, ModuleResolution> _modulesWithoutMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, MethodResolution> _methods = [];
    private int _sampledManagedFrames;
    private int _mappedManagedFrames;
    private int _unmappedNamedManagedFrames;
    private bool _methodCountsUnavailable;

    /// <summary>
    ///  Creates a tracker for one trace read and its effective local symbol paths.
    /// </summary>
    /// <param name="symbolsDirectory">The caller-supplied local directory reported in diagnostics.</param>
    /// <param name="localSymbolPath">
    ///  The effective path used for PDB lookup, including extracted PDBs when present.
    /// </param>
    public SourceResolutionTracker(string? symbolsDirectory, string? localSymbolPath)
    {
        _localSymbolPath = localSymbolPath;
        _searchedDirectories = string.IsNullOrEmpty(symbolsDirectory)
            ? []
            : [symbolsDirectory];
    }

    /// <summary>
    ///  Records source-resolution evidence for a sampled address that resolves to a managed method.
    /// </summary>
    /// <param name="address">The sampled code address and its TraceEvent method/module metadata.</param>
    /// <param name="methodName">The rendered method name, or <see langword="null"/> when unnamed.</param>
    /// <param name="sourceMapped">Whether the address resolved to a source sequence point.</param>
    public void Observe(TraceCodeAddress address, string? methodName, bool sourceMapped)
    {
        TraceMethod? method = address.Method;
        if (method is null)
        {
            return;
        }

        TraceModuleFile? module = method.MethodModuleFile ?? address.ModuleFile;
        ObserveManagedFrame(
            (int)method.MethodIndex,
            module,
            module?.Name ?? address.ModuleName,
            methodName,
            sourceMapped);
    }

    /// <summary>
    ///  Records one managed frame using a stable method key and optional module metadata.
    /// </summary>
    /// <param name="methodKey">The trace-local method identity used to deduplicate method counts.</param>
    /// <param name="module">The trace module metadata, or <see langword="null"/> when unavailable.</param>
    /// <param name="moduleName">A fallback module name used for display and consolidation.</param>
    /// <param name="methodName">The rendered method name, or <see langword="null"/> when unnamed.</param>
    /// <param name="sourceMapped">Whether this frame resolved to source.</param>
    internal void ObserveManagedFrame(
        int methodKey,
        TraceModuleFile? module,
        string? moduleName,
        string? methodName,
        bool sourceMapped)
    {
        ObserveModule(module, moduleName, sourceMapped);
        if (!sourceMapped && !string.IsNullOrEmpty(methodName))
        {
            _unmappedNamedManagedFrames = SaturatingIncrement(_unmappedNamedManagedFrames);
        }

        ObserveMethod(methodKey, moduleName, methodName, sourceMapped);
    }

    /// <summary>
    ///  Adds one sampled frame to module-level totals while bounding distinct tracked modules.
    /// </summary>
    /// <param name="module">The trace module metadata, or <see langword="null"/> when unavailable.</param>
    /// <param name="moduleName">A fallback module name used when metadata is unavailable.</param>
    /// <param name="sourceMapped">Whether the sampled frame resolved to source.</param>
    internal void ObserveModule(TraceModuleFile? module, string? moduleName, bool sourceMapped)
    {
        _sampledManagedFrames = SaturatingIncrement(_sampledManagedFrames);
        if (sourceMapped)
        {
            _mappedManagedFrames = SaturatingIncrement(_mappedManagedFrames);
        }

        string name = NormalizeModuleName(moduleName);
        ModuleResolution? resolution;
        if (module is null)
        {
            if (_modulesWithoutMetadata.TryGetValue(name, out resolution))
            {
                ObserveResolution(resolution, sourceMapped);
                return;
            }

            if (_modules.Count + _modulesWithoutMetadata.Count == MaxTrackedModules)
            {
                return;
            }

            resolution = new ModuleResolution(name, module: null);
            _modulesWithoutMetadata.Add(name, resolution);
            ObserveResolution(resolution, sourceMapped);
            return;
        }

        int key = (int)module.ModuleFileIndex;
        if (!_modules.TryGetValue(key, out resolution))
        {
            if (_modules.Count + _modulesWithoutMetadata.Count == MaxTrackedModules)
            {
                return;
            }

            resolution = new ModuleResolution(name, module);
            _modules.Add(key, resolution);
        }

        ObserveResolution(resolution, sourceMapped);
    }

    private static void ObserveResolution(ModuleResolution resolution, bool sourceMapped)
    {
        resolution.SampledFrames = SaturatingIncrement(resolution.SampledFrames);
        if (sourceMapped)
        {
            resolution.MappedFrames = SaturatingIncrement(resolution.MappedFrames);
        }
    }

    private void ObserveMethod(
        int methodKey,
        string? moduleName,
        string? methodName,
        bool sourceMapped)
    {
        if (_methodCountsUnavailable)
        {
            return;
        }

        if (!_methods.TryGetValue(methodKey, out MethodResolution? resolution))
        {
            if (_methods.Count == MaxTrackedMethods)
            {
                _methods.Clear();
                _methodCountsUnavailable = true;
                return;
            }

            resolution = new MethodResolution(
                string.IsNullOrEmpty(methodName)
                    ? null
                    : NormalizeMethodName(moduleName, methodName));

            _methods.Add(methodKey, resolution);
        }

        resolution.SampledFrames = SaturatingIncrement(resolution.SampledFrames);
        if (sourceMapped)
        {
            resolution.MappedFrames = SaturatingIncrement(resolution.MappedFrames);
        }
    }

    /// <summary>
    ///  Matches available PDBs, consolidates repeated module names, and builds bounded source-quality diagnostics.
    /// </summary>
    /// <returns>
    ///  Frame and method coverage together with matching, mismatched, and highest-impact unmapped modules.
    /// </returns>
    public SourceResolutionInfo CreateInfo()
    {
        List<ModuleResolution> modules = [.. _modules.Values, .. _modulesWithoutMetadata.Values];
        if (!string.IsNullOrEmpty(_localSymbolPath))
        {
            try
            {
                using SymbolReader reader = new(TextWriter.Null, _localSymbolPath, httpClientDelegatingHandler: null);
                foreach (ModuleResolution resolution in modules)
                {
                    resolution.PdbStatus = resolution.MappedFrames > 0
                        ? PdbMatchStatus.Matched
                        : GetPdbMatchStatus(
                            reader,
                            resolution.Module,
                            _searchedDirectories.FirstOrDefault());
                }
            }
            catch (Exception)
            {
                foreach (ModuleResolution resolution in modules)
                {
                    resolution.PdbStatus = resolution.MappedFrames > 0
                        ? PdbMatchStatus.Matched
                        : PdbMatchStatus.NotFound;
                }
            }
        }
        else
        {
            foreach (ModuleResolution resolution in modules)
            {
                resolution.PdbStatus = resolution.MappedFrames > 0
                    ? PdbMatchStatus.Matched
                    : PdbMatchStatus.NotFound;
            }
        }

        Dictionary<string, ModuleResolution> consolidated = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleResolution module in modules)
        {
            if (!consolidated.TryGetValue(module.Name, out ModuleResolution? aggregate))
            {
                aggregate = new ModuleResolution(module.Name, module: null);
                consolidated.Add(module.Name, aggregate);
            }

            aggregate.SampledFrames = SaturatingAdd(aggregate.SampledFrames, module.SampledFrames);
            aggregate.MappedFrames = SaturatingAdd(aggregate.MappedFrames, module.MappedFrames);
            aggregate.PdbStatus = MergePdbMatchStatus(
                aggregate.PdbStatus,
                module.PdbStatus);
        }

        modules = [.. consolidated.Values];

        string[] matchingPdbModules =
        [
            .. modules
                .Where(static module => module.PdbStatus == PdbMatchStatus.Matched)
                .OrderBy(static module => module.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxReportedMatchingModules)
                .Select(static module => module.Name)
        ];

        string[] pdbIdentityMismatchModules =
        [
            .. modules
                .Where(static module => module.PdbStatus == PdbMatchStatus.IdentityMismatch)
                .OrderByDescending(static module => module.SampledFrames - module.MappedFrames)
                .ThenBy(static module => module.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxReportedMismatchModules)
                .Select(static module => module.Name)
        ];

        string[] highestUnmappedModules =
        [
            .. modules
                .Where(static module => module.MappedFrames < module.SampledFrames)
                .OrderByDescending(static module => module.SampledFrames - module.MappedFrames)
                .ThenBy(static module => module.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxReportedUnmappedModules)
                .Select(static module => $"{module.Name} ({module.MappedFrames}/{module.SampledFrames} mapped)")
        ];

        int? sampledManagedMethodCount = null;
        int? sourceMappedManagedMethodCount = null;
        string[] highestUnmappedMethods = [];
        if (!_methodCountsUnavailable)
        {
            sampledManagedMethodCount = _methods.Count;
            sourceMappedManagedMethodCount = _methods.Values.Count(
                static method => method.MappedFrames > 0);

            Dictionary<string, MethodResolution> consolidatedMethods = new(StringComparer.Ordinal);
            foreach (MethodResolution method in _methods.Values)
            {
                if (method.Name is null)
                {
                    continue;
                }

                if (!consolidatedMethods.TryGetValue(method.Name, out MethodResolution? aggregate))
                {
                    aggregate = new MethodResolution(method.Name);
                    consolidatedMethods.Add(method.Name, aggregate);
                }

                aggregate.SampledFrames = SaturatingAdd(
                    aggregate.SampledFrames,
                    method.SampledFrames);

                aggregate.MappedFrames = SaturatingAdd(
                    aggregate.MappedFrames,
                    method.MappedFrames);
            }

            highestUnmappedMethods =
            [
                .. consolidatedMethods.Values
                    .Where(static method => method.MappedFrames < method.SampledFrames)
                    .OrderByDescending(static method => method.SampledFrames - method.MappedFrames)
                    .ThenBy(static method => method.Name, StringComparer.Ordinal)
                    .Take(MaxReportedUnmappedMethods)
                    .Select(static method =>
                        $"{method.Name} ({method.MappedFrames}/{method.SampledFrames} mapped)")
            ];
        }

        return new SourceResolutionInfo(
            _searchedDirectories,
            _sampledManagedFrames,
            _mappedManagedFrames,
            matchingPdbModules,
            highestUnmappedModules)
        {
            PdbIdentityMismatchModules = pdbIdentityMismatchModules,
            SampledManagedMethodCount = sampledManagedMethodCount,
            SourceMappedManagedMethodCount = sourceMappedManagedMethodCount,
            UnmappedNamedManagedFrameCount = _unmappedNamedManagedFrames,
            HighestUnmappedMethods = highestUnmappedMethods
        };
    }

    /// <summary>
    ///  Tests whether local symbol lookup finds the exact PDB signature and age recorded for a module.
    /// </summary>
    /// <param name="symbolPath">The local symbol search path.</param>
    /// <param name="pdbName">The PDB name recorded in module metadata.</param>
    /// <param name="pdbSignature">The expected portable or Windows PDB signature.</param>
    /// <param name="pdbAge">The expected PDB age.</param>
    /// <param name="modulePath">The module path supplied as symbol-reader context.</param>
    /// <param name="fileVersion">The module version supplied as symbol-reader context.</param>
    /// <returns>
    ///  <see langword="true"/> only when lookup verifies the requested identity; failures return <see langword="false"/>.
    /// </returns>
    internal static bool HasMatchingPdb(
        string symbolPath,
        string pdbName,
        Guid pdbSignature,
        int pdbAge,
        string modulePath = "",
        string fileVersion = "")
    {
        if (string.IsNullOrEmpty(symbolPath)
            || string.IsNullOrEmpty(pdbName)
            || pdbSignature == Guid.Empty)
        {
            return false;
        }

        try
        {
            using SymbolReader reader = new(TextWriter.Null, symbolPath, httpClientDelegatingHandler: null);
            return GetPdbMatchStatus(
                reader,
                candidateDirectory: null,
                pdbName,
                pdbSignature,
                pdbAge,
                modulePath,
                fileVersion) == PdbMatchStatus.Matched;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///  Distinguishes an exact PDB match, a same-named identity mismatch, and an absent candidate.
    /// </summary>
    /// <param name="symbolPath">The local symbol search path.</param>
    /// <param name="candidateDirectory">The directory checked for a same-named mismatched PDB.</param>
    /// <param name="pdbName">The PDB name recorded in module metadata.</param>
    /// <param name="pdbSignature">The expected PDB signature.</param>
    /// <param name="pdbAge">The expected PDB age.</param>
    /// <param name="modulePath">The module path supplied as symbol-reader context.</param>
    /// <param name="fileVersion">The module version supplied as symbol-reader context.</param>
    /// <returns>The strongest PDB identity outcome that can be established locally.</returns>
    internal static PdbMatchStatus GetPdbMatchStatus(
        string symbolPath,
        string candidateDirectory,
        string pdbName,
        Guid pdbSignature,
        int pdbAge,
        string modulePath = "",
        string fileVersion = "")
    {
        if (string.IsNullOrEmpty(symbolPath))
        {
            return PdbMatchStatus.NotFound;
        }

        try
        {
            using SymbolReader reader = new(TextWriter.Null, symbolPath, httpClientDelegatingHandler: null);
            return GetPdbMatchStatus(
                reader,
                candidateDirectory,
                pdbName,
                pdbSignature,
                pdbAge,
                modulePath,
                fileVersion);
        }
        catch (Exception)
        {
            return PdbMatchStatus.NotFound;
        }
    }

    private static PdbMatchStatus GetPdbMatchStatus(
        SymbolReader reader,
        TraceModuleFile? module,
        string? candidateDirectory) =>
            module is null
                ? PdbMatchStatus.NotFound
                : GetPdbMatchStatus(
                    reader,
                    candidateDirectory,
                    module.PdbName,
                    module.PdbSignature,
                    module.PdbAge,
                    module.FilePath,
                    module.FileVersion);

    private static PdbMatchStatus GetPdbMatchStatus(
        SymbolReader reader,
        string? candidateDirectory,
        string pdbName,
        Guid pdbSignature,
        int pdbAge,
        string modulePath,
        string fileVersion)
    {
        if (string.IsNullOrEmpty(pdbName) || pdbSignature == Guid.Empty)
        {
            return PdbMatchStatus.NotFound;
        }

        try
        {
            if (!string.IsNullOrEmpty(reader.FindSymbolFilePath(
                pdbName,
                pdbSignature,
                pdbAge,
                modulePath,
                fileVersion,
                portablePdbMatch: true)))
            {
                return PdbMatchStatus.Matched;
            }
        }
        catch (Exception)
        {
            return PdbMatchStatus.NotFound;
        }

        return HasSameNamedPdb(candidateDirectory, pdbName)
            ? PdbMatchStatus.IdentityMismatch
            : PdbMatchStatus.NotFound;
    }

    private static bool HasSameNamedPdb(string? candidateDirectory, string pdbName)
    {
        if (string.IsNullOrEmpty(candidateDirectory))
        {
            return false;
        }

        try
        {
            string fileName = GetPdbFileName(pdbName);
            return !string.IsNullOrEmpty(fileName)
                && File.Exists(Path.Join(candidateDirectory, fileName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///  Extracts a PDB leaf name from either Windows- or Unix-delimited metadata paths.
    /// </summary>
    /// <param name="path">The recorded PDB path or leaf name.</param>
    /// <returns>The text following the final slash or backslash.</returns>
    internal static string GetPdbFileName(string path)
    {
        int separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>
    ///  Combines repeated module outcomes using exact match, identity mismatch, then not found as precedence.
    /// </summary>
    /// <param name="first">One module-instance outcome.</param>
    /// <param name="second">Another module-instance outcome.</param>
    /// <returns>The strongest evidence supplied by either outcome.</returns>
    internal static PdbMatchStatus MergePdbMatchStatus(
        PdbMatchStatus first,
        PdbMatchStatus second)
    {
        if (first == PdbMatchStatus.Matched || second == PdbMatchStatus.Matched)
        {
            return PdbMatchStatus.Matched;
        }

        return first == PdbMatchStatus.IdentityMismatch
            || second == PdbMatchStatus.IdentityMismatch
                ? PdbMatchStatus.IdentityMismatch
                : PdbMatchStatus.NotFound;
    }

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;

    /// <summary>
    ///  Replaces a missing module name, bounds its UTF-16 length, and converts control characters to spaces.
    /// </summary>
    /// <param name="name">The trace-derived module name.</param>
    /// <returns>A display-safe module name of at most 120 characters.</returns>
    internal static string NormalizeModuleName(string? name)
    {
        string value = string.IsNullOrEmpty(name) ? "(unknown managed module)" : name;
        return NormalizeDisplayText(value, MaxModuleNameLength);
    }

    /// <summary>
    ///  Builds a bounded <c>module!method</c> identity after removing the method parameter list.
    /// </summary>
    /// <param name="moduleName">The trace-derived module name.</param>
    /// <param name="methodName">The rendered method name, optionally followed by parameters.</param>
    /// <returns>A display-safe method identity of at most 120 characters.</returns>
    internal static string NormalizeMethodName(string? moduleName, string methodName)
    {
        int parameters = methodName.IndexOf('(');
        string name = parameters < 0 ? methodName : methodName[..parameters];
        string value = $"{NormalizeModuleName(moduleName)}!{name}";
        return NormalizeDisplayText(value, MaxMethodNameLength);
    }

    private static string NormalizeDisplayText(string value, int maxLength)
    {
        int length = Math.Min(value.Length, maxLength);
        int firstControl = -1;
        for (int index = 0; index < length; index++)
        {
            if (char.IsControl(value[index]))
            {
                firstControl = index;
                break;
            }
        }

        if (firstControl < 0)
        {
            return length == value.Length ? value : value[..length];
        }

        char[] normalized = value[..length].ToCharArray();
        for (int index = firstControl; index < normalized.Length; index++)
        {
            if (char.IsControl(normalized[index]))
            {
                normalized[index] = ' ';
            }
        }

        return new string(normalized);
    }

}
