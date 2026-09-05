// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Touki;

namespace Filtrace.Server;

public sealed partial class TraceStore
{
    /// <summary>
    ///  Releases one trace conversion gate when its caller completes.
    /// </summary>
    private sealed class ConversionGateLease : DisposableBase
    {
        private readonly TraceStore _owner;
        private readonly string _fullPath;
        private readonly ConversionGate _gate;

        /// <summary>
        ///  Creates a lease whose disposal releases the path-specific conversion gate.
        /// </summary>
        /// <param name="owner">The store that owns the gate registry.</param>
        /// <param name="fullPath">The canonical trace path associated with the gate.</param>
        /// <param name="gate">The acquired gate.</param>
        /// <param name="waited">Whether acquisition had to wait behind another caller.</param>
        public ConversionGateLease(
            TraceStore owner,
            string fullPath,
            ConversionGate gate,
            bool waited)
        {
            _owner = owner;
            _fullPath = fullPath;
            _gate = gate;
            Waited = waited;
        }

        /// <summary>
        ///  Gets whether another conversion held the gate when this lease was requested.
        /// </summary>
        public bool Waited { get; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owner.ReleaseConversionGate(_fullPath, _gate);
            }
        }
    }
}
