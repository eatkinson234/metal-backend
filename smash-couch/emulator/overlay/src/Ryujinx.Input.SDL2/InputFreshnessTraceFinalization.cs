using System;
using System.IO;
using System.Threading;
using System.Text.Json;

namespace Ryujinx.Input.SDL2;

internal enum InputFreshnessTraceFinalizationStatus
{
    Disabled,
    Requested,
    InitializationFailed,
    MissingPath,
    DrainTimeout,
    ExportFailed,
    Completed,
}

internal readonly struct InputFreshnessTraceExportResult
{
    internal InputFreshnessTraceFinalizationStatus Status { get; }
    internal bool SnapshotCaptured { get; }
    internal int RecordCount { get; }
    internal string Error { get; }

    internal InputFreshnessTraceExportResult(
        InputFreshnessTraceFinalizationStatus status,
        bool snapshotCaptured,
        int recordCount,
        string error)
    {
        Status = status;
        SnapshotCaptured = snapshotCaptured;
        RecordCount = recordCount;
        Error = error;
    }
}

internal interface IInputFreshnessTraceArtifactPublisher
{
    bool TryPublish(string finalPath, byte[] contents, CancellationToken cancellationToken, out string error);
}

internal sealed class FileInputFreshnessTraceArtifactPublisher : IInputFreshnessTraceArtifactPublisher
{
    public bool TryPublish(string finalPath, byte[] contents, CancellationToken cancellationToken, out string error)
    {
        string temporaryPath = null;
        bool published = false;
        bool cleanupFailed = false;
        error = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".partial";

            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: false);
            published = true;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name;
            return false;
        }
        finally
        {
            if (!published && temporaryPath != null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    cleanupFailed = true;
                }
            }

            if (cleanupFailed)
            {
                error = error == null ? "TemporaryCleanupFailed" : $"{error};TemporaryCleanupFailed";
            }
        }
    }
}

/// <summary>
/// Owns the single background finalization worker. The process-exit caller only starts the worker,
/// waits for the bounded interval, and latches a failure if it does not finish in time.
/// </summary>
internal sealed class InputFreshnessTraceFinalizationWorker
{
    internal const int DefaultExitWaitMilliseconds = 1_000;

    private readonly bool _enabled;
    private readonly string _finalPath;
    private readonly Func<InputFreshnessTraceRecorder> _recorderProvider;
    private readonly IInputFreshnessTraceArtifactPublisher _publisher;
    private readonly Action<string> _reportStatus;
    private readonly int _drainTimeoutMilliseconds;
    private readonly int _exitWaitMilliseconds;
    private readonly ManualResetEvent _completed = new(false);
    private readonly CancellationTokenSource _cancellation = new();
    private int _status;
    private int _started;
    private int _processExitFailureObserved;

    internal InputFreshnessTraceExportResult LastResult { get; private set; }
    internal bool ProcessExitFailureObserved => Volatile.Read(ref _processExitFailureObserved) != 0;

    internal InputFreshnessTraceFinalizationWorker(
        bool enabled,
        string finalPath,
        Func<InputFreshnessTraceRecorder> recorderProvider,
        IInputFreshnessTraceArtifactPublisher publisher = null,
        Action<string> reportStatus = null,
        int drainTimeoutMilliseconds = InputFreshnessTraceRecorder.DefaultDrainTimeoutMilliseconds,
        int exitWaitMilliseconds = DefaultExitWaitMilliseconds)
    {
        _enabled = enabled;
        _finalPath = finalPath;
        _recorderProvider = recorderProvider;
        _publisher = publisher ?? new FileInputFreshnessTraceArtifactPublisher();
        _reportStatus = reportStatus ?? ReportToStandardError;
        _drainTimeoutMilliseconds = Math.Max(0, drainTimeoutMilliseconds);
        _exitWaitMilliseconds = Math.Max(0, exitWaitMilliseconds);
        _status = (int)(enabled ? InputFreshnessTraceFinalizationStatus.Requested : InputFreshnessTraceFinalizationStatus.Disabled);
    }

    internal InputFreshnessTraceFinalizationStatus Status => (InputFreshnessTraceFinalizationStatus)Volatile.Read(ref _status);

    internal void MarkInitializationFailed()
    {
        Interlocked.CompareExchange(
            ref _status,
            (int)InputFreshnessTraceFinalizationStatus.InitializationFailed,
            (int)InputFreshnessTraceFinalizationStatus.Requested);
    }

    internal bool RunProcessExitBounded()
    {
        if (!_enabled)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            try
            {
                Thread worker = new(FinalizeOnBackgroundThread)
                {
                    IsBackground = true,
                    Name = "Input freshness trace finalizer",
                };
                worker.Start();
            }
            catch
            {
                Interlocked.CompareExchange(
                    ref _status,
                    (int)InputFreshnessTraceFinalizationStatus.ExportFailed,
                    (int)InputFreshnessTraceFinalizationStatus.Requested);
                return false;
            }
        }

        bool completed;
        try
        {
            completed = _completed.WaitOne(_exitWaitMilliseconds);
        }
        catch
        {
            completed = false;
        }

        if (completed)
        {
            return Status == InputFreshnessTraceFinalizationStatus.Completed;
        }

        LatchProcessExitTimeout();
        _cancellation.Cancel();
        return false;
    }

    internal void MarkProcessExitFailureObserved()
    {
        Interlocked.Exchange(ref _processExitFailureObserved, 1);
        LatchProcessExitTimeout();
    }

    private void FinalizeOnBackgroundThread()
    {
        string detail = null;
        InputFreshnessTraceRecorder observedRecorder = null;

        try
        {
            InputFreshnessTraceFinalizationStatus currentStatus = Status;
            if (string.IsNullOrWhiteSpace(_finalPath))
            {
                SetFinalStatus(InputFreshnessTraceFinalizationStatus.MissingPath);
                detail = "trace-path-missing";
            }
            else if (currentStatus == InputFreshnessTraceFinalizationStatus.InitializationFailed)
            {
                detail = "recorder-initialization-failed";
            }
            else
            {
                InputFreshnessTraceRecorder recorder = _recorderProvider?.Invoke();
                observedRecorder = recorder;
                if (recorder == null)
                {
                    SetFinalStatus(InputFreshnessTraceFinalizationStatus.ExportFailed);
                    detail = "recorder-not-initialized";
                }
                else
                {
                    InputFreshnessTraceExportResult result = recorder.TryFinalizeToNewFile(
                        _finalPath,
                        _drainTimeoutMilliseconds,
                        _publisher,
                        _cancellation.Token);
                    LastResult = result;
                    SetFinalStatus(result.Status);
                    detail = result.Error;
                }
            }
        }
        catch (Exception exception)
        {
            SetFinalStatus(InputFreshnessTraceFinalizationStatus.ExportFailed);
            detail = exception.GetType().Name;
        }
        finally
        {
            try
            {
                _reportStatus?.Invoke($"SMASH_INPUT_TRACE status={Status} detail={detail ?? "none"}");
                _reportStatus?.Invoke("SMASH_INPUT_TRACE_COUNTERS " + JsonSerializer.Serialize(new
                {
                    status = Status.ToString(),
                    capacity = observedRecorder?.Capacity,
                    recorded = observedRecorder?.RecordCount,
                    reserved = observedRecorder?.ReservedCount,
                    overflow_count = observedRecorder?.OverflowCount,
                    error_count = observedRecorder?.ErrorCount,
                    complete = observedRecorder?.IsComplete,
                    snapshot_captured = LastResult.SnapshotCaptured,
                    snapshot_records = LastResult.RecordCount,
                    result_error = LastResult.Error,
                }));
            }
            catch
            {
                // Reporting is diagnostic only and remains outside input handling.
            }

            _completed.Set();
        }
    }

    private void LatchProcessExitTimeout()
    {
        while (true)
        {
            int current = Volatile.Read(ref _status);
            if (current != (int)InputFreshnessTraceFinalizationStatus.Requested &&
                current != (int)InputFreshnessTraceFinalizationStatus.Completed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _status, (int)InputFreshnessTraceFinalizationStatus.ExportFailed, current) == current)
            {
                return;
            }
        }
    }

    private void SetFinalStatus(InputFreshnessTraceFinalizationStatus status)
    {
        Interlocked.CompareExchange(ref _status, (int)status, (int)InputFreshnessTraceFinalizationStatus.Requested);
    }

    private static void ReportToStandardError(string message)
    {
        Console.Error.WriteLine(message);
    }
}
