using System;
using System.Reflection;
using System.Text.Json;

namespace Ryujinx.Input.SDL2;

/// <summary>
/// Emits opt-in identity records for diagnosing input-trace startup and arming.
/// This helper does not initialize CouchBridge, its receiver, or its recorder.
/// </summary>
public static class InputTraceIdentityDiagnostics
{
    public static void StopAndNeutralizeForDiagnosticClose()
    {
        // Invoked only by the opted-in self-close watcher, outside input polling.
        CouchBridge.StopAndNeutralizeForDiagnosticClose();
    }
    private const string TraceEnabledEnvironmentVariable = "SMASH_COUCH_INPUT_TRACE";
    private const string TracePathEnvironmentVariable = "SMASH_COUCH_INPUT_TRACE_PATH";
    private const string RecordPrefix = "SMASH_INPUT_TRACE_IDENTITY ";

    /// <summary>
    /// Called by the guarded application entrypoint before UI or driver setup.
    /// </summary>
    public static void ReportProgramMainStartup()
    {
        string enabledValue;
        try
        {
            enabledValue = Environment.GetEnvironmentVariable(TraceEnabledEnvironmentVariable);
        }
        catch
        {
            return;
        }

        if (!string.Equals(enabledValue, "1", StringComparison.Ordinal))
        {
            return;
        }

        string tracePath;
        try
        {
            tracePath = Environment.GetEnvironmentVariable(TracePathEnvironmentVariable);
        }
        catch (Exception exception)
        {
            TryReportFailure("program-main", exception);
            return;
        }

        TryReport("program-main", traceEnabled: true, tracePath, recorderInitialized: null);
    }

    /// <summary>
    /// Called once by CouchBridge after it registers the ProcessExit callback.
    /// </summary>
    public static void ReportCouchBridgeArmed(bool traceEnabled, string tracePath, bool recorderInitialized)
    {
        if (!traceEnabled)
        {
            return;
        }

        TryReport("couch-bridge-armed", traceEnabled, tracePath, recorderInitialized);
    }

    private static void TryReport(string stage, bool traceEnabled, string tracePath, bool? recorderInitialized)
    {
        try
        {
            Assembly assembly = typeof(InputTraceIdentityDiagnostics).Assembly;
            string modulePath;
            try
            {
                modulePath = assembly.Location;
            }
            catch
            {
                modulePath = null;
            }

            var record = new
            {
                stage,
                actualPid = Environment.ProcessId,
                moduleMvid = assembly.ManifestModule.ModuleVersionId,
                assemblyFullName = assembly.FullName,
                modulePath,
                traceEnabled,
                tracePath,
                recorderInitialized,
            };

            Console.Error.WriteLine(RecordPrefix + JsonSerializer.Serialize(record));
        }
        catch (Exception exception)
        {
            TryReportFailure(stage, exception);
        }
    }

    private static void TryReportFailure(string stage, Exception exception)
    {
        try
        {
            Console.Error.WriteLine($"{RecordPrefix}{{\"stage\":\"{stage}\",\"failure\":\"{exception.GetType().Name}\"}}");
        }
        catch
        {
            // Identity diagnostics must never interfere with input or shutdown.
        }
    }
}
