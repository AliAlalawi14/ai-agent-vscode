import * as vscode from "vscode";
import type {
  WebviewMessage,
  ExtensionMessage,
  SessionState,
  Conversation,
  MentionContext,
  HistoryMessage,
  AgentMode,
  ProviderSetupRequest,
  SetupState,
} from "../shared/protocol";
import { apiUrl } from "../shared/endpoints";
import { PROTOCOL_VERSION } from "../shared/protocol";
import { agentApiClient } from "./AgentApiClient";
import { authHeaders, backendConnection, getBackendProcess } from "./backendConnection";
import { BackendProcess } from "./BackendProcess";
import { configuredProviders, listProviderModels, removeProvider, saveProvider, setupPresets } from "./providerSetup";
import { McpPanelService } from "./mcpPanel";
import { WEB_SEARCH_KEY_SECRET } from "./BackendProcess";
import { completionProviders, resolveEndpoint } from "../completion/inlineProvider";
import { CompletionEngine, buildContext } from "../completion/core";
import { reverseApplyUnifiedPatch } from "./patchUtils";
import { findStaleBundles, recordLoadedBundles } from "./buildInfo";

/**
 * Handles bidirectional communication between extension core and webview
 */
export class MessageBroker {
  private webview: vscode.Webview;
  private disposables: vscode.Disposable[] = [];
  /** The running agent request (null when idle); aborted by Stop */
  private currentRun: AbortController | null = null;
  private context: vscode.ExtensionContext;
  private recentFiles: string[] = [];
  private readonly mcp: McpPanelService;
  private maxRecentFiles = 10;

  constructor(webview: vscode.Webview, context: vscode.ExtensionContext) {
    this.webview = webview;
    this.context = context;
    this.mcp = new McpPanelService(context, (m) => this.postMessage(m));
    // The webview (re)loads its bundle now; remember which build it got
    recordLoadedBundles(context.extensionPath);
    this.setupMessageListener();
    this.setupFileTracking();
    this.setupPlanFileWatcher();
    // A key added from the command palette (or a restart) updates this panel's status and models
    const backend = getBackendProcess();
    if (backend) {
      this.disposables.push(backend.onDidStart(() => {
        void this.handleHealthCheck();
        void this.mcp.postState();   // MCP servers start with the backend
      }));
    }
  }

  /**
   * Plans are Markdown files the user may edit (Cursor-style). When one changes on disk, send its
   * current content to the webview, which updates the plan card if it is the conversation's plan.
   */
  private setupPlanFileWatcher(): void {
    const watcher = vscode.workspace.createFileSystemWatcher("**/.ai/plans/*.plan.md");
    const refresh = async (uri: vscode.Uri) => {
      try {
        const path = vscode.workspace.asRelativePath(uri, false).split("\\").join("/");
        const plan = await agentApiClient.getPlan(path, this.getWorkspaceRoot());
        if (plan) { this.postMessage({ type: "planUpdated", plan }); }
      } catch {
        // Backend not reachable: the card updates on the next run
      }
    };
    watcher.onDidChange(refresh, null, this.disposables);
    watcher.onDidCreate(refresh, null, this.disposables);
    this.disposables.push(watcher);
  }

  postMessage(message: ExtensionMessage): void {
    this.webview.postMessage(message);
  }

  sendInit(): void {
    const theme =
      vscode.window.activeColorTheme.kind === vscode.ColorThemeKind.Light
        ? "light"
        : "dark";
    this.postMessage({ type: "init", theme });
  }

  private setupFileTracking(): void {
    const disposable = vscode.window.onDidChangeActiveTextEditor((editor) => {
      if (editor) {
        const filePath = vscode.workspace.asRelativePath(editor.document.uri);
        this.addRecentFile(filePath);
        this.postMessage({
          type: "openFilesUpdate",
          files: this.getOpenFiles(),
        });
      }
    });
    this.disposables.push(disposable);

    const openDisposable = vscode.workspace.onDidOpenTextDocument((doc) => {
      if (!doc.isUntitled) {
        const filePath = vscode.workspace.asRelativePath(doc.uri);
        this.addRecentFile(filePath);
      }
    });
    this.disposables.push(openDisposable);
  }

  private addRecentFile(filePath: string): void {
    this.recentFiles = this.recentFiles.filter((f) => f !== filePath);
    this.recentFiles.unshift(filePath);
    if (this.recentFiles.length > this.maxRecentFiles) {
      this.recentFiles = this.recentFiles.slice(0, this.maxRecentFiles);
    }
  }

  private getOpenFiles(): string[] {
    return vscode.window.visibleTextEditors
      .filter((e) => !e.document.isUntitled)
      .map((e) => vscode.workspace.asRelativePath(e.document.uri))
      .filter((v, i, a) => a.indexOf(v) === i);
  }

  private getRecentFiles(): string[] {
    const openFiles = new Set(this.getOpenFiles());
    return this.recentFiles.filter((f) => !openFiles.has(f));
  }

  private setupMessageListener(): void {
    const disposable = this.webview.onDidReceiveMessage(
      async (message: WebviewMessage) => {
        await this.handleMessage(message);
      },
    );
    this.disposables.push(disposable);
  }

  private async handleMessage(message: WebviewMessage): Promise<void> {
    switch (message.type) {
      case "init":
        this.sendInit();
        break;

      case "runTask":
        await this.handleRunTask(
          message.task,
          message.workspace,
          message.context,
          message.history,
          message.model,
          message.mode,
          message.activePlan,
          message.planPath,
        );
        break;

      case "openPlanFile":
        await this.handleOpenPlanFile(message.path);
        break;

      case "revertFile":
        await this.handleRevertFile(message.filePath, message.changeId);
        break;

      case "revertChanges":
        await this.handleRevertChanges(message.changes);
        break;

      case "resolveApproval":
        await this.handleResolveApproval(message.approvalId, message.approved);
        break;

      case "openDiff":
        await this.handleOpenDiff(
          message.filePath,
          message.before,
          message.after,
          message.proposed ?? false,
          message.patch,
          message.changeId,
        );
        break;

      case "searchSymbols":
        await this.handleSearchSymbols(message.query);
        break;

      case "searchFiles":
        await this.handleSearchFiles(message.query);
        break;

      case "cancelTask":
        this.handleCancelTask();
        break;

      case "healthCheck":
        await this.handleHealthCheck();
        break;

      case "updateSettings":
        this.handleUpdateSettings(message.settings);
        break;

      case "saveConversations":
        this.handleSaveConversations(message.conversations);
        break;

      case "loadConversations":
        this.loadConversations();
        break;

      case "saveCurrentSession":
        this.handleSaveCurrentSession(message.session);
        break;

      case "getOpenFiles":
        this.postMessage({
          type: "openFilesUpdate",
          files: this.getOpenFiles(),
        });
        break;

      case "getRecentFiles":
        this.postMessage({
          type: "recentFilesUpdate",
          files: this.getRecentFiles(),
        });
        break;

      case "getSetup":
        await this.postSetupState();
        break;

      case "listProviderModels": {
        const result = await listProviderModels(message.provider);
        this.postMessage({ type: "providerModels", requestId: message.requestId, ...result });
        break;
      }

      case "saveProvider":
        await this.handleSaveProvider(message.provider, message.models);
        break;

      case "removeProvider":
        await this.handleRemoveProvider(message.name);
        break;

      case "backendAction":
        if (message.action === "showLog") {
          getBackendProcess()?.showLog();
        } else {
          await getBackendProcess()?.restart();
          await this.handleHealthCheck();
        }
        break;

      case "getMcp":
      case "saveMcpServer":
      case "removeMcpServer":
      case "setMcpServer":
      case "setMcpSecret":
      case "importMcp":
      case "addMcpPreset":
        await this.mcp.handle(message);
        break;

      case "getCompletion":
        await this.postCompletionState();
        break;

      case "saveCompletion": {
        const c = vscode.workspace.getConfiguration("aiChat.completion");
        await c.update("enabled", message.settings.enabled, vscode.ConfigurationTarget.Global);
        await c.update("provider", message.settings.provider, vscode.ConfigurationTarget.Global);
        await c.update("model", message.settings.model, vscode.ConfigurationTarget.Global);
        await c.update("style", message.settings.style, vscode.ConfigurationTarget.Global);
        await this.postCompletionState();
        break;
      }

      case "testCompletion":
        await this.handleTestCompletion(message.settings);
        break;

      case "getWeb":
        await this.postWebState();
        break;

      case "saveWeb":
        await this.handleSaveWeb(message);
        break;

      case "openExternal":
        if (/^https:\/\//.test(message.url)) {
          void vscode.env.openExternal(vscode.Uri.parse(message.url));
        }
        break;
    }
  }

  private async handleRunTask(
    task: string,
    workspace: string,
    context?: MentionContext[],
    history?: HistoryMessage[],
    model?: string,
    mode?: AgentMode,
    activePlan?: unknown,
    planPath?: string,
  ): Promise<void> {
    if (this.currentRun) {
      this.postMessage({ type: "error", message: "Already processing a task" });
      return;
    }

    // Each run owns its controller. After Stop (or a new run) this run may still be winding down:
    // nothing it produces reaches the webview any more, and its cleanup never touches a newer run.
    const run = new AbortController();
    this.currentRun = run;
    const post = (message: ExtensionMessage) => {
      if (this.currentRun === run) {this.postMessage(message);}
    };

    try {
      const workspaceRoot = workspace || this.getWorkspaceRoot();
      const fullContext = this.withEditorContext(context ?? []);

      let hasToolCalls = false;
      let contentCharCount = 0;

      for await (const event of agentApiClient.runStream(
        {
          task,
          workspace: workspaceRoot,
          ...(fullContext.length > 0 ? { context: fullContext } : {}),
          ...(history && history.length > 0 ? { history } : {}),
          ...(model ? { model } : {}),
          ...(mode ? { mode } : {}),
          ...(activePlan ? { activePlan } : {}),
          ...(planPath ? { planPath } : {}),
          reviewEdits: reviewEditsEnabled(),
        },
        run.signal,
      )) {
        if (run.signal.aborted) {
          break;
        }

        switch (event.type) {
          case "content":
            contentCharCount += event.content.length;
            post({ type: "token", content: event.content });
            break;

          case "toolStart":
            hasToolCalls = true;
            post({
              type: "toolStart",
              tool: event.event.tool,
              toolCallId: event.event.toolCallId,
              args: event.event.args || {},
            });
            break;

          case "toolResult":
            post({
              type: "toolResult",
              tool: event.event.tool,
              toolCallId: event.event.toolCallId,
              result: event.event.summary || event.event.result || "",
              // Full (tail of) output: command cards show it
              output: event.event.result || "",
              status: event.event.status || "completed",
            });
            break;

          case "change":
            // Even after Stop: the edit is on disk, so it must reach the review (Keep / Undo)
            this.postMessage({ type: "changeEvent", change: event.change });
            break;

          case "approval":
            post({ type: "approvalEvent", approval: event.approval });
            break;

          case "plan":
            post({ type: "planEvent", plan: event.plan });
            break;

          case "questions":
            post({ type: "questionsEvent", questions: event.questions });
            break;

          case "limit":
            post({ type: "limitEvent", limit: event.limit });
            break;

          case "metrics":
            post({ type: "metrics", metrics: event.metrics });
            break;

          case "error":
            post({ type: "error", message: event.message });
            break;

          case "done":
            console.log("[MessageBroker] Stream complete");
            post({ type: "streamDone" });
            break;

          case "diagnostic":
            console.log(
              `[MessageBroker] Diagnostic [${event.stage}]: ${event.detail}`,
            );
            post({
              type: "diagnostic",
              stage: event.stage,
              detail: event.detail,
            });
            break;
        }
      }
    } catch (error) {
      const errMsg = error instanceof Error ? error.message : "Unknown error";
      const isConnectionError =
        errMsg.includes("ECONNREFUSED") ||
        errMsg.includes("fetch failed") ||
        errMsg.includes("network") ||
        errMsg.includes("ETIMEDOUT");

      post({
        type: "error",
        message: isConnectionError
          ? `Cannot reach backend server. Check that it is running and the URL is correct.`
          : errMsg,
      });

      post({ type: "streamDone" });
    } finally {
      if (this.currentRun === run) {
        this.currentRun = null;
      }
    }
  }

  /**
   * Stop: aborting the request closes the connection, which stops the backend (model call, approval wait,
   * running command). The webview has already ended the run on its side; the next message may start at once.
   */
  private handleCancelTask(): void {
    this.currentRun?.abort();
    this.currentRun = null;
  }


  private async handleHealthCheck(): Promise<void> {
    // Rebuilt while this window kept running old code? Tell the user to reload.
    const stale = findStaleBundles(this.context.extensionPath);
    this.postMessage({ type: "extensionStatus", staleBundles: stale });

    // No provider yet: don't try to start; the panel shows its setup form
    if (await getBackendProcess()?.needsSetup()) {
      this.postMessage({ type: "healthStatus", status: "setup" });
      await this.postSetupState();
      return;
    }

    try {
      // Starts this window's backend on first use (or reaches the external one from settings)
      const connection = await backendConnection();
      const response = await fetch(apiUrl(connection.url).health, {
        headers: authHeaders(connection),
      });
      if (response.ok) {
        const data = (await response.json()) as {
          status?: string;
          protocolVersion?: number;
          buildTime?: string;
        };
        if (data.protocolVersion !== PROTOCOL_VERSION) {
          // A backend process started before the latest changes: it silently ignores new request
          // fields (history, context) and sends old events, which looks like "the agent is dumb".
          const built = data.buildTime
            ? ` (built ${new Date(data.buildTime).toLocaleString()})`
            : "";
          this.postMessage({
            type: "healthStatus",
            status: "outdated",
            detail:
              `Backend protocol v${data.protocolVersion ?? "?"}${built}, extension expects ` +
              `v${PROTOCOL_VERSION}. Restart the backend (Visual Studio: Shift+F5, then F5).`,
          });
          return;
        }
        this.postMessage({
          type: "healthStatus",
          status:
            data.status?.toLowerCase() === "healthy" ? "connected" : "degraded",
        });
        // Fill the model picker (DeepSeek, Claude, OpenAI-compatible... whatever the backend has configured)
        const models = await agentApiClient.getModels().catch(() => []);
        this.postMessage({ type: "modelsAvailable", models });
      } else {
        this.postMessage({ type: "healthStatus", status: "degraded" });
      }
    } catch (error) {
      this.postMessage({
        type: "healthStatus",
        status: "disconnected",
        detail: error instanceof Error ? error.message : undefined,
      });
    }
    await this.postSetupState();
  }

  private async postCompletionState(): Promise<void> {
    const c = vscode.workspace.getConfiguration("aiChat.completion");
    this.postMessage({
      type: "completionState",
      state: {
        enabled: c.get<boolean>("enabled", false),
        provider: c.get<string>("provider", "") ?? "",
        model: c.get<string>("model", "") ?? "",
        style: (c.get<string>("style", "auto") ?? "auto") as "auto",
        providers: await completionProviders(this.context.secrets),
      },
    });
  }

  /** "Try it": one real completion with the settings in the form (saved or not). */
  private async handleTestCompletion(settings: { provider: string; model: string; style: string }): Promise<void> {
    const endpoint = await resolveEndpoint(this.context.secrets, {
      provider: settings.provider,
      model: settings.model,
      style: settings.style as "auto",
    });
    if (!endpoint || !endpoint.model) {
      this.postMessage({ type: "completionTest", ok: false, error: "Pick a provider and a model first (and check its key)." });
      return;
    }
    const sample = "function isEven(n: number): boolean {\n  return \n}\n";
    const ctx = buildContext(sample, sample.indexOf("return ") + "return ".length, "typescript", "sample.ts");
    try {
      const result = await new CompletionEngine().complete(endpoint, ctx);
      this.postMessage({
        type: "completionTest",
        ok: result.text.length > 0,
        text: result.text,
        ms: result.ms,
        ...(result.text.length === 0 ? { error: "The model returned nothing. Try another model or the Chat style." } : {}),
      });
    } catch (error) {
      this.postMessage({ type: "completionTest", ok: false, error: error instanceof Error ? error.message : String(error) });
    }
  }

  private async postWebState(): Promise<void> {
    const web = vscode.workspace.getConfiguration("aiChat.web");
    this.postMessage({
      type: "webState",
      state: {
        fetch: (web.get<string>("fetch", "ask") as "ask" | "allow" | "off") || "ask",
        searchProvider: (web.get<string>("searchProvider", "") as "" | "brave" | "tavily" | "searxng") ?? "",
        hasSearchKey: !!(await this.context.secrets.get(WEB_SEARCH_KEY_SECRET)),
        searxngUrl: web.get<string>("searxngUrl", "") ?? "",
      },
    });
  }

  /** Settings → Web. The settings change restarts the backend (extension.ts); a new key alone restarts it here. */
  private async handleSaveWeb(message: Extract<WebviewMessage, { type: "saveWeb" }>): Promise<void> {
    const web = vscode.workspace.getConfiguration("aiChat.web");
    const keyChanged = !!message.searchKey?.trim();
    if (keyChanged) { await this.context.secrets.store(WEB_SEARCH_KEY_SECRET, message.searchKey!.trim()); }
    const before = [web.get("fetch"), web.get("searchProvider"), web.get("searxngUrl")].join("|");
    await web.update("fetch", message.fetch, vscode.ConfigurationTarget.Global);
    await web.update("searchProvider", message.searchProvider, vscode.ConfigurationTarget.Global);
    await web.update("searxngUrl", message.searxngUrl?.trim() ?? "", vscode.ConfigurationTarget.Global);
    const after = [message.fetch, message.searchProvider, message.searxngUrl?.trim() ?? ""].join("|");
    if (keyChanged && before === after) { await getBackendProcess()?.restartIfRunning(); }
    await this.postWebState();
  }

  private async postSetupState(): Promise<void> {
    const backend = getBackendProcess();
    const setup: SetupState = {
      needsSetup: (await backend?.needsSetup()) ?? false,
      external: BackendProcess.isExternal(),
      backendUrl: backend?.current()?.url ?? null,
      problem: backend?.lastProblem ?? null,
      reviewEdits: reviewEditsEnabled(),
      presets: setupPresets(),
      providers: await configuredProviders(this.context.secrets),
    };
    this.postMessage({ type: "setupState", setup });
  }

  /** Saves the provider from the panel's form, restarts the backend and reports whether it came up. */
  private async handleSaveProvider(provider: ProviderSetupRequest, models: string[]): Promise<void> {
    try {
      await saveProvider(this.context, provider, models);
    } catch (error) {
      this.postMessage({ type: "providerSaved", ok: false, error: error instanceof Error ? error.message : String(error) });
      return;
    }
    await this.restartAndReport();
  }

  private async handleRemoveProvider(name: string): Promise<void> {
    await removeProvider(this.context, name);
    await this.restartAndReport();
  }

  private async restartAndReport(): Promise<void> {
    const backend = getBackendProcess();
    if (backend && !BackendProcess.isExternal()) {
      if (await backend.needsSetup()) {
        backend.stop();   // last provider removed
      } else {
        const connection = await backend.restart();
        if (!connection) {
          this.postMessage({ type: "providerSaved", ok: false, error: backend.lastProblem ?? "The backend didn't start." });
          await this.handleHealthCheck();
          return;
        }
      }
    }
    this.postMessage({ type: "providerSaved", ok: true });
    await this.handleHealthCheck();
  }

  /** Preferences changed in the panel apply to every project (user settings, not a .vscode file in the repo). */
  private handleUpdateSettings(settings: Record<string, unknown>): void {
    const config = vscode.workspace.getConfiguration("aiChat");
    for (const [key, value] of Object.entries(settings)) {
      void config.update(key, value, vscode.ConfigurationTarget.Global);
    }
  }

  private handleSaveConversations(conversations: Conversation[]): void {
    this.context.workspaceState.update("aiChat.conversations", conversations);
  }

  public loadConversations(): void {
    const conversations = this.context.workspaceState.get<Conversation[]>(
      "aiChat.conversations",
      [],
    );
    const activeId = this.context.workspaceState.get<string | null>(
      "aiChat.activeConversationId",
      null,
    );
    const session = this.context.workspaceState.get<SessionState | null>(
      "aiChat.currentSession",
      null,
    );

    this.postMessage({
      type: "restoreState",
      conversations,
      activeConversationId: activeId,
      currentSession: session,
    });
  }

  private handleSaveCurrentSession(session: SessionState): void {
    this.context.workspaceState.update("aiChat.currentSession", session);
  }

  forceSaveSession(session: SessionState): void {
    this.handleSaveCurrentSession(session);
  }

  saveActiveConversationId(id: string | null): void {
    this.context.workspaceState.update("aiChat.activeConversationId", id);
  }

  /**
   * The agent has already written the change to disk, so the diff is
   * "before the agent" (left, temp copy) vs the real file (right, editable).
   */
  private async handleOpenDiff(
    filePath: string,
    before: string | undefined,
    after: string | undefined,
    proposed: boolean,
    patch: string | undefined,
    changeId: string | undefined,
  ): Promise<void> {
    try {
      const fs = await import("fs/promises");
      const path = await import("path");
      const os = await import("os");

      const workspaceRoot = this.getWorkspaceRoot();
      const fullPath = path.join(workspaceRoot, filePath);
      const baseName = path.basename(filePath);
      const diffDir = path.join(os.tmpdir(), "ai-agent-diff", (changeId || String(Date.now())).replace(/[:\\/]/g, "_"));

      if (proposed && after !== undefined) {
        // Awaiting approval: nothing is on disk yet, so show current file (left) vs proposal (right)
        await fs.mkdir(diffDir, { recursive: true });
        const proposedPath = path.join(diffDir, `proposed-${baseName}`);
        await fs.writeFile(proposedPath, after, "utf-8");
        let leftUri = vscode.Uri.file(fullPath);
        if (before === "" || !(await fs.stat(fullPath).then(() => true, () => false))) {
          const emptyPath = path.join(diffDir, `empty-${baseName}`);
          await fs.writeFile(emptyPath, "", "utf-8");
          leftUri = vscode.Uri.file(emptyPath);
        }
        await vscode.commands.executeCommand(
          "vscode.diff",
          leftUri,
          vscode.Uri.file(proposedPath),
          `${baseName} (current ↔ proposed, not applied yet)`,
        );
        return;
      }

      let beforeText = before;
      if (beforeText === undefined || beforeText === null) {
        // Change event had only the patch: rebuild "before" from the current file
        const current = await fs.readFile(fullPath, "utf-8").catch(() => "");
        beforeText = patch ? reverseApplyUnifiedPatch(current, patch) ?? undefined : undefined;
      }

      if (beforeText === undefined) {
        // File changed again since; the patch no longer applies. Show the patch itself.
        const doc = await vscode.workspace.openTextDocument({
          content: patch || "No diff available for this change.",
          language: "diff",
        });
        await vscode.window.showTextDocument(doc, { preview: true });
        return;
      }

      // Unique folder per change, so two diffs of the same file don't overwrite each other
      await fs.mkdir(diffDir, { recursive: true });
      const beforePath = path.join(diffDir, baseName);
      await fs.writeFile(beforePath, beforeText, "utf-8");

      await vscode.commands.executeCommand(
        "vscode.diff",
        vscode.Uri.file(beforePath),
        vscode.Uri.file(fullPath),
        `${baseName} (before ↔ after agent)`,
      );
    } catch (error) {
      vscode.window.showErrorMessage(
        `Failed to open diff: ${error instanceof Error ? error.message : "Unknown error"}`,
      );
    }
  }

  /** Opens the plan file next to the chat, so the user can read and edit it. */
  private async handleOpenPlanFile(relativePath: string): Promise<void> {
    try {
      if (!/^\.ai\/plans\/[^/\\]+\.plan\.md$/.test(relativePath)) {
        throw new Error(`Not a plan file: ${relativePath}`);
      }
      const uri = vscode.Uri.joinPath(vscode.Uri.file(this.getWorkspaceRoot()), relativePath);
      const doc = await vscode.workspace.openTextDocument(uri);
      await vscode.window.showTextDocument(doc, { preview: false, viewColumn: vscode.ViewColumn.One });
    } catch (error) {
      vscode.window.showErrorMessage(
        `Could not open the plan: ${error instanceof Error ? error.message : "Unknown error"}`,
      );
    }
  }

  private async handleRevertFile(filePath: string, changeId?: string): Promise<void> {
    try {
      const workspaceRoot = this.getWorkspaceRoot();
      if (!changeId) {
        throw new Error("This change has no change ID, so it can't be reverted safely.");
      }
      // Reverse-applies the change's recorded patch on the backend (no .backup files)
      await agentApiClient.revertFile(changeId, undefined, filePath, workspaceRoot);
      vscode.window.showInformationMessage(`Reverted ${filePath}`);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unknown error";
      vscode.window.showErrorMessage(`Could not revert ${filePath}: ${message}`);
      if (changeId) {
        this.postMessage({ type: "revertFailed", changeId, message });
      }
    }
  }

  /**
   * Undo for the review (a whole file, or everything): the changes come newest first, and each undo must
   * succeed before the older one underneath it can apply. The first failure stops the rest.
   */
  private async handleRevertChanges(changes: Array<{ changeId: string; filePath: string }>): Promise<void> {
    const workspaceRoot = this.getWorkspaceRoot();
    for (let i = 0; i < changes.length; i++) {
      try {
        await agentApiClient.revertFile(changes[i].changeId, undefined, changes[i].filePath, workspaceRoot);
      } catch (error) {
        const message = error instanceof Error ? error.message : "Unknown error";
        vscode.window.showErrorMessage(`Could not undo ${changes[i].filePath}: ${message}`);
        for (const skipped of changes.slice(i)) {
          this.postMessage({ type: "revertFailed", changeId: skipped.changeId, message });
        }
        return;
      }
    }
  }

  private async handleResolveApproval(
    approvalId: string,
    approved: boolean,
  ): Promise<void> {
    try {
      await agentApiClient.approve(approvalId, approved);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unknown error";
      vscode.window.showErrorMessage(`Could not send your decision: ${message}`);
      // Let the card show that nothing happened
      this.postMessage({
        type: "approvalEvent",
        approval: { approvalId, decision: "timed_out" },
      });
    }
  }

  private async handleSearchSymbols(query: string): Promise<void> {
    try {
      const workspaceRoot = this.getWorkspaceRoot();
      const symbols = await agentApiClient.searchSymbols(query, workspaceRoot);
      this.postMessage({ type: "symbolResults", query, symbols });
    } catch {
      this.postMessage({ type: "symbolResults", query, symbols: [] });
    }
  }

  private async handleSearchFiles(query: string): Promise<void> {
    try {
      const workspaceRoot = this.getWorkspaceRoot();
      const files = await agentApiClient.searchFiles(query, workspaceRoot);
      this.postMessage({ type: "fileResults", query, files });
    } catch {
      this.postMessage({ type: "fileResults", query, files: [] });
    }
  }

  /**
   * Marks the file the user is actually looking at as active and adds the current selection.
   * The extension host knows this reliably; the webview only sees "visible" editors.
   */
  private withEditorContext(context: MentionContext[]): MentionContext[] {
    const editor = vscode.window.activeTextEditor;
    const activePath = editor && !editor.document.isUntitled
      ? vscode.workspace.asRelativePath(editor.document.uri)
      : this.recentFiles[0];
    if (!activePath) {
      return context;
    }

    const result = context.map((c) =>
      c.type === "file" && c.filePath === activePath ? { ...c, active: true } : c,
    );
    if (!result.some((c) => c.type === "file" && c.filePath === activePath)) {
      result.unshift({
        type: "file",
        name: activePath.split(/[/\\]/).pop() || activePath,
        filePath: activePath,
        active: true,
      });
    }

    const alreadyHasSelection = result.some((c) => c.type === "selection");
    if (editor && !editor.selection.isEmpty && !alreadyHasSelection) {
      result.push({
        type: "selection",
        name: `${activePath}:${editor.selection.start.line + 1}`,
        filePath: activePath,
        code: editor.document.getText(editor.selection),
        startLine: editor.selection.start.line + 1,
        endLine: editor.selection.end.line + 1,
      });
    }
    return result;
  }

  /**
   * The workspace folder the user is working in: the folder of the active file (multi-root windows),
   * else the first folder.
   */
  private getWorkspaceRoot(): string {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
      throw new Error("No workspace folder open");
    }
    const active = vscode.window.activeTextEditor?.document.uri;
    const activeFolder = active ? vscode.workspace.getWorkspaceFolder(active) : undefined;
    return (activeFolder ?? folders[0]).uri.fsPath;
  }


  dispose(): void {
    this.disposables.forEach((d) => d.dispose());
    this.disposables = [];
  }
}

/** aiChat.reviewEdits (default on): edits apply at once and the user reviews them afterwards. */
function reviewEditsEnabled(): boolean {
  return vscode.workspace.getConfiguration("aiChat").get<boolean>("reviewEdits", true);
}
