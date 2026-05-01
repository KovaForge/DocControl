import { useEffect, useMemo, useState } from 'react';
import { AiApi, CodesApi, DocumentsApi, LevelCodesApi, ProjectsApi } from '../lib/api';
import { useProject } from '../lib/projectContext';

export default function Generate() {
  const [query, setQuery] = useState('');
  const [recommendResult, setRecommendResult] = useState<any | null>(null);
  const [recommendError, setRecommendError] = useState<string | null>(null);
  const [recommendLoading, setRecommendLoading] = useState(false);
  const [level1, setLevel1] = useState('');
  const [level2, setLevel2] = useState('');
  const [level3, setLevel3] = useState('');
  const [level4, setLevel4] = useState('');
  const [level5, setLevel5] = useState('');
  const [level6, setLevel6] = useState('');
  const [freeText, setFreeText] = useState('');
  const [output, setOutput] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [levelCount, setLevelCount] = useState(3);
  const [catalog, setCatalog] = useState<any[]>([]);
  const [levelCodes, setLevelCodes] = useState<any[]>([]);
  const [catalogLoaded, setCatalogLoaded] = useState(false);
  const [missingDescriptions, setMissingDescriptions] = useState<Record<string, string>>({});
  const { projectId } = useProject();

  useEffect(() => {
    const load = async () => {
      if (!projectId) {
        setLevelCount(3);
        setCatalog([]);
        setLevelCodes([]);
        setCatalogLoaded(false);
        return;
      }
      setCatalogLoaded(false);
      try {
        const [project, codes, standaloneCodes] = await Promise.all([
          ProjectsApi.get(projectId),
          CodesApi.list(projectId),
          LevelCodesApi.list(projectId),
        ]);
        const count = typeof project?.levelCount === 'number' ? project.levelCount : 3;
        setLevelCount(Math.min(Math.max(count, 1), 6));
        setCatalog(Array.isArray(codes) ? codes : []);
        setLevelCodes(Array.isArray(standaloneCodes) ? standaloneCodes : []);
        setCatalogLoaded(true);
      } catch {
        setLevelCount(3);
        setCatalog([]);
        setLevelCodes([]);
        setCatalogLoaded(false);
      }
    };
    load();
  }, [projectId]);

  const catalogCodesByLevel = useMemo(() => {
    const map = new Map<number, Set<string>>();
    for (const entry of levelCodes) {
      const level = Number(entry?.level);
      const normalized = String(entry?.code ?? '').trim().toLowerCase();
      if (level < 1 || level > 6 || !normalized) continue;
      if (!map.has(level)) map.set(level, new Set());
      map.get(level)!.add(normalized);
    }
    for (const entry of catalog) {
      const key = entry?.key ?? {};
      const l1 = key.level1 ?? '';
      const l2 = key.level2 ?? '';
      const l3 = key.level3 ?? '';
      const l4 = key.level4 ?? '';
      const l5 = key.level5 ?? '';
      const l6 = key.level6 ?? '';
      const level = l6 ? 6 : l5 ? 5 : l4 ? 4 : l3 ? 3 : l2 ? 2 : 1;
      const code =
        level === 6 ? l6 :
        level === 5 ? l5 :
        level === 4 ? l4 :
        level === 3 ? l3 :
        level === 2 ? l2 :
        l1;
      const normalized = String(code ?? '').trim().toLowerCase();
      if (!normalized) continue;
      if (!map.has(level)) map.set(level, new Set());
      map.get(level)!.add(normalized);
    }
    return map;
  }, [catalog, levelCodes]);

  const isCatalogMissing = (level: number) => {
    const l1 = level1.trim();
    const l2 = level2.trim();
    const l3 = level3.trim();
    const l4 = level4.trim();
    const l5 = level5.trim();
    const l6 = level6.trim();

    if (level === 1 && !l1) return false;
    if (level === 2 && (!l1 || !l2)) return false;
    if (level === 3 && (!l1 || !l2 || !l3)) return false;
    if (level === 4 && (!l1 || !l2 || !l3 || !l4)) return false;
    if (level === 5 && (!l1 || !l2 || !l3 || !l4 || !l5)) return false;
    if (level === 6 && (!l1 || !l2 || !l3 || !l4 || !l5 || !l6)) return false;

    const code =
      level === 1 ? l1 :
      level === 2 ? l2 :
      level === 3 ? l3 :
      level === 4 ? l4 :
      level === 5 ? l5 :
      l6;
    const normalized = code.trim().toLowerCase();
    if (!normalized) return false;
    const set = catalogCodesByLevel.get(level);
    return !set || !set.has(normalized);
  };

  const missingLevels = catalogLoaded
    ? [
        { level: 1, code: level1.trim() },
        { level: 2, code: level2.trim() },
        { level: 3, code: level3.trim() },
        { level: 4, code: level4.trim() },
        { level: 5, code: level5.trim() },
        { level: 6, code: level6.trim() },
      ].filter((item) => item.level <= levelCount && isCatalogMissing(item.level))
    : [];

  const getMissingKey = (level: number) => {
    const l1 = level1.trim();
    const l2 = level2.trim();
    const l3 = level3.trim();
    const l4 = level4.trim();
    const l5 = level5.trim();
    const l6 = level6.trim();
    return `${level}|${l1}|${level >= 2 ? l2 : ''}|${level >= 3 ? l3 : ''}|${level >= 4 ? l4 : ''}|${level >= 5 ? l5 : ''}|${level >= 6 ? l6 : ''}`;
  };

  const runRecommend = async () => {
    if (!projectId || !query.trim()) return;
    setRecommendLoading(true);
    setRecommendError(null);
    try {
      const res = await AiApi.recommend(projectId, query.trim());
      setRecommendResult(res);
      setLevel1(res.level1 ?? '');
      setLevel2(res.level2 ?? '');
      setLevel3(res.level3 ?? '');
      setLevel4(res.level4 ?? '');
      setLevel5(res.level5 ?? '');
      setLevel6(res.level6 ?? '');
      setFreeText(res.freeText ?? '');
    } catch (err: any) {
      setRecommendError(err.message ?? 'AI recommend failed');
    } finally {
      setRecommendLoading(false);
    }
  };

  const generate = async () => {
    if (!projectId) return;
    setLoading(true);
    setError(null);
    try {
      if (missingLevels.length > 0) {
        const missingWithoutDesc = missingLevels.filter((item) => {
          const key = getMissingKey(item.level);
          return !missingDescriptions[key]?.trim();
        });
        if (missingWithoutDesc.length > 0) {
          setError('Please provide descriptions for all new codes before generating.');
          setLoading(false);
          return;
        }
        const catalogEntries = missingLevels.map((item) => {
          const key = getMissingKey(item.level);
          return buildCatalogPayload(
            item.level,
            {
              level1,
              level2,
              level3,
              level4,
              level5,
              level6,
            },
            missingDescriptions[key] ?? ''
          );
        });
        for (const item of missingLevels) {
          const key = getMissingKey(item.level);
          await LevelCodesApi.upsert(projectId, {
            level: item.level,
            code: item.code,
            description: missingDescriptions[key] ?? '',
          });
        }
        for (const entry of catalogEntries) {
          await CodesApi.upsert(projectId, entry);
        }
        const [refreshed, refreshedLevelCodes] = await Promise.all([
          CodesApi.list(projectId),
          LevelCodesApi.list(projectId),
        ]);
        setCatalog(Array.isArray(refreshed) ? refreshed : []);
        setLevelCodes(Array.isArray(refreshedLevelCodes) ? refreshedLevelCodes : []);
      }
      const res = await DocumentsApi.create(projectId, {
        level1,
        level2,
        level3,
        level4,
        level5,
        level6,
        freeText,
        extension: undefined,
      });
      setOutput(res.fileName ?? `(created) #${res.number}`);
    } catch (err: any) {
      setError(err.message ?? 'Failed to create document');
    } finally {
      setLoading(false);
    }
  };

  const preview = async () => {
    if (!projectId) return;
    setPreviewLoading(true);
    setError(null);
    try {
      const res = await DocumentsApi.preview(projectId, {
        level1,
        level2,
        level3,
        level4,
        level5,
        level6,
        freeText,
        extension: undefined,
      });
      setOutput(res.fileName ?? '');
    } catch (err: any) {
      setError(err.message ?? 'Preview failed');
    } finally {
      setPreviewLoading(false);
    }
  };

  return (
    <div className="page">
      <h1>Generate</h1>
      <p className="muted">Use AI to suggest a code, then create the document with the next number.</p>
      {!projectId && <div className="pill">Select a project first.</div>}
      <div className="card">
        <strong>AI Recommend</strong>
        <p className="muted">Ask in natural language; we will suggest code levels and free text.</p>
        <textarea
          rows={3}
          placeholder="e.g. 'Create governance register for Delpach Family Trust'"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <button onClick={runRecommend} disabled={!query.trim() || !projectId || recommendLoading}>
          {recommendLoading ? 'Working...' : 'Recommend'}
        </button>
        {recommendError && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{recommendError}</div>}
        {recommendResult && (
          <div className="card" style={{ marginTop: 10 }}>
            <div><strong>Level1:</strong> {recommendResult.level1}</div>
            <div><strong>Level2:</strong> {recommendResult.level2}</div>
            <div><strong>Level3:</strong> {recommendResult.level3}</div>
            {recommendResult.level4 && <div><strong>Level4:</strong> {recommendResult.level4}</div>}
            {recommendResult.level5 && <div><strong>Level5:</strong> {recommendResult.level5}</div>}
            {recommendResult.level6 && <div><strong>Level6:</strong> {recommendResult.level6}</div>}
            <div><strong>Free text:</strong> {recommendResult.freeText}</div>
            {recommendResult.reason && <div className="muted">Reason: {recommendResult.reason}</div>}
          </div>
        )}
      </div>
      <div className="grid" style={{ marginTop: 12 }}>
        <div className="stack">
          <strong>Generate next code</strong>
          <p className="muted">Fill in levels and free text; backend will allocate the next number.</p>
          {missingLevels.length > 0 && (
            <div className="pill" style={{ background: '#fef3c7', color: '#92400e' }}>
              Generating this file name will automatically add these code(s) to the Codes catalog. Please input descriptions.
            </div>
          )}
          <label>Level1</label>
          <div className="row" style={{ gap: 8, alignItems: 'center' }}>
            <input value={level1} onChange={(e) => setLevel1(e.target.value)} placeholder="DFT" />
            {isCatalogMissing(1) && (
              <input
                value={missingDescriptions[getMissingKey(1)] ?? ''}
                onChange={(e) => setMissingDescriptions((prev) => ({ ...prev, [getMissingKey(1)]: e.target.value }))}
                placeholder="Level1 description"
              />
            )}
          </div>
          <label>Level2</label>
          <div className="row" style={{ gap: 8, alignItems: 'center' }}>
            <input value={level2} onChange={(e) => setLevel2(e.target.value)} placeholder="GOV" />
            {isCatalogMissing(2) && (
              <input
                value={missingDescriptions[getMissingKey(2)] ?? ''}
                onChange={(e) => setMissingDescriptions((prev) => ({ ...prev, [getMissingKey(2)]: e.target.value }))}
                placeholder="Level2 description"
              />
            )}
          </div>
          <label>Level3</label>
          <div className="row" style={{ gap: 8, alignItems: 'center' }}>
            <input value={level3} onChange={(e) => setLevel3(e.target.value)} placeholder="REG" />
            {isCatalogMissing(3) && (
              <input
                value={missingDescriptions[getMissingKey(3)] ?? ''}
                onChange={(e) => setMissingDescriptions((prev) => ({ ...prev, [getMissingKey(3)]: e.target.value }))}
                placeholder="Level3 description"
              />
            )}
          </div>
          {levelCount >= 4 && (
            <>
              <label>Level4</label>
              <div className="row" style={{ gap: 8, alignItems: 'center' }}>
                <input value={level4} onChange={(e) => setLevel4(e.target.value)} placeholder="SUB" />
                {isCatalogMissing(4) && (
                  <input
                    value={missingDescriptions[getMissingKey(4)] ?? ''}
                    onChange={(e) => setMissingDescriptions((prev) => ({ ...prev, [getMissingKey(4)]: e.target.value }))}
                    placeholder="Level4 description"
                  />
                )}
              </div>
            </>
          )}
          {levelCount >= 5 && (
            <>
              <label>Level5</label>
              <div className="row" style={{ gap: 8, alignItems: 'center' }}>
                <input value={level5} onChange={(e) => setLevel5(e.target.value)} placeholder="TYPE" />
                {isCatalogMissing(5) && (
                  <input
                    value={missingDescriptions[getMissingKey(5)] ?? ''}
                    onChange={(e) => setMissingDescriptions((prev) => ({ ...prev, [getMissingKey(5)]: e.target.value }))}
                    placeholder="Level5 description"
                  />
                )}
              </div>
            </>
          )}
          {levelCount >= 6 && (
            <>
              <label>Level6</label>
              <div className="row" style={{ gap: 8, alignItems: 'center' }}>
                <input value={level6} onChange={(e) => setLevel6(e.target.value)} placeholder="ITEM" />
                {isCatalogMissing(6) && (
                  <input
                    value={missingDescriptions[getMissingKey(6)] ?? ''}
                    onChange={(e) => setMissingDescriptions((prev) => ({ ...prev, [getMissingKey(6)]: e.target.value }))}
                    placeholder="Level6 description"
                  />
                )}
              </div>
            </>
          )}
          <label>Free text</label>
          <input value={freeText} onChange={(e) => setFreeText(e.target.value)} placeholder="Delpach DocControl" />
          <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
            <button onClick={preview} disabled={!projectId || previewLoading}>
              {previewLoading ? 'Previewing...' : 'Preview'}
            </button>
            <button onClick={generate} disabled={!projectId || loading}>
              {loading ? 'Saving...' : 'Create document'}
            </button>
          </div>
          {error && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{error}</div>}
        </div>
        <div className="card">
          <strong>Preview</strong>
          <p className="muted">Filename returned by backend.</p>
          <div style={{ marginTop: 10, fontWeight: 700 }}>{output ?? '...'}</div>
        </div>
      </div>
    </div>
  );
}

function buildCatalogPayload(
  level: number,
  values: {
    level1: string;
    level2: string;
    level3: string;
    level4: string;
    level5: string;
    level6: string;
  },
  description: string
) {
  const l1 = level >= 1 ? values.level1.trim() : '';
  const l2 = level >= 2 ? values.level2.trim() : '';
  const l3 = level >= 3 ? values.level3.trim() : '';
  const l4 = level >= 4 ? values.level4.trim() : '';
  const l5 = level >= 5 ? values.level5.trim() : '';
  const l6 = level >= 6 ? values.level6.trim() : '';
  return {
    level1: l1,
    level2: l2,
    level3: l3,
    level4: level >= 4 ? l4 : '',
    level5: level >= 5 ? l5 : '',
    level6: level >= 6 ? l6 : '',
    description: description.trim(),
  };
}
