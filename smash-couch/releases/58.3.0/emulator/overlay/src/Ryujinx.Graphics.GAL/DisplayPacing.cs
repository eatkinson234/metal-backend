using System;
using System.IO;

namespace Ryujinx.Graphics.GAL;

// Process-wide pacing policy switch, read once. The default presents each frame at a real display
// refresh and locks the emulated 60 Hz vsync to the display (Metal backend only). Legacy restores the
// r15 behaviour exactly (CPU-clock present schedule, free-running composer timer):
//  - SMASH_COUCH_PACING=legacy (rig A/B; the launcher strips SMASH_COUCH_* variables), or
//  - a file named pacing-legacy in the private data directory the launcher passes
//    (SMASH_COUCH_PRIVATE_DATA), so a couch session can go back to r15 pacing at the next game launch
//    without reinstalling the runtime. Its contents are ignored; delete it to return to the default.
public static class DisplayPacing
{
    public const string LegacyFlagFileName = "pacing-legacy";

    // Why legacy pacing is on (for the log), null for the default.
    public static readonly string LegacySource = ReadLegacySource(
        Environment.GetEnvironmentVariable("SMASH_COUCH_PACING"),
        Environment.GetEnvironmentVariable("SMASH_COUCH_PRIVATE_DATA"),
        File.Exists);

    public static readonly bool Legacy = LegacySource != null;

    public static string ReadLegacySource(string pacing, string privateData, Func<string, bool> fileExists)
    {
        if (string.Equals(pacing, "legacy", StringComparison.Ordinal)) return "SMASH_COUCH_PACING=legacy";
        try
        {
            if (!string.IsNullOrEmpty(privateData) && Path.IsPathFullyQualified(privateData))
            {
                string flag = Path.Combine(privateData, LegacyFlagFileName);
                if (fileExists(flag)) return flag;
            }
        }
        catch (Exception)
        {
            // An unreadable private data path keeps the default pacing.
        }
        return null;
    }
}
