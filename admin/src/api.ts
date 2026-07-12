import type { AdminUser, ApprovalFilter, Overview, PagedUsers, UserSort } from './types';

const TOKEN_KEY = 'vass_admin_token';
const API_BASE = (import.meta.env.VITE_API_URL as string | undefined)?.replace(/\/$/, '') ?? '';

export class ApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message);
  }
}

export function getToken(): string | null {
  return sessionStorage.getItem(TOKEN_KEY);
}

export function clearToken(): void {
  sessionStorage.removeItem(TOKEN_KEY);
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = getToken();
  const response = await fetch(`${API_BASE}/api/v1${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init.headers,
    },
  });

  if (response.status === 401) clearToken();
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    const fallback = response.status === 403 ? 'Недостаточно прав администратора' : `HTTP ${response.status}`;
    throw new ApiError(body.error ?? body.title ?? fallback, response.status);
  }

  return response.status === 204 ? (undefined as T) : response.json();
}

export async function login(email: string, password: string): Promise<void> {
  const response = await fetch(`${API_BASE}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new ApiError(body.error ?? 'Не удалось войти', response.status);
  }
  const body = (await response.json()) as { token: string };
  sessionStorage.setItem(TOKEN_KEY, body.token);
}

export const adminApi = {
  overview: () => request<Overview>('/admin/overview'),
  users: (params: {
    search: string;
    status: ApprovalFilter;
    sort: UserSort;
    page: number;
    pageSize: number;
  }) => {
    const query = new URLSearchParams({
      search: params.search,
      status: params.status,
      sort: params.sort,
      page: String(params.page),
      pageSize: String(params.pageSize),
    });
    return request<PagedUsers>(`/admin/users?${query}`);
  },
  setApproval: (id: string, isApproved: boolean) =>
    request<AdminUser>(`/admin/users/${encodeURIComponent(id)}/approval`, {
      method: 'PATCH',
      body: JSON.stringify({ isApproved }),
    }),
};
