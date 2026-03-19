import { Component } from "react";
import type { ReactNode, ErrorInfo } from "react";

interface ErrorBoundaryProps { children: ReactNode; name: string }
interface ErrorBoundaryState { hasError: boolean; error: Error | null }

/**
 * React error boundary that catches render errors in any child subtree.
 * Must be a class component — React doesn't support error boundaries via hooks.
 */
class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false, error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`[ErrorBoundary:${this.props.name}]`, error, info.componentStack);
  }

  render() {
    if (this.state.hasError) {
      return (
        <div style={{
          margin: 16, padding: 24, borderRadius: 8,
          background: "#1a1625", border: "1px solid #ff4757",
          fontFamily: "'JetBrains Mono', monospace",
        }}>
          <div style={{ color: "#ff4757", fontSize: 14, fontWeight: 700, marginBottom: 8 }}>
            Something went wrong in {this.props.name}
          </div>
          <div style={{ color: "#8b8b9e", fontSize: 11, marginBottom: 16 }}>
            {this.state.error?.message ?? "Unknown error"}
          </div>
          <button
            onClick={() => this.setState({ hasError: false, error: null })}
            style={{
              padding: "6px 16px", borderRadius: 5, cursor: "pointer",
              border: "1px solid #ff4757", background: "rgba(255,71,87,0.1)",
              color: "#ff4757", fontSize: 11, fontWeight: 600,
            }}
          >
            RELOAD PANEL
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}

export default ErrorBoundary;
