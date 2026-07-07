// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>An exceptions time bucket: throw count and the type thrown most.</summary>
/// <param name="Count">Exceptions thrown in the bucket.</param>
/// <param name="TopType">Exception type thrown most, or <see langword="null"/> when none carried a type.</param>
public sealed record ExceptionBucket(int Count, string? TopType);
