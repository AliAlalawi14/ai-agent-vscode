using Ai_Agent.Agent.Services;
using Ai_Agent.Models;
using Ai_Agent.Tools.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Ai_Agent.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AgentController : ControllerBase
    {
        private readonly AgentService _agentService;
        private readonly ValidationService _validationService;
        private readonly ILogger<AgentController> _logger;

        public AgentController(
            AgentService agentService,
            ValidationService validationService,
            ILogger<AgentController> logger)
        {
            _agentService = agentService;
            _validationService = validationService;
            _logger = logger;
        }

        // No per-endpoint concurrency limit: a run waiting for Accept/Reject must not block other chats.
        // Concurrent LLM calls are capped inside AgentService instead (Agent:MaxConcurrentLlmCalls).
        [HttpPost("/v1/chat/completions")]
        public async Task RunAgentStream([FromBody] AgentRunRequest request)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";

            try
            {
                // Validate request
                var validation = _validationService.ValidateRequest(request.Task, request.Workspace);
                if (!validation.IsValid)
                {
                    var errorJson = JsonSerializer.Serialize(new { error = "Validation failed", details = validation.Errors });
                    await Response.WriteAsync($"data: {errorJson}\n\n");
                    await Response.Body.FlushAsync();
                    return;
                }

                var workspace = _validationService.ResolveWorkspace(request.Workspace);
                if (workspace == null)
                {
                    var errorJson = JsonSerializer.Serialize(new { error = "Workspace is not in Agent:AllowedWorkspaces" });
                    await Response.WriteAsync($"data: {errorJson}\n\n");
                    await Response.Body.FlushAsync();
                    return;
                }

                await foreach (var chunk in _agentService.RunStreamAsync(
                    validation.SanitizedTask, workspace, request.History, request.Context, request.Model,
                    request.Mode, request.ActivePlan, request.PlanPath))
                {
                    const string changePrefix = "[CHANGE_EVENT]";
                    const string changeSuffix = "[/CHANGE_EVENT]";
                    const string toolPrefix = "[TOOL_EVENT]";
                    const string toolSuffix = "[/TOOL_EVENT]";
                    const string metricsPrefix = "[METRICS_EVENT]";
                    const string metricsSuffix = "[/METRICS_EVENT]";
                    const string limitPrefix = "[LIMIT_EVENT]";
                    const string limitSuffix = "[/LIMIT_EVENT]";
                    const string planPrefix = "[PLAN_EVENT]";
                    const string planSuffix = "[/PLAN_EVENT]";
                    const string approvalPrefix = "[APPROVAL_EVENT]";
                    const string approvalSuffix = "[/APPROVAL_EVENT]";
                    const string questionsPrefix = "[QUESTIONS_EVENT]";
                    const string questionsSuffix = "[/QUESTIONS_EVENT]";

                    if (chunk.StartsWith(changePrefix) && chunk.EndsWith(changeSuffix))
                    {
                        // ── CHANGE_EVENT: Emit as {"change":{...}} ──
                        var inner = chunk[changePrefix.Length..^changeSuffix.Length];
                        try
                        {
                            var changeElement = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inner);
                            var changeJson = JsonSerializer.Serialize(new { change = changeElement });
                            await Response.WriteAsync($"data: {changeJson}\n\n");
                        }
                        catch
                        {
                            // Malformed change event — skip silently
                        }
                    }
                    else if (chunk.StartsWith(metricsPrefix) && chunk.EndsWith(metricsSuffix))
                    {
                        // ── METRICS_EVENT: Emit as {"metrics":{tokens, cost, latencyMs, ...}} ──
                        var inner = chunk[metricsPrefix.Length..^metricsSuffix.Length];
                        var metricsElement = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inner);
                        await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { metrics = metricsElement })}\n\n");
                    }
                    else if (chunk.StartsWith(limitPrefix) && chunk.EndsWith(limitSuffix))
                    {
                        // ── LIMIT_EVENT: Emit as {"limit":{steps, mode}} → Continue button ──
                        var inner = chunk[limitPrefix.Length..^limitSuffix.Length];
                        var limitElement = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inner);
                        await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { limit = limitElement })}\n\n");
                    }
                    else if (chunk.StartsWith(planPrefix) && chunk.EndsWith(planSuffix))
                    {
                        // ── PLAN_EVENT: Emit as {"plan":{...}} ──
                        var inner = chunk[planPrefix.Length..^planSuffix.Length];
                        var planElement = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inner);
                        await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { plan = planElement })}\n\n");
                    }
                    else if (chunk.StartsWith(questionsPrefix) && chunk.EndsWith(questionsSuffix))
                    {
                        // ── QUESTIONS_EVENT: Emit as {"questions":[...]} → the clarifying-questions card ──
                        var inner = chunk[questionsPrefix.Length..^questionsSuffix.Length];
                        var questionsElement = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inner);
                        await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { questions = questionsElement.GetProperty("questions") })}\n\n");
                    }
                    else if (chunk.StartsWith(approvalPrefix) && chunk.EndsWith(approvalSuffix))
                    {
                        // ── APPROVAL_EVENT: Emit as {"approval":{...}} ──
                        var inner = chunk[approvalPrefix.Length..^approvalSuffix.Length];
                        var approvalElement = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inner);
                        await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { approval = approvalElement })}\n\n");
                    }
                    else if (chunk.StartsWith(toolPrefix) && chunk.EndsWith(toolSuffix))
                    {
                        // ── TOOL_EVENT: Emit as {"tool":{...}} (flattened format) ──
                        var inner = chunk[toolPrefix.Length..^toolSuffix.Length];
                        try
                        {
                            var toolElement = JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inner);
                            var toolJson = JsonSerializer.Serialize(new { tool = toolElement });
                            await Response.WriteAsync($"data: {toolJson}\n\n");
                        }
                        catch
                        {
                            // Malformed tool event — skip silently
                        }
                    }
                    else
                    {
                        // Normal token chunk (content only, protocol markers already filtered)
                        var json = JsonSerializer.Serialize(new { content = chunk });
                        await Response.WriteAsync($"data: {json}\n\n");
                    }

                    await Response.Body.FlushAsync();
                }

                await Response.WriteAsync("data: {\"done\":true}\n\n");
                await Response.Body.FlushAsync();
            }
            catch (Exception) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // The user pressed Stop or closed the chat: the agent loop has already stopped
                _logger.LogInformation("Agent stream cancelled by the client");
            }
            catch (Exception ex)
            {
                var error = JsonSerializer.Serialize(new { error = ex.Message });
                await Response.WriteAsync($"data: {error}\n\n");
                await Response.Body.FlushAsync();
            }
        }

        // ── Accept / Reject a proposed change or command ──────────
        [HttpPost("approve")]
        public IActionResult Approve([FromBody] ApproveRequest request, [FromServices] ApprovalBroker approvals)
        {
            if (string.IsNullOrWhiteSpace(request.ApprovalId))
                return BadRequest(new { error = "approvalId is required" });

            // False when the agent is no longer waiting (already decided, timed out, or run cancelled)
            if (!approvals.Resolve(request.ApprovalId, request.Approved))
                return NotFound(new { error = "Nothing is waiting for this approval", approvalId = request.ApprovalId });

            _logger.LogInformation("Approval {ApprovalId}: {Decision}", request.ApprovalId, request.Approved ? "approved" : "rejected");
            return Ok(new { success = true, approvalId = request.ApprovalId, approved = request.Approved });
        }

        // ── Query changes ───────────────────────────────
        [HttpGet("changes")]
        public async Task<IActionResult> GetChanges(
            [FromQuery] string? sessionId,
            [FromQuery] string? filePath,
            [FromQuery] string? workspace,
            [FromQuery] int count = 20,
            [FromServices] ChangeTracker changeTracker = null!)
        {
            try
            {
                var workspaceRoot = _validationService.ResolveWorkspace(workspace);
                if (workspaceRoot == null)
                    return BadRequest(new { error = "Workspace is not in Agent:AllowedWorkspaces" });

                List<PendingChange> changes;

                if (!string.IsNullOrEmpty(sessionId))
                {
                    changes = await changeTracker.GetSessionChangesAsync(workspaceRoot, sessionId);
                }
                else if (!string.IsNullOrEmpty(filePath))
                {
                    changes = await changeTracker.GetFileChangesAsync(workspaceRoot, filePath);
                }
                else
                {
                    changes = await changeTracker.GetRecentChangesAsync(workspaceRoot, count);
                }

                return Ok(new
                {
                    count = changes.Count,
                    changes = changes.Select(c => new
                    {
                        c.ChangeId,
                        c.SessionId,
                        c.SequenceNumber,
                        c.FilePath,
                        c.ToolUsed,
                        c.IsNewFile,
                        c.Status,
                        c.Timestamp,
                        c.Summary,
                        patchSize = c.Patch.Length
                    })
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Revert a file change by change ID or file path ─────────────────
        [HttpPost("revert")]
        public async Task<IActionResult> RevertChange(
            [FromBody] RevertRequest request,
            [FromServices] ChangeTracker changeTracker)
        {
            try
            {
                var workspaceRoot = _validationService.ResolveWorkspace(request.Workspace);
                if (workspaceRoot == null)
                    return BadRequest(new { error = "Workspace is not in Agent:AllowedWorkspaces" });

                // Revert by specific change ID
                if (!string.IsNullOrEmpty(request.ChangeId))
                {
                    // Looked up in this workspace's own log, so a change from another workspace is never found here
                    var change = await changeTracker.GetChangeAsync(workspaceRoot, request.ChangeId);
                    if (change == null)
                        return NotFound(new { error = "Change not found", changeId = request.ChangeId });

                    if (change.Status == "reverted")
                        return Ok(new { success = true, message = "Change was already reverted", changeId = request.ChangeId });

                    // Exact restore if untouched since, verified reverse patch otherwise; never a guess
                    var (ok, error) = await changeTracker.RevertFileAsync(change, workspaceRoot);
                    if (!ok)
                        return Conflict(new { error, changeId = request.ChangeId, filePath = change.FilePath });

                    await changeTracker.MarkRevertedAsync(workspaceRoot, request.ChangeId);
                    _logger.LogInformation("Reverted change {ChangeId}: {File}", request.ChangeId, change.FilePath);

                    return Ok(new { success = true, changeId = request.ChangeId, filePath = change.FilePath });
                }

                // Revert entire session
                if (!string.IsNullOrEmpty(request.SessionId))
                {
                    var reverted = await changeTracker.RevertSessionAsync(request.SessionId, workspaceRoot);
                    return Ok(new
                    {
                        success = true,
                        sessionId = request.SessionId,
                        revertedCount = reverted.Count,
                        files = reverted.Select(r => r.FilePath).Distinct().ToList()
                    });
                }

                // (The old ".backup" revert by file path is gone: no backups are written any more,
                // and it trusted a client-supplied path.)
                return BadRequest(new { error = "Provide changeId or sessionId" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Revert failed");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Trigger re-indexing ────────────────────────
        [HttpPost("index")]
        public async Task<IActionResult> IndexWorkspace(
            [FromBody] IndexRequest? request,
            [FromServices] CodeVectorIndexer indexer)
        {
            try
            {
                var workspaceRoot = _validationService.ResolveWorkspace(request?.Workspace);
                if (workspaceRoot == null)
                    return BadRequest(new { error = "Workspace is not in Agent:AllowedWorkspaces" });

                _logger.LogInformation("Re-indexing workspace: {Workspace}, maxFiles: {MaxFiles}",
                    workspaceRoot, request?.MaxFiles);

                var stats = await indexer.IndexAsync(workspaceRoot, request?.MaxFiles);
                if (stats.Error != null)
                    return StatusCode(503, new { error = $"Indexing unavailable: {stats.Error}", workspace = workspaceRoot });

                return Ok(new { success = true, message = "Indexing complete", workspace = workspaceRoot, stats });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Indexing failed");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Models for the picker ─────────────────────────────
        /// <summary>Models of every configured provider that can run the agent (tool calling), the default one first.</summary>
        [HttpGet("models")]
        public async Task<IActionResult> ListModels([FromServices] LLM.ILLMClient llm)
        {
            var models = (await llm.GetAvailableModelsAsync(HttpContext.RequestAborted))
                .Where(m => m.SupportsFunctionCalling)
                .OrderByDescending(m => string.Equals(m.Id, llm.DefaultModel, StringComparison.OrdinalIgnoreCase))
                .Select(m => new { id = m.Id, name = m.Name, provider = m.Provider });
            return Ok(new { models, defaultModel = llm.DefaultModel });
        }

        // ── Plan files (.ai/plans/*.plan.md) ─────────────────
        /// <summary>The plan as it is in its file now (the extension refreshes the plan card after the user edits it).</summary>
        [HttpGet("plan")]
        public async Task<IActionResult> GetPlan([FromQuery] string? workspace, [FromQuery] string path, [FromServices] PlanStore planStore)
        {
            var workspaceRoot = _validationService.ResolveWorkspace(workspace);
            if (workspaceRoot == null)
                return BadRequest(new { error = "Workspace is not in Agent:AllowedWorkspaces" });
            if (PlanStore.Resolve(workspaceRoot, path) == null)
                return BadRequest(new { error = $"Not a plan file: {path} (expected {PlanStore.PlansFolder}/*{PlanStore.Extension})" });

            var plan = await planStore.LoadAsync(workspaceRoot, path);
            return plan == null ? NotFound(new { error = "Plan file not found", path }) : Ok(new { plan = PlanJson(plan) });
        }

        /// <summary>Saved plans of the workspace, newest first (to reopen an earlier plan).</summary>
        [HttpGet("plans")]
        public IActionResult ListPlans([FromQuery] string? workspace, [FromServices] PlanStore planStore)
        {
            var workspaceRoot = _validationService.ResolveWorkspace(workspace);
            if (workspaceRoot == null)
                return BadRequest(new { error = "Workspace is not in Agent:AllowedWorkspaces" });

            return Ok(new
            {
                plans = planStore.List(workspaceRoot).Select(p => new
                {
                    path = p.Path,
                    title = p.Plan.Title,
                    steps = p.Plan.Steps.Count,
                    done = p.Plan.Steps.Count(s => s.Status is "done" or "skipped"),
                    modified = p.Modified
                })
            });
        }

        private static object PlanJson(ActivePlan plan) => new
        {
            title = plan.Title,
            summary = plan.Summary,
            path = plan.Path,
            steps = plan.Steps.Select(s => new { title = s.Title, files = s.Files, details = s.Details, status = s.Status })
        };

        // ── Search symbols for @-mentions ─────────────────
        [HttpGet("symbols")]
        public IActionResult SearchSymbols(
            [FromQuery] string? query,
            [FromQuery] string? workspace,
            [FromServices] ProjectContextService projectContext)
        {
            try
            {
                var workspaceRoot = _validationService.ResolveWorkspace(workspace);
                if (workspaceRoot == null)
                    return BadRequest(new { error = "Workspace is not in Agent:AllowedWorkspaces" });

                // The same cached code map the prompt uses, for the requested workspace
                var index = projectContext.GatherContext(workspaceRoot).ProjectIndex ?? new ProjectIndex();

                var symbols = index.Classes
                    .SelectMany(c => c.Methods.Select(m => new
                    {
                        type = "method",
                        name = m,
                        className = c.Name,
                        filePath = c.FilePath
                    }).Concat(c.Properties.Select(p => new
                    {
                        type = "property",
                        name = p,
                        className = c.Name,
                        filePath = c.FilePath
                    })).Concat(new[] { new
                    {
                        type = "class",
                        name = c.Name,
                        className = c.Name,
                        filePath = c.FilePath
                    }}))
                    .Where(s => string.IsNullOrEmpty(query) ||
                                s.name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .ToList();

                return Ok(new { symbols });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Search files for @-mentions ───────────────────
        [HttpGet("files")]
        public IActionResult SearchFiles([FromQuery] string? query, [FromQuery] string? workspace)
        {
            try
            {
                var workspaceRoot = _validationService.ResolveWorkspace(workspace);
                if (workspaceRoot == null)
                    return BadRequest(new { error = "Workspace is not in Agent:AllowedWorkspaces" });

                var supportedExtensions = new[] { "*.cs", "*.js", "*.jsx", "*.ts", "*.tsx", "*.py", "*.html", "*.css", "*.json", "*.csproj", "*.sln" };
                var skipFolders = new[] { "bin", "obj", ".git", ".vs", "node_modules" };

                var allFiles = new List<string>();
                foreach (var pattern in supportedExtensions)
                {
                    try
                    {
                        allFiles.AddRange(Directory.GetFiles(workspaceRoot, pattern, SearchOption.AllDirectories));
                    }
                    catch { /* skip inaccessible directories */ }
                }

                var files = allFiles
                    .Where(f => !skipFolders.Any(sf => f.Contains(Path.DirectorySeparatorChar + sf + Path.DirectorySeparatorChar)))
                    .Select(f => Path.GetRelativePath(workspaceRoot, f))
                    .Where(f => string.IsNullOrEmpty(query) ||
                                f.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .ToList();

                return Ok(new { files });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }


    }

    public class ApproveRequest
    {
        public string ApprovalId { get; set; } = string.Empty;
        public bool Approved { get; set; }
    }

    public class RevertRequest
    {
        /// <summary>Specific change ID to revert</summary>
        public string? ChangeId { get; set; }
        /// <summary>Session ID to revert all changes from that session</summary>
        public string? SessionId { get; set; }
        /// <summary>Legacy: file path for .backup-based revert</summary>
        public string? FilePath { get; set; }
        public string? Workspace { get; set; }
    }

    public class AgentRunRequest
    {
        public string Task { get; set; } = string.Empty;

        public string? Workspace { get; set; }

        /// <summary>Previous turns of this chat (oldest first), so follow-ups make sense</summary>
        public List<HistoryMessage>? History { get; set; }

        /// <summary>Active file, @-mentions and selected code from the editor</summary>
        public List<EditorContextItem>? Context { get; set; }

        /// <summary>Model picked in the UI; ignored if unknown or without tool calling</summary>
        public string? Model { get; set; }

        /// <summary>ask | plan | agent | auto (default agent). Enforced server-side via the tool set.</summary>
        public string? Mode { get; set; }

        /// <summary>The conversation's plan (from Plan mode), with step statuses</summary>
        public ActivePlan? ActivePlan { get; set; }

        /// <summary>The conversation's plan file (.ai/plans/*.plan.md); when set, the plan is read from it (it wins over ActivePlan)</summary>
        public string? PlanPath { get; set; }
    }

    public class IndexRequest
    {
        public string? Workspace { get; set; }
        public int? MaxFiles { get; set; }
    }
}
