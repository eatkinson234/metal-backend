using System;
using System.Numerics;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.HLE.HOS.Services.Hid;
using ConfigStick = Ryujinx.Common.Configuration.Hid.Controller.StickInputId;

namespace Ryujinx.Input.SDL2;

internal sealed class CouchGamepad : IGamepad, IInputFreshnessTraceProvider
{
    private readonly int _slot;
    private readonly CouchReceiver _receiver;
    private readonly InputFreshnessTraceRecorder _traceRecorder;
    private StandardControllerInputConfig _config;
    private readonly object _gate = new();
    private float _threshold = .5f;

    internal CouchGamepad(int slot)
        : this(slot, CouchBridge.Receiver, GetTraceRecorder())
    {
    }

    internal CouchGamepad(int slot, CouchReceiver receiver, InputFreshnessTraceRecorder traceRecorder = null)
    {
        _slot = slot;
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _traceRecorder = traceRecorder;
    }

    private static InputFreshnessTraceRecorder GetTraceRecorder()
    {
        return CouchBridge.TryGetInputFreshnessTraceRecorder(out InputFreshnessTraceRecorder recorder) ? recorder : null;
    }

    public GamepadFeaturesFlag Features => GamepadFeaturesFlag.None;
    public string Id => $"couch-{_slot + 1}";
    public string Name => $"Couch Player {_slot + 1}";

    // A reserved logical slot stays connected with neutral state during physical loss.
    public bool IsConnected => true;

    public bool InputFreshnessTraceEnabled => _traceRecorder != null && _receiver.CapturesFreshnessTrace;
    public INpadInputFreshnessWriteObserver InputFreshnessWriteObserver => _traceRecorder;

    public void Dispose() { }
    public void SetLed(uint rgb) { }
    public void Rumble(float low, float high, uint duration) { }
    public Vector3 GetMotionData(MotionInputId id) => Vector3.Zero;
    public void SetTriggerThreshold(float threshold) => _threshold = threshold;

    public void SetConfiguration(InputConfig config)
    {
        lock (_gate)
        {
            _config = (StandardControllerInputConfig)config;
            _threshold = _config.TriggerThreshold;
        }
    }

    public GamepadStateSnapshot GetStateSnapshot()
    {
        CouchSlotRead snapshot = _receiver.ReadSnapshot(_slot, false);
        return ToGamepadState(snapshot);
    }

    public bool IsPressed(GamepadButtonInputId id) => GetStateSnapshot().IsPressed(id);
    public (float, float) GetStick(StickInputId id) => GetStateSnapshot().GetStick(id);

    public GamepadStateSnapshot GetMappedStateSnapshot()
    {
        return GetMappedStateSnapshot(out _);
    }

    public GamepadStateSnapshot GetMappedStateSnapshot(out NpadInputFreshnessProvenance provenance)
    {
        bool trace = InputFreshnessTraceEnabled;
        CouchSlotRead selected = _receiver.ReadSnapshot(_slot, trace);
        GamepadStateSnapshot raw = ToGamepadState(selected);
        GamepadStateSnapshot mapped = MapState(raw);

        provenance = selected.Provenance;
        if (trace && provenance.ReadStatus != NpadInputFreshnessReadStatus.NotTraced)
        {
            try
            {
                ulong mappedMachTicks = _receiver.ReadMappedMachTicks();
                provenance = new NpadInputFreshnessProvenance(
                    provenance.ReadStatus,
                    provenance.LogicalSlot,
                    provenance.SessionGeneration,
                    provenance.HasPacket,
                    provenance.Sequence,
                    provenance.CaptureMachTicks,
                    provenance.AcceptedMachTicks,
                    provenance.ReadMachTicks,
                    mappedMachTicks,
                    provenance.TimebaseNumerator,
                    provenance.TimebaseDenominator);
            }
            catch
            {
                // Preserve the mapped input even if optional timestamp capture fails.
                _traceRecorder.MarkIncomplete();
            }
        }

        return mapped;
    }

    private GamepadStateSnapshot MapState(GamepadStateSnapshot raw)
    {
        lock (_gate)
        {
            if (_config == null) return raw;

            GamepadStateSnapshot result = default;
            var left = _config.LeftJoycon;
            var right = _config.RightJoycon;

            void Map(GamepadButtonInputId to, GamepadInputId from)
            {
                if (from != GamepadInputId.Unbound)
                {
                    result.SetPressed(to, raw.IsPressed((GamepadButtonInputId)from));
                }
            }

            Map(GamepadButtonInputId.A, right.ButtonA);
            Map(GamepadButtonInputId.B, right.ButtonB);
            Map(GamepadButtonInputId.X, right.ButtonX);
            Map(GamepadButtonInputId.Y, right.ButtonY);
            Map(GamepadButtonInputId.Plus, right.ButtonPlus);
            Map(GamepadButtonInputId.Minus, left.ButtonMinus);
            Map(GamepadButtonInputId.LeftShoulder, left.ButtonL);
            Map(GamepadButtonInputId.RightShoulder, right.ButtonR);
            Map(GamepadButtonInputId.LeftTrigger, left.ButtonZl);
            Map(GamepadButtonInputId.RightTrigger, right.ButtonZr);
            Map(GamepadButtonInputId.DpadUp, left.DpadUp);
            Map(GamepadButtonInputId.DpadDown, left.DpadDown);
            Map(GamepadButtonInputId.DpadLeft, left.DpadLeft);
            Map(GamepadButtonInputId.DpadRight, left.DpadRight);
            Map(GamepadButtonInputId.LeftStick, _config.LeftJoyconStick.StickButton);
            Map(GamepadButtonInputId.RightStick, _config.RightJoyconStick.StickButton);

            var leftStick = _config.LeftJoyconStick;
            var rightStick = _config.RightJoyconStick;
            (float x, float y) = raw.GetStick((StickInputId)leftStick.Joystick);
            if (leftStick.InvertStickX) x = -x;
            if (leftStick.InvertStickY) y = -y;
            if (leftStick.Rotate90CW) (x, y) = (y, -x);
            result.SetStick(StickInputId.Left, x, y);

            (x, y) = raw.GetStick((StickInputId)rightStick.Joystick);
            if (rightStick.InvertStickX) x = -x;
            if (rightStick.InvertStickY) y = -y;
            if (rightStick.Rotate90CW) (x, y) = (y, -x);
            result.SetStick(StickInputId.Right, x, y);

            return result;
        }
    }

    private GamepadStateSnapshot ToGamepadState(CouchSlotRead snapshot)
    {
        GamepadStateSnapshot result = default;
        for (int i = 1; i <= 16; i++)
        {
            result.SetPressed((GamepadButtonInputId)i, (snapshot.Buttons & (1U << i)) != 0);
        }

        result.SetPressed(GamepadButtonInputId.LeftTrigger, snapshot.LT > _threshold);
        result.SetPressed(GamepadButtonInputId.RightTrigger, snapshot.RT > _threshold);
        result.SetStick(StickInputId.Left, snapshot.LX, snapshot.LY);
        result.SetStick(StickInputId.Right, snapshot.RX, snapshot.RY);
        return result;
    }
}
