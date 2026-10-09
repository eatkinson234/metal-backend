using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.Utilities
{
    // This guard is intentionally independent of the normal Ryujinx startup
    // stack. The private prototype must reject unsafe launches before any
    // command-line parser, configuration loader, UI, or data-directory code
    // can observe the arguments.
    public static class NativePrototypeLaunchGuard
    {
        private const string RequireNativeMetalVariable = "SMASH_COUCH_REQUIRE_NATIVE_METAL";
        private const string PrivateDataVariable = "SMASH_COUCH_PRIVATE_DATA";
        private const string PrivateLabDirectoryName = "Lab";

        public static bool TryValidate(string[] args)
        {
            return TryValidate(
                args,
                Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AppContext.BaseDirectory,
                message => Console.Error.WriteLine(message));
        }

        // The injected environment and path roots keep this method deterministic
        // for tests. It performs reads only; it never creates, changes, or
        // deletes a file or directory.
        public static bool TryValidate(
            string[] args,
            Func<string, string> getEnvironmentVariable,
            string homeDirectory,
            string applicationBaseDirectory,
            Action<string> report)
        {
            try
            {
                ValidateOrThrow(args, getEnvironmentVariable, homeDirectory, applicationBaseDirectory);
                return true;
            }
            catch (LaunchRejectedException exception)
            {
                Report(report, $"Native Metal prototype launch rejected: {exception.Message}");
                return false;
            }
            catch (Exception exception)
            {
                // Treat unexpected filesystem/path failures as a rejection. A
                // guard failure must never fall through into normal startup.
                Report(report, $"Native Metal prototype launch rejected: validation failure ({exception.Message})");
                return false;
            }
        }

        private static void ValidateOrThrow(
            string[] args,
            Func<string, string> getEnvironmentVariable,
            string homeDirectory,
            string applicationBaseDirectory)
        {
            if (args is null)
            {
                throw Reject("argument vector is missing");
            }

            if (getEnvironmentVariable is null || string.IsNullOrEmpty(homeDirectory) || string.IsNullOrEmpty(applicationBaseDirectory))
            {
                throw Reject("guard inputs are incomplete");
            }

            if (!string.Equals(getEnvironmentVariable(RequireNativeMetalVariable), "1", StringComparison.Ordinal))
            {
                throw Reject($"{RequireNativeMetalVariable}=1 is required");
            }

            string rootDataArgument = null;
            string graphicsBackend = null;
            string gameArgument = null;
            bool fullscreen = false;

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (string.IsNullOrEmpty(argument))
                {
                    throw Reject("empty command-line argument");
                }

                switch (argument)
                {
                    case "-r":
                    case "--root-data-dir":
                        if (rootDataArgument is not null)
                        {
                            throw Reject("root data directory may be specified only once");
                        }

                        rootDataArgument = ReadOptionValue(args, ref index, argument);
                        break;
                    case "-g":
                    case "--graphics-backend":
                        if (graphicsBackend is not null)
                        {
                            throw Reject("graphics backend may be specified only once");
                        }

                        graphicsBackend = ReadOptionValue(args, ref index, argument);
                        break;
                    case "-f":
                    case "--fullscreen":
                        if (fullscreen)
                        {
                            throw Reject("fullscreen may be specified only once");
                        }

                        fullscreen = true;
                        break;
                    default:
                        if (string.Equals(argument, "nogui", StringComparison.Ordinal))
                        {
                            throw Reject("headless alias 'nogui' is not permitted");
                        }

                        if (argument.StartsWith("-", StringComparison.Ordinal))
                        {
                            throw Reject($"unsupported option '{argument}'");
                        }

                        if (gameArgument is not null)
                        {
                            throw Reject("exactly one game file is permitted");
                        }

                        gameArgument = argument;
                        break;
                }
            }

            if (rootDataArgument is null)
            {
                throw Reject("one -r/--root-data-dir path is required");
            }

            if (!string.Equals(graphicsBackend, "metal", StringComparison.OrdinalIgnoreCase))
            {
                throw Reject("-g/--graphics-backend metal is required");
            }

            if (gameArgument is null)
            {
                throw Reject("one existing game file is required");
            }

            string normalizedRoot = NormalizePath(rootDataArgument, "private data path");
            string normalizedHome = NormalizePath(homeDirectory, "home path");
            string normalizedLab = NormalizePath(
                Path.Combine(normalizedHome, "Library", "Application Support", "Smash Couch", PrivateLabDirectoryName),
                "private lab path");

            if (!IsStrictDescendant(normalizedRoot, normalizedLab))
            {
                throw Reject("private data path must be strictly below the Lab directory");
            }

            string privateDataValue = getEnvironmentVariable(PrivateDataVariable);
            if (!string.Equals(privateDataValue, normalizedRoot, StringComparison.Ordinal))
            {
                throw Reject($"{PrivateDataVariable} must equal the normalized -r path");
            }

            EnsurePrivateDirectoryIsSafe(normalizedRoot);

            string normalizedGame = NormalizePath(gameArgument, "game path");
            EnsureGameFileIsSafe(normalizedGame);

            string normalizedApplicationBase = NormalizePath(applicationBaseDirectory, "application base path");
            string localConfig = Path.Combine(normalizedApplicationBase, "Config.json");
            if (PathEntryExists(localConfig))
            {
                throw Reject("app-local Config.json is not permitted");
            }
        }

        private static string ReadOptionValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw Reject($"option '{option}' requires a value");
            }

            string value = args[++index];
            if (string.IsNullOrEmpty(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                throw Reject($"option '{option}' has a malformed value");
            }

            return value;
        }

        private static string NormalizePath(string path, string description)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw Reject($"{description} is empty");
            }

            string fullPath = Path.GetFullPath(path);
            return Path.TrimEndingDirectorySeparator(fullPath);
        }

        private static bool IsStrictDescendant(string candidate, string parent)
        {
            if (string.Equals(candidate, parent, StringComparison.Ordinal))
            {
                return false;
            }

            string prefix = parent.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? parent
                : parent + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static void EnsurePrivateDirectoryIsSafe(string path)
        {
            EnsureNoSymlinkInPath(path, "private data path");

            if (!Directory.Exists(path))
            {
                throw Reject("private data directory must already exist");
            }

            if (HasSymlinkDescendant(path))
            {
                throw Reject("private data directory contains a symlink");
            }
        }

        private static void EnsureGameFileIsSafe(string path)
        {
            EnsureNoSymlinkInPath(path, "game path");

            if (!File.Exists(path))
            {
                throw Reject("game path must name an existing regular file");
            }
        }

        private static void EnsureNoSymlinkInPath(string path, string description)
        {
            string current = path;
            while (true)
            {
                if (IsSymbolicLink(current))
                {
                    throw Reject($"{description} has a symlink ancestor");
                }

                DirectoryInfo directory = new(current);
                DirectoryInfo parent = directory.Parent;
                if (parent is null || string.Equals(parent.FullName, directory.FullName, StringComparison.Ordinal))
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        private static bool HasSymlinkDescendant(string root)
        {
            Stack<string> pending = new();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (IsSymbolicLink(entry))
                    {
                        return true;
                    }

                    if (Directory.Exists(entry))
                    {
                        pending.Push(entry);
                    }
                }
            }

            return false;
        }

        private static bool PathEntryExists(string path)
        {
            return IsSymbolicLink(path) || File.Exists(path) || Directory.Exists(path);
        }

        private static bool IsSymbolicLink(string path)
        {
            FileInfo file = new(path);
            if (file.LinkTarget is not null)
            {
                return true;
            }

            DirectoryInfo directory = new(path);
            return directory.LinkTarget is not null;
        }

        private static LaunchRejectedException Reject(string message) => new(message);

        private static void Report(Action<string> report, string message)
        {
            try
            {
                report?.Invoke(message);
            }
            catch
            {
                // A diagnostic sink must not turn a safe rejection into startup.
            }
        }

        private sealed class LaunchRejectedException(string message) : Exception(message);
    }
}
