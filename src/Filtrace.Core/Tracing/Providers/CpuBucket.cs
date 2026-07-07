// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>A CPU-sample time bucket: sample count and the leaf method most were taken in.</summary>
/// <param name="SampleCount">CPU samples in the bucket.</param>
/// <param name="TopMethod">Shortened leaf method most samples were in, or <see langword="null"/> when none resolved.</param>
public sealed record CpuBucket(int SampleCount, string? TopMethod);
