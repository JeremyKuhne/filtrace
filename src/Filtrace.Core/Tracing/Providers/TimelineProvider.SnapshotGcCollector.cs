// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Reconstructs bounded snapshot GC detail from the raw events already consumed
    ///  by the cross-lane pass.
    /// </summary>
    internal sealed class SnapshotGcCollector
    {
        private readonly Dictionary<GcIdentity, RawGcCollection> _active = [];
        private readonly double _endMs;
        private readonly List<SnapshotGcRecord> _longest = [];
        private readonly Dictionary<GcPauseIdentity, double> _pauseStarts = [];
        private readonly double _startMs;
        private int _collectionCount;
        private bool _namesTruncated;

        internal SnapshotGcCollector(double startMs, double endMs)
        {
            _startMs = startMs;
            _endMs = endMs;
        }

        internal bool DetailTruncated { get; private set; }

        internal void Observe(TraceEvent data)
        {
            switch (data)
            {
                case GCStartTraceData start:
                    ObserveStart(start);
                    break;
                case GCEndTraceData end:
                    ObserveEnd(end);
                    break;
                case GCSuspendEETraceData suspend when IsGcPauseReason(suspend.Reason):
                    ObserveSuspend(suspend);
                    break;
                case GCNoUserDataTraceData restart when IsEeRestartEvent(data):
                    ObserveRestart(restart);
                    break;
            }
        }

        internal void ObservePause(int clrInstanceId, GcPauseInterval interval)
        {
            RawGcCollection? collection = CurrentCollection(
                interval.ProcessInstanceIndex,
                clrInstanceId,
                interval.EndMs);
            if (collection is null)
            {
                return;
            }

            collection.PauseMs += interval.EndMs - interval.StartMs;
            collection.LastPauseStartMs = interval.StartMs;
            collection.LastPauseEndMs = interval.EndMs;
            bool overlapsWindow = interval.StartMs <= _endMs && interval.EndMs >= _startMs;
            collection.PauseContainsStart |= overlapsWindow && interval.Contains(collection.StartMs);
            if (overlapsWindow && collection.EndMs is double endMs)
            {
                collection.PauseContainsEnd |= interval.Contains(endMs);
            }

            if (collection.EndMs is not null
                && (!collection.IsBackground || collection.PauseContainsEnd))
            {
                Complete(collection.Identity);
            }
        }

        internal SnapshotGcSummary Build(GcPauseAggregate pauses, out bool namesTruncated)
        {
            while (_active.Count > 0)
            {
                using Dictionary<GcIdentity, RawGcCollection>.Enumerator enumerator = _active.GetEnumerator();
                enumerator.MoveNext();
                Complete(enumerator.Current.Key);
            }

            namesTruncated = _namesTruncated;
            SnapshotGcRecord[] top = [.. _longest
                .OrderByDescending(static collection => collection.PauseMs)
                .ThenBy(static collection => collection.Number)];
            return new SnapshotGcSummary(
                _collectionCount,
                Math.Round(pauses.TotalPauseMs, 2),
                Math.Round(pauses.MaxPauseMs, 2),
                top);
        }

        private void ObserveStart(GCStartTraceData start)
        {
            if (!TryGetProcessInstanceIndex(start, out int processInstanceIndex))
            {
                return;
            }

            ObserveStart(
                processInstanceIndex,
                start.ClrInstanceID,
                start.Count,
                start.TimeStampRelativeMSec,
                start.Depth,
                start.Type,
                start.Reason);
        }

        internal void ObserveStart(
            int processInstanceIndex,
            int clrInstanceId,
            int collectionNumber,
            double startMs,
            int generation,
            GCType type,
            GCReason reason)
        {
            GcIdentity identity = new(processInstanceIndex, clrInstanceId, collectionNumber);
            if (_active.ContainsKey(identity))
            {
                return;
            }

            if (_active.Count >= MaxSnapshotRetainedKeysPerFamily)
            {
                DetailTruncated = true;
                return;
            }

            _active.Add(
                identity,
                new RawGcCollection(
                    identity,
                    startMs,
                    generation,
                    type.ToString(),
                    reason.ToString(),
                    type == GCType.BackgroundGC));
        }

        private void ObserveEnd(GCEndTraceData end)
        {
            if (!TryGetProcessInstanceIndex(end, out int processInstanceIndex))
            {
                return;
            }

            ObserveEnd(processInstanceIndex, end.ClrInstanceID, end.Count, end.TimeStampRelativeMSec);
        }

        internal void ObserveEnd(
            int processInstanceIndex,
            int clrInstanceId,
            int collectionNumber,
            double endMs)
        {
            GcIdentity identity = new(processInstanceIndex, clrInstanceId, collectionNumber);
            if (_active.TryGetValue(identity, out RawGcCollection? collection))
            {
                collection.EndMs = endMs;
                bool lastPauseOverlapsWindow = collection.LastPauseStartMs <= _endMs
                    && collection.LastPauseEndMs >= _startMs;
                collection.PauseContainsEnd |= collection.IsBackground
                    && lastPauseOverlapsWindow
                    && endMs >= collection.LastPauseStartMs
                    && endMs <= collection.LastPauseEndMs;
                if ((!collection.IsBackground && collection.PauseContainsStart)
                    || (collection.IsBackground && collection.PauseContainsEnd))
                {
                    Complete(identity);
                }
            }
        }

        private void ObserveSuspend(GCSuspendEETraceData suspend)
        {
            if (!TryGetPauseIdentity(suspend, out PauseIdentity identity))
            {
                return;
            }

            ObserveSuspend(identity, suspend.ClrInstanceID, suspend.TimeStampRelativeMSec);
        }

        internal void ObserveSuspend(PauseIdentity identity, int clrInstanceId, double startMs)
        {
            if (!double.IsFinite(startMs))
            {
                return;
            }

            GcPauseIdentity gcIdentity = new(
                identity.ProcessInstanceIndex,
                identity.ThreadInstanceIndex,
                clrInstanceId);
            if (_pauseStarts.ContainsKey(gcIdentity))
            {
                return;
            }

            if (_pauseStarts.Count >= MaxSnapshotRetainedKeysPerFamily)
            {
                DetailTruncated = true;
                return;
            }

            _pauseStarts.Add(gcIdentity, startMs);
        }

        private void ObserveRestart(GCNoUserDataTraceData restart)
        {
            if (!TryGetPauseIdentity(restart, out PauseIdentity identity))
            {
                return;
            }

            ObserveRestart(identity, restart.ClrInstanceID, restart.TimeStampRelativeMSec);
        }

        internal void ObserveRestart(PauseIdentity identity, int clrInstanceId, double endMs)
        {
            GcPauseIdentity gcIdentity = new(
                identity.ProcessInstanceIndex,
                identity.ThreadInstanceIndex,
                clrInstanceId);
            if (!_pauseStarts.Remove(gcIdentity, out double startMs))
            {
                return;
            }

            if (!double.IsFinite(endMs) || endMs < startMs)
            {
                return;
            }

            ObservePause(
                clrInstanceId,
                new GcPauseInterval(
                    identity.ProcessInstanceIndex,
                    startMs,
                    endMs));
        }

        private RawGcCollection? CurrentCollection(
            int processInstanceIndex,
            int clrInstanceId,
            double pauseEndMs)
        {
            RawGcCollection? background = null;
            RawGcCollection? foreground = null;
            foreach (RawGcCollection collection in _active.Values)
            {
                if (collection.Identity.ProcessInstanceIndex != processInstanceIndex
                    || collection.Identity.ClrInstanceId != clrInstanceId
                    || collection.StartMs > pauseEndMs)
                {
                    continue;
                }

                if (collection.IsBackground)
                {
                    if (background is null || collection.StartMs > background.StartMs)
                    {
                        background = collection;
                    }
                }
                else if (foreground is null || collection.StartMs > foreground.StartMs)
                {
                    foreground = collection;
                }
            }

            return foreground ?? background;
        }

        private void Complete(GcIdentity identity)
        {
            RawGcCollection collection = _active[identity];
            _active.Remove(identity);
            bool relevant = IsTimelineTimestampInWindow(collection.StartMs, _startMs, _endMs)
                || collection.PauseContainsStart
                || (collection.IsBackground && collection.PauseContainsEnd);
            if (!relevant)
            {
                return;
            }

            _collectionCount++;
            string kind = BoundSnapshotName(collection.Kind, out bool kindTruncated);
            string reason = BoundSnapshotName(collection.Reason, out bool reasonTruncated);
            _namesTruncated |= kindTruncated || reasonTruncated;
            _longest.Add(
                new SnapshotGcRecord(
                    identity.CollectionNumber,
                    Math.Round(collection.StartMs, 2),
                    collection.Generation,
                    kind,
                    reason,
                    Math.Round(collection.PauseMs, 2)));
            if (_longest.Count > SnapshotDetailLimit)
            {
                SnapshotGcRecord drop = _longest
                    .OrderBy(static candidate => candidate.PauseMs)
                    .ThenByDescending(static candidate => candidate.Number)
                    .First();
                _longest.Remove(drop);
            }
        }

        private static bool TryGetProcessInstanceIndex(TraceEvent data, out int processInstanceIndex)
        {
            if (data.ProcessID > 0 && TraceLogExtensions.Process(data) is Etlx.TraceProcess process)
            {
                processInstanceIndex = (int)process.ProcessIndex;
                return true;
            }

            processInstanceIndex = -1;
            return false;
        }

        private sealed class RawGcCollection
        {
            public RawGcCollection(
                GcIdentity identity,
                double startMs,
                int generation,
                string kind,
                string reason,
                bool isBackground)
            {
                Identity = identity;
                StartMs = startMs;
                Generation = generation;
                Kind = kind;
                Reason = reason;
                IsBackground = isBackground;
            }

            public GcIdentity Identity { get; }

            public double StartMs { get; }

            public int Generation { get; }

            public string Kind { get; }

            public string Reason { get; }

            public bool IsBackground { get; }

            public double? EndMs { get; set; }

            public double PauseMs { get; set; }

            public double LastPauseStartMs { get; set; } = double.NaN;

            public double LastPauseEndMs { get; set; } = double.NaN;

            public bool PauseContainsStart { get; set; }

            public bool PauseContainsEnd { get; set; }
        }

        private readonly record struct GcIdentity(
            int ProcessInstanceIndex,
            int ClrInstanceId,
            int CollectionNumber);

        private readonly record struct GcPauseIdentity(
            int ProcessInstanceIndex,
            int ThreadInstanceIndex,
            int ClrInstanceId);
    }
}
