// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Bounded cross-lane evidence around one trace-relative timestamp.
/// </summary>
/// <param name="AtMs">The requested center timestamp, in milliseconds from trace start.</param>
/// <param name="Gc">Garbage collections in the resolved window.</param>
/// <param name="Cpu">Top CPU leaf methods in the resolved window.</param>
/// <param name="Exceptions">Top exception types in the resolved window.</param>
/// <param name="Alloc">Top allocation types in the resolved window.</param>
/// <param name="Jit">Top jitted methods in the resolved window.</param>
/// <param name="Events">Top raw event types in the resolved window.</param>
/// <param name="NamesTruncated">Whether any emitted trace-derived name exceeded the snapshot name limit.</param>
public sealed record TimelineSnapshot(
    double AtMs,
    SnapshotGcSummary Gc,
    SnapshotCpuSummary Cpu,
    SnapshotExceptionSummary Exceptions,
    SnapshotAllocationSummary Alloc,
    SnapshotJitSummary Jit,
    SnapshotEventSummary Events,
    bool NamesTruncated);

/// <summary>Garbage-collection activity in a timeline snapshot.</summary>
/// <param name="CollectionCount">Collections that started or had a managed-thread pause in the window.</param>
/// <param name="TotalPauseMs">Summed managed-thread pause overlap with the window, in milliseconds.</param>
/// <param name="MaxPauseMs">Longest single managed-thread pause overlap with the window, in milliseconds.</param>
/// <param name="Collections">Longest collections, bounded by the snapshot detail limit.</param>
public sealed record SnapshotGcSummary(
    int CollectionCount,
    double TotalPauseMs,
    double MaxPauseMs,
    IReadOnlyList<SnapshotGcRecord> Collections);

/// <summary>One garbage collection retained in a timeline snapshot.</summary>
/// <param name="Number">The collection sequence number.</param>
/// <param name="StartMs">Collection start, in milliseconds from trace start.</param>
/// <param name="Generation">The condemned generation.</param>
/// <param name="Kind">The collection kind.</param>
/// <param name="Reason">Why the collection was triggered.</param>
/// <param name="PauseMs">Full managed-thread pause duration, which may extend outside the snapshot window.</param>
public sealed record SnapshotGcRecord(
    int Number,
    double StartMs,
    int Generation,
    string Kind,
    string Reason,
    double PauseMs);

/// <summary>CPU activity in a timeline snapshot.</summary>
/// <param name="SampleCount">Total stack-bearing CPU samples in the window.</param>
/// <param name="MethodCount">Distinct resolved leaf methods in the window.</param>
/// <param name="Methods">Top resolved leaf methods, bounded by the snapshot detail limit.</param>
public sealed record SnapshotCpuSummary(
    long SampleCount,
    int MethodCount,
    IReadOnlyList<SnapshotCpuMethod> Methods);

/// <summary>One CPU leaf method retained in a timeline snapshot.</summary>
/// <param name="Name">Short method name.</param>
/// <param name="SampleCount">Samples attributed to the method.</param>
/// <param name="Percent">Percentage of all stack-bearing CPU samples in the window.</param>
public sealed record SnapshotCpuMethod(string Name, long SampleCount, double Percent);

/// <summary>Exception activity in a timeline snapshot.</summary>
/// <param name="ExceptionCount">Total exception throws in the window.</param>
/// <param name="TypeCount">Distinct exception types in the window.</param>
/// <param name="Types">Top exception types, bounded by the snapshot detail limit.</param>
public sealed record SnapshotExceptionSummary(
    long ExceptionCount,
    int TypeCount,
    IReadOnlyList<SnapshotCountRow> Types);

/// <summary>Allocation activity in a timeline snapshot.</summary>
/// <param name="TickCount">Total positive allocation ticks in the window.</param>
/// <param name="Bytes">Sampled allocation bytes represented by those ticks.</param>
/// <param name="TypeCount">Distinct allocation types in the window.</param>
/// <param name="Types">Top allocation types by bytes, bounded by the snapshot detail limit.</param>
public sealed record SnapshotAllocationSummary(
    long TickCount,
    long Bytes,
    int TypeCount,
    IReadOnlyList<SnapshotAllocationType> Types);

/// <summary>One allocation type retained in a timeline snapshot.</summary>
/// <param name="Name">Allocated type name.</param>
/// <param name="TickCount">Allocation ticks for the type.</param>
/// <param name="Bytes">Sampled allocation bytes for the type.</param>
public sealed record SnapshotAllocationType(string Name, long TickCount, long Bytes);

/// <summary>JIT activity in a timeline snapshot.</summary>
/// <param name="CompilationCount">Total method-jitting-started events in the window.</param>
/// <param name="MethodCount">Distinct method names in the window.</param>
/// <param name="Methods">Top method names, bounded by the snapshot detail limit.</param>
public sealed record SnapshotJitSummary(
    long CompilationCount,
    int MethodCount,
    IReadOnlyList<SnapshotCountRow> Methods);

/// <summary>Raw event activity in a timeline snapshot.</summary>
/// <param name="EventCount">Total raw events in the window.</param>
/// <param name="TypeCount">Distinct provider/event-name pairs in the window.</param>
/// <param name="Types">Top event types, bounded by the snapshot detail limit.</param>
public sealed record SnapshotEventSummary(
    long EventCount,
    int TypeCount,
    IReadOnlyList<SnapshotEventType> Types);

/// <summary>One named count retained in a timeline snapshot.</summary>
/// <param name="Name">The exception type or method name.</param>
/// <param name="Count">Occurrences in the snapshot window.</param>
public sealed record SnapshotCountRow(string Name, long Count);

/// <summary>One raw event type retained in a timeline snapshot.</summary>
/// <param name="Provider">Event provider name.</param>
/// <param name="Name">Event name.</param>
/// <param name="Count">Occurrences in the snapshot window.</param>
public sealed record SnapshotEventType(string Provider, string Name, long Count);