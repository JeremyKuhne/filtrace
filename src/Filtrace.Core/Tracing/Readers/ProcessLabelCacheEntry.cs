// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Caches rendered process labels for one operating-system process identifier.
/// </summary>
/// <param name="processId">The operating-system process identifier.</param>
/// <param name="processName">The first process name observed for the identifier.</param>
internal sealed class ProcessLabelCacheEntry(int processId, string processName)
{
    private const int MaximumCachedProcessNamesPerId = 8;
    private readonly string _processName = processName;
    private readonly string _label = CreateLabel(processId, processName);
    private Dictionary<string, string>? _reusedProcessLabels;

    /// <summary>
    ///  Gets the number of retained alternate names for this process identifier.
    /// </summary>
    internal int CachedAlternateNameCount => _reusedProcessLabels?.Count ?? 0;

    /// <summary>
    ///  Gets the rendered label for a process lifetime using this identifier.
    /// </summary>
    /// <param name="name">The process lifetime's name.</param>
    /// <returns>The cached process label.</returns>
    public string GetLabel(string name)
    {
        if (string.Equals(name, _processName, StringComparison.Ordinal))
        {
            return _label;
        }

        _reusedProcessLabels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_reusedProcessLabels.TryGetValue(name, out string? label))
        {
            label = CreateLabel(processId, name);
            if (CanCacheReusedProcessName(_reusedProcessLabels.Count))
            {
                _reusedProcessLabels.Add(name, label);
            }
        }

        return label;
    }

    /// <summary>
    ///  Determines whether another alternate name can be retained for a reused process identifier.
    /// </summary>
    /// <param name="cachedNameCount">The current number of retained alternate names.</param>
    /// <returns><see langword="true"/> when another alternate name can be retained.</returns>
    internal static bool CanCacheReusedProcessName(int cachedNameCount) =>
        cachedNameCount < MaximumCachedProcessNamesPerId;

    /// <summary>
    ///  Renders the process label for a process identifier and lifetime name.
    /// </summary>
    /// <param name="processId">The operating-system process identifier.</param>
    /// <param name="processName">The process lifetime name, or empty when unknown.</param>
    /// <returns>The invariant process label.</returns>
    internal static string CreateLabel(int processId, string processName)
    {
        string pid = processId.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(processName) ? pid : $"{processName}({pid})";
    }
}