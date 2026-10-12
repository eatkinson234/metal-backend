using System;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// Diagnostics hook for the render thread's waits on programs that had not finished compiling
    /// (ProgramQueue.WaitForProgram). Receives Stopwatch timestamps; the backend installs it.
    /// </summary>
    public static class ProgramWaitObserver
    {
        public static Action<long, long> Observed { get; set; }
    }
}
