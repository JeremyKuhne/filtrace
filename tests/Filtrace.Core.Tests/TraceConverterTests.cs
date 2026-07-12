// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing;

[TestClass]
public sealed class TraceConverterTests
{
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromSeconds(10);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // convert / clean mutate the filesystem (they write and delete the ETLX sidecar),
    // so each test works on a private temp copy of the fixture rather than the shared
    // committed one.
    private static string CopyToTemp(string fixture, out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"filtrace-conv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dest = Path.Combine(tempDir, fixture);
        File.Copy(FixturePath(fixture), dest);
        return dest;
    }

    [TestMethod]
    public void Convert_NetTrace_WritesTheEtlxSidecar()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string etlx = TraceConverter.Convert(trace);

            etlx.Should().Be(trace + ".etlx", "TraceEvent appends .etlx to the trace path");
            File.Exists(etlx).Should().BeTrue();
            new FileInfo(etlx).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_ExistingCurrentCache_ReportsHit()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Converted);

            EtlxCacheResult second = TraceConverter.ConvertWithState(trace);

            second.State.Should().Be(EtlxCacheState.Hit);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_ConcurrentSameTrace_ConvertsOnceAndPublishesValidCache()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        using ManualResetEventSlim start = new(initialState: false);
        try
        {
            Task<EtlxCacheResult>[] conversions = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    return TraceConverter.ConvertWithState(trace);
                }))
                .ToArray();

            start.Set();
            EtlxCacheResult[] results = Task.WhenAll(conversions).GetAwaiter().GetResult();

            results.Should().ContainSingle(result => result.State == EtlxCacheState.Converted);
            results.Select(result => result.Path).Should().OnlyContain(path => path == trace + ".etlx");
            using TraceLog traceLog = new(trace + ".etlx");
            traceLog.EventCount.Should().BeGreaterThan(0);
            Directory.EnumerateFiles(tempDir, "*.new").Should().BeEmpty();
            Directory.EnumerateFiles(tempDir, ".filtrace-etlx-*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_StaleTraceEventTemporaryFile_Recovers()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        string staleTemporary = $"{trace}.etlx.new";
        try
        {
            File.WriteAllText(staleTemporary, "incomplete");

            EtlxCacheResult result = TraceConverter.ConvertWithState(trace);

            result.State.Should().Be(EtlxCacheState.Recovered);
            File.Exists(staleTemporary).Should().BeFalse();
            using TraceLog traceLog = new(result.Path);
            traceLog.EventCount.Should().BeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_LockedStaleTemporaryFile_StillConverts()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        string staleTemporary = $"{trace}.etlx.new";
        try
        {
            using FileStream locked = new(
                staleTemporary,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None);

            EtlxCacheResult result = TraceConverter.ConvertWithState(trace);

            File.Exists(result.Path).Should().BeTrue();
            using TraceLog traceLog = new(result.Path);
            traceLog.EventCount.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(staleTemporary);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_CanceledWhileWaiting_ThrowsOperationCanceled()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        using ManualResetEventSlim mutexHeld = new(initialState: false);
        using ManualResetEventSlim releaseMutex = new(initialState: false);
        using CancellationTokenSource cancellation = new();
        Task mutexOwner = Task.Run(() =>
        {
            using Mutex conversionMutex = new(initiallyOwned: false, TraceConverter.LockNameFor(trace));
            if (!conversionMutex.WaitOne(SynchronizationTimeout))
            {
                throw new TimeoutException("Timed out acquiring the ETLX conversion mutex.");
            }

            try
            {
                mutexHeld.Set();
                if (!releaseMutex.Wait(SynchronizationTimeout))
                {
                    throw new TimeoutException("Timed out waiting to release the ETLX conversion mutex.");
                }
            }
            finally
            {
                conversionMutex.ReleaseMutex();
            }
        });
        try
        {
            mutexHeld.Wait(SynchronizationTimeout).Should().BeTrue();
            Task<EtlxCacheResult> conversion = Task.Run(() =>
                TraceConverter.ConvertWithState(trace, cancellation.Token));
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

            Action wait = () => conversion.GetAwaiter().GetResult();

            wait.Should().Throw<OperationCanceledException>();
            File.Exists(trace + ".etlx").Should().BeFalse();
        }
        finally
        {
            releaseMutex.Set();
            mutexOwner.GetAwaiter().GetResult();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Clean_AfterConvert_RemovesTheSidecar()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string etlx = TraceConverter.Convert(trace);
            File.Exists(etlx).Should().BeTrue();

            string? removed = TraceConverter.Clean(trace);

            removed.Should().Be(etlx);
            File.Exists(etlx).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Clean_WithNoCache_ReturnsNull()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            // No prior convert, so there is no sidecar to remove.
            TraceConverter.Clean(trace).Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Convert_Speedscope_ThrowsNotSupported()
    {
        // A speedscope export is parsed as JSON and has no ETLX cache.
        Action act = () => TraceConverter.Convert(FixturePath("folding.speedscope.json"));

        act.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void Convert_MissingFile_ThrowsFileNotFound()
    {
        Action act = () => TraceConverter.Convert(FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void Convert_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        Action act = () => TraceConverter.Convert(path!);

        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void EtlxPathFor_AppendsTheExtension()
    {
        TraceConverter.EtlxPathFor("a/b/foo.nettrace").Should().Be("a/b/foo.nettrace.etlx");
    }

    [TestMethod]
    public void EtlxPathFor_Etl_ReplacesTheExtension()
    {
        TraceConverter.EtlxPathFor("a/b/foo.etl").Should().Be("a/b/foo.etlx");
    }
}
