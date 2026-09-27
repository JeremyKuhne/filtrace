// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Filtrace.Tracing;

[TestClass]
public sealed class TraceConverterTests
{
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromSeconds(10);

    private static string FixturePath(string name) =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", name);

    // Never mutate the shared fixtures or their adjacent ignored ETLX caches.
    private static string CopyToTemp(string fixture, out string tempDir)
    {
        tempDir = Path.Join(Path.GetTempPath(), $"filtrace-conv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dest = Path.Join(tempDir, fixture);
        File.Copy(FixturePath(fixture), dest);
        return dest;
    }

    [TestMethod]
    public void Convert_NetTrace_WritesTheEtlxAndProvenance()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string etlx = TraceConverter.Convert(trace);

            etlx.Should().Be(trace + ".etlx", "TraceEvent appends .etlx to the trace path");
            File.Exists(etlx).Should().BeTrue();
            new FileInfo(etlx).Length.Should().BeGreaterThan(0);
            string markerPath = EtlxCacheProvenance.PathFor(etlx);
            File.Exists(markerPath).Should().BeTrue();
            markerPath.Should().NotBe(CaptureMetadataReader.PathFor(trace));
            using JsonDocument marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            marker.RootElement.GetProperty("state").GetString().Should().Be("ready");
            marker.RootElement.GetProperty("backendModule").GetString().Should().NotBeNullOrEmpty();
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
    [DataRow("alloc.nettrace")]
    [DataRow("etw.etl")]
    public void ConvertWithState_ExistingReadableUnmarkedCache_IsNotReusedOrReplaced(string fixture)
    {
        if (fixture.EndsWith(".etl", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ETW conversion requires Windows.");
        }

        string trace = CopyToTemp(fixture, out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            using (EtlxTraceLog traceLog = new(cachePath))
            {
                traceLog.EventCount.Should().BeGreaterThan(0);
            }

            byte[] originalCache = File.ReadAllBytes(cachePath);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            File.Delete(markerPath);

            Action convert = () => TraceConverter.ConvertWithState(trace);

            convert.Should().Throw<IOException>()
                .WithMessage("*no Filtrace provenance marker*--action clean*");

            File.ReadAllBytes(cachePath).Should().Equal(originalCache);
            File.Exists(markerPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("conversionEpoch")]
    [DataRow("backendModule")]
    [DataRow("cacheLength")]
    public void ConvertWithState_MismatchedProvenance_LeavesCacheUntouched(string mismatch)
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            byte[] originalCache = File.ReadAllBytes(cachePath);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            JsonObject marker = JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject();
            switch (mismatch)
            {
                case "conversionEpoch":
                    marker["conversionEpoch"] = 999;
                    break;
                case "backendModule":
                    marker["backendModule"] = Guid.NewGuid().ToString("D");
                    break;
                case "cacheLength":
                    marker["cache"]!["length"] = originalCache.LongLength + 1;
                    break;
                default:
                    Assert.Fail($"Unexpected mismatch '{mismatch}'.");
                    break;
            }

            File.WriteAllText(markerPath, marker.ToJsonString());

            Action convert = () => TraceConverter.ConvertWithState(trace);

            convert.Should().Throw<IOException>().WithMessage("*--action clean*");
            File.ReadAllBytes(cachePath).Should().Equal(originalCache);
            File.ReadAllText(markerPath).Should().Be(marker.ToJsonString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_MalformedProvenance_LeavesCacheUntouched()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            byte[] originalCache = File.ReadAllBytes(cachePath);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            File.WriteAllText(markerPath, "{invalid");

            Action convert = () => TraceConverter.ConvertWithState(trace);

            convert.Should().Throw<IOException>()
                .WithMessage("*invalid Filtrace provenance marker*--action clean*");

            File.ReadAllBytes(cachePath).Should().Equal(originalCache);
            File.ReadAllText(markerPath).Should().Be("{invalid");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_ProvenanceSizeBoundary_RejectsOnlyOversizedMarker()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string markerPath = EtlxCacheProvenance.PathFor(TraceConverter.Convert(trace));
            string padded = File.ReadAllText(markerPath).PadRight(EtlxCacheProvenance.MaxBytes);
            File.WriteAllText(markerPath, padded);

            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Hit);

            File.AppendAllText(markerPath, " ");
            Action convert = () => TraceConverter.ConvertWithState(trace);

            convert.Should().Throw<IOException>()
                .WithMessage($"*exceeds {EtlxCacheProvenance.MaxBytes} bytes*--action clean*");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_ChangedSource_RebuildsOwnedCache()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            string previousMarker = File.ReadAllText(markerPath);
            EtlxFileIdentity original = EtlxFileIdentity.ReadExisting(trace);
            File.Copy(FixturePath("activity.nettrace"), trace, overwrite: true);
            EtlxFileIdentity.ReadExisting(trace).Should().NotBe(original);

            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Converted);
            File.ReadAllText(markerPath).Should().NotBe(previousMarker);
            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Hit);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_PendingMarker_RecoversWithoutReusingCache()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            int expectedEventCount;
            using (EtlxTraceLog original = new(cachePath))
            {
                expectedEventCount = original.EventCount;
            }

            string otherTrace = Path.Join(tempDir, "activity.nettrace");
            File.Copy(FixturePath("activity.nettrace"), otherTrace);
            string otherCache = TraceConverter.Convert(otherTrace);
            using (EtlxTraceLog foreign = new(otherCache))
            {
                foreign.EventCount.Should().NotBe(expectedEventCount);
            }

            File.Copy(otherCache, cachePath, overwrite: true);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            File.Delete(markerPath);
            EtlxCacheProvenance.WritePending(markerPath);

            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Recovered);
            using (EtlxTraceLog rebuilt = new(cachePath))
            {
                rebuilt.EventCount.Should().Be(expectedEventCount);
            }

            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Hit);
            Directory.EnumerateFiles(tempDir, ".filtrace-etlx-*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_OrphanMarker_RecoversMissingCache()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            File.Delete(cachePath);

            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Recovered);
            File.Exists(cachePath).Should().BeTrue();
            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Hit);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_InvalidOrphanMarker_IsNotOverwritten()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.EtlxPathFor(trace);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            const string MarkerContents = """{"schemaVersion":1,"producer":"other-tool","state":"ready"}""";
            File.WriteAllText(markerPath, MarkerContents);

            Action convert = () => TraceConverter.ConvertWithState(trace);

            convert.Should().Throw<IOException>()
                .WithMessage("*invalid Filtrace provenance marker*--action clean*");

            File.Exists(cachePath).Should().BeFalse();
            File.ReadAllText(markerPath).Should().Be(MarkerContents);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_UnreadableCurrentCache_RebuildsAndReportsRecovery()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        string cachePath = TraceConverter.EtlxPathFor(trace);
        try
        {
            TraceConverter.Convert(trace);
            TraceCacheTestHelpers.CorruptCacheWithoutChangingIdentity(cachePath);
            Action openCache = () =>
            {
                using EtlxTraceLog traceLog = new(cachePath);
            };

            openCache.Should().Throw<FastSerialization.SerializationException>();

            EtlxCacheResult result = TraceConverter.ConvertWithState(trace);

            result.Path.Should().Be(cachePath);
            result.State.Should().Be(EtlxCacheState.Recovered);
            using (EtlxTraceLog traceLog = new(result.Path))
            {
                traceLog.EventCount.Should().BeGreaterThan(0);
            }

            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Hit);
            Directory.EnumerateFiles(tempDir, ".filtrace-etlx-*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_FailedRecovery_PreservesTheExistingCache()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        string cachePath = TraceConverter.EtlxPathFor(trace);
        try
        {
            TraceConverter.Convert(trace);
            byte[] cacheContents = File.ReadAllBytes(cachePath);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            string markerContents = File.ReadAllText(markerPath);
            File.WriteAllText(trace, "invalid nettrace");

            Action convert = () => TraceConverter.ConvertWithState(trace);

            convert.Should().Throw<Exception>();
            File.ReadAllBytes(cachePath).Should().Equal(cacheContents);
            File.ReadAllText(markerPath).Should().Be(markerContents);
            Directory.EnumerateFiles(tempDir, ".filtrace-etlx-*").Should().BeEmpty();
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
            using EtlxTraceLog traceLog = new(trace + ".etlx");
            traceLog.EventCount.Should().BeGreaterThan(0);
            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Hit);
            File.Exists(EtlxCacheProvenance.PathFor(trace + ".etlx")).Should().BeTrue();
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
            using EtlxTraceLog traceLog = new(result.Path);
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
            using EtlxTraceLog traceLog = new(result.Path);
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
            string markerPath = EtlxCacheProvenance.PathFor(etlx);
            File.Exists(markerPath).Should().BeTrue();

            string? removed = TraceConverter.Clean(trace);

            removed.Should().Be(etlx);
            File.Exists(etlx).Should().BeFalse();
            File.Exists(markerPath).Should().BeFalse();
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
            TraceConverter.Clean(trace).Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Clean_UnmarkedCache_RemovesOnlyTheCacheNotCaptureMetadata()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            File.Delete(EtlxCacheProvenance.PathFor(cachePath));
            string captureMarker = CaptureMetadataReader.PathFor(trace);
            File.WriteAllText(captureMarker, "{\"schemaVersion\":1,\"analyses\":{}}");

            TraceConverter.Clean(trace).Should().Be(cachePath);

            File.Exists(cachePath).Should().BeFalse();
            File.Exists(EtlxCacheProvenance.PathFor(cachePath)).Should().BeFalse();
            File.ReadAllText(captureMarker).Should().Be("{\"schemaVersion\":1,\"analyses\":{}}");
            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Converted);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Clean_OrphanMarker_RemovesMarkerAndReturnsNull()
    {
        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            File.Delete(cachePath);

            TraceConverter.Clean(trace).Should().BeNull();
            File.Exists(markerPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ConvertWithState_LockedDestination_RestoresPreviousMarker()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Open-file replacement is platform-specific.");
        }

        string trace = CopyToTemp("alloc.nettrace", out string tempDir);
        try
        {
            string cachePath = TraceConverter.Convert(trace);
            string markerPath = EtlxCacheProvenance.PathFor(cachePath);
            byte[] previousCache = File.ReadAllBytes(cachePath);
            string previousMarker = File.ReadAllText(markerPath);
            File.SetLastWriteTimeUtc(trace, File.GetLastWriteTimeUtc(trace).AddSeconds(2));

            using (FileStream locked = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Action convert = () => TraceConverter.ConvertWithState(trace);

                convert.Should().Throw<IOException>();
            }

            File.ReadAllBytes(cachePath).Should().Equal(previousCache);
            File.ReadAllText(markerPath).Should().Be(previousMarker);
            Directory.EnumerateFiles(tempDir, ".filtrace-etlx-*").Should().BeEmpty();
            TraceConverter.ConvertWithState(trace).State.Should().Be(EtlxCacheState.Converted);
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
    [DataRow(stringArrayData: null)]
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
