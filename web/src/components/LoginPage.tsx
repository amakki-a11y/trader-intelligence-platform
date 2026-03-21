import { useState, type FormEvent, type CSSProperties } from "react";
import C from "../styles/colors";
import { useAuth } from "../contexts/AuthContext";

/**
 * Login page with username/password form.
 * Shows error messages for invalid credentials.
 * Design: centered dark card matching the dashboard theme.
 */
export default function LoginPage() {
  const { login } = useAuth();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError("");
    setLoading(true);
    const result = await login(username, password);
    if (!result.ok) setError(result.error || "Login failed");
    setLoading(false);
  };

  const card: CSSProperties = {
    width: 380, padding: 40, borderRadius: 12,
    background: C.card, border: `1px solid ${C.border}`,
    boxShadow: "0 8px 32px rgba(0,0,0,0.4)",
  };

  const input: CSSProperties = {
    width: "100%", padding: "10px 14px", borderRadius: 6,
    border: `1px solid ${C.border}`, background: C.bg3,
    color: C.t1, fontSize: 14, fontFamily: "'DM Sans',system-ui,sans-serif",
    outline: "none", marginTop: 6,
  };

  const btn: CSSProperties = {
    width: "100%", padding: "12px", borderRadius: 6, border: "none",
    background: C.teal, color: C.bg, fontSize: 14, fontWeight: 600,
    cursor: loading ? "wait" : "pointer", opacity: loading ? 0.7 : 1,
    fontFamily: "'DM Sans',system-ui,sans-serif", marginTop: 8,
  };

  return (
    <div style={{
      width: "100%", height: "100vh", display: "flex",
      alignItems: "center", justifyContent: "center",
      background: C.bg2, fontFamily: "'DM Sans',system-ui,sans-serif",
    }}>
      <div style={card}>
        <div style={{ textAlign: "center", marginBottom: 32 }}>
          <div style={{
            width: 56, height: 56, borderRadius: 12, margin: "0 auto 16px",
            background: "linear-gradient(135deg, #3DD9A020, #9B8AFF20)",
            border: `1px solid ${C.border}`,
            display: "flex", alignItems: "center", justifyContent: "center",
            fontSize: 24, fontWeight: 700, color: C.teal,
          }}>R</div>
          <h1 style={{ color: C.t1, fontSize: 20, fontWeight: 600 }}>Trader Intelligence Platform</h1>
          <p style={{ color: C.t3, fontSize: 12, marginTop: 4 }}>Sign in to continue</p>
        </div>

        <form onSubmit={handleSubmit}>
          <div style={{ marginBottom: 16 }}>
            <label style={{ color: C.t2, fontSize: 12, fontWeight: 500 }}>Username</label>
            <input
              style={input}
              type="text"
              value={username}
              onChange={e => setUsername(e.target.value)}
              autoFocus
              autoComplete="username"
            />
          </div>
          <div style={{ marginBottom: 20 }}>
            <label style={{ color: C.t2, fontSize: 12, fontWeight: 500 }}>Password</label>
            <input
              style={input}
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              autoComplete="current-password"
            />
          </div>

          {error && (
            <div style={{
              padding: "8px 12px", borderRadius: 6, marginBottom: 12,
              background: "rgba(255,82,82,0.1)", border: "1px solid rgba(255,82,82,0.3)",
              color: C.red, fontSize: 12,
            }}>{error}</div>
          )}

          <button type="submit" style={btn} disabled={loading}>
            {loading ? "Signing in..." : "Sign In"}
          </button>
        </form>

        <p style={{ color: C.t3, fontSize: 10, textAlign: "center", marginTop: 24 }}>
          TIP v2.0 — BBC Corp
        </p>
      </div>
    </div>
  );
}
