using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.Graphics.Metal;

/// <summary>
/// Tracks Metal programs while their creation is admitted. Closing prevents new
/// creations, waits for every admitted creation to release its lease, and then
/// returns the programs that were successfully registered.
/// </summary>
internal sealed class ProgramCreationRegistry<T> where T : class
{
    private readonly object _gate = new();
    private readonly HashSet<T> _programs = new(ReferenceEqualityComparer.Instance);
    private int _activeCreations;
    private bool _closing;
    private T[]? _closedSnapshot;

    internal CreationLease? BeginCreate()
    {
        lock (_gate)
        {
            if (_closing)
            {
                return null;
            }

            _activeCreations++;
            return new CreationLease(this);
        }
    }

    internal bool Register(T program, CreationLease lease)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(lease);

        lock (_gate)
        {
            // A lease is the linearization token for one admitted creation. It
            // may register after closing starts, but never after it is released,
            // and never for more than one program.
            if (!ReferenceEquals(lease.Registry, this) || lease.IsDisposed || lease.IsRegistered)
            {
                return false;
            }

            if (!_programs.Add(program))
            {
                return false;
            }

            lease.MarkRegistered();
            return true;
        }
    }

    internal bool Remove(T program)
    {
        ArgumentNullException.ThrowIfNull(program);

        lock (_gate)
        {
            return _programs.Remove(program);
        }
    }

    internal T[] CloseAndSnapshot()
    {
        lock (_gate)
        {
            if (_closedSnapshot is not null)
            {
                return _closedSnapshot;
            }

            _closing = true;
            while (_activeCreations != 0)
            {
                Monitor.Wait(_gate);
            }

            // Remove() is also gate-protected, so this copy is stable. It only
            // copies references; program disposal stays outside this gate.
            _closedSnapshot ??= _programs.ToArray();
            return _closedSnapshot;
        }
    }

    private void Release(CreationLease lease)
    {
        lock (_gate)
        {
            if (lease.IsDisposed)
            {
                return;
            }

            lease.MarkDisposed();
            _activeCreations--;
            Monitor.PulseAll(_gate);
        }
    }

    internal sealed class CreationLease : IDisposable
    {
        private readonly ProgramCreationRegistry<T> _registry;
        private bool _disposed;
        private bool _registered;

        internal CreationLease(ProgramCreationRegistry<T> registry)
        {
            _registry = registry;
        }

        internal ProgramCreationRegistry<T> Registry => _registry;
        internal bool IsDisposed => _disposed;
        internal bool IsRegistered => _registered;
        internal void MarkDisposed() => _disposed = true;
        internal void MarkRegistered() => _registered = true;

        public void Dispose()
        {
            _registry.Release(this);
        }
    }
}
