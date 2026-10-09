using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Ryujinx.HLE.HOS.Services.Hid;

namespace Ryujinx.Input.SDL2;

// Local, session-scoped transport. No persisted physical-controller identifiers.
internal sealed class CouchReceiver : IDisposable
{
    // "SCINPUT2" in little-endian order. The timestamp is a raw Darwin
    // mach_continuous_time tick captured by the Swift sender.
    internal const ulong PacketMagicV2 = 0x325455504e494353UL;
    internal const int PacketHeaderBytes = 40;
    internal const int PacketBytes = PacketHeaderBytes + 8 * 32;
    internal const ulong MaxAgeNanoseconds = 150_000_000UL;

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebaseInfo
    {
        public uint Numer;
        public uint Denom;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_continuous_time")]
    private static extern ulong MachContinuousTimeNative();

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_timebase_info")]
    private static extern int MachTimebaseInfoNative(out MachTimebaseInfo info);

    private static readonly (ulong Numer, ulong Denom) DarwinTimebase = ReadDarwinTimebase();
    private static long _nextSessionGeneration;

    private readonly byte[] _token;
    private readonly Socket _socket;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly Func<ulong> _nowTicks;
    private readonly ulong _timebaseNumer;
    private readonly ulong _timebaseDenom;
    private readonly bool _captureFreshnessTrace;
    private readonly Func<bool> _diagnosticStopRequested;
    private readonly ulong _sessionGeneration;
    private readonly byte[] _latest = new byte[PacketBytes];
    private ulong _captureTime;
    private ulong _acceptedMachTicks;
    private ulong _sequence;
    private bool _hasPacket;
    private bool _diagnosticCloseStopped;
    private volatile bool _stopping;
    private readonly string _path;

    internal bool CapturesFreshnessTrace => _captureFreshnessTrace;

    public CouchReceiver(string path, string token)
        : this(path, token, ContinuousTime, DarwinTimebase.Numer, DarwinTimebase.Denom, false)
    {
    }

    // Internal clock injection keeps age-boundary tests deterministic without
    // adding a runtime lifecycle or test-only transport API.
    internal CouchReceiver(
        string path,
        string token,
        Func<ulong> nowTicks,
        ulong timebaseNumer,
        ulong timebaseDenom,
        bool captureFreshnessTrace = false,
        Func<bool> diagnosticStopRequested = null)
    {
        _token = Convert.FromHexString(token);
        if (_token.Length != 16) throw new ArgumentException("Invalid couch session token");
        if (timebaseNumer == 0 || timebaseDenom == 0) throw new ArgumentException("Invalid Darwin timebase");
        _nowTicks = nowTicks ?? throw new ArgumentNullException(nameof(nowTicks));
        _timebaseNumer = timebaseNumer;
        _timebaseDenom = timebaseDenom;
        _captureFreshnessTrace = captureFreshnessTrace;
        // Install the shared bridge latch before the receiver thread can accept.
        _diagnosticStopRequested = diagnosticStopRequested;
        _sessionGeneration = captureFreshnessTrace ? unchecked((ulong)Interlocked.Increment(ref _nextSessionGeneration)) : 0;
        _path = path;
        // Never delete an existing endpoint: fail closed on collisions.
        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _thread = new Thread(Receive) { IsBackground = true, Name = "Couch input receiver" };
        _thread.Start();
    }

    private static (ulong Numer, ulong Denom) ReadDarwinTimebase()
    {
        if (MachTimebaseInfoNative(out MachTimebaseInfo info) != 0 || info.Numer == 0 || info.Denom == 0)
            throw new InvalidOperationException("Unable to read Darwin mach timebase");
        return (info.Numer, info.Denom);
    }

    internal static ulong ContinuousTime() => MachContinuousTimeNative();

    // Used only by the local transport test to timestamp a datagram with the
    // same Darwin clock as the receiver.
    internal static ulong ContinuousTimeForTests() => ContinuousTime();
    internal static (ulong Numer, ulong Denom) TimebaseForTests() => DarwinTimebase;

    private bool IsFresh(ulong origin, ulong now)
    {
        if (origin == 0 || now < origin) return false;

        // Convert without overflowing ulong: (now-origin)*numer <=
        // 150,000,000ns*denom. UInt128 is available in the net9 target.
        UInt128 elapsedNumerator = (UInt128)(now - origin) * _timebaseNumer;
        UInt128 maximumNumerator = (UInt128)MaxAgeNanoseconds * _timebaseDenom;
        return elapsedNumerator <= maximumNumerator;
    }

    private void Receive()
    {
        byte[] data = new byte[PacketBytes + 1];
        while (!_stopping)
        {
            try
            {
                if (!_socket.Poll(100000, SelectMode.SelectRead)) continue;
                int n = _socket.Receive(data);
                Accept(data.AsSpan(0, n));
            }
            catch (SocketException) { if (_stopping) return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    internal bool Accept(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketBytes ||
            BinaryPrimitives.ReadUInt64LittleEndian(packet) != PacketMagicV2 ||
            !packet.Slice(8, 16).SequenceEqual(_token)) return false;

        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(24));
        ulong captureTime = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(32));
        // Validate at arrival so an old queued datagram cannot enter the latest
        // snapshot. Read() repeats this check against the original timestamp.
        if (!IsFresh(captureTime, _nowTicks())) return false;

        for (int i = 0; i < 8; i++)
        {
            int at = PacketHeaderBytes + i * 32;
            if (BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(at)) > 1) return false;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(at + 4)) & ~0x1fffeU) != 0) return false;
            for (int j = 0; j < 6; j++)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(at + 8 + j * 4));
                if (!float.IsFinite(value) || Math.Abs(value) > 1 || (j >= 4 && value < 0)) return false;
            }
        }

        lock (_gate)
        {
            if (_diagnosticCloseStopped || (_diagnosticStopRequested?.Invoke() ?? false)) return false;
            // Recheck immediately before sequence/copy so acceptance
            // linearizes only while the sender origin is still fresh.
            ulong acceptedMachTicks = _nowTicks();
            if (!IsFresh(captureTime, acceptedMachTicks)) return false;
            if (_hasPacket && seq <= _sequence) return false;
            packet.CopyTo(_latest);
            _sequence = seq;
            _captureTime = captureTime;
            if (_captureFreshnessTrace)
            {
                _acceptedMachTicks = acceptedMachTicks;
            }
            _hasPacket = true;
        }
        return true;
    }

    internal (uint Buttons, float LX, float LY, float RX, float RY, float LT, float RT) Read(int slot)
    {
        CouchSlotRead snapshot = ReadSnapshot(slot, false);
        return (snapshot.Buttons, snapshot.LX, snapshot.LY, snapshot.RX, snapshot.RY, snapshot.LT, snapshot.RT);
    }

    internal CouchSlotRead ReadSnapshot(int slot, bool includeFreshnessTrace)
    {
        if (slot < 0 || slot >= 8) return default;

        bool trace = includeFreshnessTrace && _captureFreshnessTrace;
        lock (_gate)
        {
            if (!_hasPacket || (_diagnosticStopRequested?.Invoke() ?? false))
            {
                if (!trace) return default;

                ulong noPacketReadTicks = _nowTicks();
                return new CouchSlotRead(default, CreateProvenance(
                    NpadInputFreshnessReadStatus.NoPacket,
                    slot,
                    false,
                    0,
                    0,
                    0,
                    noPacketReadTicks,
                    0));
            }

            ulong readMachTicks = _nowTicks();
            NpadInputFreshnessReadStatus status = NpadInputFreshnessReadStatus.FreshEnabled;

            // A dead/stalled launcher cannot leave buttons or sticks held. Age
            // is measured from the sender's capture timestamp, never arrival.
            if (!IsFresh(_captureTime, readMachTicks))
            {
                status = NpadInputFreshnessReadStatus.Stale;
                return new CouchSlotRead(default, trace ? CreateProvenance(status, slot, true, _sequence, _captureTime, _acceptedMachTicks, readMachTicks, 0) : default);
            }

            ReadOnlySpan<byte> s = _latest.AsSpan(PacketHeaderBytes + slot * 32, 32);
            if (BinaryPrimitives.ReadUInt32LittleEndian(s) == 0)
            {
                status = NpadInputFreshnessReadStatus.SlotDisabled;
                return new CouchSlotRead(default, trace ? CreateProvenance(status, slot, true, _sequence, _captureTime, _acceptedMachTicks, readMachTicks, 0) : default);
            }

            var state = (BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)), F(s, 8), F(s, 12), F(s, 16), F(s, 20), F(s, 24), F(s, 28));
            NpadInputFreshnessProvenance provenance = trace
                ? CreateProvenance(status, slot, true, _sequence, _captureTime, _acceptedMachTicks, readMachTicks, 0)
                : default;
            return new CouchSlotRead(state, provenance);
        }
    }

    internal ulong ReadMappedMachTicks() => _nowTicks();

    private NpadInputFreshnessProvenance CreateProvenance(
        NpadInputFreshnessReadStatus status,
        int slot,
        bool hasPacket,
        ulong sequence,
        ulong captureMachTicks,
        ulong acceptedMachTicks,
        ulong readMachTicks,
        ulong mappedMachTicks)
    {
        return new NpadInputFreshnessProvenance(
            status,
            slot,
            _sessionGeneration,
            hasPacket,
            sequence,
            captureMachTicks,
            acceptedMachTicks,
            readMachTicks,
            mappedMachTicks,
            _timebaseNumer,
            _timebaseDenom);
    }

    private static float F(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(at));

    // Diagnostic-only gate. Serialize with packet acceptance/read snapshots so
    // no later datagram can restore held input, even if UI shutdown stalls.
    internal void StopAndNeutralizeForDiagnosticClose()
    {
        lock (_gate)
        {
            _diagnosticCloseStopped = true;
            _hasPacket = false;
            Array.Clear(_latest);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _socket.Dispose();
        _thread.Join(500);
        try { File.Delete(_path); } catch (IOException) { }
    }
}

internal readonly struct CouchSlotRead
{
    internal readonly uint Buttons;
    internal readonly float LX;
    internal readonly float LY;
    internal readonly float RX;
    internal readonly float RY;
    internal readonly float LT;
    internal readonly float RT;
    internal readonly NpadInputFreshnessProvenance Provenance;

    internal CouchSlotRead(
        (uint Buttons, float LX, float LY, float RX, float RY, float LT, float RT) state,
        NpadInputFreshnessProvenance provenance)
    {
        Buttons = state.Buttons;
        LX = state.LX;
        LY = state.LY;
        RX = state.RX;
        RY = state.RY;
        LT = state.LT;
        RT = state.RT;
        Provenance = provenance;
    }
}

internal static class CouchBridge
{
    private static int DiagnosticCloseStopRequested;
    internal static bool Enabled => Environment.GetEnvironmentVariable("SMASH_COUCH_SOCKET") is { Length: > 0 };
    private static readonly bool InputFreshnessTraceEnabledFromEnvironment =
        Environment.GetEnvironmentVariable("SMASH_COUCH_INPUT_TRACE") == "1";
    private static readonly string InputFreshnessTracePath = Environment.GetEnvironmentVariable("SMASH_COUCH_INPUT_TRACE_PATH");
    private static readonly Lazy<InputFreshnessTraceRecorder> TraceRecorder = InputFreshnessTraceEnabledFromEnvironment
        ? new Lazy<InputFreshnessTraceRecorder>(() => new InputFreshnessTraceRecorder(CouchReceiver.ContinuousTime))
        : null;
    private static readonly InputFreshnessTraceFinalizationWorker TraceFinalizer = InputFreshnessTraceEnabledFromEnvironment
        ? new InputFreshnessTraceFinalizationWorker(
            enabled: true,
            finalPath: InputFreshnessTracePath,
            recorderProvider: GetInitializedTraceRecorder)
        : null;
    private static readonly Lazy<CouchReceiver> Instance = new(() => CreateReceiver(null));

    // Optional cold-path barrier for the deterministic CPU fixture. Production
    // always passes null; this same factory supplies the ordinary Lazy instance.
    internal static CouchReceiver CreateReceiver(Action<CouchReceiver> beforePublication)
    {
        CouchReceiver receiver = new(
            Environment.GetEnvironmentVariable("SMASH_COUCH_SOCKET"),
            Environment.GetEnvironmentVariable("SMASH_COUCH_TOKEN"),
            CouchReceiver.ContinuousTime,
            CouchReceiver.TimebaseForTests().Numer,
            CouchReceiver.TimebaseForTests().Denom,
            InputFreshnessTraceEnabledFromEnvironment,
            () => Volatile.Read(ref DiagnosticCloseStopRequested) != 0);
        try
        {
            beforePublication?.Invoke(receiver);
            if (Volatile.Read(ref DiagnosticCloseStopRequested) != 0)
                receiver.StopAndNeutralizeForDiagnosticClose();
            return receiver;
        }
        catch
        {
            receiver.Dispose();
            throw;
        }
    }

    static CouchBridge()
    {
        if (InputFreshnessTraceEnabledFromEnvironment)
        {
            AppDomain.CurrentDomain.ProcessExit += ExportTraceOnExit;
            InputTraceIdentityDiagnostics.ReportCouchBridgeArmed(
                InputFreshnessTraceEnabledFromEnvironment,
                InputFreshnessTracePath,
                TraceRecorder?.IsValueCreated ?? false);
        }
    }

    internal static CouchReceiver Receiver
    {
        get
        {
            CouchReceiver receiver = Instance.Value;
            if (Volatile.Read(ref DiagnosticCloseStopRequested) != 0)
                receiver.StopAndNeutralizeForDiagnosticClose();
            return receiver;
        }
    }

    internal static void StopAndNeutralizeForDiagnosticClose()
    {
        // Latch first even if Lazy is absent or still executing its factory.
        // Accept/read consult the same latch before creation can publish state.
        Interlocked.Exchange(ref DiagnosticCloseStopRequested, 1);
        if (Instance.IsValueCreated) Instance.Value.StopAndNeutralizeForDiagnosticClose();
    }

    internal static InputFreshnessTraceFinalizationStatus InputFreshnessTraceStatus =>
        TraceFinalizer?.Status ?? InputFreshnessTraceFinalizationStatus.Disabled;

    internal static bool TryGetInputFreshnessTraceRecorder(out InputFreshnessTraceRecorder recorder)
    {
        recorder = null;
        if (!InputFreshnessTraceEnabledFromEnvironment) return false;

        try
        {
            recorder = TraceRecorder.Value;
            return true;
        }
        catch
        {
            TraceFinalizer?.MarkInitializationFailed();
            return false;
        }
    }

    private static InputFreshnessTraceRecorder GetInitializedTraceRecorder()
    {
        return TraceRecorder?.IsValueCreated == true ? TraceRecorder.Value : null;
    }

    private static void ExportTraceOnExit(object sender, EventArgs args)
    {
        // The process-exit callback only starts the owned worker and waits for its bounded result.
        // Serialization and file operations never run inline on this callback.
        if (TraceFinalizer != null && !TraceFinalizer.RunProcessExitBounded())
        {
            TraceFinalizer.MarkProcessExitFailureObserved();
        }
    }
}
