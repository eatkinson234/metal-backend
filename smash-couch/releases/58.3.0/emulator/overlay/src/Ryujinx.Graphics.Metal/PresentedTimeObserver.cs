using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal;

// Production presented-time feedback for refresh-anchored presents. Uses the presented-handler bridge
// already shipped in every runtime bundle (libSmashFramePhase.dylib, the same native observer the
// frame-phase recorder uses): each registered drawable gets an addPresentedHandler block that only copies
// scalars into a bounded lock-free ring; no managed code runs on Metal's callback threads and no drawable
// is retained. One observer per process, created on the render thread, drained only by it (single
// consumer), and deliberately never closed or destroyed, like the library itself.
internal sealed unsafe class PresentedTimeObserver
{
    private readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, int> _register;
    private readonly delegate* unmanaged[Cdecl]<nint, FramePhaseEvent*, nuint, nuint> _drain;
    private readonly nint _observer;

    private PresentedTimeObserver(nint library)
    {
        var create = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(library, "PHObserverCreate");
        var eventSize = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(library, "PHEventSize");
        _register = (delegate* unmanaged[Cdecl]<nint, nint, ulong, int>)NativeLibrary.GetExport(library, "PHObserverRegister");
        _drain = (delegate* unmanaged[Cdecl]<nint, FramePhaseEvent*, nuint, nuint>)NativeLibrary.GetExport(library, "PHObserverDrain");
        if (sizeof(FramePhaseEvent) != 80 || eventSize() != 80)
            throw new InvalidDataException("Presented-time C ABI layout mismatch");
        _observer = create();
        if (_observer == 0) throw new InvalidOperationException("Cannot allocate presented-time observer");
    }

    internal static PresentedTimeObserver TryCreate(out string error)
    {
        try
        {
            nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libSmashFramePhase.dylib"));
            error = null;
            return new PresentedTimeObserver(library);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // Before the drawable is presented. 0 on success.
    internal int Register(nint drawable, long sequence) => _register(_observer, drawable, (ulong)sequence);

    internal int Drain(FramePhaseEvent* events, int capacity) => (int)_drain(_observer, events, (nuint)capacity);
}
