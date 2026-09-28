declare global {
  interface Window {
    acquireVsCodeApi: () => VsCodeApi
  }
}

interface VsCodeApi {
  postMessage: (message: unknown) => void
  setState: (state: unknown) => void
  getState: () => unknown
}

const vscode = window.acquireVsCodeApi()

export { vscode }
