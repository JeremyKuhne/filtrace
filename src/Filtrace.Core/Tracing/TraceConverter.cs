// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Security.Cryptography;
using System.Text;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing;

/// <summary>
///  Builds, opens, and removes the <c>.etlx</c> conversion cache beside a
///  <c>.nettrace</c> or <c>.etl</c> trace, backing every TraceEvent reader and the
///  <c>convert</c> / <c>clean</c> file-op verbs.
/// </summary>
/// <remarks>
///  <para>
///   Every analysis of a <c>.nettrace</c> or <c>.etl</c> first converts it to an
///   ETLX file (the indexed form TraceEvent reads). TraceEvent caches that ETLX
///   beside the source and reuses it on the next read, so converting up front makes
///   the first real query fast, and cleaning it forces a rebuild when a stale cache
///   is suspected. A speedscope export carries no ETLX (it is parsed as JSON), so
///   neither operation applies to it.
///  </para>
///  <para>
///   Conversion is coordinated per canonical source path across processes, written
///   through a unique sibling temporary file, and atomically published to the final
///   cache path. Readers therefore never observe a partially written ETLX.
///  </para>
/// </remarks>
public static class TraceConverter
{
    private const int MaxTemporaryFilesToClean = 100;

    /// <summary>
    ///  Converts the <c>.nettrace</c> or <c>.etl</c> trace at <paramref name="path"/>
    ///  to its ETLX cache, returning the ETLX file path.
    /// </summary>
    /// <param name="path">The trace file path.</param>
    /// <returns>The path of the ETLX file written beside the trace.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="NotSupportedException">The file is not a convertible trace format.</exception>
    public static string Convert(string path) => ConvertWithState(path).Path;

    /// <summary>
    ///  Converts the trace to its ETLX cache and reports whether the request hit,
    ///  waited for, converted, or recovered that cache.
    /// </summary>
    /// <param name="path">The trace file path.</param>
    /// <param name="cancellationToken">Cancels a request waiting for another converter.</param>
    /// <returns>The ETLX cache path and request state.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="NotSupportedException">The file is not a convertible trace format.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static EtlxCacheResult ConvertWithState(
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ValidateConvertible(path);
        cancellationToken.ThrowIfCancellationRequested();

        string cachePath = EtlxPathFor(fullPath);
        string lockKey = LockKeyFor(fullPath);
        using Mutex conversionMutex = new(initiallyOwned: false, $"filtrace-etlx-{lockKey}");
        bool lockAcquired = false;
        bool waited = false;
        bool recovered = false;

        try
        {
            try
            {
                lockAcquired = conversionMutex.WaitOne(0);
                if (!lockAcquired)
                {
                    waited = true;
                    int signaled = WaitHandle.WaitAny([conversionMutex, cancellationToken.WaitHandle]);
                    if (signaled == 1)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    lockAcquired = true;
                }
            }
            catch (AbandonedMutexException)
            {
                lockAcquired = true;
                recovered = true;
            }

            recovered |= CleanTemporaryFiles(cachePath, lockKey);
            if (IsCurrent(fullPath, cachePath))
            {
                EtlxCacheState state = recovered
                    ? EtlxCacheState.Recovered
                    : waited ? EtlxCacheState.Waited : EtlxCacheState.Hit;
                return new EtlxCacheResult(cachePath, state);
            }

            string temporaryPath = TemporaryPathFor(cachePath, lockKey);
            try
            {
                TraceLogOptions options = new() { ContinueOnError = true };
                if (fullPath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
                {
                    TraceLog.CreateFromEventPipeDataFile(fullPath, temporaryPath, options);
                }
                else
                {
                    TraceLog.CreateFromEventTraceLogFile(fullPath, temporaryPath, options);
                }

                File.Move(temporaryPath, cachePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
                TryDelete($"{temporaryPath}.new");
            }

            return new EtlxCacheResult(
                cachePath,
                recovered ? EtlxCacheState.Recovered : EtlxCacheState.Converted);
        }
        finally
        {
            if (lockAcquired)
            {
                conversionMutex.ReleaseMutex();
            }
        }
    }

    internal static TraceLog OpenTraceLog(
        string path,
        out EtlxCacheState cacheState,
        CancellationToken cancellationToken = default)
    {
        EtlxCacheResult result = ConvertWithState(path, cancellationToken);
        cacheState = result.State;
        return new TraceLog(result.Path);
    }

    /// <summary>
    ///  Removes the ETLX cache beside the trace at <paramref name="path"/>, if present.
    /// </summary>
    /// <param name="path">The trace file path.</param>
    /// <returns>
    ///  The ETLX path that was deleted, or <see langword="null"/> when no cache existed.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="NotSupportedException">The file is not a convertible trace format.</exception>
    public static string? Clean(string path)
    {
        string fullPath = ValidateConvertible(path);
        string etlxPath = EtlxPathFor(fullPath);
        string lockKey = LockKeyFor(fullPath);
        using Mutex conversionMutex = new(initiallyOwned: false, $"filtrace-etlx-{lockKey}");
        bool lockAcquired = false;

        try
        {
            try
            {
                lockAcquired = conversionMutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                lockAcquired = true;
            }

            CleanTemporaryFiles(etlxPath, lockKey);
            if (!File.Exists(etlxPath))
            {
                return null;
            }

            File.Delete(etlxPath);
            return etlxPath;
        }
        finally
        {
            if (lockAcquired)
            {
                conversionMutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    ///  The ETLX cache path TraceEvent uses for the trace at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The trace file path.</param>
    /// <returns>The ETLX file path.</returns>
    public static string EtlxPathFor(string path) =>
        path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(path, ".etlx")
            : $"{path}.etlx";

    internal static string LockNameFor(string path)
    {
        string fullPath = ValidateConvertible(path);
        return $"filtrace-etlx-{LockKeyFor(fullPath)}";
    }

    private static bool CleanTemporaryFiles(string cachePath, string lockKey)
    {
        bool recovered = TryDelete($"{cachePath}.new");
        string directory = Path.GetDirectoryName(cachePath) ?? Directory.GetCurrentDirectory();
        string prefix = $".filtrace-etlx-{lockKey}-";
        int examined = 0;

        // Bound work in an attacker-controlled trace directory. A later request resumes
        // cleanup if a pathological directory contains more than this many stale files.
        foreach (string candidate in Directory.EnumerateFiles(directory, $"{prefix}*.tmp*"))
        {
            recovered |= TryDelete(candidate);
            examined++;
            if (examined == MaxTemporaryFilesToClean)
            {
                break;
            }
        }

        return recovered;
    }

    private static bool IsCurrent(string sourcePath, string cachePath)
    {
        FileInfo cache = new(cachePath);
        return cache.Exists
            && cache.Length > 0
            && cache.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(sourcePath);
    }

    private static string LockKeyFor(string fullPath)
    {
        // Match TraceStore's path comparer: Linux is case-sensitive; Windows and the
        // default macOS file system are case-insensitive.
        string canonicalPath = OperatingSystem.IsLinux() ? fullPath : fullPath.ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        return System.Convert.ToHexString(hash);
    }

    private static string TemporaryPathFor(string cachePath, string lockKey)
    {
        string directory = Path.GetDirectoryName(cachePath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(
            directory,
            $".filtrace-etlx-{lockKey}-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static string ValidateConvertible(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        if (!fullPath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
            && !fullPath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Only .nettrace and .etl traces have an ETLX cache; '{fullPath}' does not.");
        }

        return fullPath;
    }
}
