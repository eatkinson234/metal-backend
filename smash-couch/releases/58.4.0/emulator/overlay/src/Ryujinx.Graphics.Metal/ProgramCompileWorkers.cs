using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Metal
{
    // Bounded worker pool for guest program preparation (MSL library and
    // function compilation, plus recorded pipelines for disk-cached programs).
    // MTLDevice library/pipeline creation is thread-safe; each program's own
    // state is written only by its single work item and published through
    // Task completion. SMASH_COUCH_PROGRAM_WORKERS=0 restores serial creation.
    static class ProgramCompileWorkers
    {
        private static readonly int Count = ReadCount();
        private static readonly BlockingCollection<Action> _queue = new();
        private static readonly object _startGate = new();
        private static volatile bool _started;
        private static int _running;
        private static long _queued;
        private static long _waits;
        private static long _waitTicks;
        private static long _failures;
        private static readonly object _logGate = new();
        private static long _loggedQueued;
        private static long _loggedWaits;
        private static long _loggedWaitTicks;
        private static long _loggedFailures;

        internal static void CountFailure() => Interlocked.Increment(ref _failures);

        internal static bool Enabled => Count > 0;

        private static int ReadCount()
        {
            string value = Environment.GetEnvironmentVariable("SMASH_COUCH_PROGRAM_WORKERS");
            if (int.TryParse(value, out int count)) return Math.Clamp(count, 0, 16);
            return Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        }

        internal static Task Run(Action work)
        {
            if (!_started)
            {
                lock (_startGate)
                {
                    if (!_started)
                    {
                        // Publish only after every worker is running; if a start
                        // throws, the caller compiles synchronously instead of
                        // queueing work that nothing would consume. Workers that
                        // did start stay counted, so a later attempt starts only
                        // the missing ones and the pool never exceeds Count.
                        while (_running < Count)
                        {
                            new Thread(Work) { Name = $"Metal.ProgramCompile.{_running}", IsBackground = true }.Start();
                            _running++;
                        }
                        _started = true;
                        Logger.Info?.Print(LogClass.Gpu, $"MetalProgramWorkers: started {Count} program compile workers");
                    }
                }
            }

            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _queued);
            _queue.Add(() =>
            {
                try
                {
                    work();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    // Work items convert their own failures to Failure status;
                    // anything escaping is still surfaced to the waiter.
                    completion.SetException(exception);
                }
            });
            return completion.Task;
        }

        private static void Work()
        {
            foreach (Action work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }

        internal static void Wait(Task compile)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                compile.Wait();
            }
            catch (AggregateException exception)
            {
                Logger.Error?.Print(LogClass.Gpu, $"MetalProgramWorkers: background compile threw: {exception.InnerException}");
            }
            Interlocked.Increment(ref _waits);
            Interlocked.Add(ref _waitTicks, Stopwatch.GetTimestamp() - start);
        }

        internal static string Summary() =>
            $"MetalProgramWorkers: workers={Count};queued={Interlocked.Read(ref _queued)};pending={_queue.Count};" +
            $"waits={Interlocked.Read(ref _waits)};waitMs={Interlocked.Read(ref _waitTicks) * 1000.0 / Stopwatch.Frequency:F1};" +
            $"failures={Interlocked.Read(ref _failures)}";

        // Periodic line with deltas since the previous one. The first line (60 s
        // in) is the boot cost; later lines isolate mid-game queueing and any
        // render-thread waits, which the dispose-time total conflates.
        internal static void LogSummaryIfChanged(string reason)
        {
            lock (_logGate)
            {
                long queued = Interlocked.Read(ref _queued);
                long waits = Interlocked.Read(ref _waits);
                long waitTicks = Interlocked.Read(ref _waitTicks);
                long failures = Interlocked.Read(ref _failures);
                if (queued == _loggedQueued && waits == _loggedWaits && failures == _loggedFailures)
                {
                    return;
                }

                Logger.Info?.Print(LogClass.Gpu, Summary() +
                    $";delta:queued=+{queued - _loggedQueued};waits=+{waits - _loggedWaits};" +
                    $"waitMs=+{(waitTicks - _loggedWaitTicks) * 1000.0 / Stopwatch.Frequency:F1};" +
                    $"failures=+{failures - _loggedFailures};reason={reason}");
                _loggedQueued = queued;
                _loggedWaits = waits;
                _loggedWaitTicks = waitTicks;
                _loggedFailures = failures;
            }
        }
    }
}
