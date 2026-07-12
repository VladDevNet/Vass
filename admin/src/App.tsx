import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertCircle,
  ChevronLeft,
  ChevronRight,
  Clock3,
  Gauge,
  LogOut,
  MessageSquareText,
  RefreshCw,
  Search,
  ShieldCheck,
  ShieldOff,
  Type,
  Users,
  X,
} from 'lucide-react';
import { adminApi, ApiError, clearToken, getToken, login } from './api';
import type { AdminUser, ApprovalFilter, Overview, PagedUsers, UserSort } from './types';

const PAGE_SIZE = 25;

function formatNumber(value: number): string {
  return new Intl.NumberFormat('ru-RU').format(value);
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value));
}

function relativeTime(value: string): string {
  const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000);
  const formatter = new Intl.RelativeTimeFormat('ru', { numeric: 'auto' });
  const ranges: Array<[number, Intl.RelativeTimeFormatUnit]> = [
    [60, 'second'],
    [60, 'minute'],
    [24, 'hour'],
    [30, 'day'],
    [12, 'month'],
  ];
  let amount = seconds;
  for (const [limit, unit] of ranges) {
    if (Math.abs(amount) < limit) return formatter.format(Math.round(amount), unit);
    amount /= limit;
  }
  return formatter.format(Math.round(amount), 'year');
}

function isOnline(value: string): boolean {
  return Date.now() - new Date(value).getTime() < 5 * 60 * 1000;
}

function LoginScreen({ onAuthenticated }: { onAuthenticated: () => void }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await login(email.trim(), password);
      await adminApi.overview();
      onAuthenticated();
    } catch (err) {
      clearToken();
      setError(err instanceof Error ? err.message : 'Не удалось войти');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="login-page">
      <section className="login-panel" aria-labelledby="login-title">
        <div className="brand brand-dark">
          <img src="/admin/vass-icon.png" alt="" />
          <div>
            <strong>Vass</strong>
            <span>Control</span>
          </div>
        </div>
        <div className="login-heading">
          <p className="eyebrow">Администрирование</p>
          <h1 id="login-title">Вход в панель</h1>
          <p>Используйте учетную запись с ролью Admin.</p>
        </div>
        <form onSubmit={submit} className="login-form">
          <label>
            Email
            <input type="email" value={email} onChange={(event) => setEmail(event.target.value)} autoComplete="username" required autoFocus />
          </label>
          <label>
            Пароль
            <input type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="current-password" required />
          </label>
          {error && <div className="inline-error"><AlertCircle size={17} />{error}</div>}
          <button className="primary-button" type="submit" disabled={submitting}>
            {submitting ? <RefreshCw className="spin" size={18} /> : <ShieldCheck size={18} />}
            Войти
          </button>
        </form>
      </section>
    </main>
  );
}

function StatCard({ icon, label, value, detail, tone }: {
  icon: React.ReactNode;
  label: string;
  value: string;
  detail: string;
  tone: 'blue' | 'green' | 'amber' | 'violet';
}) {
  return (
    <article className={`stat-card stat-${tone}`}>
      <div className="stat-icon">{icon}</div>
      <div>
        <span>{label}</span>
        <strong>{value}</strong>
        <small>{detail}</small>
      </div>
    </article>
  );
}

function ConfirmDialog({ user, onCancel, onConfirm, busy }: {
  user: AdminUser;
  onCancel: () => void;
  onConfirm: () => void;
  busy: boolean;
}) {
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && onCancel()}>
      <section className="modal" role="dialog" aria-modal="true" aria-labelledby="confirm-title">
        <button className="icon-button modal-close" onClick={onCancel} aria-label="Закрыть"><X size={19} /></button>
        <div className="danger-icon"><ShieldOff size={24} /></div>
        <h2 id="confirm-title">Ограничить доступ?</h2>
        <p><strong>{user.email}</strong> будет немедленно отключен. Все выданные ему JWT перестанут работать.</p>
        <div className="modal-actions">
          <button className="secondary-button" onClick={onCancel} disabled={busy}>Отмена</button>
          <button className="danger-button" onClick={onConfirm} disabled={busy}>
            {busy && <RefreshCw className="spin" size={17} />}
            Ограничить
          </button>
        </div>
      </section>
    </div>
  );
}

function Dashboard({ onLogout }: { onLogout: () => void }) {
  const [overview, setOverview] = useState<Overview | null>(null);
  const [users, setUsers] = useState<PagedUsers | null>(null);
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<ApprovalFilter>('all');
  const [sort, setSort] = useState<UserSort>('lastActiveDesc');
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [blockTarget, setBlockTarget] = useState<AdminUser | null>(null);
  const [mutationId, setMutationId] = useState<string | null>(null);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setSearch(searchInput.trim());
      setPage(1);
    }, 300);
    return () => window.clearTimeout(timer);
  }, [searchInput]);

  const loadData = useCallback(async (showLoader = true) => {
    if (showLoader) setLoading(true);
    setError(null);
    try {
      const [nextOverview, nextUsers] = await Promise.all([
        adminApi.overview(),
        adminApi.users({ search, status: filter, sort, page, pageSize: PAGE_SIZE }),
      ]);
      setOverview(nextOverview);
      setUsers(nextUsers);
      setLastUpdated(new Date());
    } catch (err) {
      if (err instanceof ApiError && (err.status === 401 || err.status === 403)) {
        onLogout();
        return;
      }
      setError(err instanceof Error ? err.message : 'Не удалось загрузить данные');
    } finally {
      setLoading(false);
    }
  }, [filter, onLogout, page, search, sort]);

  useEffect(() => { void loadData(); }, [loadData]);

  async function setApproval(user: AdminUser, approved: boolean) {
    setMutationId(user.id);
    try {
      const updated = await adminApi.setApproval(user.id, approved);
      setUsers((current) => current ? {
        ...current,
        items: current.items.map((item) => item.id === updated.id ? updated : item),
      } : current);
      setBlockTarget(null);
      await loadData(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось изменить доступ');
    } finally {
      setMutationId(null);
    }
  }

  const rangeLabel = useMemo(() => {
    if (!users || users.totalCount === 0) return '0 пользователей';
    const start = (users.page - 1) * users.pageSize + 1;
    const end = Math.min(users.page * users.pageSize, users.totalCount);
    return `${start}–${end} из ${formatNumber(users.totalCount)}`;
  }, [users]);

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <img src="/admin/vass-icon.png" alt="" />
          <div><strong>Vass</strong><span>Control</span></div>
        </div>
        <nav aria-label="Основная навигация">
          <button className="nav-item nav-item-active"><Users size={19} />Пользователи</button>
        </nav>
        <div className="sidebar-footer">
          <div className="service-status"><span />API подключен</div>
          <button className="logout-button" onClick={onLogout}><LogOut size={18} />Выйти</button>
        </div>
      </aside>

      <main className="content">
        <header className="page-header">
          <div>
            <p className="eyebrow">Бета-контроль</p>
            <h1>Пользователи</h1>
            <p>Регистрации, активность и объем диалогов.</p>
          </div>
          <div className="header-actions">
            {lastUpdated && <span className="updated-at"><Clock3 size={15} />{lastUpdated.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}</span>}
            <button className="secondary-button" onClick={() => void loadData(false)} disabled={loading} title="Обновить данные">
              <RefreshCw className={loading ? 'spin' : ''} size={17} />Обновить
            </button>
          </div>
        </header>

        {error && <div className="page-error"><AlertCircle size={18} /><span>{error}</span><button onClick={() => setError(null)} aria-label="Закрыть"><X size={17} /></button></div>}

        <section className="stats-grid" aria-label="Сводка">
          <StatCard icon={<Users size={21} />} label="Пользователи" value={overview ? formatNumber(overview.totalUsers) : '—'} detail={overview ? `${overview.pendingUsers} ожидают решения` : 'Загрузка'} tone="blue" />
          <StatCard icon={<Activity size={21} />} label="Активны за 24 часа" value={overview ? formatNumber(overview.activeLast24Hours) : '—'} detail={overview ? `${overview.activeLast7Days} за 7 дней` : 'Загрузка'} tone="green" />
          <StatCard icon={<MessageSquareText size={21} />} label="Сообщения" value={overview ? formatNumber(overview.totalMessages) : '—'} detail="Во всех диалогах" tone="amber" />
          <StatCard icon={<Type size={21} />} label="Символы" value={overview ? formatNumber(overview.totalCharacters) : '—'} detail="Пользователь + ассистент" tone="violet" />
        </section>

        <section className="table-panel">
          <div className="toolbar">
            <div className="search-box"><Search size={18} /><input value={searchInput} onChange={(event) => setSearchInput(event.target.value)} placeholder="Поиск по email" aria-label="Поиск пользователей" /></div>
            <div className="segmented" aria-label="Фильтр доступа">
              {(['all', 'approved', 'pending'] as ApprovalFilter[]).map((value) => (
                <button key={value} className={filter === value ? 'active' : ''} onClick={() => { setFilter(value); setPage(1); }}>
                  {value === 'all' ? 'Все' : value === 'approved' ? 'Разрешены' : 'Ожидают'}
                </button>
              ))}
            </div>
            <label className="sort-control">
              <Gauge size={17} />
              <select value={sort} onChange={(event) => { setSort(event.target.value as UserSort); setPage(1); }} aria-label="Сортировка">
                <option value="lastActiveDesc">Недавняя активность</option>
                <option value="createdDesc">Сначала новые</option>
                <option value="createdAsc">Сначала старые</option>
                <option value="messagesDesc">Больше сообщений</option>
                <option value="emailAsc">Email A–Я</option>
              </select>
            </label>
          </div>

          <div className="table-scroll">
            <table>
              <thead><tr><th>Пользователь</th><th>Доступ</th><th>Последняя активность</th><th className="numeric">Сообщения</th><th className="numeric">Символы</th><th>Регистрация</th><th><span className="sr-only">Действие</span></th></tr></thead>
              <tbody>
                {loading && !users ? Array.from({ length: 6 }).map((_, index) => <tr className="skeleton-row" key={index}><td colSpan={7}><span /></td></tr>) : null}
                {!loading && users?.items.length === 0 ? <tr><td colSpan={7} className="empty-state">Пользователи не найдены</td></tr> : null}
                {users?.items.map((user) => (
                  <tr key={user.id}>
                    <td><div className="user-cell"><div className="avatar-initial">{user.email.slice(0, 1).toUpperCase()}</div><div><strong>{user.email}</strong><span>{user.id.slice(0, 8)}</span></div></div></td>
                    <td><span className={`status-badge ${user.isApproved ? 'approved' : 'pending'}`}>{user.isApproved ? <ShieldCheck size={14} /> : <ShieldOff size={14} />}{user.isApproved ? 'Разрешен' : 'Ожидает'}</span>{user.isAdmin && <span className="admin-badge">Admin</span>}</td>
                    <td><div className="activity-cell"><span className={isOnline(user.lastActiveAt) ? 'online-dot' : 'offline-dot'} /> <strong>{relativeTime(user.lastActiveAt)}</strong><small>{formatDate(user.lastActiveAt)}</small></div></td>
                    <td className="numeric metric-cell">{formatNumber(user.messageCount)}</td>
                    <td className="numeric metric-cell">{formatNumber(user.characterCount)}</td>
                    <td className="date-cell">{formatDate(user.createdAt)}</td>
                    <td className="action-cell">
                      {user.isAdmin ? <span className="protected-label">Защищен</span> : user.isApproved ? (
                        <button className="row-action restrict" onClick={() => setBlockTarget(user)} disabled={mutationId === user.id}><ShieldOff size={16} />Ограничить</button>
                      ) : (
                        <button className="row-action approve" onClick={() => void setApproval(user, true)} disabled={mutationId === user.id}>{mutationId === user.id ? <RefreshCw className="spin" size={16} /> : <ShieldCheck size={16} />}Разрешить</button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <footer className="pagination">
            <span>{rangeLabel}</span>
            <div>
              <button className="icon-button" onClick={() => setPage((value) => Math.max(1, value - 1))} disabled={!users || users.page <= 1} aria-label="Предыдущая страница"><ChevronLeft size={18} /></button>
              <span>Страница {users?.page ?? 1} из {users?.totalPages ?? 1}</span>
              <button className="icon-button" onClick={() => setPage((value) => Math.min(users?.totalPages ?? value, value + 1))} disabled={!users || users.page >= users.totalPages} aria-label="Следующая страница"><ChevronRight size={18} /></button>
            </div>
          </footer>
        </section>
      </main>

      {blockTarget && <ConfirmDialog user={blockTarget} busy={mutationId === blockTarget.id} onCancel={() => setBlockTarget(null)} onConfirm={() => void setApproval(blockTarget, false)} />}
    </div>
  );
}

export default function App() {
  const [authenticated, setAuthenticated] = useState(() => Boolean(getToken()));

  const logout = useCallback(() => {
    clearToken();
    setAuthenticated(false);
  }, []);

  return authenticated
    ? <Dashboard onLogout={logout} />
    : <LoginScreen onAuthenticated={() => setAuthenticated(true)} />;
}
