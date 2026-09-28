using Ai_Agent.Config;
using Ai_Agent.LLM;
using Ai_Agent.Models;
using Ai_Agent.Tools;
using Ai_Agent.Tools.Services;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace Ai_Agent.Agent.Services
{
    public class AgentService
    {
        private readonly ILLMClient _llmClient;
        private readonly ToolFactory _toolFactory;
        private readonly PromptBuilder _promptBuilder;
        private readonly TokenCounter _tokenCounter;
        private readonly ContextPruner _contextPruner;
        private readonly AgentMetrics _metrics;
        private readonly CostTracker _costTracker;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IOptions<AgentOptions> _options;
        private readonly ILogger<AgentService> _logger;
        private readonly string _defaultWorkspace;
        private readonly UnifiedDiffService _diffService;
        private readonly ChangeTracker _changeTracker;
        private readonly ConversationMemoryService _memoryService;
        private readonly ApprovalBroker _approvals;
        private readonly AuditLog _audit;
        private readonly ProjectContextService _projectContext;
        private readonly CodeVectorIndexer _vectorIndexer;
        private readonly PlanStore _planStore;

        // Caps LLM calls in flight across all chats; held only while the model streams
        private readonly SemaphoreSlim _llmSlots;

        // How long a proposed change waits for Accept/Reject before it is dropped
        private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(10);

        // Caps on client-supplied history so one request can't flood the context window
        private const int MaxHistoryMessages = 20;
        private const int MaxHistoryMessageChars = 6000;

        // Above this (before + after), change events carry only the patch
        private const int MaxDiffEventChars = 400_000;

        private static readonly HashSet<string> _fileWritingTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "replace_lines", "edit_file", "delete_file", "move_file"
        };

        public AgentService(
            ILLMClient llmClient,
            ToolFactory toolFactory,
            PromptBuilder promptBuilder,
            TokenCounter tokenCounter,
            ContextPruner contextPruner,
            AgentMetrics metrics,
            CostTracker costTracker,
            IHttpContextAccessor httpContextAccessor,
            IOptions<AgentOptions> options,
            ILogger<AgentService> logger,
            UnifiedDiffService diffService,
            ChangeTracker changeTracker,
            ConversationMemoryService memoryService,
            ApprovalBroker approvals,
            AuditLog audit,
            ProjectContextService projectContext,
            CodeVectorIndexer vectorIndexer,
            PlanStore planStore)
        {
            _llmClient = llmClient;
            _toolFactory = toolFactory;
            _promptBuilder = promptBuilder;
            _tokenCounter = tokenCounter;
            _contextPruner = contextPruner;
            _metrics = metrics;
            _costTracker = costTracker;
            _httpContextAccessor = httpContextAccessor;
            _options = options;
            _logger = logger;
            _defaultWorkspace = options.Value.WorkspaceRoot;
            _diffService = diffService;
            _changeTracker = changeTracker;
            _memoryService = memoryService;
            _approvals = approvals;
            _audit = audit;
            _projectContext = projectContext;
            _vectorIndexer = vectorIndexer;
            _planStore = planStore;
            _llmSlots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentLlmCalls));
        }

        private string GetCorrelationId() => _httpContextAccessor.GetCorrelationId();

        public async IAsyncEnumerable<string> RunStreamAsync(
            string userRequest,
            string? workspace = null,
            IReadOnlyList<HistoryMessage>? history = null,
            IReadOnlyList<EditorContextItem>? editorContext = null,
            string? model = null,
            string? mode = null,
            ActivePlan? activePlan = null,
            string? planPath = null)
        {
            var correlationId = GetCorrelationId();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var workspaceRoot = workspace ?? _defaultWorkspace;
            mode = AgentModes.Normalize(mode);
            var toolRegistry = _toolFactory.CreateRegistry(workspaceRoot, mode);
            var toolDefinitions = toolRegistry.GetToolDefinitions();

            _logger.LogInformation(
                "[TRACE] ENTER RunStreamAsync | Corr={Corr} | Task=\"{Task}\" | Workspace={Workspace} | Tools={ToolCount}",
                correlationId, Truncate(userRequest, 100), workspaceRoot, toolDefinitions.Count);

            var promptClock = System.Diagnostics.Stopwatch.StartNew();
            var systemPrompt = await _promptBuilder.BuildSystemPromptAsync(toolRegistry, workspaceRoot, mode);
            promptClock.Stop();
            _logger.LogInformation(
                "[TRACE] PROMPT_BUILT | Corr={Corr} | Mode={Mode} | Size={PromptSize} chars | Duration={DurationMs}ms",
                correlationId, mode, systemPrompt.Length, promptClock.ElapsedMilliseconds);

            var sessionId = Guid.CreateVersion7().ToString();
            var changeSequence = 0;

            _logger.LogInformation(
                "[TRACE] SESSION_START | Corr={Corr} | SessionId={SessionId}",
                correlationId, sessionId);

            var requestAborted = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

            // The plan FILE is the source of truth: the user may have edited it since the chat last saw it
            if (!string.IsNullOrWhiteSpace(planPath) && await _planStore.LoadAsync(workspaceRoot, planPath) is { } planFromFile)
                activePlan = planFromFile;

            var messages = BuildInitialMessages(systemPrompt, userRequest, workspaceRoot, history, editorContext, activePlan);
            var planSubmitted = false;
            var questionsAsked = false;   // plan mode asked the user; the run ends and the answers come next
            var budgetNoteSent = false;   // the model was told it is nearly out of steps
            var stats = new RunStats { PromptBuildMs = promptClock.ElapsedMilliseconds };

            // Loop guard: don't let the model repeat a call that already failed, or hammer the terminal
            var failedCalls = new HashSet<string>();
            var consecutiveCommandFailures = 0;
            const int MaxConsecutiveCommandFailures = 3;
            var resolvedModel = await ResolveModelAsync(model);
            var changedFiles = new List<string>();

            _logger.LogInformation(
                "[TRACE] CONTEXT | Corr={Corr} | History={HistoryCount} msgs | EditorContext={ContextCount} items | Model={Model}",
                correlationId, messages.Count - 2, editorContext?.Count ?? 0, resolvedModel ?? "(default)");

            var iteration = 0;
            var maxIterations = _options.Value.MaxIterationsFor(mode);

            // Emit session start event so frontend can track/revert the session
            var sessionStartEvent = new
            {
                type = "session_start",
                sessionId = sessionId,
                task = userRequest.Length > 200 ? userRequest[..200] + "..." : userRequest
            };
            yield return $"[TOOL_EVENT]{JsonSerializer.Serialize(sessionStartEvent)}[/TOOL_EVENT]";

            while (iteration < maxIterations)
            {
                iteration++;
                var iterStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogInformation(
                    "[TRACE] STREAM_ITER | Corr={Corr} | #{Iter}/{Max} | Messages={MsgCount}",
                    correlationId, iteration, maxIterations, messages.Count);

                // The user pressed Stop / closed the chat: stop before spending more tokens
                if (requestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("[TRACE] CANCELLED | Corr={Corr} | before step #{Iter}", correlationId, iteration);
                    await SaveMemoryAsync(userRequest, "(cancelled by the user)", success: false, iteration, changedFiles, workspaceRoot, sessionId);
                    yield break;
                }

                // Long runs: keep the conversation inside the context window (tool-call pairs stay intact)
                var contextBudget = _options.Value.MaxContextTokens;
                if (_tokenCounter.EstimateTokens(messages) > contextBudget * 0.6)
                {
                    messages = _contextPruner.Prune(messages, (int)(contextBudget * 0.45));
                }

                // Warn the model before the budget runs out, so it ends with a useful summary instead of mid-change
                if (maxIterations > 4 && iteration == maxIterations - 2)
                {
                    budgetNoteSent = true;
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = "(System note: only 3 steps are left for this request, including this one. Finish the change " +
                                  "you are making, then reply with a short summary of what is done and what remains, so the user can say \"continue\".)"
                    });
                }

                // Stream the AI response with batching
                var fullContent = new StringBuilder();
                var tokenBuffer = new StringBuilder();
                var lastFlushTime = DateTime.UtcNow;
                var batchInterval = TimeSpan.FromMilliseconds(50);

                // Accumulate tool call deltas during streaming
                var toolCallAccumulator = new Dictionary<int, ToolCallAccumulator>();
                List<ReasoningBlock>? reasoning = null;   // provider thinking blocks, echoed back with this turn
                string? stopReason = null;

                _logger.LogInformation(
                    "[TRACE] STREAM_LLM | Corr={Corr} | #{Iter} | Sending {MsgCount} msgs with {ToolCount} tool defs",
                    correlationId, iteration, messages.Count, toolDefinitions.Count);
                var llmStreamStart = System.Diagnostics.Stopwatch.StartNew();

                // Wait for a free LLM slot (only the model call holds it, not approval waits)
                await _llmSlots.WaitAsync(requestAborted);
                try
                {
                    await foreach (var chunk in _llmClient.StreamMessageAsync(messages, toolDefinitions, resolvedModel, requestAborted))
                    {
                        if (chunk.InputTokens.HasValue) stats.InputTokens += chunk.InputTokens.Value;
                        if (chunk.OutputTokens.HasValue) stats.OutputTokens += chunk.OutputTokens.Value;
                        if (chunk.CacheHitTokens.HasValue) stats.CacheHitTokens += chunk.CacheHitTokens.Value;
                        if (chunk.Reasoning != null) (reasoning ??= new()).AddRange(chunk.Reasoning);
                        if (chunk.StopReason != null) stopReason = chunk.StopReason;
    
                        // Handle text content
                        if (chunk.Content != null)
                        {
                            fullContent.Append(chunk.Content);
                            tokenBuffer.Append(chunk.Content);
    
                            if (DateTime.UtcNow - lastFlushTime >= batchInterval)
                            {
                                var bufferedContent = tokenBuffer.ToString();
                                if (!string.IsNullOrEmpty(bufferedContent))
                                {
                                    yield return bufferedContent;
                                }
                                tokenBuffer.Clear();
                                lastFlushTime = DateTime.UtcNow;
                            }
                        }
    
                        // Handle tool call deltas
                        if (chunk.ToolCallDeltas != null)
                        {
                            foreach (var delta in chunk.ToolCallDeltas)
                            {
                                if (!toolCallAccumulator.TryGetValue(delta.Index, out var acc))
                                {
                                    acc = new ToolCallAccumulator();
                                    toolCallAccumulator[delta.Index] = acc;
                                }
    
                                if (delta.Id != null) acc.Id = delta.Id;
                                if (delta.Name != null) acc.Name = delta.Name;
                                if (delta.Arguments != null) acc.ArgumentsBuilder.Append(delta.Arguments);
                            }
                        }
                    }
                }
                finally
                {
                    _llmSlots.Release();
                }

                llmStreamStart.Stop();

                // Flush remaining text buffer
                if (tokenBuffer.Length > 0)
                {
                    var finalContent = tokenBuffer.ToString();
                    if (!string.IsNullOrEmpty(finalContent))
                    {
                        yield return finalContent;
                    }
                }

                // Build final tool calls from accumulated deltas
                var finalToolCalls = toolCallAccumulator
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new ToolCall
                    {
                        Id = kv.Value.Id ?? string.Empty,
                        Function = new ToolCallFunction
                        {
                            Name = kv.Value.Name ?? string.Empty,
                            Arguments = kv.Value.ArgumentsBuilder.ToString()
                        }
                    })
                    .Where(tc => !string.IsNullOrEmpty(tc.Function.Name))
                    .ToList();

                // The provider's safety filter declined: say so instead of acting on a partial reply
                if (stopReason == "refusal")
                {
                    finalToolCalls.Clear();
                    const string refusalNote = "\n\n_The model declined this request._";
                    fullContent.Append(refusalNote);
                    yield return refusalNote;
                }

                // Cut off at the output limit: tool calls may be half-written, so none of them runs
                var truncatedByLimit = stopReason == "max_tokens" && finalToolCalls.Count > 0;

                if (finalToolCalls.Count > 0)
                {
                    _logger.LogInformation(
                        "[TRACE] STREAM_TOOLS | Corr={Corr} | #{Iter} | LLMDuration={Llms}ms | Tools=[{ToolNames}]",
                        correlationId, iteration, llmStreamStart.ElapsedMilliseconds,
                        string.Join(", ", finalToolCalls.Select(tc => tc.Function.Name)));
                    // Add assistant message with tool calls
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = fullContent.ToString(),
                        ToolCalls = finalToolCalls,
                        Reasoning = reasoning
                    });

                    foreach (var toolCall in finalToolCalls)
                    {
                        var toolName = toolCall.Function.Name;
                        var parameters = ParseArguments(toolCall.Function.Arguments, out var argsError);
                        if (truncatedByLimit)
                            argsError = "Your reply hit the output limit, so this tool call may be incomplete and was NOT run. " +
                                        "Make smaller changes (edit_file on a few lines) instead of one large write.";

                        // ── STREAM TOOL START EVENT ───────────────────────
                        _logger.LogInformation(
                            "[TRACE] EVENT tool_start | Corr={Corr} | #{Iter} | Tool={Tool}",
                            correlationId, iteration, toolName);
                        var toolStartEvent = new
                        {
                            type = "tool_start",
                            toolCallId = toolCall.Id,
                            tool = toolName,
                            args = parameters
                        };
                        var toolStartJson = JsonSerializer.Serialize(toolStartEvent);
                        yield return $"[TOOL_EVENT]{toolStartJson}[/TOOL_EVENT]";

                        // ── APPROVAL GATE: writes and commands wait for the user's Accept ──
                        // rejection != null means the tool must NOT run; it becomes the tool result instead.
                        string? rejection = null;
                        string? approvalId = null;

                        // ── LOOP GUARD (checked before asking the user to approve anything) ──
                        var callSignature = CallSignature(toolName, parameters);
                        if (failedCalls.Contains(callSignature))
                        {
                            rejection = "ERROR: You already made this exact call in this request and it failed. " +
                                        "Do not repeat it: change the approach, or explain the problem to the user and ask how to proceed.";
                        }
                        else if (toolName == "run_terminal" && consecutiveCommandFailures >= MaxConsecutiveCommandFailures)
                        {
                            rejection = $"ERROR: {MaxConsecutiveCommandFailures} commands in a row failed, so running commands is paused for this request. " +
                                        "Stop and explain to the user what failed (quote the key error) and ask how to proceed.";
                        }
                        if (rejection != null)
                        {
                            _logger.LogWarning("[TRACE] LOOP_GUARD | Corr={Corr} | #{Iter} | Tool={Tool} blocked", correlationId, iteration, toolName);
                        }

                        var gatedTool = toolRegistry.GetTool(toolName);
                        if (rejection == null && gatedTool is { RequiresApproval: true } && argsError == null && !IsAutoApproved(mode, toolName, parameters))
                        {
                            var preview = await gatedTool.PreviewAsync(parameters);
                            if (preview.Error != null)
                            {
                                rejection = $"ERROR: {preview.Error}";
                            }
                            else
                            {
                                approvalId = $"{sessionId}:{(string.IsNullOrEmpty(toolCall.Id) ? Guid.NewGuid().ToString("N") : toolCall.Id)}";
                                _approvals.Register(approvalId);   // before the event, so an instant click isn't lost

                                var isFileChange = preview.FilePath != null;
                                var previewPatch = isFileChange
                                    ? _diffService.ComputeDiff(preview.FilePath!, preview.Before ?? string.Empty, preview.After ?? string.Empty)
                                    : null;
                                var includePreviewText = (preview.Before?.Length ?? 0) + (preview.After?.Length ?? 0) <= MaxDiffEventChars;
                                var approvalEvent = new
                                {
                                    approvalId,
                                    toolCallId = toolCall.Id,   // the tool card that is now waiting
                                    tool = toolName,
                                    kind = isFileChange ? "file" : "command",
                                    filePath = preview.FilePath,
                                    isNewFile = preview.IsNewFile,
                                    before = isFileChange && includePreviewText ? preview.Before : null,
                                    after = isFileChange && includePreviewText ? preview.After : null,
                                    patch = previewPatch,
                                    command = preview.Command,
                                    summary = preview.Summary ?? (isFileChange
                                        ? $"{(preview.IsNewFile ? "Create" : "Edit")} {Path.GetFileName(preview.FilePath)}"
                                        : $"Run: {preview.Command}")
                                };
                                _logger.LogInformation(
                                    "[TRACE] EVENT approval_required | Corr={Corr} | #{Iter} | Tool={Tool} | ApprovalId={ApprovalId}",
                                    correlationId, iteration, toolName, approvalId);
                                yield return $"[APPROVAL_EVENT]{JsonSerializer.Serialize(approvalEvent)}[/APPROVAL_EVENT]";

                                var decision = await _approvals.WaitAsync(approvalId, ApprovalTimeout, requestAborted);
                                await _audit.ApprovalAsync(correlationId, approvalId, toolName, decision.ToString());
                                _logger.LogInformation(
                                    "[TRACE] APPROVAL | Corr={Corr} | ApprovalId={ApprovalId} | Decision={Decision}",
                                    correlationId, approvalId, decision);

                                if (decision == ApprovalDecision.Cancelled)
                                {
                                    // The client went away (Cancel / closed panel): stop, don't keep calling the LLM
                                    yield break;
                                }

                                var decisionName = decision switch
                                {
                                    ApprovalDecision.Approved => "approved",
                                    ApprovalDecision.Rejected => "rejected",
                                    _ => "timed_out"
                                };
                                yield return $"[APPROVAL_EVENT]{JsonSerializer.Serialize(new { approvalId, toolCallId = toolCall.Id, decision = decisionName })}[/APPROVAL_EVENT]";

                                rejection = decision switch
                                {
                                    ApprovalDecision.Approved => null,
                                    ApprovalDecision.Rejected =>
                                        $"The user REJECTED this {(isFileChange ? $"change to {preview.FilePath}" : $"command ({preview.Command})")}. " +
                                        "Nothing was changed. Do not retry the same thing; ask the user what they want instead.",
                                    _ => "No approval was given within 10 minutes, so nothing was changed. Ask the user whether to continue."
                                };
                            }
                        }

                        // ── Auto-approved command: still show it as a command card (no buttons) ──
                        if (rejection == null && argsError == null && toolName == "run_terminal" && gatedTool != null &&
                            IsAutoApproved(mode, toolName, parameters))
                        {
                            var autoPreview = await gatedTool.PreviewAsync(parameters);
                            if (autoPreview.Error != null)
                            {
                                rejection = $"ERROR: {autoPreview.Error}";
                            }
                            else
                            {
                                var autoEvent = new
                                {
                                    approvalId = $"{sessionId}:{toolCall.Id}",
                                    toolCallId = toolCall.Id,
                                    tool = toolName,
                                    kind = "command",
                                    command = autoPreview.Command,
                                    autoApproved = true,
                                    summary = $"Run: {autoPreview.Command}"
                                };
                                yield return $"[APPROVAL_EVENT]{JsonSerializer.Serialize(autoEvent)}[/APPROVAL_EVENT]";
                            }
                        }

                        // ── Capture file contents BEFORE execution (a move touches two files) ──
                        var snapshots = new List<FileSnapshot>();
                        if (rejection == null && _fileWritingTools.Contains(toolName))
                        {
                            foreach (var relativePath in ChangedPaths(toolName, parameters))
                            {
                                // Outside the workspace: skip the snapshot (the tool itself will refuse the write)
                                var fullPath = WorkspacePath.Resolve(workspaceRoot, relativePath);
                                if (fullPath == null) continue;
                                var existed = File.Exists(fullPath);
                                snapshots.Add(new FileSnapshot(relativePath, fullPath,
                                    existed ? await File.ReadAllTextAsync(fullPath) : string.Empty, IsNewFile: !existed));
                            }
                        }

                        // Stopped while a previous tool ran: don't start another one
                        if (requestAborted.IsCancellationRequested)
                        {
                            _logger.LogInformation("[TRACE] CANCELLED | Corr={Corr} | before tool {Tool}", correlationId, toolName);
                            yield break;
                        }

                        // ── Execute the tool (never throws: errors go back to the model) ──
                        var toolResult = rejection ?? await ExecuteToolSafeAsync(toolRegistry, toolName, parameters, argsError, requestAborted);

                        // ── Capture AFTER and emit one change event per changed file ──
                        foreach (var snap in rejection == null ? snapshots : new List<FileSnapshot>())
                        {
                            var existsNow = File.Exists(snap.FullPath);
                            var afterContent = existsNow ? await File.ReadAllTextAsync(snap.FullPath) : string.Empty;
                            var created = snap.IsNewFile && existsNow;
                            var deleted = !snap.IsNewFile && !existsNow;
                            if (!created && !deleted && afterContent == snap.Before)
                                continue;   // untouched, or the tool failed (a failed write to a new path is not a change)

                            changeSequence++;
                            if (!changedFiles.Contains(snap.RelativePath)) changedFiles.Add(snap.RelativePath);

                            // Record via ChangeTracker (stores patch + metadata; a deletion keeps the content for revert)
                            var change = await _changeTracker.RecordChangeAsync(
                                workspaceRoot,
                                sessionId,
                                changeSequence,
                                snap.RelativePath,
                                toolName,
                                snap.Before,
                                afterContent,
                                snap.IsNewFile,
                                isDeletion: deleted);

                            // The cached code map is stale now; the next request rebuilds it
                            _projectContext.Invalidate(workspaceRoot);
                            _ = _vectorIndexer.ReindexFileAsync(workspaceRoot, snap.RelativePath);   // background, best-effort

                            _logger.LogInformation(
                                "[TRACE] EVENT change | Corr={Corr} | #{Iter} | ChangeId={ChangeId} | File={File} | PatchSize={PatchSize}",
                                correlationId, iteration, change.ChangeId, snap.RelativePath, change.Patch.Length);

                            // Full before/after lets the editor show a real side-by-side diff.
                            // Skipped for very large files; the patch is always included.
                            var includeFullText = snap.Before.Length + afterContent.Length <= MaxDiffEventChars;
                            var changeEvent = new
                            {
                                changeId = change.ChangeId,
                                approvalId,   // lets the UI turn the approved card into the applied change
                                sessionId = change.SessionId,
                                filePath = snap.RelativePath,
                                toolUsed = toolName,
                                patch = change.Patch,
                                before = includeFullText ? snap.Before : null,
                                after = includeFullText ? afterContent : null,
                                isNewFile = snap.IsNewFile,
                                isDeletion = deleted,
                                summary = change.Summary,
                                patchSize = change.Patch.Length
                            };
                            yield return $"[CHANGE_EVENT]{JsonSerializer.Serialize(changeEvent)}[/CHANGE_EVENT]";
                        }

                        // ── STREAM TOOL RESULT EVENT ──────────────────────
                        _logger.LogInformation(
                            "[TRACE] EVENT tool_result | Corr={Corr} | #{Iter} | Tool={Tool}",
                            correlationId, iteration, toolName);
                        var toolResultEvent = new
                        {
                            type = "tool_result",
                            toolCallId = toolCall.Id,
                            tool = toolName,
                            // Command output is shown in the card, so send more of it (and its end, where errors are)
                            result = toolName == "run_terminal"
                                ? (toolResult.Length > 6000 ? "…" + toolResult[^6000..] : toolResult)
                                : (toolResult.Length > 1000 ? toolResult[..1000] + "..." : toolResult),
                            status = toolResult.StartsWith("ERROR") ? "error" : "completed",
                            summary = GenerateToolSummary(toolName, parameters, toolResult)
                        };
                        var toolResultJson = JsonSerializer.Serialize(toolResultEvent);
                        yield return $"[TOOL_EVENT]{toolResultJson}[/TOOL_EVENT]";

                        // ── Loop-guard bookkeeping (user rejections don't count as failures) ──
                        var callFailed = toolResult.StartsWith("ERROR");
                        stats.ToolCalls++;
                        if (callFailed) stats.ToolErrors++;
                        await _audit.ToolCallAsync(correlationId, sessionId, workspaceRoot, mode, toolName, parameters,
                            rejection != null && !rejection.StartsWith("ERROR") ? "rejected" : callFailed ? "error" : "ok");
                        if (callFailed) failedCalls.Add(callSignature);
                        if (toolName == "run_terminal")
                            consecutiveCommandFailures = callFailed ? consecutiveCommandFailures + 1 : 0;

                        // ── PLAN EVENTS: the plan checklist card and its progress ──
                        if (!toolResult.StartsWith("ERROR"))
                        {
                            if (toolName == "submit_plan")
                            {
                                var plan = SubmitPlanTool.Parse(parameters, out _);
                                if (plan != null)
                                {
                                    planSubmitted = true;
                                    // Saved as an editable file; a revision updates the conversation's plan file (keeping progress)
                                    string? savedPath = null;
                                    try
                                    {
                                        savedPath = await _planStore.SaveAsync(workspaceRoot, plan,
                                            SubmitPlanTool.IsRevision(parameters) ? activePlan?.Path : null);
                                    }
                                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                                    {
                                        _logger.LogWarning("Could not save the plan file: {Message}", ex.Message);
                                    }
                                    yield return $"[PLAN_EVENT]{JsonSerializer.Serialize(new { type = "plan", plan = new { title = plan.Title, summary = plan.Summary, path = savedPath, steps = plan.Steps.Select(s => new { title = s.Title, files = s.Files, details = s.Details, status = s.Status }) } })}[/PLAN_EVENT]";
                                }
                            }
                            else if (toolName == "update_plan" && UpdatePlanTool.Validate(parameters, out var planStep, out var planStatus) == null)
                            {
                                yield return $"[PLAN_EVENT]{JsonSerializer.Serialize(new { type = "update", step = planStep, status = planStatus })}[/PLAN_EVENT]";
                                // Progress shows in the plan file too
                                if (!string.IsNullOrEmpty(activePlan?.Path))
                                    await _planStore.SetStepStatusAsync(workspaceRoot, activePlan.Path, planStep, planStatus);
                            }
                            else if (toolName == "ask_questions" && AskQuestionsTool.Parse(parameters, out _) is { } questions)
                            {
                                questionsAsked = true;
                                yield return $"[QUESTIONS_EVENT]{JsonSerializer.Serialize(new { questions = questions.Select(q => new { question = q.Question, options = q.Options, multiple = q.Multiple }) })}[/QUESTIONS_EVENT]";
                            }
                        }

                        // ── Add tool result message ───────────────────────
                        messages.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolCall.Id,
                            Name = toolName,
                            Content = toolResult
                        });
                    }
                    // No injected "continue" user turn: the tool results alone prompt the next step

                    if (planSubmitted || questionsAsked)
                    {
                        // Plan mode ends with the plan card (the user decides what to build) or the questions card
                        stopwatch.Stop();
                        var outcome = planSubmitted ? "plan" : "questions";
                        _logger.LogInformation("[TRACE] EXIT RunStreamAsync | Corr={Corr} | {Outcome} | Iterations={Iter}", correlationId, outcome, iteration);
                        await SaveMemoryAsync(userRequest, planSubmitted ? "(submitted a plan)" : "(asked clarifying questions)",
                            success: true, iteration, changedFiles, workspaceRoot, sessionId);
                        yield return RunSummary(correlationId, mode, resolvedModel, iteration, stats, stopwatch.ElapsedMilliseconds, outcome);
                        yield break;
                    }
                }
                else
                {
                    stopwatch.Stop();
                    _logger.LogInformation(
                        "[TRACE] EXIT RunStreamAsync | Corr={Corr} | SUCCESS | Iterations={Iter} | SessionId={SessionId} | Duration={DurationMs}ms",
                        correlationId, iteration, sessionId, stopwatch.ElapsedMilliseconds);
                    await SaveMemoryAsync(userRequest, fullContent.ToString(), success: true, iteration, changedFiles, workspaceRoot, sessionId);

                    yield return RunSummary(correlationId, mode, resolvedModel, iteration, stats, stopwatch.ElapsedMilliseconds, "done");

                    // Wrapped up because of the budget note: offer Continue just like a hard stop
                    if (budgetNoteSent)
                        yield return $"[LIMIT_EVENT]{JsonSerializer.Serialize(new { steps = iteration, mode })}[/LIMIT_EVENT]";
                    yield break;
                }
            }

            stopwatch.Stop();
            _logger.LogWarning(
                "[TRACE] EXIT RunStreamAsync | Corr={Corr} | FAILED | MaxIterations reached | Iterations={Iter} | Duration={DurationMs}ms",
                correlationId, iteration, stopwatch.ElapsedMilliseconds);
            await SaveMemoryAsync(userRequest, "(stopped at the step limit before finishing)", success: false, iteration, changedFiles, workspaceRoot, sessionId);

            // The UI shows a Continue button; the text stays short
            yield return RunSummary(correlationId, mode, resolvedModel, iteration, stats, stopwatch.ElapsedMilliseconds, "limit");
            yield return $"[LIMIT_EVENT]{JsonSerializer.Serialize(new { steps = maxIterations, mode })}[/LIMIT_EVENT]";
            yield return $"\n\n_Paused after {maxIterations} steps (the limit for {mode} mode)._";
        }

        /// <summary>
        /// [system, ...history, user]. The user turn carries the editor context block (active file, selection,
        /// @-mentions) ahead of the actual request, so "this"/"it" can be resolved.
        /// </summary>
        private List<ChatMessage> BuildInitialMessages(
            string systemPrompt, string userRequest, string workspaceRoot,
            IReadOnlyList<HistoryMessage>? history, IReadOnlyList<EditorContextItem>? editorContext,
            ActivePlan? activePlan = null)
        {
            var messages = new List<ChatMessage> { new() { Role = "system", Content = systemPrompt } };

            if (history != null)
            {
                foreach (var h in history
                    .Where(h => (h.Role == "user" || h.Role == "assistant") && !string.IsNullOrWhiteSpace(h.Content))
                    .TakeLast(MaxHistoryMessages))
                {
                    var content = h.Content.Length > MaxHistoryMessageChars
                        ? h.Content[..MaxHistoryMessageChars] + "\n(…truncated)"
                        : h.Content;
                    messages.Add(new ChatMessage { Role = h.Role, Content = content });
                }
            }

            var contextBlock = _promptBuilder.BuildPlanBlock(activePlan) +
                               _promptBuilder.BuildEditorContext(workspaceRoot, editorContext);
            var userContent = string.IsNullOrEmpty(contextBlock)
                ? userRequest
                : $"{contextBlock}\n\nUser request: {userRequest}";
            messages.Add(new ChatMessage { Role = "user", Content = userContent });

            return messages;
        }

        private sealed class RunStats
        {
            public int InputTokens;
            public int OutputTokens;
            public int CacheHitTokens;   // part of InputTokens served from the provider's prompt cache
            public long PromptBuildMs;
            public int ToolCalls;
            public int ToolErrors;
        }

        /// <summary>
        /// One structured log line per run (steps, tokens, cost, tool errors, duration) and the metrics event
        /// the chat shows under the answer.
        /// </summary>
        private string RunSummary(string correlationId, string mode, string? model, int steps, RunStats stats, long durationMs, string outcome)
        {
            var modelName = model ?? _llmClient.DefaultModel;
            var cost = _costTracker.CalculateCost(modelName, stats.InputTokens, stats.OutputTokens, stats.CacheHitTokens);
            _logger.LogInformation(
                "[RUN] Corr={Corr} | Outcome={Outcome} | Mode={Mode} | Model={Model} | Steps={Steps} | ToolCalls={ToolCalls} | ToolErrors={ToolErrors} | TokensIn={In} | TokensOut={Out} | CostUsd={Cost:F5} | DurationMs={Duration}",
                correlationId, outcome, mode, modelName, steps, stats.ToolCalls, stats.ToolErrors, stats.InputTokens, stats.OutputTokens, cost.TotalCost, durationMs);
            _metrics.RecordAgentRun(correlationId, outcome != "limit", steps, stats.InputTokens + stats.OutputTokens);

            var metricsEvent = new
            {
                tokens = stats.InputTokens + stats.OutputTokens,
                tokensIn = stats.InputTokens,
                tokensOut = stats.OutputTokens,
                cacheHitTokens = stats.CacheHitTokens,
                promptBuildMs = stats.PromptBuildMs,
                cost = Math.Round(cost.TotalCost, 6),
                latencyMs = durationMs,
                steps,
                toolCalls = stats.ToolCalls,
                toolErrors = stats.ToolErrors,
                model = modelName,
                outcome
            };
            _ = _audit.RunAsync(new { correlationId, mode, metricsEvent });
            return $"[METRICS_EVENT]{JsonSerializer.Serialize(metricsEvent)}[/METRICS_EVENT]";
        }

        /// <summary>Tool name + arguments in a stable order, to recognize a repeated call.</summary>
        private static string CallSignature(string toolName, Dictionary<string, string> parameters) =>
            toolName + "|" + JsonSerializer.Serialize(new SortedDictionary<string, string>(parameters, StringComparer.Ordinal));

        /// <summary>Auto mode: edits go through without asking; commands only if they are on the auto-approve list.</summary>
        private bool IsAutoApproved(string mode, string toolName, Dictionary<string, string> parameters)
        {
            if (mode != AgentModes.Auto) return false;
            if (toolName != "run_terminal") return true;

            var command = parameters.GetValueOrDefault("command")?.Trim() ?? string.Empty;
            return _options.Value.AutoApproveCommands.Any(allowed =>
                command.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith(allowed + " ", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Uses the model the user picked only if the provider knows it and it supports tool calling.</summary>
        private async Task<string?> ResolveModelAsync(string? requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;

            var models = await _llmClient.GetAvailableModelsAsync();
            var match = models.FirstOrDefault(m => string.Equals(m.Id, requested, StringComparison.OrdinalIgnoreCase));
            if (match is { SupportsFunctionCalling: true }) return match.Id;

            _logger.LogWarning("Ignoring model '{Model}': unknown or no tool-calling support", requested);
            return null;
        }

        private async Task SaveMemoryAsync(
            string userRequest, string response, bool success, int iterations,
            List<string> changedFiles, string workspaceRoot, string sessionId)
        {
            if (!_options.Value.MemoryEnabled) return;
            try
            {
                var title = userRequest.Split('\n')[0].Trim();
                var files = string.Join(", ", changedFiles);
                await _memoryService.SaveAsync(new ConversationMemory
                {
                    SessionId = sessionId,
                    Title = title.Length > 100 ? title[..100] : title,
                    UserRequest = userRequest.Length > 2000 ? userRequest[..2000] : userRequest,
                    Summary = response.Length > 500 ? response[..500] : response,
                    KeyTerms = string.Join(", ", userRequest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => w.Length > 3).Take(10)),
                    FilesModified = files.Length > 1000 ? files[..1000] : files,
                    BuildSucceeded = success,
                    IterationsUsed = iterations,
                    Workspace = workspaceRoot
                });
            }
            catch (Exception ex)
            {
                // Memory is best-effort; never fail the user's request because of it
                _logger.LogWarning(ex, "Failed to save conversation memory");
            }
        }

        private async Task<string> ExecuteToolSafeAsync(
            ToolRegistry toolRegistry, string toolName, Dictionary<string, string> parameters, string? argsError,
            CancellationToken cancellationToken = default)
        {
            if (argsError != null)
                return $"ERROR: {argsError}";

            // Private keys / certificates / credential files are never read into the conversation
            var requestedPaths = new[] { parameters.GetValueOrDefault("path"), parameters.GetValueOrDefault("paths") }
                .Where(p => !string.IsNullOrEmpty(p))
                .SelectMany(p => p!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (requestedPaths.Any(SecretRedactor.IsSecretFile))
                return "ERROR: That file holds keys or credentials; it is not shared with the model.";

            try
            {
                // Everything a tool returns goes to the LLM: strip passwords, API keys, tokens first
                return SecretRedactor.Redact(await toolRegistry.ExecuteToolAsync(toolName, parameters, cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool {Tool} threw", toolName);
                return $"ERROR: {toolName} failed: {ex.Message}";
            }
        }

        /// <summary>A file's state before a writing tool ran.</summary>
        private sealed record FileSnapshot(string RelativePath, string FullPath, string Before, bool IsNewFile);

        /// <summary>Every workspace file a writing tool may change (move_file: source and destination).</summary>
        private static IEnumerable<string> ChangedPaths(string toolName, Dictionary<string, string> parameters)
        {
            if (toolName == "move_file") return MoveFileTool.AffectedPaths(parameters);
            var path = ResolveFilePath(parameters);
            return path == null ? Array.Empty<string>() : new[] { path };
        }

        private static string? ResolveFilePath(Dictionary<string, string> parameters)
        {
            var pathKeys = new[] { "path", "file_path", "filePath", "filename", "file" };
            return pathKeys
                .Select(k => parameters.GetValueOrDefault(k))
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        }

        private static Dictionary<string, string> ParseArguments(string argumentsJson, out string? error) =>
            ToolArguments.Parse(argumentsJson, out error);

        private string GenerateToolSummary(string toolName, Dictionary<string, string> parameters, string result)
        {
            return toolName.ToLower() switch
            {
                "read_file" => $"Read {parameters.GetValueOrDefault("path", "file")}",
                "read_files" => $"Read {parameters.GetValueOrDefault("paths", "files")?.Split(',').Length ?? 0} files",
                "write_file" => result.Contains("ERROR") ? "Failed to write file" : $"Wrote {parameters.GetValueOrDefault("path", "file")}",
                "replace_lines" => result.Contains("ERROR") ? "Failed to replace lines" : $"Modified {parameters.GetValueOrDefault("path", "file")}",
                "search_code" => $"Found {result.Split('\n').Count(l => l.Contains(":"))} matches",
                "edit_file" => result.StartsWith("ERROR") ? "Edit failed" : $"Edited {parameters.GetValueOrDefault("path", "file")}",
                "find_files" => result.StartsWith("No files") ? "No matches" : result.Split('\n')[0],
                "semantic_search" => $"Found {result.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l))} results",
                "list_directory" => $"Listed {parameters.GetValueOrDefault("path", "directory")}",
                "run_terminal" => result.Contains("ERROR") ? "Command failed" : "Command executed",
                "delete_file" => result.StartsWith("ERROR") ? "Delete failed" : $"Deleted {parameters.GetValueOrDefault("path", "file")}",
                "move_file" => result.StartsWith("ERROR") ? "Move failed" : $"Moved to {parameters.GetValueOrDefault("new_path", "file")}",
                _ => $"Executed {toolName}"
            };
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }

        private class ToolCallAccumulator
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public StringBuilder ArgumentsBuilder { get; } = new();
        }
    }
}
