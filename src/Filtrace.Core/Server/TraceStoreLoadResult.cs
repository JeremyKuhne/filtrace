// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Server;

/// <summary>
///  A loaded trace paired with the request-specific ETLX cache state.
/// </summary>
/// <param name="Trace">The loaded trace.</param>
/// <param name="EtlxCacheState">How this request obtained ETLX, or <see langword="null"/> when ETLX is not used.</param>
public sealed record TraceStoreLoadResult(LoadedTrace Trace, EtlxCacheState? EtlxCacheState);