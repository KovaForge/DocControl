import { useEffect, useState } from 'react';
import { SettingsApi } from '../lib/api';
import { useProject } from '../lib/projectContext';

type AiSettings = {
  provider: string;
  openAiModel: string;
  geminiModel: string;
};

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
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

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
      } catch (err: any) {
        setError(err.message ?? 'Failed to load settings');
      }
    };
    load();
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
    } catch (err: any) {
      setError(err.message ?? 'Failed to save');
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
