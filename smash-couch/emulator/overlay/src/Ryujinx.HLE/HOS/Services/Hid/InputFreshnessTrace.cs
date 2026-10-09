namespace Ryujinx.HLE.HOS.Services.Hid
{
    /// <summary>Why the selected latest packet did or did not supply this input snapshot.</summary>
    public enum NpadInputFreshnessReadStatus
    {
        NotTraced = 0,
        NoPacket = 1,
        FreshEnabled = 2,
        SlotDisabled = 3,
        Stale = 4,
    }

    /// <summary>The active-style ring selected for a primary Npad input write.</summary>
    public enum NpadInputFreshnessRingKind
    {
        Unknown = 0,
        FullKey = 1,
        Handheld = 2,
        JoyDual = 3,
        JoyLeft = 4,
        JoyRight = 5,
        Palma = 6,
        SystemExt = 7,
    }

    /// <summary>
    /// Provenance copied with one selected slot state and carried through mapping and HLE conversion.
    /// Ticks use the sender/receiver host's Darwin continuous-time domain.
    /// </summary>
    public readonly struct NpadInputFreshnessProvenance
    {
        public NpadInputFreshnessReadStatus ReadStatus { get; }
        public int LogicalSlot { get; }
        public ulong SessionGeneration { get; }
        public bool HasPacket { get; }
        public ulong Sequence { get; }
        public ulong CaptureMachTicks { get; }
        public ulong AcceptedMachTicks { get; }
        public ulong ReadMachTicks { get; }
        public ulong MappedMachTicks { get; }
        public ulong TimebaseNumerator { get; }
        public ulong TimebaseDenominator { get; }

        public NpadInputFreshnessProvenance(
            NpadInputFreshnessReadStatus readStatus,
            int logicalSlot,
            ulong sessionGeneration,
            bool hasPacket,
            ulong sequence,
            ulong captureMachTicks,
            ulong acceptedMachTicks,
            ulong readMachTicks,
            ulong mappedMachTicks,
            ulong timebaseNumerator,
            ulong timebaseDenominator)
        {
            ReadStatus = readStatus;
            LogicalSlot = logicalSlot;
            SessionGeneration = sessionGeneration;
            HasPacket = hasPacket;
            Sequence = sequence;
            CaptureMachTicks = captureMachTicks;
            AcceptedMachTicks = acceptedMachTicks;
            ReadMachTicks = readMachTicks;
            MappedMachTicks = mappedMachTicks;
            TimebaseNumerator = timebaseNumerator;
            TimebaseDenominator = timebaseDenominator;
        }
    }

    /// <summary>
    /// Optional, dependency-neutral observation at the actual active-style primary ring write.
    /// Implementations must capture their write timestamp first and must not throw into input handling.
    /// </summary>
    public interface INpadInputFreshnessWriteObserver
    {
        void RecordPrimaryRingWrite(
            in NpadInputFreshnessProvenance provenance,
            PlayerIndex playerIndex,
            NpadInputFreshnessRingKind ringKind,
            ulong samplingNumber,
            ControllerKeys buttons,
            JoystickPosition leftStick,
            JoystickPosition rightStick);

        void MarkIncomplete();
    }

    /// <summary>A completed input-path trace ending immediately after the active-style ring write.</summary>
    public readonly struct NpadInputFreshnessTraceEvent
    {
        public NpadInputFreshnessProvenance Provenance { get; }
        public ulong RingWriteMachTicks { get; }
        public PlayerIndex PlayerIndex { get; }
        public NpadInputFreshnessRingKind RingKind { get; }
        public ulong SamplingNumber { get; }
        public ControllerKeys Buttons { get; }
        public JoystickPosition LeftStick { get; }
        public JoystickPosition RightStick { get; }

        public NpadInputFreshnessTraceEvent(
            NpadInputFreshnessProvenance provenance,
            ulong ringWriteMachTicks,
            PlayerIndex playerIndex,
            NpadInputFreshnessRingKind ringKind,
            ulong samplingNumber,
            ControllerKeys buttons,
            JoystickPosition leftStick,
            JoystickPosition rightStick)
        {
            Provenance = provenance;
            RingWriteMachTicks = ringWriteMachTicks;
            PlayerIndex = playerIndex;
            RingKind = ringKind;
            SamplingNumber = samplingNumber;
            Buttons = buttons;
            LeftStick = leftStick;
            RightStick = rightStick;
        }
    }
}
