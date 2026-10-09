using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Input.SDL2;

/// <summary>Opt-in, one-window self-close. No external process/UI control.</summary>
public sealed class DiagnosticPreloadClose : IDisposable
{
    private const long CloseTargetNanoseconds = 60_000_000_000;
    private const long InvalidAfterNanoseconds = 120_000_000_000;
    private const long ReplayCloseTargetNanoseconds = 330_000_000_000;
    private const long ReplayInvalidAfterNanoseconds = 360_000_000_000;
    private const long Replay600CloseTargetNanoseconds = 600_000_000_000;
    private const long Replay600InvalidAfterNanoseconds = 630_000_000_000;
    private static int _started;
    private volatile bool _cancelled;
    private int _closeIssued;
    private readonly string _path;
    private readonly int _pid;
    private readonly long _target;
    private readonly long _invalidAfter;
    private readonly string _profile;
    private readonly long _preloadWait;
    private readonly Action _neutralize;
    private readonly Action _close;
    private readonly Thread _thread;

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebase { internal uint Numer; internal uint Denom; }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_absolute_time")]
    private static extern ulong MachAbsoluteTime();
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_timebase_info")]
    private static extern int MachTimebaseInfo(out MachTimebase info);

    internal static long MonotonicNanoseconds()
    {
        if (MachTimebaseInfo(out MachTimebase info) != 0 || info.Numer == 0 || info.Denom == 0)
            throw new InvalidOperationException("monotonic-clock-unavailable");
        return checked((long)((UInt128)MachAbsoluteTime() * info.Numer / info.Denom));
    }

    public static DiagnosticPreloadClose TryStartForOwnedWindow(Action neutralize, Action requestNormalClose)
    {
        // Default-off must avoid filesystem access, threads, and bridge initialization.
        if (Environment.GetEnvironmentVariable("SMASH_COUCH_DIAGNOSTIC_CLOSE") != "1") return null;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return null;
        try
        {
            if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("macos-only");
            string profile = Environment.GetEnvironmentVariable("SMASH_COUCH_DIAGNOSTIC_CLOSE_PROFILE");
            CloseProfile selected = SelectProfile(profile);
            string stage = Environment.GetEnvironmentVariable("SMASH_COUCH_DIAGNOSTIC_CLOSE_STAGE");
            string path = ValidateStage(stage, AppContext.BaseDirectory);
            return StartOwnedWatchForProfile(path, Environment.ProcessId, selected.Target,
                240_000_000_000, neutralize, requestNormalClose, selected.Name);
        }
        catch (Exception e)
        {
            Report("setup-refused", new { error = e.GetType().Name });
            return null;
        }
    }

    internal static string ValidateStage(string stage, string appBase)
    {
        if (string.IsNullOrEmpty(stage) || !Path.IsPathFullyQualified(stage))
            throw new InvalidOperationException("stage-missing");
        stage = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar);
        string lab = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Smash Couch", "Lab") + Path.DirectorySeparatorChar;
        if (!stage.StartsWith(lab, StringComparison.Ordinal) || stage.Contains("..", StringComparison.Ordinal) ||
            !Path.GetFullPath(appBase).StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("foreign-stage");
        RejectLinks(new DirectoryInfo(stage));
        if (!Directory.Exists(appBase)) throw new InvalidOperationException("application-base-missing");
        RejectLinks(new DirectoryInfo(appBase));
        if (!File.Exists(Path.Combine(stage, "owner-manifest.json")))
            throw new InvalidOperationException("owner-manifest-missing");
        RejectLinks(new FileInfo(Path.Combine(stage, "owner-manifest.json")));
        return Path.Combine(stage, "preload-status.json");
    }

    private static void RejectLinks(FileSystemInfo info)
    {
        for (FileSystemInfo current = info; current != null; current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("symlink-refused");
        }
    }

    internal readonly record struct CloseProfile(string Name, long Target, long InvalidAfter);

    // Only fixed profiles are selectable; no arbitrary duration from environment.
    internal static CloseProfile SelectProfile(string profile) => profile switch
    {
        null or "" or "smoke60" => new("smoke60", CloseTargetNanoseconds, InvalidAfterNanoseconds),
        "replay330" => new("replay330", ReplayCloseTargetNanoseconds, ReplayInvalidAfterNanoseconds),
        "replay600" => new("replay600", Replay600CloseTargetNanoseconds, Replay600InvalidAfterNanoseconds),
        _ => throw new ArgumentException("unknown-diagnostic-close-profile"),
    };

    // Existing CPU fixtures retain the smoke profile and shortened target.
    // Production uses the exact selected fixed target. This internal seam cannot
    // obtain a duration from the environment or exceed its selected profile.
    internal static DiagnosticPreloadClose StartOwnedWatch(string path, int pid, long target,
        long preloadWait, Action neutralize, Action requestNormalClose)
    {
        return StartOwnedWatchForProfile(path, pid, target, preloadWait, neutralize, requestNormalClose, "smoke60");
    }

    internal static DiagnosticPreloadClose StartOwnedWatchForProfile(string path, int pid, long target,
        long preloadWait, Action neutralize, Action requestNormalClose, string profile)
    {
        CloseProfile selected = SelectProfile(profile);
        if (pid <= 0 || target <= 0 || target > selected.Target || preloadWait <= 0 ||
            neutralize == null || requestNormalClose == null) throw new ArgumentException("invalid-watch-contract");
        return new DiagnosticPreloadClose(path, pid, target, preloadWait, neutralize, requestNormalClose, selected);
    }

    private DiagnosticPreloadClose(string path, int pid, long target, long preloadWait, Action neutralize, Action close,
        CloseProfile profile)
    {
        _path = path; _pid = pid; _target = target; _preloadWait = preloadWait;
        _invalidAfter = profile.InvalidAfter; _profile = profile.Name;
        _neutralize = neutralize; _close = close;
        _thread = new Thread(Watch) { IsBackground = true, Name = "Owned diagnostic normal-close deadline" };
        _thread.Start();
    }

    private readonly record struct MarkerIdentity(long Origin, string Hash, long Length, long CreationTicks, long WriteTicks);

    private MarkerIdentity? ReadMarker()
    {
        if (!File.Exists(_path)) return null;
        FileInfo before = new(_path);
        RejectLinks(before);
        if (before.Length <= 0 || before.Length > 4096) throw new InvalidOperationException("marker-size");
        long creation = before.CreationTimeUtc.Ticks, write = before.LastWriteTimeUtc.Ticks, size = before.Length;
        byte[] bytes = File.ReadAllBytes(_path);
        FileInfo after = new(_path);
        RejectLinks(after);
        if (after.Length != size || after.CreationTimeUtc.Ticks != creation || after.LastWriteTimeUtc.Ticks != write)
            throw new InvalidOperationException("marker-changed-during-read");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement r = document.RootElement;
        if (r.GetProperty("child_pid").GetInt32() != _pid) throw new InvalidOperationException("foreign-pid");
        string state = r.GetProperty("state").GetString();
        if (state == "ready") return null;
        if (state != "complete") throw new InvalidOperationException("preload-not-complete");
        long origin = r.GetProperty("monotonic_ns").GetInt64();
        long start = r.GetProperty("start_monotonic_ns").GetInt64();
        long now = MonotonicNanoseconds();
        if (origin <= 0 || start <= 0 || start > origin || origin > now || now - origin >= _invalidAfter)
            throw new InvalidOperationException("invalid-preload-origin");
        return new MarkerIdentity(origin, Convert.ToHexString(SHA256.HashData(bytes)), size, creation, write);
    }

    private void Watch()
    {
        try
        {
            long began = MonotonicNanoseconds();
            MarkerIdentity? original = null;
            while (!_cancelled)
            {
                long now = MonotonicNanoseconds();
                MarkerIdentity? current = ReadMarker();
                if (original == null)
                {
                    if (current != null)
                    {
                        original = current;
                        Report("armed", new { child_pid = _pid, origin_ns = original.Value.Origin,
                            close_target_ns = _target, observed_ns = now, profile = _profile, invalid_after_ns = _invalidAfter });
                    }
                    else if (now - began >= _preloadWait)
                    {
                        IssueClose("preload-timeout", null);
                        return;
                    }
                }
                else if (current == null || current.Value != original.Value)
                {
                    IssueClose("original-preload-marker-changed", original.Value.Origin);
                    return;
                }
                if (original != null && now - original.Value.Origin >= _target)
                {
                    IssueClose("original-preload-deadline", original.Value.Origin);
                    return;
                }
                Thread.Sleep(20);
            }
            Report("cancelled", new { child_pid = _pid });
        }
        catch (Exception e)
        {
            Report("marker-or-watch-refused", new { child_pid = _pid, error = e.GetType().Name, reason = e.Message });
            IssueClose("watch-refusal", null);
        }
    }

    private void IssueClose(string reason, long? origin)
    {
        if (_cancelled || Interlocked.Exchange(ref _closeIssued, 1) != 0) return;
        // Gate/neutral is synchronous and precedes the exact-window UI dispatch.
        try
        {
            _neutralize();
            Report("input-stopped-neutral", new { child_pid = _pid, profile = _profile, reason, origin_ns = origin,
                stopped_ns = MonotonicNanoseconds() });
        }
        catch (Exception e) { Report("neutral-failed", new { child_pid = _pid, error = e.GetType().Name }); }
        try
        {
            _close();
            Report("normal-close-posted", new { child_pid = _pid, profile = _profile, requested_ns = MonotonicNanoseconds() });
        }
        catch (Exception e) { Report("close-request-failed", new { child_pid = _pid, error = e.GetType().Name }); }
    }

    private static void Report(string status, object fields)
    {
        try { Console.Error.WriteLine("SMASH_DIAGNOSTIC_CLOSE " + JsonSerializer.Serialize(new { status, fields })); }
        catch { }
    }

    public void Dispose() { _cancelled = true; }
}
