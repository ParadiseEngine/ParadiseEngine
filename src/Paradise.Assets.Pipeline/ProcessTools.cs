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
    /// <summary>Subprocess and executable-resolution helpers. A <c>Win32Exception</c> from a non-runnable file escapes <c>Run</c> today (issue #206).</summary>
    public static class ProcessTools
    {
        public readonly record struct ProcessResult(bool Started, bool TimedOut, int ExitCode, string Stdout, string Stderr)
        {
            public bool Succeeded => Started && !TimedOut && ExitCode == 0;
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

            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return new ProcessResult(false, false, -1, string.Empty, string.Empty);
            }

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

        /// <summary>Env var, then candidate paths, then PATH; existence only, not executability (issue #206).</summary>
        public static string? FindExecutable(string? environmentVariableValue, IEnumerable<string> candidatePaths, string executableName)
        {
            if (!string.IsNullOrWhiteSpace(environmentVariableValue) && File.Exists(environmentVariableValue))
            {
                return environmentVariableValue;
            }

            foreach (string candidate in candidatePaths)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            foreach (string candidate in ExecutableSearchPaths(executableName))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
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
                    process.Kill();
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
