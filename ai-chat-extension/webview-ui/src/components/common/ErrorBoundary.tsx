import React from 'react'
import { AlertCircle, RefreshCw } from 'lucide-react'

interface Props {
  children: React.ReactNode
}

interface State {
  hasError: boolean
  error: Error | null
}

export class ErrorBoundary extends React.Component<Props, State> {
  constructor(props: Props) {
    super(props)
    this.state = { hasError: false, error: null }
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error }
  }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    console.error('[ErrorBoundary]', error, info.componentStack)
  }

  handleReset = () => {
    this.setState({ hasError: false, error: null })
  }

  render() {
    if (this.state.hasError) {
      return (
        <div className="flex flex-col items-center justify-center h-full gap-3 p-6 text-center">
          <AlertCircle size={32} className="text-error" />
          <h2 className="text-[14px] font-medium text-text-primary">
            Something went wrong
          </h2>
          <p className="text-[12px] text-text-secondary max-w-[300px]">
            {this.state.error?.message || 'An unexpected error occurred in the chat panel.'}
          </p>
          <button
            onClick={this.handleReset}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-[12px] font-medium
                       bg-accent text-white hover:bg-accent-hover transition-colors"
          >
            <RefreshCw size={12} />
            Reload Panel
          </button>
        </div>
      )
    }

    return this.props.children
  }
}
