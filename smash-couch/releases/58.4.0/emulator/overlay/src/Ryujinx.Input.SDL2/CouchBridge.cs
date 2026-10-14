using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Ryujinx.Common.Logging;
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

    // Ordered transition queue (per slot) between the receiver and the HID
    // update. The launcher sends full state every 4 ms and the host normally
    // reads every ~1 ms, so the queue holds at most the latest state and adds
    // nothing. When several datagrams arrive between two reads (a stalled host
    // loop), each distinct button/trigger/stick-zone state is delivered in
    // order, one read apart, instead of only the newest surviving. Same-key
    // updates coalesce into the tail (fresh axes); a full queue overwrites its
    // tail, so the earliest transitions and the final state are always kept.
    // Staleness and disconnect neutralization are unchanged.
    // SMASH_COUCH_INPUT_QUEUE=0 restores latest-wins (counters still run).
    internal const int TransitionQueueCapacity = 8;
    private const int SlotBytes = 32;
    private readonly bool _transitionQueue;
    private readonly byte[] _queueStates = new byte[8 * TransitionQueueCapacity * SlotBytes];
    private readonly ulong[] _queueSequences = new ulong[8 * TransitionQueueCapacity];
    private readonly ulong[] _queueCaptures = new ulong[8 * TransitionQueueCapacity];
    private readonly ulong[] _queueAccepted = new ulong[8 * TransitionQueueCapacity];
    private readonly int[] _queueHead = new int[8];
    private readonly int[] _queueCount = new int[8];
    private readonly ulong[] _deliveredKey = new ulong[8];
    // Per-slot trigger press threshold of the consumer (CouchGamepad), so a
    // release and re-press across it are never coalesced into one state.
    private readonly float[] _triggerThresholds = [.5f, .5f, .5f, .5f, .5f, .5f, .5f, .5f];
    private readonly int[] _acceptedSinceRead = new int[8];
    private long _accepted, _rejectedFormat, _rejectedStale, _rejectedInvalid, _rejectedOrder, _rejectedStopped;
    private long _supersededPackets, _collapsedTransitions, _heldTransitions, _queueOverflows, _staleQueueDrops;
    private Timer _summaryTimer;
    private long _loggedEvents = -1;

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
        _transitionQueue = Environment.GetEnvironmentVariable("SMASH_COUCH_INPUT_QUEUE") != "0";
        _path = path;
        // Never delete an existing endpoint: fail closed on collisions.
        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _thread = new Thread(Receive) { IsBackground = true, Name = "Couch input receiver" };
        _thread.Start();
        _summaryTimer = new Timer(_ => LogSummaryIfChanged("periodic"), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
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
            !packet.Slice(8, 16).SequenceEqual(_token))
        {
            Interlocked.Increment(ref _rejectedFormat);
            return false;
        }

        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(24));
        ulong captureTime = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(32));
        // Validate at arrival so an old queued datagram cannot enter the latest
        // snapshot. Read() repeats this check against the original timestamp.
        if (!IsFresh(captureTime, _nowTicks()))
        {
            Interlocked.Increment(ref _rejectedStale);
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            int at = PacketHeaderBytes + i * 32;
            bool valid = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(at)) <= 1 &&
                (BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(at + 4)) & ~0x1fffeU) == 0;
            for (int j = 0; valid && j < 6; j++)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(at + 8 + j * 4));
                valid = float.IsFinite(value) && Math.Abs(value) <= 1 && (j < 4 || value >= 0);
            }
            if (!valid)
            {
                Interlocked.Increment(ref _rejectedInvalid);
                return false;
            }
        }

        lock (_gate)
        {
            if (_diagnosticCloseStopped || (_diagnosticStopRequested?.Invoke() ?? false))
            {
                _rejectedStopped++;
                return false;
            }
            // Recheck immediately before sequence/copy so acceptance
            // linearizes only while the sender origin is still fresh.
            ulong acceptedMachTicks = _nowTicks();
            if (!IsFresh(captureTime, acceptedMachTicks))
            {
                _rejectedStale++;
                return false;
            }
            if (_hasPacket && seq <= _sequence)
            {
                _rejectedOrder++;
                return false;
            }
            packet.CopyTo(_latest);
            _sequence = seq;
            _captureTime = captureTime;
            if (_captureFreshnessTrace)
            {
                _acceptedMachTicks = acceptedMachTicks;
            }
            _hasPacket = true;
            _accepted++;
            for (int slot = 0; slot < 8; slot++)
            {
                _acceptedSinceRead[slot]++;
                EnqueueTransition(slot, packet.Slice(PacketHeaderBytes + slot * SlotBytes, SlotBytes), seq, captureTime, acceptedMachTicks);
            }
        }
        return true;
    }

    internal void SetTriggerThreshold(int slot, float threshold)
    {
        if (slot < 0 || slot >= 8) return;
        lock (_gate)
        {
            _triggerThresholds[slot] = threshold;
        }
    }

    // Distinct-state key: enable, buttons, trigger press (at the consumer's
    // threshold) and stick zones. Axis motion inside a zone only refreshes the
    // queued tail. Called under _gate.
    private ulong TransitionKey(int slot, ReadOnlySpan<byte> state)
    {
        static ulong Zone(float value) => value < -0.5f ? 1UL : value > 0.5f ? 2UL : 0UL;
        float threshold = _triggerThresholds[slot];
        ulong key = BinaryPrimitives.ReadUInt32LittleEndian(state.Slice(4));
        key |= (ulong)(BinaryPrimitives.ReadUInt32LittleEndian(state) & 1) << 32;
        key |= (F(state, 24) > threshold ? 1UL : 0UL) << 33;
        key |= (F(state, 28) > threshold ? 1UL : 0UL) << 34;
        key |= Zone(F(state, 8)) << 35 | Zone(F(state, 12)) << 37 | Zone(F(state, 16)) << 39 | Zone(F(state, 20)) << 41;
        return key;
    }

    private Span<byte> QueueEntry(int slot, int index) =>
        _queueStates.AsSpan((slot * TransitionQueueCapacity + index) * SlotBytes, SlotBytes);

    private void EnqueueTransition(int slot, ReadOnlySpan<byte> state, ulong seq, ulong capture, ulong accepted)
    {
        int count = _queueCount[slot];
        int tail = (_queueHead[slot] + count - 1) % TransitionQueueCapacity;
        ulong key = TransitionKey(slot, state);
        ulong previous = count > 0 ? TransitionKey(slot, QueueEntry(slot, tail)) : _deliveredKey[slot];
        int index;
        if (count > 0 && key == previous)
        {
            index = tail; // Same state: refresh axes and provenance in place.
        }
        else if (count == 0 && key == previous)
        {
            return; // Nothing new since the last delivery; reads use the latest packet.
        }
        else if (count == TransitionQueueCapacity)
        {
            index = tail;
            _queueOverflows++;
        }
        else
        {
            index = (_queueHead[slot] + count) % TransitionQueueCapacity;
            _queueCount[slot] = count + 1;
        }
        state.CopyTo(QueueEntry(slot, index));
        int at = slot * TransitionQueueCapacity + index;
        _queueSequences[at] = seq;
        _queueCaptures[at] = capture;
        _queueAccepted[at] = accepted;
    }

    private void ClearTransitions(int slot)
    {
        _queueHead[slot] = 0;
        _queueCount[slot] = 0;
        _acceptedSinceRead[slot] = 0;
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

            return ReadLatestLocked(slot, trace, _nowTicks());
        }
    }

    // Latest-state read under _gate with a single clock sample taken by the caller.
    private CouchSlotRead ReadLatestLocked(int slot, bool trace, ulong readMachTicks)
    {
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

    // Consuming read used once per HID update (CouchGamepad.GetMappedStateSnapshot).
    // Delivers the oldest queued transition; ReadSnapshot stays a latest-state peek.
    internal CouchSlotRead ReadNextSnapshot(int slot, bool includeFreshnessTrace)
    {
        if (slot < 0 || slot >= 8) return default;

        bool trace = includeFreshnessTrace && _captureFreshnessTrace;
        lock (_gate)
        {
            if (!_hasPacket || (_diagnosticStopRequested?.Invoke() ?? false))
            {
                ClearTransitions(slot);
                _deliveredKey[slot] = 0;
                return ReadSnapshot(slot, includeFreshnessTrace);
            }
            ulong readMachTicks = _nowTicks();
            if (!IsFresh(_captureTime, readMachTicks))
            {
                ClearTransitions(slot);
                _deliveredKey[slot] = 0;
                return ReadLatestLocked(slot, trace, readMachTicks);
            }

            int superseded = _acceptedSinceRead[slot] - 1;
            if (superseded > 0) _supersededPackets += superseded;
            _acceptedSinceRead[slot] = 0;

            while (_queueCount[slot] > 0 && !IsFresh(_queueCaptures[slot * TransitionQueueCapacity + _queueHead[slot]], readMachTicks))
            {
                _queueHead[slot] = (_queueHead[slot] + 1) % TransitionQueueCapacity;
                _queueCount[slot]--;
                _staleQueueDrops++;
            }

            int count = _queueCount[slot];
            if (count > 1)
            {
                // Distinct states that latest-wins would never have delivered:
                // with the queue each is delivered once with newer ones behind
                // it (held); without it they are collapsed into the latest.
                if (_transitionQueue) _heldTransitions++;
                else _collapsedTransitions += count - 1;
            }

            if (!_transitionQueue || count == 0)
            {
                ClearTransitions(slot);
                CouchSlotRead latest = ReadLatestLocked(slot, trace, readMachTicks);
                _deliveredKey[slot] = TransitionKey(slot, _latest.AsSpan(PacketHeaderBytes + slot * SlotBytes, SlotBytes));
                return latest;
            }

            int at = slot * TransitionQueueCapacity + _queueHead[slot];
            ReadOnlySpan<byte> s = QueueEntry(slot, _queueHead[slot]);
            ulong sequence = _queueSequences[at], capture = _queueCaptures[at], accepted = _queueAccepted[at];
            _deliveredKey[slot] = TransitionKey(slot, s);
            _queueHead[slot] = (_queueHead[slot] + 1) % TransitionQueueCapacity;
            _queueCount[slot] = count - 1;

            if (BinaryPrimitives.ReadUInt32LittleEndian(s) == 0)
            {
                return new CouchSlotRead(default, trace ? CreateProvenance(NpadInputFreshnessReadStatus.SlotDisabled, slot, true, sequence, capture, accepted, readMachTicks, 0) : default);
            }
            var state = (BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)), F(s, 8), F(s, 12), F(s, 16), F(s, 20), F(s, 24), F(s, 28));
            return new CouchSlotRead(state, trace
                ? CreateProvenance(NpadInputFreshnessReadStatus.FreshEnabled, slot, true, sequence, capture, accepted, readMachTicks, 0)
                : default);
        }
    }

    internal string Summary()
    {
        lock (_gate)
        {
            return $"CouchInput: queue={(_transitionQueue ? "on" : "off")};accepted={_accepted};" +
                $"rejected=format:{Interlocked.Read(ref _rejectedFormat)},stale:{Interlocked.Read(ref _rejectedStale)}," +
                $"invalid:{Interlocked.Read(ref _rejectedInvalid)},order:{_rejectedOrder},stopped:{_rejectedStopped};" +
                $"superseded={_supersededPackets};heldTransitions={_heldTransitions};collapsedTransitions={_collapsedTransitions};" +
                $"overflows={_queueOverflows};staleQueueDrops={_staleQueueDrops}";
        }
    }

    // Periodic so the counts survive a killed session; quiet when unchanged.
    private void LogSummaryIfChanged(string reason)
    {
        try
        {
            long events;
            lock (_gate)
            {
                events = _accepted + _rejectedOrder + _rejectedStopped + _supersededPackets + _heldTransitions +
                    _collapsedTransitions + _queueOverflows + _staleQueueDrops;
            }
            events += Interlocked.Read(ref _rejectedFormat) + Interlocked.Read(ref _rejectedStale) + Interlocked.Read(ref _rejectedInvalid);
            if (Interlocked.Exchange(ref _loggedEvents, events) == events) return;
            Logger.Info?.Print(LogClass.Hid, $"{Summary()};reason={reason}");
        }
        catch (Exception)
        {
            // An exception escaping a timer callback would end the process.
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

    // Input updates resume after a blocking dialog (NpadManager.UnblockInputUpdates, through the driver's
    // Clear): transitions queued while nothing consumed them happened before the resume, so the next
    // consuming read starts from the latest state, which is also the new baseline: an unchanged keepalive is
    // not queued again ahead of the next real change. A button still held reads as held; a press released
    // during the dialog does not replay. Unlike the diagnostic close, later packets are accepted as usual.
    internal void DiscardQueuedTransitions()
    {
        lock (_gate)
        {
            for (int slot = 0; slot < 8; slot++)
            {
                ClearTransitions(slot);
                _deliveredKey[slot] = _hasPacket ? TransitionKey(slot, _latest.AsSpan(PacketHeaderBytes + slot * SlotBytes, SlotBytes)) : 0;
            }
        }
    }

    // Diagnostic-only gate. Serialize with packet acceptance/read snapshots so
    // no later datagram can restore held input, even if UI shutdown stalls.
    internal void StopAndNeutralizeForDiagnosticClose()
    {
        lock (_gate)
        {
            _diagnosticCloseStopped = true;
            _hasPacket = false;
            Array.Clear(_latest);
            for (int slot = 0; slot < 8; slot++)
            {
                ClearTransitions(slot);
                _deliveredKey[slot] = 0;
            }
        }
    }

    public void Dispose()
    {
        _summaryTimer?.Dispose();
        _summaryTimer = null;
        LogSummaryIfChanged("dispose");
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

    internal static void DiscardQueuedTransitions()
    {
        if (Instance.IsValueCreated) Instance.Value.DiscardQueuedTransitions();
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
