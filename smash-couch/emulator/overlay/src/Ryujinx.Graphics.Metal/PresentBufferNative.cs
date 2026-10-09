using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
namespace Ryujinx.Graphics.Metal;
[StructLayout(LayoutKind.Sequential, Pack=8)]
internal struct PresentBufferEvent
{
    public ulong Instance, Sequence, DrawableId, ExpectedBufferPointer, CallbackBufferPointer;
    public ulong AbsoluteBefore, ContinuousBefore;
    public double CaBefore;
    public ulong ContinuousAfter, AbsoluteAfter;
    public double CaAfter, GpuStartTime, GpuEndTime;
    public long ErrorCode;
    public uint Kind, ObservedStatus, GpuValidity, ErrorPresent, Numer, Denom;
}
[StructLayout(LayoutKind.Sequential, Pack=8)]
internal struct PresentBufferStatistics
{
    public ulong Attempts, Registered, ScheduledRegistered, CompletedRegistered;
    public ulong ScheduledCallbacks, CompletedCallbacks, ScheduledPending, CompletedPending;
    public ulong ScheduledDrops, CompletedDrops, ScheduledDrained, CompletedDrained;
    public ulong IdentityFailures, GpuInvalid, RegistrationErrors, RejectedAfterClose;
    public ulong DuplicateOrCapacity, DuplicateCallbacks, ActiveRegistrations, CallbackExceptions;
}
internal sealed unsafe class PresentBufferNative
{
    internal readonly delegate* unmanaged[Cdecl]<nint> Create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, ulong, ulong*, int> Register;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> Close;
    internal readonly delegate* unmanaged[Cdecl]<nint, PresentBufferEvent*, nuint, nuint> Drain;
    internal readonly delegate* unmanaged[Cdecl]<nint, PresentBufferStatistics*, int> ReadStats;
    internal readonly delegate* unmanaged[Cdecl]<nint*, int> TryDestroy;
    internal PresentBufferNative(nint library)
    {
        nint Get(string n) => NativeLibrary.GetExport(library,n);
        var version=(delegate* unmanaged[Cdecl]<uint>)Get("PCB1ABIVersion");
        var eventSize=(delegate* unmanaged[Cdecl]<nuint>)Get("PCB1EventSize");
        var statsSize=(delegate* unmanaged[Cdecl]<nuint>)Get("PCB1StatsSize");
        if(version()!=1 || sizeof(PresentBufferEvent)!=136 || eventSize()!=136 ||
            sizeof(PresentBufferStatistics)!=160 || statsSize()!=160) throw new InvalidDataException("Present-buffer v1 ABI size/version mismatch");
        ValidateOffsets<PresentBufferEvent>((delegate* unmanaged[Cdecl]<uint,nuint>)Get("PCB1EventOffset"));
        ValidateOffsets<PresentBufferStatistics>((delegate* unmanaged[Cdecl]<uint,nuint>)Get("PCB1StatsOffset"));
        Create=(delegate* unmanaged[Cdecl]<nint>)Get("PCB1Create");
        Register=(delegate* unmanaged[Cdecl]<nint,nint,nint,ulong,ulong,ulong*,int>)Get("PCB1Register");
        Close=(delegate* unmanaged[Cdecl]<nint,void>)Get("PCB1Close");
        Drain=(delegate* unmanaged[Cdecl]<nint,PresentBufferEvent*,nuint,nuint>)Get("PCB1Drain");
        ReadStats=(delegate* unmanaged[Cdecl]<nint,PresentBufferStatistics*,int>)Get("PCB1ReadStats");
        TryDestroy=(delegate* unmanaged[Cdecl]<nint*,int>)Get("PCB1TryDestroy");
    }
    private static void ValidateOffsets<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(delegate* unmanaged[Cdecl]<uint,nuint> native) where T:struct
    {
        // Reflection is construction-only. Declaration-order fields are the ABI contract;
        // managed and native offset vectors are independently calculated, not inferred from size.
        var fields=typeof(T).GetFields(BindingFlags.Public|BindingFlags.Instance).OrderBy(f=>f.MetadataToken).ToArray();
        for(uint i=0;i<fields.Length;i++)
            if((nuint)Marshal.OffsetOf<T>(fields[i].Name)!=native(i)) throw new InvalidDataException("Present-buffer v1 ABI offset mismatch: "+fields[i].Name);
    }
}
