using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL.Multithreading;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Metal
{
    // Parallel stage compilation for mid-game graphics programs.
    //
    // A program's stages compile one after another on its compile worker, and
    // mid-game the render thread waits for the whole program: the threaded
    // renderer's SetProgram follows CreateProgram (ProgramQueue.WaitForProgram).
    // The fragment stage of a mid-game vertex+fragment program compiles on a
    // stage thread while the worker compiles the vertex stage, so that wait is
    // the longer stage instead of the sum. A stage is handed over only when a
    // stage thread is idle; otherwise the worker compiles it as before, so a
    // program never waits behind another program's stage. Boot programs keep
    // the sequential path; their workers are already saturated.
    //
    // Also counts the render thread's waits for programs that were not compiled
    // yet (reported by the threaded renderer), the stall this targets.
    //
    // SMASH_COUCH_PARALLEL_STAGES: on (default) | off.
    [SupportedOSPlatform("macos")]
    static class ProgramStageCompilers
    {
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SMASH_COUCH_PARALLEL_STAGES")?.Trim()
            .ToLowerInvariant() is not ("off" or "0");

        private static readonly int ThreadCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        private static readonly SemaphoreSlim _idle = new(ThreadCount, ThreadCount);
        private static readonly BlockingCollection<Action> _stages = new();
        private static readonly object _startGate = new();
        private static volatile bool _started;
        private static volatile bool _broken;

        private static long _parallel, _declined, _failed;
        private static long _renderWaits, _renderWaitTicks, _maxRenderWaitTicks;
        private static string _lastSummary;

        // Compiles one stage on an idle stage thread. Returns null (and starts
        // nothing) when no stage thread is idle; the caller compiles the stage.
        // The returned task completes with the stage's function or faults with
        // the stage's compile exception; the caller owns the function.
        internal static Task<MTLFunction> TryStart(Func<MTLFunction> compile)
        {
            if (!Enabled || _broken || !EnsureStarted() || !_idle.Wait(0))
            {
                Interlocked.Increment(ref _declined);
                return null;
            }

            TaskCompletionSource<MTLFunction> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _stages.Add(() =>
                {
                    MTLFunction function = default;
                    Exception error = null;
                    try
                    {
                        using NSAutoreleasePool pool = new();
                        function = compile();
                    }
                    catch (Exception exception)
                    {
                        Interlocked.Increment(ref _failed);
                        error = exception;
                    }
                    // Free the slot before the waiting worker resumes, so the
                    // next program already finds this thread idle.
                    try
                    {
                        _idle.Release();
                    }
                    finally
                    {
                        if (error != null)
                        {
                            result.TrySetException(error);
                        }
                        else
                        {
                            result.TrySetResult(function);
                        }
                    }
                });
            }
            catch (Exception exception)
            {
                _idle.Release();
                _broken = true;
                Logger.Warning?.Print(LogClass.Gpu, $"MetalProgramStages: queue failed, compiling stages in order: {exception.Message}");
                Interlocked.Increment(ref _declined);
                return null;
            }

            Interlocked.Increment(ref _parallel);
            return result.Task;
        }

        private static bool EnsureStarted()
        {
            if (_started)
            {
                return true;
            }

            lock (_startGate)
            {
                if (_started || _broken)
                {
                    return _started;
                }

                try
                {
                    for (int index = 0; index < ThreadCount; index++)
                    {
                        new Thread(Work) { Name = $"Metal.StageCompile.{index}", IsBackground = true }.Start();
                    }
                }
                catch (Exception exception)
                {
                    // Threads already started keep serving the queue; nothing new is handed over.
                    _broken = true;
                    Logger.Warning?.Print(LogClass.Gpu, $"MetalProgramStages: could not start stage threads, compiling stages in order: {exception.Message}");
                    return false;
                }

                _started = true;
                Logger.Info?.Print(LogClass.Gpu, $"MetalProgramStages: started {ThreadCount} stage compile threads");
                return true;
            }
        }

        private static void Work()
        {
            // Each job completes its own task and releases its slot; nothing escapes.
            foreach (Action stage in _stages.GetConsumingEnumerable())
            {
                stage();
            }
        }

        // Render thread (threaded renderer): a SetProgram waited for a program
        // that had not finished compiling. Diagnostics only; never throws.
        internal static void ObserveRenderWait(long startTicks, long endTicks)
        {
            try
            {
                long ticks = Math.Max(0, endTicks - startTicks);
                Interlocked.Increment(ref _renderWaits);
                Interlocked.Add(ref _renderWaitTicks, ticks);
                long max = Interlocked.Read(ref _maxRenderWaitTicks);
                while (ticks > max)
                {
                    long seen = Interlocked.CompareExchange(ref _maxRenderWaitTicks, ticks, max);
                    if (seen == max)
                    {
                        break;
                    }
                    max = seen;
                }
                NativeTimingDiagnostics.Record(NativeTimingKind.ProgramWait, startTicks, endTicks);
            }
            catch (Exception)
            {
                // A diagnostics failure must not reach the render thread.
            }
        }

        internal static void Install()
        {
            ProgramWaitObserver.Observed = ObserveRenderWait;
        }

        private static string Milliseconds(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1");

        internal static string Summary()
        {
            return $"MetalProgramStages: mode={(Enabled ? "on" : "off")};threads={ThreadCount};" +
                $"parallel={Interlocked.Read(ref _parallel)};declined={Interlocked.Read(ref _declined)};" +
                $"failed={Interlocked.Read(ref _failed)};renderWaits={Interlocked.Read(ref _renderWaits)};" +
                $"renderWaitMs={Milliseconds(Interlocked.Read(ref _renderWaitTicks))};" +
                $"maxRenderWaitMs={Milliseconds(Interlocked.Read(ref _maxRenderWaitTicks))}";
        }

        internal static void LogSummaryIfChanged(string reason)
        {
            string summary = Summary();
            if (summary == Volatile.Read(ref _lastSummary))
            {
                return;
            }
            Volatile.Write(ref _lastSummary, summary);
            Logger.Info?.Print(LogClass.Gpu, $"{summary};reason={reason}");
        }
    }
}
