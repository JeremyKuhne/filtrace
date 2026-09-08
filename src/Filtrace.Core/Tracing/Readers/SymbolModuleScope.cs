// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

/// <summary>
///  The trace-local module files relevant to an already resolved process scope.
/// </summary>
internal sealed class SymbolModuleScope
{
    private static SymbolModuleScope All { get; } = new(
        processScope: null,
        moduleFileIndexes: null,
        moduleNames: null);

    private readonly ScopeResolution? _processScope;
    private readonly HashSet<int>? _moduleFileIndexes;
    private readonly HashSet<string>? _moduleNames;

    private SymbolModuleScope(
        ScopeResolution? processScope,
        HashSet<int>? moduleFileIndexes,
        HashSet<string>? moduleNames)
    {
        _processScope = processScope;
        _moduleFileIndexes = moduleFileIndexes;
        _moduleNames = moduleNames;
    }

    /// <summary>
    ///  Builds a module scope from exact process instances and their module lifetimes.
    /// </summary>
    /// <param name="traceLog">The trace whose loaded modules and sampled frames are inspected.</param>
    /// <param name="processScope">The exact process instances selected for analysis.</param>
    /// <returns>The module identities relevant to symbol work for the selected instances.</returns>
    public static SymbolModuleScope Create(EtlxTraceLog traceLog, ScopeResolution processScope)
    {
        if (processScope.ProcessInstanceIndexes is null)
        {
            return All;
        }

        HashSet<int> moduleFileIndexes = [];
        HashSet<string> moduleNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (EtlxTraceProcess process in traceLog.Processes)
        {
            if (!processScope.Includes(process))
            {
                continue;
            }

            foreach (TraceLoadedModule loadedModule in process.LoadedModules)
            {
                Add(loadedModule.ModuleFile, loadedModule.Name, moduleFileIndexes, moduleNames);
            }
        }

        // Some traces do not carry complete image-load metadata. Sampled addresses are
        // authoritative evidence that a module was used by the selected process instance,
        // and retain shared runtime helpers that may also appear in unrelated processes.
        foreach (TraceEvent data in traceLog.Events)
        {
            if (!processScope.Includes(data) || data is not (SampledProfileTraceData or ClrThreadSampleTraceData))
            {
                continue;
            }

            for (TraceCallStack? frame = data.CallStack(); frame is not null; frame = frame.Caller)
            {
                TraceCodeAddress address = frame.CodeAddress;
                Add(address.ModuleFile, address.ModuleName, moduleFileIndexes, moduleNames);
            }
        }

        return new SymbolModuleScope(processScope, moduleFileIndexes, moduleNames);
    }

    /// <summary>
    ///  Creates a module-only scope for deterministic lookup and extraction checks.
    /// </summary>
    /// <param name="moduleFileIndexes">The trace-local module-file indexes to include.</param>
    /// <param name="moduleNames">The assembly names to include during directory scanning.</param>
    /// <returns>A module scope with no process-event restriction.</returns>
    internal static SymbolModuleScope Create(
        IEnumerable<int> moduleFileIndexes,
        IEnumerable<string> moduleNames)
    {
        return new SymbolModuleScope(
            processScope: null,
            [.. moduleFileIndexes],
            new HashSet<string>(moduleNames, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///  Whether a trace event belongs to the exact process-instance scope.
    /// </summary>
    /// <param name="data">The trace event to test.</param>
    /// <returns><see langword="true"/> when the event belongs to the selected process instance.</returns>
    public bool Includes(TraceEvent data) => _processScope?.Includes(data) ?? true;

    /// <summary>
    ///  Whether a trace module file is relevant to symbol lookup for this scope.
    /// </summary>
    /// <param name="moduleFile">The trace-local module identity to test.</param>
    /// <returns><see langword="true"/> when the module is relevant to the selected process instances.</returns>
    public bool Includes(TraceModuleFile moduleFile) =>
        _moduleFileIndexes is null || _moduleFileIndexes.Contains((int)moduleFile.ModuleFileIndex);

    /// <summary>
    ///  Whether a build-output assembly can correspond to a relevant trace module.
    /// </summary>
    /// <param name="assemblyPath">The local assembly path to test by module name.</param>
    /// <returns><see langword="true"/> when the assembly may correspond to a selected trace module.</returns>
    public bool IncludesAssembly(string assemblyPath)
    {
        if (_moduleNames is null)
        {
            return true;
        }

        string name = Path.GetFileNameWithoutExtension(assemblyPath);
        return name.Length > 0 && _moduleNames.Contains(name);
    }

    private static void Add(
        TraceModuleFile? moduleFile,
        string? fallbackName,
        HashSet<int> moduleFileIndexes,
        HashSet<string> moduleNames)
    {
        if (moduleFile is not null)
        {
            moduleFileIndexes.Add((int)moduleFile.ModuleFileIndex);
            AddName(moduleNames, moduleFile.Name);
            AddName(moduleNames, moduleFile.FilePath);
        }

        AddName(moduleNames, fallbackName);
    }

    private static void AddName(HashSet<string> moduleNames, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > 0)
            {
                moduleNames.Add(name);
            }
        }
    }
}