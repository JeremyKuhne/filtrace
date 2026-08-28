// Copyright (c) 2025 Jeremy W Kuhne
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