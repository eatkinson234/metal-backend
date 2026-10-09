using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Metal;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct FramePhaseEvent
{
    public ulong Sequence, ExpectedDrawableId, CallbackDrawableId;
    public double PresentedTime;
    public ulong AbsoluteBefore, ContinuousBefore;
    public double CaBefore;
    public ulong ContinuousAfter, AbsoluteAfter;
    public double CaAfter;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct FramePhaseClock
{
    public ulong AbsoluteBefore, ContinuousBefore;
    public double CaBefore, CaAfter;
    public ulong ContinuousAfter, AbsoluteAfter;
    public uint Numer, Denom;
}

// Explicit path, no process-global resolver. The library intentionally stays
// loaded until process exit, including when a late callback prevents shutdown.
internal sealed unsafe class FramePhaseNative
{
    private readonly nint _library;
    internal readonly PresentBufferNative Ownership;
    internal readonly delegate* unmanaged[Cdecl]<nint> Create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, ulong, int> Register;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> Close;
    internal readonly delegate* unmanaged[Cdecl]<nint, FramePhaseEvent*, nuint, nuint> Drain;
    internal readonly delegate* unmanaged[Cdecl]<nint*, int> TryDestroy;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong> Registered, Callbacks, Pending, Mismatches, ZeroTimes, Rejected, Dropped;
    internal readonly delegate* unmanaged[Cdecl]<FramePhaseClock*, int> ReadClock;

    internal FramePhaseNative(string path)
    {
        _library = NativeLibrary.Load(path);
        Create = (delegate* unmanaged[Cdecl]<nint>)Export("PHObserverCreate");
        Register = (delegate* unmanaged[Cdecl]<nint, nint, ulong, int>)Export("PHObserverRegister");
        Close = (delegate* unmanaged[Cdecl]<nint, void>)Export("PHObserverClose");
        Drain = (delegate* unmanaged[Cdecl]<nint, FramePhaseEvent*, nuint, nuint>)Export("PHObserverDrain");
        TryDestroy = (delegate* unmanaged[Cdecl]<nint*, int>)Export("PHObserverTryDestroy");
        Registered = (delegate* unmanaged[Cdecl]<nint, ulong>)Export("PHObserverRegistered");
        Callbacks = (delegate* unmanaged[Cdecl]<nint, ulong>)Export("PHObserverCallbacks");
        Pending = (delegate* unmanaged[Cdecl]<nint, ulong>)Export("PHObserverPending");
        Mismatches = (delegate* unmanaged[Cdecl]<nint, ulong>)Export("PHObserverIdMismatches");
        ZeroTimes = (delegate* unmanaged[Cdecl]<nint, ulong>)Export("PHObserverZeroPresentedTime");
        Rejected = (delegate* unmanaged[Cdecl]<nint, ulong>)Export("PHObserverRejectedAfterClose");
        Dropped = (delegate* unmanaged[Cdecl]<nint, ulong>)Export("PHObserverDroppedEvents");
        ReadClock = (delegate* unmanaged[Cdecl]<FramePhaseClock*, int>)Export("PHReadClock");
        var eventSize = (delegate* unmanaged[Cdecl]<nuint>)Export("PHEventSize");
        var clockSize = (delegate* unmanaged[Cdecl]<nuint>)Export("PHClockSampleSize");
        if (sizeof(FramePhaseEvent) != 80 || sizeof(FramePhaseClock) != 56 ||
            eventSize() != 80 || clockSize() != 56)
            throw new InvalidDataException("Frame phase C ABI layout mismatch");
        Ownership = PresentBufferDiagnostic.Enabled ? new PresentBufferNative(_library) : null;
    }

    private nint Export(string name) => NativeLibrary.GetExport(_library, name);
}
