// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Readers;

internal sealed class SourceResolutionTracker
{
    private const int MaxTrackedModules = 1024;
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

    public SourceResolutionTracker(string? symbolsDirectory, string? localSymbolPath)
    {
        _localSymbolPath = localSymbolPath;
        _searchedDirectories = string.IsNullOrEmpty(symbolsDirectory)
            ? []
            : [symbolsDirectory];
    }

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

            resolution = new ModuleResolution(name, null);
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

    public SourceResolutionInfo CreateInfo()
    {
        List<ModuleResolution> modules = [.. _modules.Values, .. _modulesWithoutMetadata.Values];
        if (!string.IsNullOrEmpty(_localSymbolPath))
        {
            try
            {
                using SymbolReader reader = new(TextWriter.Null, _localSymbolPath, null);
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
                aggregate = new ModuleResolution(module.Name, null);
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
            using SymbolReader reader = new(TextWriter.Null, symbolPath, null);
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
            using SymbolReader reader = new(TextWriter.Null, symbolPath, null);
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
                && File.Exists(Path.Combine(candidateDirectory, fileName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string GetPdbFileName(string path)
    {
        int separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator < 0 ? path : path[(separator + 1)..];
    }

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

    internal static string NormalizeModuleName(string? name)
    {
        string value = string.IsNullOrEmpty(name) ? "(unknown managed module)" : name;
        return NormalizeDisplayText(value, MaxModuleNameLength);
    }

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

    private sealed class ModuleResolution(string name, TraceModuleFile? module)
    {
        public string Name { get; } = name;
        public TraceModuleFile? Module { get; } = module;
        public int SampledFrames { get; set; }
        public int MappedFrames { get; set; }
        public PdbMatchStatus PdbStatus { get; set; }
    }

    private sealed class MethodResolution(string? name)
    {
        public string? Name { get; } = name;
        public int SampledFrames { get; set; }
        public int MappedFrames { get; set; }
    }

    internal enum PdbMatchStatus
    {
        NotFound,
        IdentityMismatch,
        Matched
    }
}