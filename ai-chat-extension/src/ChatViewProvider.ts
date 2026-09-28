import * as vscode from "vscode";
import { MessageBroker } from "./services/MessageBroker";
import type { ExtensionMessage, SessionState, Message, ToolExecution } from "./shared/protocol";

/**
 * Provides the AI Chat sidebar webview
 */
export class ChatViewProvider implements vscode.WebviewViewProvider {
  public static readonly viewType = "ai-chat-sidebar";
  private _view?: vscode.WebviewView;
  private _broker?: MessageBroker;
  private _extensionUri: vscode.Uri;
  private _context: vscode.ExtensionContext;

  constructor(extensionUri: vscode.Uri, context: vscode.ExtensionContext) {
    this._extensionUri = extensionUri;
    this._context = context;
  }

  /**
   * Send a message from extension to the webview
   */
  public sendMessage(message: ExtensionMessage): void {
    this._view?.webview.postMessage(message);
  }

  public resolveWebviewView(
    webviewView: vscode.WebviewView,
    _context: vscode.WebviewViewResolveContext,
    _token: vscode.CancellationToken,
  ): void {
    this._view = webviewView;

    // Configure webview
    webviewView.webview.options = {
      enableScripts: true,
      localResourceRoots: [
        this._extensionUri,
        vscode.Uri.parse("http://localhost:5173"),
      ],
    };

    // Set HTML content
    webviewView.webview.html = this._getHtmlForWebview(webviewView.webview);

    // Initialize message broker with context for persistence
    this._broker = new MessageBroker(webviewView.webview, this._context);

    // Request restoration from persistence
    this._broker.loadConversations();

    // Handle disposal - force save session before cleanup
    webviewView.onDidDispose(() => {
      // Note: In a real implementation, we'd query the webview for current state
      // For now, the webview auto-saves on changes, so we just clean up
      this._broker?.dispose();
      this._broker = undefined;
    });
  }

  /**
   * Generate HTML for webview - supports both dev and production modes
   */
  private _getHtmlForWebview(webview: vscode.Webview): string {
    const isDevelopment = false; // Use production build

    if (isDevelopment) {
      return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline' http://localhost:5173 'unsafe-eval'; style-src 'unsafe-inline' http://localhost:5173; connect-src http://localhost:5036 ws://localhost:5173 http://localhost:5173; font-src http://localhost:5173;">
  <title>AI Chat</title>
  <script type="module">
    import RefreshRuntime from 'http://localhost:5173/@react-refresh'
    RefreshRuntime.injectIntoGlobalHook(window)
    window.$RefreshReg$ = () => {}
    window.$RefreshSig$ = () => (type) => type
    window.__vite_plugin_react_preamble_installed__ = true
  </script>
  <script type="module" src="http://localhost:5173/src/main.tsx"></script>
  <link rel="stylesheet" href="http://localhost:5173/src/index.css">
</head>
<body style="padding: 0; margin: 0;">
  <div id="root"></div>
</body>
</html>`;
    }

    // Production: Load compiled assets
    const scriptUri = webview.asWebviewUri(
      vscode.Uri.joinPath(
        this._extensionUri,
        "webview-ui",
        "build",
        "assets",
        "index.js",
      ),
    );
    const styleUri = webview.asWebviewUri(
      vscode.Uri.joinPath(
        this._extensionUri,
        "webview-ui",
        "build",
        "assets",
        "index.css",
      ),
    );

    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <!-- style-src 'unsafe-inline': mermaid diagrams in plans carry their own <style> inside the SVG -->
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline' ${webview.cspSource}; style-src 'unsafe-inline' ${webview.cspSource}; connect-src http://localhost:5036;">
  <link href="${styleUri}" rel="stylesheet">
  <title>AI Chat</title>
</head>
<body style="padding: 0; margin: 0;">
  <div id="root"></div>
  <script type="module" src="${scriptUri}"></script>
</body>
</html>`;
  }
}
