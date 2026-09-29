using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ai_Agent.Agent.Services
{
    /// <summary>One check the verify loop runs: a build or the tests, as a program and its arguments (no shell).</summary>
    public sealed record VerifyCommand(string Kind, string Display, string Program, IReadOnlyList<string> Args);

    public sealed record TestCounts(int Passed, int Failed, int Skipped)
    {
        public int Total => Passed + Failed + Skipped;
    }

    public sealed record VerifyStepResult(
        string Kind, string Command, bool Ok, int? ExitCode, long DurationMs, string Summary, string OutputTail,
        TestCounts? Tests, bool TimedOut, bool Skipped);

    public sealed record VerifyResult(bool Passed, IReadOnlyList<VerifyStepResult> Steps)
    {
        public TestCounts? Tests => Steps.LastOrDefault(s => s.Tests != null)?.Tests;
    }

    /// <summary>
    /// The project's build and test commands, found from its files, so the agent can check its own work and the
    /// user sees "build passed, 12/12 tests" instead of trusting "done". Configured commands (Agent:VerifyCommands)
    /// win over detection.
    /// </summary>
    public static class ProjectCommands
    {
        public static List<VerifyCommand> Detect(string root, IReadOnlyList<string>? configured = null)
        {
            if (configured is { Count: > 0 })
                return configured.Select(c => FromLine(c, c.Contains("test", StringComparison.OrdinalIgnoreCase) ? "test" : "build"))
                    .Where(c => c != null).Select(c => c!).ToList();

            var list = new List<VerifyCommand>();
            bool Has(string pattern) => Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any();

            // .NET: a solution or project at the root (or one level down)
            var dotnet = Has("*.sln") || Has("*.slnx") || Has("*.csproj") || Has("*.fsproj") ||
                         SafeDirs(root).Any(d => Directory.EnumerateFiles(d, "*.csproj").Any());
            if (dotnet)
            {
                list.Add(new("build", "dotnet build", "dotnet", new[] { "build", "--nologo" }));
                if (HasDotnetTests(root))
                    list.Add(new("test", "dotnet test", "dotnet", new[] { "test", "--no-build", "--nologo" }));
                return list;
            }

            var packageJson = Path.Combine(root, "package.json");
            if (File.Exists(packageJson))
            {
                var pm = File.Exists(Path.Combine(root, "pnpm-lock.yaml")) ? "pnpm"
                       : File.Exists(Path.Combine(root, "yarn.lock")) ? "yarn"
                       : File.Exists(Path.Combine(root, "bun.lockb")) || File.Exists(Path.Combine(root, "bun.lock")) ? "bun"
                       : "npm";
                var scripts = ReadScripts(packageJson);
                if (scripts.TryGetValue("build", out _))
                    list.Add(new("build", $"{pm} run build", pm, new[] { "run", "build" }));
                else if (File.Exists(Path.Combine(root, "tsconfig.json")))
                    list.Add(new("build", "npx tsc --noEmit", "npx", new[] { "tsc", "--noEmit" }));
                if (scripts.TryGetValue("test", out var test) && !test.Contains("no test specified"))
                    list.Add(new("test", $"{pm} test", pm, pm == "npm" ? new[] { "test", "--silent" } : new[] { "test" }));
                return list;
            }

            if (File.Exists(Path.Combine(root, "Cargo.toml")))
                return new() { new("build", "cargo build", "cargo", new[] { "build" }), new("test", "cargo test", "cargo", new[] { "test" }) };

            if (File.Exists(Path.Combine(root, "go.mod")))
                return new() { new("build", "go build ./...", "go", new[] { "build", "./..." }), new("test", "go test ./...", "go", new[] { "test", "./..." }) };

            if (File.Exists(Path.Combine(root, "pom.xml")))
            {
                var mvn = File.Exists(Path.Combine(root, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw")) ? Path.Combine(root, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw") : "mvn";
                return new() { new("test", "mvn test", mvn, new[] { "-q", "test" }) };
            }

            if (File.Exists(Path.Combine(root, "build.gradle")) || File.Exists(Path.Combine(root, "build.gradle.kts")))
            {
                var wrapper = Path.Combine(root, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
                return new() { new("test", "gradle test", File.Exists(wrapper) ? wrapper : "gradle", new[] { "test", "-q" }) };
            }

            var python = File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "setup.py")) ||
                         File.Exists(Path.Combine(root, "pytest.ini")) || File.Exists(Path.Combine(root, "requirements.txt"));
            if (python && (Directory.Exists(Path.Combine(root, "tests")) || Has("test_*.py") || File.Exists(Path.Combine(root, "pytest.ini"))))
                list.Add(new("test", "pytest", OperatingSystem.IsWindows() ? "python" : "python3", new[] { "-m", "pytest", "-q" }));

            return list;
        }

        /// <summary>A configured command line ("npm run lint") as program + arguments; quotes group arguments.</summary>
        public static VerifyCommand? FromLine(string line, string kind)
        {
            var args = Regex.Matches(line.Trim(), "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
            if (args.Count == 0) return null;
            return new VerifyCommand(kind, line.Trim(), args[0], args.Skip(1).ToList());
        }

        private static IEnumerable<string> SafeDirs(string root)
        {
            try { return Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.') && Path.GetFileName(d) is not ("node_modules" or "bin" or "obj")).Take(50).ToList(); }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        private static bool HasDotnetTests(string root)
        {
            IEnumerable<string> projects;
            try { projects = Directory.EnumerateFiles(root, "*.csproj", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }).Take(100).ToList(); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            return projects.Any(p =>
            {
                try { return File.ReadAllText(p).Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase); }
                catch (IOException) { return false; }
            });
        }

        private static Dictionary<string, string> ReadScripts(string packageJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJson), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
                    return scripts.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(p => p.Name, p => p.Value.GetString()!);
            }
            catch (Exception e) when (e is JsonException or IOException) { }
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Test counts from the summary lines of common test runners.</summary>
    public static class TestSummaryParser
    {
        public static TestCounts? Parse(string output)
        {
            // dotnet test: "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12" (one line per test project)
            var dotnet = Regex.Matches(output, @"(?:Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)");
            if (dotnet.Count > 0)
                return new TestCounts(dotnet.Sum(m => I(m, 2)), dotnet.Sum(m => I(m, 1)), dotnet.Sum(m => I(m, 3)));

            // cargo: "test result: ok. 12 passed; 0 failed; 1 ignored" (one per test binary)
            var cargo = Regex.Matches(output, @"test result: \w+\. (\d+) passed; (\d+) failed; (\d+) ignored");
            if (cargo.Count > 0)
                return new TestCounts(cargo.Sum(m => I(m, 1)), cargo.Sum(m => I(m, 2)), cargo.Sum(m => I(m, 3)));

            // jest: "Tests:       1 failed, 2 skipped, 11 passed, 14 total"
            var jest = Regex.Match(output, @"Tests:\s+(.*?)(\d+) total");
            if (jest.Success)
                return new TestCounts(Count(jest.Groups[1].Value, "passed"), Count(jest.Groups[1].Value, "failed"),
                    Count(jest.Groups[1].Value, "skipped") + Count(jest.Groups[1].Value, "todo"));

            // vitest: "Tests  1 failed | 11 passed | 2 skipped (14)"
            var vitest = Regex.Match(output, @"Tests\s+((?:\d+ \w+(?: \| )?)+)\s+\((\d+)\)");
            if (vitest.Success)
                return new TestCounts(Count(vitest.Groups[1].Value, "passed"), Count(vitest.Groups[1].Value, "failed"), Count(vitest.Groups[1].Value, "skipped"));

            // pytest: "===== 1 failed, 11 passed, 2 skipped in 0.52s =====" (the last summary line)
            var pytest = Regex.Matches(output, @"=+ (.*?) in [\d.]+s(?: \([^)]*\))? =+");
            if (pytest.Count > 0)
            {
                var line = pytest[^1].Groups[1].Value;
                if (Regex.IsMatch(line, @"\d+ (passed|failed)"))
                    return new TestCounts(Count(line, "passed"), Count(line, "failed") + Count(line, "error"), Count(line, "skipped"));
            }

            // Maven surefire: "Tests run: 12, Failures: 1, Errors: 0, Skipped: 0" (the last one is the total)
            var maven = Regex.Matches(output, @"Tests run: (\d+), Failures: (\d+), Errors: (\d+), Skipped: (\d+)");
            if (maven.Count > 0)
            {
                var m = maven[^1];
                var failed = I(m, 2) + I(m, 3);
                return new TestCounts(I(m, 1) - failed - I(m, 4), failed, I(m, 4));
            }

            // mocha: "12 passing", "1 failing", "2 pending"
            var passing = Regex.Match(output, @"(\d+) passing");
            if (passing.Success)
                return new TestCounts(I(passing, 1), Count(output, "failing"), Count(output, "pending"));

            return null;
        }

        private static int I(Match m, int group) => int.Parse(m.Groups[group].Value);

        private static int Count(string text, string word)
        {
            var m = Regex.Match(text, $@"(\d+) {word}");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
    }

    /// <summary>
    /// Runs the verify commands one after another (a failed build skips the tests), without a shell, in the
    /// workspace, with a time limit each. Stop (cancellation) kills the running process tree.
    /// </summary>
    public class VerifyRunner
    {
        public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan TestTimeout { get; init; } = TimeSpan.FromMinutes(10);
        private const int TailChars = 3000;

        public async Task<VerifyResult> RunAsync(string root, IReadOnlyList<VerifyCommand> commands, CancellationToken cancellationToken)
        {
            var steps = new List<VerifyStepResult>();
            var failed = false;
            foreach (var command in commands)
            {
                if (failed)
                {
                    steps.Add(new VerifyStepResult(command.Kind, command.Display, false, null, 0, "Skipped: the build failed", "", null, false, true));
                    continue;
                }
                var step = await RunOneAsync(root, command, cancellationToken);
                steps.Add(step);
                if (!step.Ok) failed = true;
            }
            return new VerifyResult(!failed, steps);
        }

        private async Task<VerifyStepResult> RunOneAsync(string root, VerifyCommand command, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo(Tools.Services.TerminalTool.ResolveProgram(command.Program))
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in command.Args) startInfo.ArgumentList.Add(arg);
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";
            startInfo.Environment["CI"] = "true";              // test runners don't wait for input or watch files
            startInfo.Environment["NO_COLOR"] = "1";
            startInfo.Environment["FORCE_COLOR"] = "0";

            Process process;
            try
            {
                process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new VerifyStepResult(command.Kind, command.Display, false, null, clock.ElapsedMilliseconds,
                    $"'{command.Program}' could not be started. Is it installed and on PATH?", e.Message, null, false, false);
            }

            using (process)
            {
                var output = new StringBuilder();
                var gate = new object();
                void Append(string? line) { if (line == null) return; lock (gate) { output.AppendLine(line); if (output.Length > 200_000) output.Remove(0, output.Length - 100_000); } }
                process.OutputDataReceived += (_, e) => Append(e.Data);
                process.ErrorDataReceived += (_, e) => Append(e.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(command.Kind == "test" ? TestTimeout : BuildTimeout);
                var timedOut = false;
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    process.WaitForExit();   // flush the async output readers
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    if (cancellationToken.IsCancellationRequested) throw;
                    timedOut = true;
                }

                string text;
                lock (gate) text = Regex.Replace(output.ToString(), @"\x1B\[[0-9;]*[A-Za-z]", "");   // strip colors
                var tail = text.Length > TailChars ? "…" + text[^TailChars..] : text;
                var exit = timedOut ? (int?)null : process.ExitCode;
                // Counts whenever the output has a test summary, whatever the command is called (configured ones too)
                var tests = TestSummaryParser.Parse(text);
                var ok = !timedOut && exit == 0 && (tests == null || tests.Failed == 0);
                var limit = command.Kind == "test" ? TestTimeout : BuildTimeout;
                var summary = timedOut
                    ? $"{Capital(command.Kind)} timed out after {limit.TotalMinutes:0} min"
                    : command.Kind == "test" || tests != null
                        ? tests == null
                            ? ok ? "Tests passed" : $"Tests failed (exit code {exit})"
                            : tests.Failed == 0 ? $"{tests.Passed}/{tests.Total} tests passed" : $"{tests.Failed} of {tests.Total} tests failed"
                        : ok ? "Build passed" : $"Build failed (exit code {exit})";
                return new VerifyStepResult(command.Kind, command.Display, ok, exit, clock.ElapsedMilliseconds, summary, tail.Trim(), tests, timedOut, false);
            }
        }

        private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
