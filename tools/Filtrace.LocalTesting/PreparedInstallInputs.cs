// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Owns prepared install inputs and their temporary package directory.
/// </summary>
internal sealed class PreparedInstallInputs : IDisposable
{
    private readonly SourcePreparationOperation _operation;
    private bool _disposed;

    /// <summary>
    ///  Creates an owner for validated inputs and their temporary package directory.
    /// </summary>
    /// <param name="inputs">The validated source-built inputs.</param>
    /// <param name="operation">Exclusive ownership of the source preparation and artifacts.</param>
    internal PreparedInstallInputs(
        LocalTestingInstallInputs inputs,
        SourcePreparationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(operation);
        Inputs = inputs;
        _operation = operation;
    }

    /// <summary>
    ///  Gets the validated local artifacts passed to the coordinator.
    /// </summary>
    public LocalTestingInstallInputs Inputs { get; }

    /// <summary>
    ///  Gets the owned temporary package directory.
    /// </summary>
    internal string PackageDirectory => _operation.PackageDirectory;

    /// <summary>
    ///  Gets the fixed private source-preparation operation directory.
    /// </summary>
    internal string OperationDirectory => _operation.OperationDirectory;

    /// <summary>
    ///  Gets the nonfatal cleanup failure, when the owned directory could not be removed.
    /// </summary>
    internal Exception? CleanupFailure => _operation.CleanupFailure;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operation.Dispose();
    }
}