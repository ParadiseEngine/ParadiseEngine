#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Paradise.Assets.Pipeline
{
    /// <summary>Subprocess and executable-resolution helpers.</summary>
    public static class ProcessTools
    {
        public readonly record struct ProcessResult(bool Started, bool TimedOut, int ExitCode, string Stdout, string Stderr)
        {
            public bool Succeeded => Started && !TimedOut && ExitCode == 0;

            /// <summary>One line naming which of the three ways a run can fail, with the tool's own output after it.</summary>
            public string Describe(string what, int timeoutMilliseconds) =>
                !Started ? $"{what} could not be started: {Stderr.Trim()}"
                : TimedOut ? $"{what} timed out after {(timeoutMilliseconds >= 60_000 ? $"{timeoutMilliseconds / 60_000} minute(s)" : $"{timeoutMilliseconds / 1_000} second(s)")}.\n{Stdout}{Stderr}"
                : $"{what} failed (code {ExitCode}).\n{Stdout}{Stderr}";
        }

        public static ProcessResult Run(
            string fileName,
            string arguments,
            int timeoutMilliseconds,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (environment != null)
            {
                foreach (KeyValuePair<string, string> entry in environment)
                {
                    startInfo.EnvironmentVariables[entry.Key] = entry.Value;
                }
            }

            // A file that exists but is not executable, or is the wrong architecture, throws
            // here rather than failing the child; that is a tool problem to report, not a crash.
            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return new ProcessResult(false, false, -1, string.Empty, $"{fileName}: {exception.Message}");
            }

            if (process == null)
            {
                return new ProcessResult(false, false, -1, string.Empty, $"{fileName}: the process did not start");
            }

            using Process owned = process;

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                Kill(process);
                process.WaitForExit(5_000);
                try
                {
                    Task.WhenAll(stdoutTask, stderrTask).Wait(1_000);
                }
                catch
                {
                }

                return new ProcessResult(true, true, -1, CompletedOutput(stdoutTask), CompletedOutput(stderrTask));
            }

            // WaitForExit(timeout) does not guarantee the async streams are drained.
            process.WaitForExit();
            return new ProcessResult(true, false, process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }

        /// <summary>Env var, then candidate paths, then PATH; the first file that is runnable, not merely present.</summary>
        public static string? FindExecutable(string? environmentVariableValue, IEnumerable<string> candidatePaths, string executableName)
        {
            if (!string.IsNullOrWhiteSpace(environmentVariableValue) && IsRunnable(environmentVariableValue))
            {
                return environmentVariableValue;
            }

            foreach (string candidate in candidatePaths)
            {
                if (IsRunnable(candidate))
                {
                    return candidate;
                }
            }

            foreach (string candidate in ExecutableSearchPaths(executableName))
            {
                if (IsRunnable(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>Exists and, off Windows, carries an execute bit. An unpacked archive without one would otherwise be found and then fail to start.</summary>
        public static bool IsRunnable(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            try
            {
                return (File.GetUnixFileMode(path) & anyExecute) != 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static IEnumerable<string> ExecutableSearchPaths(string executableName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';')
                : new[] { "" };

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (string extension in extensions)
                {
                    yield return Path.Combine(directory, executableName + extension);
                }
            }
        }

        public static string ComputeFileSha256(string fullPath)
        {
            using FileStream stream = File.OpenRead(fullPath);
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        // CommandLineToArgvW rules: a run of backslashes before a quote must be doubled, or a
        // trailing backslash ("C:\dir\") escapes the closing quote.
        public static string QuoteArgument(string argument)
        {
            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(c);
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    // The whole tree: `dotnet run` wrappers would otherwise leave the real tool running.
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        private static string CompletedOutput(Task<string> outputTask)
        {
            if (!outputTask.IsCompleted)
            {
                return string.Empty;
            }

            try
            {
                return outputTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                return $"[Failed to read process output: {exception.Message}]\n";
            }
        }
    }
}
