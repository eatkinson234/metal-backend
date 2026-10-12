using Ryujinx.Graphics.GAL;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    // Overnight diagnostic: on request, write the guest frame being presented so an
    // unattended runner can verify which screen is showing without screen recording.
    // Off unless SMASH_COUCH_SNAPSHOT_DIR names an existing directory inside
    // SMASH_COUCH_PRIVATE_DATA. A request is the file "request" containing a label.
    [SupportedOSPlatform("macos")]
    static class FrameSnapshot
    {
        private static readonly string _dir = ValidateDirectory();
        private static int _frames;

        private static string ValidateDirectory()
        {
            string dir = Environment.GetEnvironmentVariable("SMASH_COUCH_SNAPSHOT_DIR");
            string root = Environment.GetEnvironmentVariable("SMASH_COUCH_PRIVATE_DATA");
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(dir) || !Directory.Exists(dir))
                {
                    return null;
                }

                // Compare resolved locations so a symlink cannot lead outside the private data root.
                string real = Path.GetFullPath(new DirectoryInfo(dir).ResolveLinkTarget(true)?.FullName ?? dir);
                string realRoot = Path.GetFullPath(new DirectoryInfo(root).ResolveLinkTarget(true)?.FullName ?? root);
                return real == Path.GetFullPath(dir) && real.StartsWith(realRoot.TrimEnd('/') + "/", StringComparison.Ordinal) ? real : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void TryCapture(Texture texture)
        {
            // Checking for a request every 15 frames costs one stat four times a second.
            if (_dir == null || ++_frames % 15 != 0)
            {
                return;
            }

            string request = Path.Combine(_dir, "request");
            string label;
            try
            {
                if (!File.Exists(request))
                {
                    return;
                }

                label = File.ReadAllText(request).Trim();
                File.Delete(request);
            }
            catch (Exception)
            {
                return;
            }

            if (label.Length == 0 || label.Length > 64 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                label = "snapshot";
            }

            // A diagnostic must never take down the render thread; failures are recorded instead.
            try
            {
                long started = Stopwatch.GetTimestamp();
                using PinnedSpan<byte> data = texture.GetData(0, 0);
                ReadOnlySpan<byte> bytes = data.Get();
                using (FileStream file = new(Path.Combine(_dir, label + ".raw"), FileMode.CreateNew, FileAccess.Write))
                {
                    file.Write(bytes);
                }

                File.WriteAllText(Path.Combine(_dir, label + ".json"), string.Create(CultureInfo.InvariantCulture,
                    $"{{\"width\":{texture.Width},\"height\":{texture.Height},\"format\":\"{texture.Info.Format}\"," +
                    $"\"bytes\":{bytes.Length},\"stopwatch_ticks\":{started},\"capture_ms\":" +
                    $"{(Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency:F3}}}"));
            }
            catch (Exception error)
            {
                try
                {
                    File.WriteAllText(Path.Combine(_dir, label + ".error"), error.ToString());
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
