import * as vscode from 'vscode'
import { ChatViewProvider } from './ChatViewProvider'
import { recordLoadedBundles } from './services/buildInfo'

export function activate(context: vscode.ExtensionContext): void {
  console.log('AI Chat Extension is now active!')
  recordLoadedBundles(context.extensionPath)

  const provider = new ChatViewProvider(context.extensionUri, context)

  // Register webview view provider
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider(
      ChatViewProvider.viewType,
      provider
    )
  )

  // Command: Open sidebar
  context.subscriptions.push(
    vscode.commands.registerCommand('aiChat.openSidebar', () => {
      vscode.commands.executeCommand('workbench.view.extension.ai-chat-sidebar-container')
    })
  )

  // Command: New conversation (clears chat)
  context.subscriptions.push(
    vscode.commands.registerCommand('aiChat.newChat', () => {
      provider.sendMessage({ type: 'clearChat' })
    })
  )

  // Command: Ask about selection (right-click or Ctrl+Shift+A)
  context.subscriptions.push(
    vscode.commands.registerCommand('aiChat.askAboutSelection', () => {
      const editor = vscode.window.activeTextEditor
      if (!editor) {
        vscode.window.showInformationMessage('No active editor with a selection')
        return
      }

      const selection = editor.selection
      if (selection.isEmpty) {
        vscode.window.showInformationMessage('Select some code first')
        return
      }

      const code = editor.document.getText(selection)
      const filePath = vscode.workspace.asRelativePath(editor.document.uri)
      const language = editor.document.languageId

      // Open sidebar first
      vscode.commands.executeCommand('workbench.view.extension.ai-chat-sidebar-container')

      // Send selection to webview
      setTimeout(() => {
        provider.sendMessage({
          type: 'sendSelection',
          filePath,
          code,
          language,
        })
      }, 300) // small delay to ensure webview is ready
    })
  )
}

export function deactivate(): void {
  console.log('AI Chat Extension is deactivated')
}
