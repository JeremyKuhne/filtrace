// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Server;

/// <summary>
///  A loaded trace paired with the request-specific ETLX cache state.
/// </summary>
/// <param name="Trace">The loaded trace.</param>
/// <param name="EtlxCacheState">
///  This request's ETLX cache activity, or <see langword="null"/> for speedscope
///  and an already parsed in-memory hit that did not wait for same-trace work.
///  A request that waited and reused another load's result or ETLX reports
///  <see cref="Tracing.EtlxCacheState.Waited"/>, including a different metric or scope.
/// </param>
public sealed record TraceStoreLoadResult(LoadedTrace Trace, EtlxCacheState? EtlxCacheState);
