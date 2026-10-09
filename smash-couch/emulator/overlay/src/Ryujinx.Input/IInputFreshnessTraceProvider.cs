using Ryujinx.HLE.HOS.Services.Hid;

namespace Ryujinx.Input
{
    /// <summary>
    /// Optional sidecar implemented by input sources that can return a mapped state together
    /// with provenance from the exact same source snapshot.
    /// </summary>
    public interface IInputFreshnessTraceProvider
    {
        bool InputFreshnessTraceEnabled { get; }

        INpadInputFreshnessWriteObserver InputFreshnessWriteObserver { get; }

        GamepadStateSnapshot GetMappedStateSnapshot(out NpadInputFreshnessProvenance provenance);
    }
}
