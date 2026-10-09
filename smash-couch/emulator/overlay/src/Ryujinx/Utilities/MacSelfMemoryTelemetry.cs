#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Utilities;

/// <summary>
/// Optional private-run heartbeat. The parent must reject a missing or stale sample;
/// this thread cannot enforce an absolute memory cap or recover a kernel-stuck app.
/// </summary>
internal sealed class MacSelfMemoryTelemetry : IDisposable
{
    private const string Library = "/usr/lib/libSystem.B.dylib";
    private const int TaskVmInfo = 22;
    private const uint TaskVmInfoCount = 93; // Installed macOS SDK: 372-byte task_vm_info_data_t.
    private const uint TaskVmInfoRev1Count = 38; // First revision containing phys_footprint.
    private const int FootprintOffsetBytes = 144;
    private const int IntervalMilliseconds = 250;
    private static int _active;
    private static readonly Lazy<IntPtr> TaskSelfVariable = new(() =>
        NativeLibrary.GetExport(NativeLibrary.Load(Library), "mach_task_self_"));

    private readonly string _path;
    private readonly string _nonce;
    private readonly Thread _thread;
    private readonly ManualResetEvent _stop = new(false);
    private int _disposed;

    [DllImport(Library, EntryPoint = "task_info")]
    private static extern int TaskInfo(uint task, int flavor, [Out] int[] info, ref uint count);

    private MacSelfMemoryTelemetry(string path, string nonce)
    {
        _path = path;
        _nonce = nonce;
        _thread = new Thread(Run) { IsBackground = true, Name = "private-self-memory-telemetry" };
        _thread.Start();
    }

    /// <returns>Null only when both configuration variables are absent.</returns>
    /// <exception cref="ArgumentException">Malformed or unsafe configuration.</exception>
    /// <exception cref="IOException">The exact output path already exists.</exception>
    internal static IDisposable? StartIfConfigured()
    {
        string? rawPath = Environment.GetEnvironmentVariable("SMASH_COUCH_SELF_TELEMETRY_PATH");
        string? nonce = Environment.GetEnvironmentVariable("SMASH_COUCH_SELF_TELEMETRY_NONCE");
        if (rawPath is null && nonce is null)
        {
            return null;
        }
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Self memory telemetry requires macOS");
        }
        if (rawPath is null || nonce is null || nonce.Length != 32)
        {
            throw new ArgumentException("Both self telemetry path and 32-hex nonce are required");
        }
        foreach (char digit in nonce)
        {
            if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f') ||
                  (digit >= 'A' && digit <= 'F')))
            {
                throw new ArgumentException("Self telemetry nonce must be 32 hexadecimal characters");
            }
        }
        string path = ValidatePath(rawPath);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InvalidOperationException("Self memory telemetry is already active");
        }
        try
        {
            return new MacSelfMemoryTelemetry(path, nonce);
        }
        catch
        {
            Volatile.Write(ref _active, 0);
            throw;
        }
    }

    private static string ValidatePath(string rawPath)
    {
        string? rawRoot = Environment.GetEnvironmentVariable("SMASH_COUCH_PRIVATE_DATA");
        if (string.IsNullOrEmpty(rawRoot) || string.IsNullOrEmpty(rawPath) ||
            !Path.IsPathFullyQualified(rawRoot) || !Path.IsPathFullyQualified(rawPath))
        {
            throw new ArgumentException("Self telemetry needs absolute private root and output paths");
        }
        string root = Path.GetFullPath(rawRoot);
        string path = Path.GetFullPath(rawPath);
        if (root != rawRoot || path != rawPath ||
            !path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            Path.GetFileName(path).Length == 0)
        {
            throw new ArgumentException("Self telemetry path must be a normalized child of private data");
        }
        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            throw new ArgumentException("Self telemetry output needs a parent directory");
        }
        // Reject symlinks in *all* existing ancestors, including those above private data.
        for (string? cursor = parent; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(cursor);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ArgumentException("Self telemetry parent directory is missing", error);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
            {
                throw new ArgumentException("Self telemetry path contains a symlink or non-directory ancestor");
            }
        }
        // File.Exists alone misses dangling links; LinkTarget detects those too.
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException("Self telemetry target must not already exist");
        }
        return path;
    }

    private static ulong ReadOwnFootprint()
    {
        // mach_task_self_ is libSystem's exported mach_port_t variable, not a function.
        uint task = unchecked((uint)Marshal.ReadInt32(TaskSelfVariable.Value));
        int[] info = new int[TaskVmInfoCount];
        uint count = TaskVmInfoCount;
        int result = TaskInfo(task, TaskVmInfo, info, ref count);
        if (result != 0 || count < TaskVmInfoRev1Count)
        {
            throw new IOException($"self task_info failed (kern_return={result}, count={count})");
        }
        uint low = unchecked((uint)info[FootprintOffsetBytes / sizeof(int)]);
        uint high = unchecked((uint)info[FootprintOffsetBytes / sizeof(int) + 1]);
        return low | ((ulong)high << 32);
    }

    private void Run()
    {
        long sequence = 0;
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                ulong footprint = ReadOwnFootprint();
                Ryujinx.Common.Memory.VicAllocationWitness.SampleHealthy();
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                sequence++;
                // Same-directory rename replaces the previous complete JSON file atomically.
                string temporary = _path + ".tmp-" + Environment.ProcessId + "-" + sequence;
                try
                {
                    using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write,
                               FileShare.None, 4096, FileOptions.None))
                    {
                        using (Utf8JsonWriter writer = new(file))
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("schema_version", 1);
                            writer.WriteString("nonce", _nonce);
                            writer.WriteNumber("pid", Environment.ProcessId);
                            writer.WriteNumber("sequence", sequence);
                            writer.WriteNumber("physical_footprint_bytes", footprint);
                            writer.WriteEndObject();
                            writer.Flush();
                        }
                    }
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }
                    File.Move(temporary, _path, overwrite: sequence != 1);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                if (_stop.WaitOne(IntervalMilliseconds))
                {
                    return;
                }
            }
        }
        catch (Exception error)
        {
            // Stop writing; the supervising parent decides from sample age.
            try { Console.Error.WriteLine($"Self memory telemetry stopped: {error.GetType().Name}"); }
            catch { /* Even diagnostics cannot block shutdown. */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stop.Set();
        _thread.Join(500); // A kernel-blocked task_info must not block shutdown indefinitely.
        _stop.Dispose();
        Volatile.Write(ref _active, 0);
    }
}
