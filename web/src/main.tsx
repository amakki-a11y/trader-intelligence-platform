import React from "react";
import ReactDOM from "react-dom/client";

function App(): React.ReactElement {
  return (
    <div
      style={{
        backgroundColor: "#0C0F14",
        color: "#E2E8F0",
        minHeight: "100vh",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        fontFamily:
          "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif",
      }}
    >
      <div style={{ textAlign: "center" }}>
        <h1
          style={{
            fontSize: "2rem",
            fontWeight: 700,
            marginBottom: "0.5rem",
          }}
        >
          Trader Intelligence Platform
        </h1>
        <p style={{ fontSize: "1.1rem", color: "#94A3B8" }}>
          Dashboard coming in Phase 4.
        </p>
      </div>
    </div>
  );
}

const rootElement = document.getElementById("root");
if (rootElement) {
  ReactDOM.createRoot(rootElement).render(
    <React.StrictMode>
      <App />
    </React.StrictMode>
  );
}
