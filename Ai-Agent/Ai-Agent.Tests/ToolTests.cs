using Ai_Agent.Agent.Services;
using Ai_Agent.Tools.Services;

namespace Ai_Agent.Tests
{
    public class WorkspacePathTests
    {
        // C:\ws\project on Windows, /ws/project elsewhere; it doesn't have to exist
        private static readonly string Root = Path.GetFullPath("/ws/project");

        [Theory]
        [InlineData("Calculator.cs")]
        [InlineData("Controllers/HomeController.cs")]
        [InlineData("sub/../Calculator.cs")]
        [InlineData("")]
        public void Paths_inside_the_workspace_resolve(string relative) =>
            Assert.NotNull(WorkspacePath.Resolve(Root, relative));

        public static TheoryData<string> OutsidePaths()
        {
            var paths = new TheoryData<string>
            {
                "../../etc/passwd",
                "../project-evil/x.cs",    // prefix trick: /ws/project-evil starts with /ws/project
                "sub/../../outside.txt",
                Path.GetFullPath("/elsewhere/x.txt"),
            };
            // Backslashes and drive letters are separators only on Windows; elsewhere they are file name characters
            if (OperatingSystem.IsWindows())
            {
                paths.Add(@"..\..\Windows\win.ini");
                paths.Add(@"C:\Windows\win.ini");
                paths.Add(@"..\project-evil\x.cs");
                paths.Add(@"sub\..\..\outside.txt");
            }
            return paths;
        }

        [Theory]
        [MemberData(nameof(OutsidePaths))]
        public void Paths_outside_the_workspace_are_refused(string relative) =>
            Assert.Null(WorkspacePath.Resolve(Root, relative));

        [Fact]
        public void Internal_bookkeeping_files_are_recognized()
        {
            Assert.True(WorkspacePath.IsInternalFile(".ai_changes.ndjson"));
            Assert.False(WorkspacePath.IsInternalFile("Program.cs"));
        }
    }

    public class ToolArgumentsTests
    {
        [Fact]
        public void Strings_are_unescaped_so_multiline_content_is_real_newlines()
        {
            var args = ToolArguments.Parse("{\"path\":\"a.cs\",\"content\":\"line1\\nline2 \\\"q\\\"\"}", out var error);
            Assert.Null(error);
            Assert.Equal("line1\nline2 \"q\"", args["content"]);
        }

        [Fact]
        public void Numbers_booleans_and_arrays_are_kept_as_json_text()
        {
            var args = ToolArguments.Parse("{\"startLine\":5,\"replace_all\":true,\"steps\":[{\"title\":\"a\"}]}", out _);
            Assert.Equal("5", args["startLine"]);
            Assert.Equal("true", args["replace_all"]);
            Assert.StartsWith("[", args["steps"]);
        }

        [Fact]
        public void Truncated_json_reports_an_error_instead_of_empty_arguments()
        {
            var args = ToolArguments.Parse("{\"path\":\"a.cs\",\"content\":\"unterminated", out var error);
            Assert.NotNull(error);
            Assert.Empty(args);
        }
    }

    public class EditFileToolTests
    {
        private static Dictionary<string, string> Args(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

        [Fact]
        public async Task Unique_match_is_replaced_keeping_crlf_and_bom()
        {
            using var ws = new TempWorkspace();
            ws.Write("Calc.cs", "class C\r\n{\r\n    int A() => 1;\r\n}\r\n", bom: true);

            var result = await new EditFileTool(ws.Root).ExecuteAsync(Args(
                ("path", "Calc.cs"), ("old_string", "    int A() => 1;"), ("new_string", "    int A() => 2;\n    // added")));

            Assert.StartsWith("SUCCESS", result);
            var text = ws.Read("Calc.cs");
            Assert.Contains("int A() => 2;\r\n    // added\r\n", text);
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // no lone LF introduced
            Assert.Equal(0xEF, ws.ReadBytes("Calc.cs")[0]);          // BOM kept
        }

        [Fact]
        public async Task Ambiguous_match_is_refused_and_file_unchanged()
        {
            using var ws = new TempWorkspace();
            ws.Write("Calc.cs", "int A() => 1;\nint B() => 1;\n");

            var result = await new EditFileTool(ws.Root).ExecuteAsync(Args(("path", "Calc.cs"), ("old_string", " => 1;"), ("new_string", " => 3;")));

            Assert.StartsWith("ERROR", result);
            Assert.Contains("appears 2 times", result);
            Assert.Equal("int A() => 1;\nint B() => 1;\n", ws.Read("Calc.cs"));
        }

        [Fact]
        public async Task Near_miss_reports_the_line_of_the_first_matching_line()
        {
            using var ws = new TempWorkspace();
            ws.Write("Calc.cs", "class C\n{\n    int A() => 1;\n    int B() => 2;\n}\n");

            var result = await new EditFileTool(ws.Root).ExecuteAsync(Args(
                ("path", "Calc.cs"), ("old_string", "int A() => 1;\n  int B() => 2;"), ("new_string", "x")));

            Assert.StartsWith("ERROR", result);
            Assert.Contains("line 3", result);
        }

        [Fact]
        public async Task Preview_does_not_write()
        {
            using var ws = new TempWorkspace();
            ws.Write("Calc.cs", "class C {}\n");

            var preview = await new EditFileTool(ws.Root).PreviewAsync(Args(("path", "Calc.cs"), ("old_string", "class C"), ("new_string", "class D")));

            Assert.Contains("class D", preview.After);
            Assert.Equal("class C {}\n", ws.Read("Calc.cs"));
        }
    }

    public class FindFilesToolTests
    {
        [Fact]
        public async Task Globs_match_across_folders_and_skip_build_output_and_internal_files()
        {
            using var ws = new TempWorkspace();
            ws.Write("Calc.cs", "x");
            ws.Write("Controllers/HomeController.cs", "x");
            ws.Write("bin/Debug/Skip.cs", "x");
            ws.Write(".ai_changes.ndjson", "x");
            var tool = new FindFilesTool(ws.Root);

            var all = await tool.ExecuteAsync(new() { ["pattern"] = "**/*.cs" });
            Assert.Contains("Calc.cs", all);
            Assert.Contains("Controllers/HomeController.cs", all);
            Assert.DoesNotContain("Skip.cs", all);

            Assert.Contains("Controllers/HomeController.cs", await tool.ExecuteAsync(new() { ["pattern"] = "HomeController.cs" }));
            Assert.DoesNotContain(".ai_changes", await tool.ExecuteAsync(new() { ["pattern"] = "*" }));
            Assert.StartsWith("ERROR", await tool.ExecuteAsync(new() { ["pattern"] = "../*" }));
        }
    }

    public class TerminalToolTests
    {
        [Theory]
        [InlineData("dir & calc")]
        [InlineData("dotnet build && calc")]
        [InlineData("cmd /c dir")]
        [InlineData("dotnet run")]
        [InlineData("git diff --no-index C:/Windows/win.ini x")]
        [InlineData("git diff --output=x")]
        [InlineData("powershell -c whoami")]
        public async Task Dangerous_or_unlisted_commands_are_refused_before_approval(string command)
        {
            var preview = await new TerminalTool(Path.GetTempPath()).PreviewAsync(new() { ["command"] = command });
            Assert.NotNull(preview.Error);
        }

        [Theory]
        [InlineData("dotnet build")]
        [InlineData("dotnet test --filter Name~Calc")]
        [InlineData("git status")]
        public async Task Allowed_commands_get_an_approval_preview(string command)
        {
            var preview = await new TerminalTool(Path.GetTempPath()).PreviewAsync(new() { ["command"] = command });
            Assert.Null(preview.Error);
            Assert.Equal(command, preview.Command);
        }
    }

    public class SubmitPlanToolTests
    {
        [Fact]
        public void Parses_steps_array_with_files_and_details()
        {
            var plan = SubmitPlanTool.Parse(new()
            {
                ["title"] = "Orders",
                ["summary"] = "## Goal\nAdd orders",
                ["steps"] = "[{\"title\":\"Model\",\"files\":[\"Order.cs\"],\"details\":\"Create Order\"},\"Controller\"]"
            }, out var error);

            Assert.Null(error);
            Assert.NotNull(plan);
            Assert.Equal(2, plan!.Steps.Count);
            Assert.Equal(new[] { "Order.cs" }, plan.Steps[0].Files);
            Assert.Equal("Controller", plan.Steps[1].Title);
            Assert.Contains("Goal", plan.Summary);
        }

        [Fact]
        public void Rejects_a_plan_without_steps()
        {
            Assert.Null(SubmitPlanTool.Parse(new() { ["title"] = "x", ["steps"] = "[]" }, out var error));
            Assert.NotNull(error);
        }
    }
}
