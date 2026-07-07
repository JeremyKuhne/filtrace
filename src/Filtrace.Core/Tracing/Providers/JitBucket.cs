// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>A JIT time bucket: methods that began compiling.</summary>
/// <param name="MethodCount">Methods that started JIT compilation in the bucket.</param>
public sealed record JitBucket(int MethodCount);
