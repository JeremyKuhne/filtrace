// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using FastTrace;
using FastTrace.Etlx;
using FastTrace.Parsers.Clr;
using Etlx = FastTrace.Etlx;

namespace Filtrace.Tracing.Providers;

public sealed partial class TimelineProvider
{
    /// <summary>
    ///  Reconstructs bounded snapshot GC detail from the raw events already consumed
    ///  by the cross-lane pass.
    /// </summary>
    internal sealed partial class SnapshotGcCollector
    {
        private readonly Dictionary<GcIdentity, RawGcCollection> _active = [];
        private readonly double _endMs;
        private readonly List<SnapshotGcRecord> _longest = [];
        private readonly Dictionary<GcPauseIdentity, double> _pauseStarts = [];
        private readonly double _startMs;
        private int _collectionCount;
        private bool _namesTruncated;

        /// <summary>
        ///  Creates a collector for GC collections and pauses that overlap one snapshot window.
        /// </summary>
        /// <param name="startMs">The inclusive window start in trace-relative milliseconds.</param>
        /// <param name="endMs">The inclusive window end in trace-relative milliseconds.</param>
        internal SnapshotGcCollector(double startMs, double endMs)
        {
            _startMs = startMs;
            _endMs = endMs;
        }

        /// <summary>
        ///  Gets whether a bounded collection or pause-state table rejected additional detail.
        /// </summary>
        internal bool DetailTruncated { get; private set; }

        /// <summary>
        ///  Routes a raw GC start, end, suspend, or restart event into the reconstruction state machine.
        /// </summary>
        /// <param name="data">The raw CLR event to observe.</param>
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

        /// <summary>
        ///  Attributes a completed pause interval to the foreground or background collection active at its end.
        /// </summary>
        /// <param name="clrInstanceId">The CLR instance that emitted the pause.</param>
        /// <param name="interval">The completed pause interval and process-instance identity.</param>
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

        /// <summary>
        ///  Completes remaining active collections and builds the bounded, pause-ranked GC snapshot detail.
        /// </summary>
        /// <param name="pauses">Merged pause intervals and aggregate in-window durations.</param>
        /// <param name="namesTruncated">Whether a retained collection kind or reason required bounding.</param>
        /// <returns>Collection count, aggregate pauses, and the longest retained collections.</returns>
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

        /// <summary>
        ///  Starts tracking a unique collection while bounded active-state capacity remains.
        /// </summary>
        /// <param name="processInstanceIndex">The TraceEvent process-instance index.</param>
        /// <param name="clrInstanceId">The CLR instance id.</param>
        /// <param name="collectionNumber">The collection sequence number within the CLR instance.</param>
        /// <param name="startMs">The collection start in trace-relative milliseconds.</param>
        /// <param name="generation">The condemned generation reported by the runtime.</param>
        /// <param name="type">Whether the collection is foreground, background, or another runtime GC type.</param>
        /// <param name="reason">The runtime reason that triggered the collection.</param>
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

        /// <summary>
        ///  Records a collection end and completes it when its foreground or background pause evidence is sufficient.
        /// </summary>
        /// <param name="processInstanceIndex">The TraceEvent process-instance index.</param>
        /// <param name="clrInstanceId">The CLR instance id.</param>
        /// <param name="collectionNumber">The collection sequence number within the CLR instance.</param>
        /// <param name="endMs">The collection end in trace-relative milliseconds.</param>
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

        /// <summary>
        ///  Retains one finite GC suspension start per process, thread, and CLR instance while capacity remains.
        /// </summary>
        /// <param name="identity">The process-thread instance that suspended.</param>
        /// <param name="clrInstanceId">The CLR instance id.</param>
        /// <param name="startMs">The suspension start in trace-relative milliseconds.</param>
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

        /// <summary>
        ///  Matches a restart to a retained suspension and attributes the resulting valid interval to a collection.
        /// </summary>
        /// <param name="identity">The process-thread instance that restarted.</param>
        /// <param name="clrInstanceId">The CLR instance id.</param>
        /// <param name="endMs">The restart time in trace-relative milliseconds.</param>
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

    }
}
