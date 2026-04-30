export type HttpMethod = 'GET' | 'POST' | 'DELETE';

export type RegisteredUser = { id: number; email: string; displayName: string; createdAtUtc: string; mfaEnabled: boolean; requiresPasswordReset?: boolean; authToken?: string };
export type CurrentUser = { userId: number; email: string; displayName: string; mfaEnabled: boolean; hasPassword?: boolean };
export type LoginUser = { id: number; email: string; displayName: string; mfaEnabled: boolean; requiresPasswordReset?: boolean; authToken?: string };
export type ProfileUpdate = { displayName: string };

const API_BASE = import.meta.env.VITE_API_BASE ?? '/api';

const defaultHeaders = () => {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
  };

  const authMode = localStorage.getItem('dc.authMode');
  const authToken = localStorage.getItem('dc.authToken');
  const userId = localStorage.getItem('dc.userId');
  const email = localStorage.getItem('dc.email');
  const name = localStorage.getItem('dc.name');

  if (!import.meta.env.DEV && authMode === 'password') {
    if (authToken) {
      headers.Authorization = `Bearer ${authToken}`;
    }
    if (userId && !Number.isNaN(Number(userId)) && Number(userId) > 0) {
      headers['x-user-id'] = userId;
      if (email) headers['x-user-email'] = email;
      if (name) headers['x-user-name'] = name;
    } else if (!authToken) {
      throw new Error('Missing legacy session. Please sign in again.');
    }
  } else if (import.meta.env.DEV) {
    if (!userId || Number.isNaN(Number(userId)) || Number(userId) <= 0) {
      throw new Error('User not registered. Please sign up first.');
    }

    headers['x-user-id'] = userId;
    if (email) headers['x-user-email'] = email;
    if (name) headers['x-user-name'] = name;
  }

  return headers;
};

export async function api<T>(path: string, method: HttpMethod = 'GET', body?: unknown, opts?: { skipAuth?: boolean }): Promise<T> {
  const headers = opts?.skipAuth ? { 'Content-Type': 'application/json' } : defaultHeaders();

  const res = await fetch(`${API_BASE}${path}`, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined,
  });

  if (!res.ok) {
    const text = await res.text();
    throw new Error(`${res.status} ${res.statusText}: ${text}`);
  }
  if (res.status === 204) return undefined as T;
  return res.json();
}

export const ProjectsApi = {
  list: () => api<any[]>('/projects'),
  create: (
    name: string,
    description: string,
    separator: string,
    paddingLength: number,
    levelCount: number,
    levelLabels: string[],
    levelLengths: number[],
  ) =>
    api<any>('/projects', 'POST', {
      name,
      description,
      separator,
      paddingLength,
      levelCount,
      level1Label: levelLabels[0],
      level2Label: levelLabels[1],
      level3Label: levelLabels[2],
      level4Label: levelLabels[3],
      level5Label: levelLabels[4],
      level6Label: levelLabels[5],
      level1Length: levelLengths[0],
      level2Length: levelLengths[1],
      level3Length: levelLengths[2],
      level4Length: levelLengths[3],
      level5Length: levelLengths[4],
      level6Length: levelLengths[5],
    }),
  get: (projectId: number) => api<any>(`/projects/${projectId}`),
  update: (projectId: number, name: string, description: string) =>
    api<any>(`/projects/${projectId}`, 'POST', { name, description }),
  setDefault: (projectId: number) => api<any>(`/projects/${projectId}/default`, 'POST'),
};

export const AuthApi = {
  register: (email: string, displayName: string, password: string) =>
    api<RegisteredUser>('/auth/register', 'POST', { email, displayName, password }, { skipAuth: true }),
  login: (email: string, password: string) =>
    api<LoginUser>('/auth/login', 'POST', { email, password }, { skipAuth: true }),
  setInitialPassword: (email: string, password: string) =>
    api<LoginUser>('/auth/password/initial', 'POST', { email, password }, { skipAuth: true }),
  changePassword: (currentPassword: string, newPassword: string) =>
    api<any>('/auth/password/change', 'POST', { currentPassword, newPassword }),
  updateProfile: (displayName: string) =>
    api<ProfileUpdate>('/auth/profile', 'POST', { displayName }),
  me: () => api<CurrentUser>('/auth/me'),
  startMfa: () => api<{ secret: string; otpauthUrl: string }>('/auth/mfa/start', 'POST'),
  verifyMfa: (code: string) => api<{ mfaEnabled: boolean }>('/auth/mfa/verify', 'POST', { code }),
  backupCodes: () => api<{ codes: string[] }>('/auth/mfa/backup', 'POST'),
  linkLegacy: (legacyEmail: string, password: string, mfaCode: string) =>
    api<any>('/auth/link', 'POST', { legacyEmail, password, mfaCode }),
};

export const CodesApi = {
  list: (projectId: number) => api<any[]>(`/projects/${projectId}/codes`),
  listSeries: (projectId: number) => api<any[]>(`/projects/${projectId}/series`),
  upsert: (
    projectId: number,
    payload: {
      level1: string;
      level2: string;
      level3: string;
      level4?: string | null;
      level5?: string | null;
      level6?: string | null;
      description?: string | null;
    },
  ) => api<any>(`/projects/${projectId}/codes`, 'POST', payload),
  importCsv: async (projectId: number, csv: string) => {
    const headers = defaultHeaders();
    headers['Content-Type'] = 'text/csv';

    const res = await fetch(`${API_BASE}/projects/${projectId}/codes/import`, {
      method: 'POST',
      headers,
      body: csv,
    });

    if (!res.ok) {
      const text = await res.text();
      throw new Error(`${res.status} ${res.statusText}: ${text}`);
    }

    return res.json();
  },
  importJson: (projectId: number, codes: any[]) => api<any>(`/projects/${projectId}/codes/import/json`, 'POST', codes),
  exportJson: (projectId: number) => api<any[]>(`/projects/${projectId}/codes/export`),
  purge: (projectId: number) => api<{ deletedDocuments: number; deletedCodes: number }>(`/projects/${projectId}/codes/purge`, 'DELETE'),
  purgeSeries: (projectId: number) => api<{ deletedSeries: number }>(`/projects/${projectId}/series/purge`, 'DELETE'),
};

export const MembersApi = {
  list: (projectId: number) => api<any[]>(`/projects/${projectId}/members`),
  invite: (projectId: number, email: string, role: string, daysValid = 7) =>
    api<any>(`/projects/${projectId}/invites`, 'POST', { email, role, daysValid }),
  remove: (projectId: number, userId: number) => api<any>(`/projects/${projectId}/members/${userId}`, 'DELETE'),
  changeRole: (projectId: number, userId: number, role: string) =>
    api<any>(`/projects/${projectId}/members/${userId}/role`, 'POST', { role }),
  accept: (token: string) => api<any>(`/invites/accept`, 'POST', { token }),
  pendingInvites: (projectId: number) => api<any[]>(`/projects/${projectId}/invites`),
  cancelInvite: (projectId: number, inviteId: number) => api<any>(`/projects/${projectId}/invites/${inviteId}`, 'DELETE'),
};

export const DocumentsApi = {
  list: (projectId: number, opts?: { q?: string; take?: number; skip?: number }) => {
    const params = new URLSearchParams();
    if (opts?.q) params.set('q', opts.q);
    if (opts?.take) params.set('take', String(opts.take));
    if (opts?.skip) params.set('skip', String(opts.skip));
    const suffix = params.toString();
    return api<any[] | { items: any[]; total: number; skip: number; take: number }>(
      `/projects/${projectId}/documents${suffix ? `?${suffix}` : ''}`
    );
  },
  create: (
    projectId: number,
    payload: {
      level1: string;
      level2: string;
      level3: string;
      level4?: string;
      level5?: string;
      level6?: string;
      freeText?: string;
      extension?: string;
    },
  ) => api<any>(`/projects/${projectId}/documents`, 'POST', payload),
  preview: (
    projectId: number,
    payload: {
      level1: string;
      level2: string;
      level3: string;
      level4?: string;
      level5?: string;
      level6?: string;
      freeText?: string;
      extension?: string;
    },
  ) => api<{ number: number; fileName: string }>(`/projects/${projectId}/documents/preview`, 'POST', payload),
  importCsv: async (projectId: number, csv: string) => {
    const headers = defaultHeaders();
    headers['Content-Type'] = 'text/csv';
    const res = await fetch(`${API_BASE}/projects/${projectId}/documents/import/csv`, {
      method: 'POST',
      headers,
      body: csv,
    });
    if (!res.ok) {
      const text = await res.text();
      throw new Error(`${res.status} ${res.statusText}: ${text}`);
    }
    return res.json();
  },
  importJson: (projectId: number, entries: any[]) =>
    api<any>(`/projects/${projectId}/documents/import/json`, 'POST', entries),
  importSimple: (
    projectId: number,
    entries: { code: string; fileName?: string; freeText?: string; description?: string }[],
  ) => api<any>(`/projects/${projectId}/documents/import`, 'POST', { entries }),
  exportJson: (projectId: number) => api<any[]>(`/projects/${projectId}/documents/export`),
  purge: (projectId: number) => api<{ deleted: number }>(`/projects/${projectId}/documents`, 'DELETE'),
};

export const AuditApi = {
  list: (projectId: number, take = 50) => api<any[]>(`/projects/${projectId}/audit?take=${take}`),
};

export const SettingsApi = {
  get: (projectId: number) => api<any>(`/projects/${projectId}/settings`),
  save: (projectId: number, payload: any) => api<any>(`/projects/${projectId}/settings`, 'POST', payload),
};

export const AiApi = {
  interpret: (projectId: number, query: string) =>
    api<any>(`/projects/${projectId}/ai/interpret`, 'POST', { query }),
  recommend: (projectId: number, query: string) =>
    api<any>(`/projects/${projectId}/ai/recommend`, 'POST', { query }),
};
