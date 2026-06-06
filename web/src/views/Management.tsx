import { useEffect, useState } from 'react';
import { CodesApi, DocumentsApi } from '../lib/api';
import { useProject } from '../lib/projectContext';

type FilePickOptions = { accept: string };

type DocumentRow = {
  id: number;
  fileName: string;
  freeText?: string | null;
  createdAtUtc: string;
};

async function pickFileText(opts: FilePickOptions): Promise<string | null> {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = opts.accept;
    input.style.display = 'none';
    input.onchange = () => {
      const file = input.files?.[0];
      if (!file) {
        resolve(null);
        return;
      }
      const reader = new FileReader();
      reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : null);
      reader.onerror = () => resolve(null);
      reader.readAsText(file);
    };
    document.body.appendChild(input);
    input.click();
    setTimeout(() => document.body.removeChild(input), 0);
  });
}

function parseCsvLine(line: string): string[] {
  const result: string[] = [];
  let current = '';
  let inQuotes = false;

  for (let i = 0; i < line.length; i += 1) {
    const ch = line[i];
    if (ch === '"') {
      if (inQuotes && line[i + 1] === '"') {
        current += '"';
        i += 1;
      } else {
        inQuotes = !inQuotes;
      }
      continue;
    }
    if (ch === ',' && !inQuotes) {
      result.push(current);
      current = '';
      continue;
    }
    current += ch;
  }

  result.push(current);
  return result;
}

function parseDocumentsCsv(csv: string) {
  const lines = csv.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  if (lines.length === 0) return [];
  const dataLines = lines[0].toLowerCase().startsWith('code') ? lines.slice(1) : lines;

  return dataLines
    .map((line) => parseCsvLine(line))
    .map((parts) => ({
      code: (parts[0] ?? '').trim(),
      freeText: (parts[1] ?? '').trim(),
    }))
    .filter((entry) => entry.code.length > 0);
}

function downloadFile(contents: string, filename: string, mime = 'text/plain') {
  const blob = new Blob([contents], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function errorMessage(err: unknown, fallback: string) {
  return err instanceof Error ? err.message : fallback;
}

export default function Management() {
  const { projectId } = useProject();
  const [loading, setLoading] = useState(false);
  const [currentAction, setCurrentAction] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [cleanupQuery, setCleanupQuery] = useState('');
  const [cleanupDocs, setCleanupDocs] = useState<DocumentRow[]>([]);
  const [cleanupTotal, setCleanupTotal] = useState(0);
  const [selectedDocIds, setSelectedDocIds] = useState<Set<number>>(new Set());

  const resetStatus = () => {
    setMessage(null);
    setError(null);
  };

  const loadCleanupDocuments = async (query = cleanupQuery) => {
    if (!projectId) return;
    setLoading(true);
    setCurrentAction('loadCleanupDocuments');
    resetStatus();
    try {
      const data = await DocumentsApi.list(projectId, { q: query, take: 100, skip: 0 });
      const items = Array.isArray(data)
        ? data
        : Array.isArray(data.items)
          ? data.items
          : [];
      setCleanupDocs(items);
      setCleanupTotal(Array.isArray(data) ? data.length : typeof data.total === 'number' ? data.total : items.length);
      setSelectedDocIds(new Set());
    } catch (err: unknown) {
      setError(errorMessage(err, 'Failed to load documents'));
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  useEffect(() => {
    if (!projectId) {
      setCleanupDocs([]);
      setCleanupTotal(0);
      setSelectedDocIds(new Set());
      return;
    }
    loadCleanupDocuments();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const importCodesCsv = async () => {
    if (!projectId) return;
    const text = await pickFileText({ accept: '.csv,text/csv' });
    if (!text) return;
    setLoading(true);
    setCurrentAction('importCodesCsv');
    resetStatus();
    try {
      const result = await CodesApi.importCsv(projectId, text);
      const imported = (result?.successCount ?? result?.imported) ?? 0;
      const errors = result?.errors?.length ?? 0;
      setMessage(`Imported ${imported} code(s) from CSV${errors ? ` with ${errors} error(s)` : ''}.`);
    } catch (err: any) {
      setError(err.message ?? 'Code import failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const importCodesJson = async () => {
    if (!projectId) return;
    const text = await pickFileText({ accept: '.json,application/json' });
    if (!text) return;
    setLoading(true);
    setCurrentAction('importCodesJson');
    resetStatus();
    try {
      const parsed = JSON.parse(text);
      const result = await CodesApi.importJson(projectId, parsed);
      const imported = result?.imported ?? parsed.length ?? 0;
      setMessage(`Imported ${imported} code(s) from JSON.`);
    } catch (err: any) {
      setError(err.message ?? 'Code import failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const exportCodesJson = async () => {
    if (!projectId) return;
    setLoading(true);
    setCurrentAction('exportCodesJson');
    resetStatus();
    try {
      const data = await CodesApi.exportJson(projectId);
      downloadFile(JSON.stringify(data, null, 2), 'codes.json', 'application/json');
      setMessage(`Exported ${data?.length ?? 0} code(s) to JSON.`);
    } catch (err: any) {
      setError(err.message ?? 'Export failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const exportCodesCsv = async () => {
    if (!projectId) return;
    setLoading(true);
    setCurrentAction('exportCodesCsv');
    resetStatus();
    try {
      const data = await CodesApi.exportJson(projectId);
      const headers = ['Level1', 'Level2', 'Level3', 'Level4', 'Level5', 'Level6', 'Description'];
      const rows = data.map((c: any) =>
        [c.level1, c.level2, c.level3, c.level4 ?? '', c.level5 ?? '', c.level6 ?? '', c.description ?? ''].map((v) =>
          `"${String(v ?? '').replace(/"/g, '""')}"`
        ).join(',')
      );
      const csv = [headers.join(','), ...rows].join('\n');
      downloadFile(csv, 'codes.csv', 'text/csv');
      setMessage(`Exported ${data?.length ?? 0} code(s) to CSV.`);
    } catch (err: any) {
      setError(err.message ?? 'Export failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const importDocsCsv = async () => {
    if (!projectId) return;
    const text = await pickFileText({ accept: '.csv,text/csv' });
    if (!text) return;
    setLoading(true);
    setCurrentAction('importDocsCsv');
    resetStatus();
    try {
      const entries = parseDocumentsCsv(text);
      if (entries.length === 0) {
        setError('CSV did not contain any document rows.');
        return;
      }
      setMessage(`Importing ${entries.length} document(s)...`);
      const batchSize = 100;
      let imported = 0;
      const errors: string[] = [];
      for (let i = 0; i < entries.length; i += batchSize) {
        const batch = entries.slice(i, i + batchSize);
        const result = await DocumentsApi.importSimple(projectId, batch);
        imported += result?.imported ?? 0;
        if (Array.isArray(result?.errors)) {
          errors.push(...result.errors);
        }
      }
      setMessage(`Imported ${imported} document(s) from CSV${errors.length ? ` with ${errors.length} error(s)` : ''}.`);
      if (errors.length > 0) {
        setError(`First error: ${errors[0]}`);
      }
    } catch (err: any) {
      setError(err.message ?? 'Document import failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const importDocsJson = async () => {
    if (!projectId) return;
    const text = await pickFileText({ accept: '.json,application/json' });
    if (!text) return;
    setLoading(true);
    setCurrentAction('importDocsJson');
    resetStatus();
    try {
      const parsed = JSON.parse(text);
      const planned = Array.isArray(parsed) ? parsed.length : parsed?.entries?.length ?? 0;
      if (planned > 0) setMessage(`Importing ${planned} document(s)...`);
      const result = await DocumentsApi.importJson(projectId, parsed);
      const imported = result?.imported ?? planned;
      setMessage(`Imported ${imported} document(s) from JSON.`);
    } catch (err: any) {
      setError(err.message ?? 'Document import failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const exportDocsJson = async () => {
    if (!projectId) return;
    setLoading(true);
    setCurrentAction('exportDocsJson');
    resetStatus();
    try {
      const data = await DocumentsApi.exportJson(projectId);
      downloadFile(JSON.stringify(data, null, 2), 'documents.json', 'application/json');
      setMessage(`Exported ${data?.length ?? 0} document(s) to JSON.`);
    } catch (err: any) {
      setError(err.message ?? 'Export failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const exportDocsCsv = async () => {
    if (!projectId) return;
    setLoading(true);
    setCurrentAction('exportDocsCsv');
    resetStatus();
    try {
      const data = await DocumentsApi.exportJson(projectId);
      const headers = ['Code', 'FreeText', 'FileName', 'CreatedAtUtc'];
      const rows = data.map((d: any) =>
        [d.code, d.freeText ?? '', d.fileName ?? '', d.createdAtUtc ?? ''].map((v) =>
          `"${String(v ?? '').replace(/"/g, '""')}"`
        ).join(',')
      );
      const csv = [headers.join(','), ...rows].join('\n');
      downloadFile(csv, 'documents.csv', 'text/csv');
      setMessage(`Exported ${data?.length ?? 0} document(s) to CSV.`);
    } catch (err: any) {
      setError(err.message ?? 'Export failed');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const purgeDocuments = async () => {
    if (!projectId) return;
    const confirmed = window.confirm('This will permanently delete all documents in this project. Continue?');
    if (!confirmed) return;
    setLoading(true);
    setCurrentAction('purgeDocuments');
    resetStatus();
    try {
      const result = await DocumentsApi.purge(projectId);
      setMessage(`Deleted ${result.deleted} document(s).`);
    } catch (err: any) {
      setError(err.message ?? 'Failed to purge documents');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const purgeCodes = async () => {
    if (!projectId) return;
    const confirmed = window.confirm('Permanently delete all codes (and their documents). Continue?');
    if (!confirmed) return;
    setLoading(true);
    setCurrentAction('purgeCodes');
    resetStatus();
    try {
      const result = await CodesApi.purge(projectId);
      setMessage(`Deleted ${result.deletedCodes} code(s) and ${result.deletedDocuments} document(s).`);
    } catch (err: any) {
      setError(err.message ?? 'Failed to purge codes');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const purgeSeries = async () => {
    if (!projectId) return;
    const confirmed = window.confirm('Permanently delete all code series counters. Documents must be empty. Continue?');
    if (!confirmed) return;
    setLoading(true);
    setCurrentAction('purgeSeries');
    resetStatus();
    try {
      const result = await CodesApi.purgeSeries(projectId);
      setMessage(`Deleted ${result.deletedSeries} code series.`);
    } catch (err: any) {
      setError(err.message ?? 'Failed to purge code series');
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  const toggleSelectedDocument = (id: number) => {
    setSelectedDocIds((current) => {
      const next = new Set(current);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const deleteSelectedDocuments = async () => {
    if (!projectId || selectedDocIds.size === 0) return;
    const selectedDocs = cleanupDocs.filter((doc) => selectedDocIds.has(doc.id));
    const names = selectedDocs.map((doc) => doc.fileName).join('\n');
    const confirmed = window.confirm(`Permanently delete ${selectedDocs.length} selected document(s)?\n\n${names}`);
    if (!confirmed) return;

    setLoading(true);
    setCurrentAction('deleteSelectedDocuments');
    resetStatus();
    try {
      let deleted = 0;
      for (const doc of selectedDocs) {
        const result = await DocumentsApi.delete(projectId, doc.id);
        if (result.deleted) {
          deleted += 1;
        }
      }
      await loadCleanupDocuments();
      setMessage(`Deleted ${deleted} document(s). Numbering counters were recalculated for affected series.`);
    } catch (err: unknown) {
      setError(errorMessage(err, 'Failed to delete selected documents'));
    } finally {
      setLoading(false);
      setCurrentAction(null);
    }
  };

  return (
    <div className="page">
      <h1>Management</h1>
      <p className="muted">Import, export, and purge data for the current project.</p>
      {!projectId && <div className="pill">Select a project first.</div>}
      {message && <div className="pill" style={{ background: '#ecfdf3', color: '#166534' }}>{message}</div>}
      {error && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{error}</div>}

      {projectId && (
        <>
          <div className="card" style={{ marginTop: 12 }}>
            <strong>Import</strong>
            <p className="muted">Codes (CSV: Level,Code,Code Description)</p>
            <pre style={{ whiteSpace: 'pre-wrap', background: '#0f172a', color: '#e2e8f0', padding: 8, borderRadius: 6 }}>
{`Level,Code,Code Description
1,DFT,Delpach Family Trust
2,GOV,Governance
3,REG,Registers`}
            </pre>
            <div className="row" style={{ gap: 8 }}>
              <button onClick={importCodesCsv} disabled={loading}>Import Codes CSV</button>
              <button onClick={importCodesJson} disabled={loading}>Import Codes JSON</button>
            </div>
            <p className="muted" style={{ marginTop: 12 }}>Documents (CSV: Code,FreeText)</p>
            <pre style={{ whiteSpace: 'pre-wrap', background: '#0f172a', color: '#e2e8f0', padding: 8, borderRadius: 6 }}>
{`Code,FreeText
DFT-GOV-PLN-001,Governance Plan
MIC-GAI-BST-002,DocControl Scope`}
            </pre>
            <div className="row" style={{ gap: 8 }}>
              <button onClick={importDocsCsv} disabled={loading}>Import Documents CSV</button>
              <button onClick={importDocsJson} disabled={loading}>Import Documents JSON</button>
            </div>
          </div>

          <div className="card" style={{ marginTop: 12 }}>
            <strong>Export</strong>
            <p className="muted">Codes</p>
            <div className="row" style={{ gap: 8 }}>
              <button onClick={exportCodesCsv} disabled={loading}>Export Codes CSV</button>
              <button onClick={exportCodesJson} disabled={loading}>Export Codes JSON</button>
            </div>
            <p className="muted" style={{ marginTop: 12 }}>Documents</p>
            <div className="row" style={{ gap: 8 }}>
              <button onClick={exportDocsCsv} disabled={loading}>Export Documents CSV</button>
              <button onClick={exportDocsJson} disabled={loading}>Export Documents JSON</button>
            </div>
          </div>

          <div className="card" style={{ marginTop: 12 }}>
            <strong>Document cleanup</strong>
            <p className="muted" style={{ margin: '8px 0' }}>
              Search and delete incorrect document records. Deleting all records in a code series resets its next number to 001.
            </p>
            <div className="row" style={{ gap: 8, alignItems: 'center', marginBottom: 12 }}>
              <input
                value={cleanupQuery}
                onChange={(e) => setCleanupQuery(e.target.value)}
                placeholder="Search code, file name, or free text"
              />
              <button onClick={() => loadCleanupDocuments()} disabled={loading}>
                {loading && currentAction === 'loadCleanupDocuments' ? 'Loading...' : 'Search'}
              </button>
              <button
                onClick={() => {
                  setCleanupQuery('');
                  loadCleanupDocuments('');
                }}
                disabled={loading}
                style={{ background: '#334155', color: '#e2e8f0' }}
              >
                Clear
              </button>
              <button
                onClick={deleteSelectedDocuments}
                disabled={loading || selectedDocIds.size === 0}
                style={{ background: '#b91c1c' }}
              >
                {loading && currentAction === 'deleteSelectedDocuments'
                  ? 'Deleting...'
                  : `Delete selected (${selectedDocIds.size})`}
              </button>
            </div>
            <p className="muted" style={{ margin: '0 0 8px' }}>
              Showing {cleanupDocs.length} of {cleanupTotal} matching document(s).
            </p>
            {cleanupDocs.length === 0 ? (
              <p className="muted">No documents found.</p>
            ) : (
              <table className="table">
                <thead>
                  <tr>
                    <th>Select</th>
                    <th>File name</th>
                    <th>Free text</th>
                    <th>Created</th>
                  </tr>
                </thead>
                <tbody>
                  {cleanupDocs.map((doc) => (
                    <tr key={doc.id}>
                      <td>
                        <input
                          type="checkbox"
                          checked={selectedDocIds.has(doc.id)}
                          onChange={() => toggleSelectedDocument(doc.id)}
                        />
                      </td>
                      <td className="muted">{doc.fileName}</td>
                      <td className="muted">{doc.freeText ?? ''}</td>
                      <td className="muted">{new Date(doc.createdAtUtc).toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          <div className="card" style={{ marginTop: 12 }}>
            <strong>Purge</strong>
            <p className="muted" style={{ margin: '8px 0' }}>
              Purge removes data permanently for this project.
            </p>
            <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
              <button onClick={purgeDocuments} disabled={loading} style={{ background: '#b91c1c' }}>
                {loading && currentAction === 'purgeDocuments' ? 'Purging...' : 'Purge documents'}
              </button>
              <button onClick={purgeCodes} disabled={loading} style={{ background: '#b91c1c' }}>
                {loading && currentAction === 'purgeCodes' ? 'Purging...' : 'Purge codes'}
              </button>
              <button onClick={purgeSeries} disabled={loading} style={{ background: '#b91c1c' }}>
                {loading && currentAction === 'purgeSeries' ? 'Purging...' : 'Purge code series'}
              </button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
