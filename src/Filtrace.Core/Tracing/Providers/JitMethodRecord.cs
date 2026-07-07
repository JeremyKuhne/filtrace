// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One jitted method's structured record in a <see cref="JitStatsResult"/>.
/// </summary>
/// <param name="MethodName">The fully qualified method name.</param>
/// <param name="ModuleILPath">The path of the module the method was jitted from.</param>
/// <param name="ILSize">The method's IL size, in bytes.</param>
/// <param name="NativeSize">The size of the jitted native code, in bytes.</param>
/// <param name="CompileMs">How long the method took to compile, in milliseconds.</param>
/// <param name="OptimizationTier">The optimization tier the method was compiled at.</param>
public sealed record JitMethodRecord(
    string MethodName,
    string ModuleILPath,
    int ILSize,
    int NativeSize,
    double CompileMs,
    string OptimizationTier);
