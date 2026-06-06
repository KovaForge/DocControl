import { useEffect, useState } from 'react';
import { SettingsApi, type AgentToken } from '../lib/api';
import { useProject } from '../lib/projectContext';

type AiSettings = {
  provider: string;
  openAiModel: string;
  geminiModel: string;
};

function errorMessage(err: unknown, fallback: string) {
  return err instanceof Error ? err.message : fallback;
}


export default function Settings() {
  const { projectId } = useProject();
  const [ai, setAi] = useState<AiSettings>({ provider: 'OpenAi', openAiModel: 'gpt-4.1', geminiModel: 'gemini-3-flash-preview' });
  const [openAiKey, setOpenAiKey] = useState('');
  const [geminiKey, setGeminiKey] = useState('');
  const [hasOpenAiKey, setHasOpenAiKey] = useState(false);
  const [hasGeminiKey, setHasGeminiKey] = useState(false);
  const [openAiKeySuffix, setOpenAiKeySuffix] = useState<string | null>(null);
  const [geminiKeySuffix, setGeminiKeySuffix] = useState<string | null>(null);
  const [clearOpenAiKey, setClearOpenAiKey] = useState(false);
  const [clearGeminiKey, setClearGeminiKey] = useState(false);
  const [agentTokens, setAgentTokens] = useState<AgentToken[]>([]);
  const [agentTokenName, setAgentTokenName] = useState('');
  const [agentTokenExpiryDays, setAgentTokenExpiryDays] = useState('90');
  const [createdAgentToken, setCreatedAgentToken] = useState<string | null>(null);
  const [tokenLoading, setTokenLoading] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadAgentTokens = async () => {
    if (!projectId) return;
    setTokenLoading(true);
    try {
      const data = await SettingsApi.listAgentTokens(projectId);
      setAgentTokens(Array.isArray(data.items) ? data.items : []);
    } catch (err: unknown) {
      setError(errorMessage(err, 'Failed to load agent tokens'));
    } finally {
      setTokenLoading(false);
    }
  };

  useEffect(() => {
    const load = async () => {
      if (!projectId) return;
      setError(null);
      try {
        const data = await SettingsApi.get(projectId);
        setAi(data.aiSettings);
        setHasOpenAiKey(data.hasOpenAiKey ?? false);
        setHasGeminiKey(data.hasGeminiKey ?? false);
        setOpenAiKeySuffix(data.openAiKeySuffix ?? null);
        setGeminiKeySuffix(data.geminiKeySuffix ?? null);
      } catch (err: unknown) {
        setError(errorMessage(err, 'Failed to load settings'));
      }
    };
    load();
    loadAgentTokens();
    setCreatedAgentToken(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const save = async () => {
    if (!projectId) return;
    setError(null);
    setMessage(null);
    try {
      const result = await SettingsApi.save(projectId, {
        aiSettings: ai,
        openAiKey,
        geminiKey,
        clearOpenAiKey,
        clearGeminiKey,
      });
      setMessage('Saved');
      setHasOpenAiKey(result?.hasOpenAi ?? (!clearOpenAiKey && (hasOpenAiKey || !!openAiKey)));
      setHasGeminiKey(result?.hasGemini ?? (!clearGeminiKey && (hasGeminiKey || !!geminiKey)));
      setOpenAiKeySuffix(result?.openAiKeySuffix ?? (clearOpenAiKey ? null : openAiKeySuffix));
      setGeminiKeySuffix(result?.geminiKeySuffix ?? (clearGeminiKey ? null : geminiKeySuffix));
      setOpenAiKey('');
      setGeminiKey('');
      setClearOpenAiKey(false);
      setClearGeminiKey(false);
    } catch (err: unknown) {
      setError(errorMessage(err, 'Failed to save'));
    }
  };

  const createAgentToken = async () => {
    if (!projectId) return;
    setError(null);
    setMessage(null);
    setCreatedAgentToken(null);
    setTokenLoading(true);
    try {
      const expiryDays = agentTokenExpiryDays === 'never' ? null : Number(agentTokenExpiryDays);
      const expiresAtUtc = expiryDays
        ? new Date(Date.now() + expiryDays * 24 * 60 * 60 * 1000).toISOString()
        : null;
      const result = await SettingsApi.createAgentToken(projectId, {
        name: agentTokenName,
        expiresAtUtc,
      });
      setCreatedAgentToken(result.token);
      setAgentTokenName('');
      setMessage('Agent token created. Copy it now; it will not be shown again.');
      await loadAgentTokens();
    } catch (err: unknown) {
      setError(errorMessage(err, 'Failed to create agent token'));
    } finally {
      setTokenLoading(false);
    }
  };

  const revokeAgentToken = async (token: AgentToken) => {
    if (!projectId) return;
    const confirmed = window.confirm(`Revoke agent token "${token.name}"?`);
    if (!confirmed) return;
    setError(null);
    setMessage(null);
    setTokenLoading(true);
    try {
      await SettingsApi.revokeAgentToken(projectId, token.id);
      setMessage('Agent token revoked.');
      await loadAgentTokens();
    } catch (err: unknown) {
      setError(errorMessage(err, 'Failed to revoke agent token'));
    } finally {
      setTokenLoading(false);
    }
  };

  return (
    <div className="page">
      <h1>Settings</h1>
      <p className="muted">
        Project settings: AI provider/model + keys. Endpoint: GET/POST
        <code>/api/projects/{"{projectId}"}/settings</code>.
      </p>
      {!projectId && <div className="pill">Select a project first.</div>}
      {message && <div className="pill" style={{ background: '#ecfdf3', color: '#166534' }}>{message}</div>}
      {error && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{error}</div>}
      {projectId && (
        <div className="grid">
          <div className="card">
            <h3>AI</h3>
            <div className="stack">
              <label>Provider</label>
              <select value={ai.provider} onChange={(e) => setAi({ ...ai, provider: e.target.value })}>
                <option value="OpenAi">OpenAI</option>
                <option value="Gemini">Gemini</option>
              </select>
              <label>OpenAI Model</label>
              <input value={ai.openAiModel} onChange={(e) => setAi({ ...ai, openAiModel: e.target.value })} />
              <label>Gemini Model</label>
              <input value={ai.geminiModel} onChange={(e) => setAi({ ...ai, geminiModel: e.target.value })} />
              <label>OpenAI Key (optional)</label>
              <input
                value={openAiKey}
                onChange={(e) => {
                  setOpenAiKey(e.target.value);
                  setClearOpenAiKey(false);
                }}
                placeholder={hasOpenAiKey ? 'Key stored' : undefined}
              />
              <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                {hasOpenAiKey && (
                  <span className="muted">
                    Key is stored{openAiKeySuffix ? ` (...${openAiKeySuffix})` : '.'}
                  </span>
                )}
                <button
                  type="button"
                  onClick={() => {
                    setClearOpenAiKey(true);
                    setOpenAiKey('');
                  }}
                  disabled={!hasOpenAiKey}
                >
                  Clear
                </button>
              </div>
              {clearOpenAiKey && <span className="muted">OpenAI key will be cleared on save.</span>}
              <label>Gemini Key (optional)</label>
              <input
                value={geminiKey}
                onChange={(e) => {
                  setGeminiKey(e.target.value);
                  setClearGeminiKey(false);
                }}
                placeholder={hasGeminiKey ? 'Key stored' : undefined}
              />
              <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                {hasGeminiKey && (
                  <span className="muted">
                    Key is stored{geminiKeySuffix ? ` (...${geminiKeySuffix})` : '.'}
                  </span>
                )}
                <button
                  type="button"
                  onClick={() => {
                    setClearGeminiKey(true);
                    setGeminiKey('');
                  }}
                  disabled={!hasGeminiKey}
                >
                  Clear
                </button>
              </div>
              {clearGeminiKey && <span className="muted">Gemini key will be cleared on save.</span>}
            </div>
          </div>

          <div className="card">
            <h3>Agent tokens</h3>
            <p className="muted">
              Personal bearer tokens for OpenClaw, Hermes, or another agent to call the DocControl API as you.
            </p>
            <div className="stack">
              <label>Token name</label>
              <input
                value={agentTokenName}
                onChange={(e) => setAgentTokenName(e.target.value)}
                placeholder="OpenClaw on laptop"
              />
              <label>Expires</label>
              <select value={agentTokenExpiryDays} onChange={(e) => setAgentTokenExpiryDays(e.target.value)}>
                <option value="90">90 days</option>
                <option value="30">30 days</option>
                <option value="365">1 year</option>
                <option value="never">Never</option>
              </select>
              <button type="button" onClick={createAgentToken} disabled={tokenLoading}>
                {tokenLoading ? 'Working...' : 'Create token'}
              </button>
            </div>

            {createdAgentToken && (
              <div className="pill" style={{ marginTop: 12, background: '#fffbeb', color: '#92400e' }}>
                <strong>Copy this token now. It will not be shown again.</strong>
                <code style={{ display: 'block', marginTop: 8, overflowX: 'auto' }}>{createdAgentToken}</code>
                <button
                  type="button"
                  style={{ marginTop: 8 }}
                  onClick={() => navigator.clipboard?.writeText(createdAgentToken)}
                >
                  Copy token
                </button>
              </div>
            )}

            <div style={{ marginTop: 16 }}>
              <strong>Existing tokens</strong>
              {tokenLoading && agentTokens.length === 0 ? <p className="muted">Loading tokens...</p> : null}
              {!tokenLoading && agentTokens.length === 0 ? <p className="muted">No agent tokens created yet.</p> : null}
              {agentTokens.length > 0 && (
                <table className="table" style={{ marginTop: 8 }}>
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Prefix</th>
                      <th>Created</th>
                      <th>Last used</th>
                      <th>Expires</th>
                      <th>Action</th>
                    </tr>
                  </thead>
                  <tbody>
                    {agentTokens.map((token) => (
                      <tr key={token.id}>
                        <td className="muted">{token.name}</td>
                        <td className="muted">{token.prefix}</td>
                        <td className="muted">{new Date(token.createdAtUtc).toLocaleString()}</td>
                        <td className="muted">
                          {token.lastUsedAtUtc ? new Date(token.lastUsedAtUtc).toLocaleString() : 'Never'}
                        </td>
                        <td className="muted">
                          {token.expiresAtUtc ? new Date(token.expiresAtUtc).toLocaleDateString() : 'Never'}
                        </td>
                        <td>
                          <button type="button" onClick={() => revokeAgentToken(token)} disabled={tokenLoading}>
                            Revoke
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </div>
        </div>
      )}
      {projectId && (
        <div style={{ marginTop: 12 }}>
          <button onClick={save}>Save</button>
        </div>
      )}
    </div>
  );
}
