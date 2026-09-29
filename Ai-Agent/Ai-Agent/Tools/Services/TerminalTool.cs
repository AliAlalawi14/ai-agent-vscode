using Ai_Agent.Tools.Interfaces;
using System.Diagnostics;
using System.Text;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Runs an allow-listed developer command WITHOUT a shell: the program is started directly and each
    /// argument is passed as-is (ProcessStartInfo.ArgumentList), so "&amp;", "|", "&gt;" or "$(...)" can't chain
    /// or redirect anything. Output is read asynchronously with a real timeout that kills the process tree.
    /// </summary>
    public class TerminalTool : ITool
    {
        private readonly string _workspaceRoot;

        // (program, first argument) pairs that may run, from Agent:AllowedCommands (defaults: dotnet/git read+build).
        private readonly (string Program, string Subcommand)[] _allowed;
        private readonly string _allowedList;

        // Options that write files, run other programs, or read outside the workspace (git diff --no-index)
        private static readonly string[] BlockedArguments = { "--output", "--ext-diff", "-o", "--exec", "--upload-pack", "--no-index" };

        // Windows runs .cmd/.bat programs (npm, npx, yarn...) through cmd.exe, which gives these characters meaning
        private static readonly char[] BatchUnsafeChars = { '%', '^', '!', '(', ')', '"' };

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);   // dotnet test can be slow
        private const int MaxOutputLength = 5000;

        public string Name => "run_terminal";

        public string Description =>
            $"Runs ONE developer command in the workspace, without a shell. Allowed: {_allowedList} (plus their options, e.g. " +
            "'dotnet test --filter Name~Calc'). Not available: pipes, &&, redirects, dir/ls/type/cat, dotnet run. " +
            "To find or read files use find_files, list_directory and read_file instead. The user must approve each command.";

        public Dictionary<string, string> Parameters => new()
        {
            { "command", "The command, e.g. 'dotnet build' or 'git diff --stat'" }
        };

        // Commands run on the user's machine, so each one is approved in the chat first
        public bool RequiresApproval => true;

        public TerminalTool(string workspaceRoot, IEnumerable<string>? allowedCommands = null)
        {
            _workspaceRoot = workspaceRoot;
            _allowed = (allowedCommands ?? new Config.AgentOptions().AllowedCommands)
                .Select(c => c.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant()))
                .Distinct()
                .ToArray();
            _allowedList = string.Join(", ", _allowed.Select(a => $"{a.Program} {a.Subcommand}"));
        }

        public Task<ToolPreview> PreviewAsync(Dictionary<string, string> parameters)
        {
            var (error, command, _) = Validate(parameters);
            // A command that would be refused anyway is not shown to the user for approval
            return Task.FromResult(error != null ? ToolPreview.Fail(error) : new ToolPreview { Command = command });
        }

        public Task<string> ExecuteAsync(Dictionary<string, string> parameters) =>
            ExecuteAsync(parameters, CancellationToken.None);

        public async Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            var (validationError, command, args) = Validate(parameters);
            if (validationError != null)
                return $"ERROR: {validationError}";

            try
            {
                var startInfo = new ProcessStartInfo(ResolveProgram(args[0]))
                {
                    WorkingDirectory = _workspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var arg in args.Skip(1))
                    startInfo.ArgumentList.Add(arg);
                startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                startInfo.Environment["DOTNET_NOLOGO"] = "1";

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Could not start '{args[0]}'");

                // Read both streams concurrently, otherwise a full buffer can deadlock the child process
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                // Stops on the timeout OR when the user presses Stop
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
                    return cancellationToken.IsCancellationRequested
                        ? $"ERROR: '{command}' was stopped because the user cancelled the request."
                        : $"ERROR: '{command}' timed out after {Timeout.TotalSeconds:0} seconds and was stopped.";
                }

                var output = await stdoutTask;
                var error = await stderrTask;

                var result = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(output))
                    result.AppendLine(output.TrimEnd());
                if (!string.IsNullOrWhiteSpace(error))
                {
                    if (result.Length > 0) result.AppendLine();
                    result.AppendLine("--- STDERR ---");
                    result.AppendLine(error.TrimEnd());
                }

                var finalOutput = result.ToString().TrimEnd();

                // Keep the END of long output: build/test errors and summaries are printed last
                if (finalOutput.Length > MaxOutputLength)
                {
                    finalOutput = $"(Output truncated: showing the last {MaxOutputLength} chars)\n" +
                                  finalOutput[^MaxOutputLength..];
                }

                // Always state the exit code; a non-zero exit starts with "ERROR:" so the run is reported as failed
                return process.ExitCode == 0
                    ? $"{finalOutput}\n\nExit code: 0"
                    : $"ERROR: Command failed with exit code {process.ExitCode}\n\n{finalOutput}";
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return $"ERROR: '{args[0]}' could not be started ({ex.Message}). Is it installed and on PATH?";
            }
            catch (Exception ex)
            {
                return $"ERROR executing command: {ex.Message}";
            }
        }

        private (string? Error, string Command, List<string> Args) Validate(Dictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
                return ("Missing 'command' parameter", string.Empty, new());

            command = command.Trim();
            var args = SplitArguments(command);
            if (args.Count == 0)
                return ("Empty command", command, args);

            if (command.IndexOfAny(new[] { '&', '|', ';', '<', '>', '`', '\n', '\r' }) >= 0 || command.Contains("$("))
                return ("Shell operators (&&, |, ;, >, <, `, $()) are not supported: run one command at a time.", command, args);

            var program = args[0].ToLowerInvariant();
            if (program.EndsWith(".exe")) program = program[..^4];
            var sub = args.Count > 1 ? args[1].ToLowerInvariant() : string.Empty;

            if (!_allowed.Any(a => a.Program == program && a.Subcommand == sub))
                return ($"Command not allowed: '{command}'. Allowed: {_allowedList}. " +
                        "Use find_files / list_directory / read_file to look at files.", command, args);

            if (args.Skip(2).Any(a => BlockedArguments.Any(b => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                                                                 a.StartsWith(b + "=", StringComparison.OrdinalIgnoreCase))))
                return ($"Option not allowed in '{command}' (it could write files or run other programs).", command, args);

            if (IsBatchFile(ResolveProgram(program)) && command.IndexOfAny(BatchUnsafeChars) >= 0)
                return ($"'{program}' runs through cmd.exe on Windows, so {string.Join(" ", BatchUnsafeChars)} are not allowed in its arguments.", command, args);

            args[0] = program;
            return (null, command, args);
        }

        /// <summary>
        /// Without a shell, Windows only finds "name.exe"; npm, npx, yarn... are "name.cmd". Looks the program up on
        /// PATH with PATHEXT's extensions; returns the name unchanged when nothing matches (Process.Start reports it).
        /// </summary>
        public static string ResolveProgram(string program)
        {
            if (!OperatingSystem.IsWindows() || Path.HasExtension(program) || Path.IsPathRooted(program)) return program;

            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(e => e.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                            e.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                            e.Equals(".bat", StringComparison.OrdinalIgnoreCase));
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in dirs)
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(dir.Trim('"'), program + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            return program;
        }

        private static bool IsBatchFile(string path) =>
            path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        /// <summary>Splits a command line into arguments; double quotes group, and are removed.</summary>
        private static List<string> SplitArguments(string command)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;
            foreach (var c in command)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) args.Add(current.ToString());
            return args;
        }
    }
}
