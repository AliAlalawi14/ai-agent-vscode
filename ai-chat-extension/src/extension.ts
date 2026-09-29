import * as vscode from 'vscode';
import { ChatViewProvider } from './ChatViewProvider';
import { recordLoadedBundles } from './services/buildInfo';
import { BackendProcess, PROVIDER_KEYS } from './services/BackendProcess';
import { setBackendProcess } from './services/backendConnection';
import { registerProviderCommands } from './services/providerCommands';

let mcpRestart: ReturnType<typeof setTimeout> | undefined;

export function activate(context: vscode.ExtensionContext): void {
  console.log('Stoat is now active');
  recordLoadedBundles(context.extensionPath);

  // Zero-config: this window's backend (started on first use unless aiChat.backendUrl points elsewhere)
  const backend = new BackendProcess(context);
  setBackendProcess(backend);
  context.subscriptions.push(backend);

  // Keys live in VS Code SecretStorage, never in settings files
  context.subscriptions.push(
    vscode.commands.registerCommand('aiChat.setApiKey', async () => {
      const pick = await vscode.window.showQuickPick(
        PROVIDER_KEYS.map((p) => ({ label: p.label, provider: p })),
        { placeHolder: 'Which provider is this key for?' }
      );
      if (!pick) { return; }
      const key = await vscode.window.showInputBox({
        prompt: `${pick.provider.label} API key (stored in VS Code's secret storage). Leave empty to remove it.`,
        password: true,
        ignoreFocusOut: true,
      });
      if (key === undefined) { return; }
      if (key.trim()) {
        await context.secrets.store(pick.provider.secret, key.trim());
      } else {
        await context.secrets.delete(pick.provider.secret);
      }
      if (!BackendProcess.isExternal()) {
        const connection = await backend.restart();
        if (connection) { vscode.window.showInformationMessage(`Stoat: ${pick.provider.label} key saved; backend restarted.`); }
      }
    }),
    ...registerProviderCommands(context, backend),
    vscode.commands.registerCommand('aiChat.restartBackend', () => backend.restart()),
    vscode.commands.registerCommand('aiChat.showBackendLog', () => backend.showLog()),
    // New/removed folders change what the backend may touch: restart it with the new list
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      if (!BackendProcess.isExternal()) { void backend.restart(); }
    }),
    // MCP servers changed (panel or settings.json): restart so the backend starts the new set
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (!(e.affectsConfiguration('aiChat.mcpServers') || e.affectsConfiguration('aiChat.web')) || BackendProcess.isExternal()) { return; }
      clearTimeout(mcpRestart);
      mcpRestart = setTimeout(() => { void backend.restartIfRunning(); }, 400);
    })
  );

  const provider = new ChatViewProvider(context.extensionUri, context);

  // Register webview view provider
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider(
      ChatViewProvider.viewType,
      provider
    )
  );

  // Command: Open sidebar
  context.subscriptions.push(
    vscode.commands.registerCommand('aiChat.openSidebar', () => {
      vscode.commands.executeCommand('workbench.view.extension.ai-chat-sidebar-container');
    })
  );

  // Command: New conversation (clears chat)
  context.subscriptions.push(
    vscode.commands.registerCommand('aiChat.newChat', () => {
      provider.sendMessage({ type: 'clearChat' });
    })
  );

  // Command: Ask about selection (right-click or Ctrl+Shift+A)
  context.subscriptions.push(
    vscode.commands.registerCommand('aiChat.askAboutSelection', () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor) {
        vscode.window.showInformationMessage('No active editor with a selection');
        return;
      }

      const selection = editor.selection;
      if (selection.isEmpty) {
        vscode.window.showInformationMessage('Select some code first');
        return;
      }

      const code = editor.document.getText(selection);
      const filePath = vscode.workspace.asRelativePath(editor.document.uri);
      const language = editor.document.languageId;

      // Open sidebar first
      vscode.commands.executeCommand('workbench.view.extension.ai-chat-sidebar-container');

      // Send selection to webview
      setTimeout(() => {
        provider.sendMessage({
          type: 'sendSelection',
          filePath,
          code,
          language,
        });
      }, 300); // small delay to ensure webview is ready
    })
  );
}

export function deactivate(): void {
  console.log('Stoat is deactivated');
}
