import { useState, type FormEvent, type CSSProperties } from "react";
import C from "../styles/colors";
import { useAuth } from "../contexts/AuthContext";

/**
 * Change password form. Used both:
 * - Standalone (forced on first login when must_change_pwd = true)
 * - Embedded in Settings page tab
 * When embedded=true, renders without the full-screen wrapper.
 */
export default function ChangePasswordPage({ embedded }: { embedded?: boolean }) {
  const { changePassword, user } = useAuth();
  const [oldPwd, setOldPwd] = useState("");
  const [newPwd, setNewPwd] = useState("");
  const [confirmPwd, setConfirmPwd] = useState("");
  const [error, setError] = useState("");
  const [success, setSuccess] = useState(false);
  const [loading, setLoading] = useState(false);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError("");

    if (newPwd.length < 8) {
      setError("New password must be at least 8 characters");
      return;
    }
    if (newPwd !== confirmPwd) {
      setError("Passwords do not match");
      return;
    }

    setLoading(true);
    const result = await changePassword(oldPwd, newPwd);
    if (result.ok) {
      setSuccess(true);
    } else {
      setError(result.error || "Failed to change password");
    }
    setLoading(false);
  };

  const card: CSSProperties = {
    width: embedded ? "100%" : 380, padding: embedded ? 24 : 40, borderRadius: 12,
    background: C.card, border: `1px solid ${C.border}`,
    boxShadow: embedded ? "none" : "0 8px 32px rgba(0,0,0,0.4)",
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

  const successContent = (
    <div style={card}>
      <div style={{ textAlign: "center" }}>
        <div style={{ fontSize: 48, marginBottom: 16 }}>✓</div>
        <h2 style={{ color: C.teal, fontSize: 18, marginBottom: 8 }}>Password Changed</h2>
        <p style={{ color: C.t2, fontSize: 13 }}>Please sign in with your new password.</p>
      </div>
    </div>
  );

  const formContent = (
    <div style={card}>
      <div style={{ textAlign: "center", marginBottom: 24 }}>
        <h2 style={{ color: C.t1, fontSize: 18, fontWeight: 600 }}>Change Password</h2>
        {user?.mustChangePassword && (
          <p style={{ color: C.amber, fontSize: 12, marginTop: 8 }}>
            You must change your password before continuing.
          </p>
        )}
      </div>

      <form onSubmit={handleSubmit}>
        <div style={{ marginBottom: 16 }}>
          <label style={{ color: C.t2, fontSize: 12, fontWeight: 500 }}>Current Password</label>
          <input style={input} type="password" value={oldPwd} onChange={e => setOldPwd(e.target.value)} autoFocus />
        </div>
        <div style={{ marginBottom: 16 }}>
          <label style={{ color: C.t2, fontSize: 12, fontWeight: 500 }}>New Password</label>
          <input style={input} type="password" value={newPwd} onChange={e => setNewPwd(e.target.value)} />
        </div>
        <div style={{ marginBottom: 20 }}>
          <label style={{ color: C.t2, fontSize: 12, fontWeight: 500 }}>Confirm New Password</label>
          <input style={input} type="password" value={confirmPwd} onChange={e => setConfirmPwd(e.target.value)} />
        </div>

        {error && (
          <div style={{
            padding: "8px 12px", borderRadius: 6, marginBottom: 12,
            background: "rgba(255,82,82,0.1)", border: "1px solid rgba(255,82,82,0.3)",
            color: C.red, fontSize: 12,
          }}>{error}</div>
        )}

        <button type="submit" style={btn} disabled={loading}>
          {loading ? "Changing..." : "Change Password"}
        </button>
      </form>
    </div>
  );

  // Embedded mode: no full-screen wrapper
  if (embedded) {
    return success ? successContent : formContent;
  }

  // Standalone mode: full-screen centered
  return (
    <div style={{
      width: "100%", height: "100vh", display: "flex",
      alignItems: "center", justifyContent: "center",
      background: C.bg2, fontFamily: "'DM Sans',system-ui,sans-serif",
    }}>
      {success ? successContent : formContent}
    </div>
  );
}
