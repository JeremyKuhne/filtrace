// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The ETLX cache path and how the request was satisfied.
/// </summary>
/// <param name="Path">The canonical ETLX cache path.</param>
/// <param name="State">How the cache request was satisfied.</param>
public sealed record EtlxCacheResult(string Path, EtlxCacheState State);