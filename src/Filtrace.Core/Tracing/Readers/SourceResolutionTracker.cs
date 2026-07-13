// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Readers;

internal sealed class SourceResolutionTracker
{
    private const int MaxTrackedModules = 1024;
    private const int MaxReportedMatchingModules = 16;
    private const int MaxReportedUnmappedModules = 8;
    private const int MaxModuleNameLength = 120;

    private readonly string? _localSymbolPath;
    private readonly string[] _searchedDirectories;
    private readonly Dictionary<int, ModuleResolution> _modules = [];
    private int _sampledManagedFrames;
    private int _mappedManagedFrames;

    public SourceResolutionTracker(string? symbolsDirectory, string? localSymbolPath)
    {
        _localSymbolPath = localSymbolPath;
        _searchedDirectories = string.IsNullOrEmpty(symbolsDirectory)
            ? []
            : [symbolsDirectory];
    }

    public void Observe(TraceCodeAddress address, bool sourceMapped)
    {
        TraceMethod? method = address.Method;
        if (method is null)
        {
            return;
        }

        _sampledManagedFrames = SaturatingIncrement(_sampledManagedFrames);
        if (sourceMapped)
        {
            _mappedManagedFrames = SaturatingIncrement(_mappedManagedFrames);
        }

        TraceModuleFile? module = method.MethodModuleFile ?? address.ModuleFile;
        int key = module is null ? int.MinValue : (int)module.ModuleFileIndex;
        if (!_modules.TryGetValue(key, out ModuleResolution? resolution))
        {
            if (_modules.Count == MaxTrackedModules)
            {
                return;
            }

            string name = module?.Name ?? address.ModuleName;
            resolution = new ModuleResolution(NormalizeModuleName(name), module);
            _modules.Add(key, resolution);
        }

        resolution.SampledFrames = SaturatingIncrement(resolution.SampledFrames);
        if (sourceMapped)
        {
            resolution.MappedFrames = SaturatingIncrement(resolution.MappedFrames);
        }
    }

    public SourceResolutionInfo CreateInfo()
    {
        List<ModuleResolution> modules = [.. _modules.Values];
        if (!string.IsNullOrEmpty(_localSymbolPath))
        {
            try
            {
                using SymbolReader reader = new(TextWriter.Null, _localSymbolPath, null);
                foreach (ModuleResolution resolution in modules)
                {
                    resolution.MatchingPdb = resolution.MappedFrames > 0
                        || HasMatchingPdb(reader, resolution.Module);
                }
            }
            catch (Exception)
            {
                foreach (ModuleResolution resolution in modules)
                {
                    resolution.MatchingPdb = resolution.MappedFrames > 0;
                }
            }
        }
        else
        {
            foreach (ModuleResolution resolution in modules)
            {
                resolution.MatchingPdb = resolution.MappedFrames > 0;
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
            aggregate.MatchingPdb |= module.MatchingPdb;
        }

        modules = [.. consolidated.Values];

        string[] matchingPdbModules =
        [
            .. modules
                .Where(static module => module.MatchingPdb)
                .OrderBy(static module => module.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxReportedMatchingModules)
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

        return new SourceResolutionInfo(
            _searchedDirectories,
            _sampledManagedFrames,
            _mappedManagedFrames,
            matchingPdbModules,
            highestUnmappedModules);
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
            return HasMatchingPdb(
                reader,
                pdbName,
                pdbSignature,
                pdbAge,
                modulePath,
                fileVersion);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasMatchingPdb(SymbolReader reader, TraceModuleFile? module) =>
        module is not null
        && HasMatchingPdb(
            reader,
            module.PdbName,
            module.PdbSignature,
            module.PdbAge,
            module.FilePath,
            module.FileVersion);

    private static bool HasMatchingPdb(
        SymbolReader reader,
        string pdbName,
        Guid pdbSignature,
        int pdbAge,
        string modulePath,
        string fileVersion)
    {
        if (string.IsNullOrEmpty(pdbName) || pdbSignature == Guid.Empty)
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(reader.FindSymbolFilePath(
                pdbName,
                pdbSignature,
                pdbAge,
                modulePath,
                fileVersion,
                portablePdbMatch: true));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;

    internal static string NormalizeModuleName(string? name)
    {
        string value = string.IsNullOrEmpty(name) ? "(unknown managed module)" : name;
        int length = Math.Min(value.Length, MaxModuleNameLength);
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
        public bool MatchingPdb { get; set; }
    }
}