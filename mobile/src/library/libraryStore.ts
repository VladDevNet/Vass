import { Directory, File, Paths } from 'expo-file-system';
import { sanitizeLibraryHtml } from './libraryHtml';
import {
  isLibraryKind,
  type LibraryArtifact,
  type LibraryArtifactDraft,
  type LibraryCatalogEntry,
  type LibraryIndex,
  type LibraryKind,
  type LibraryRevision,
} from './types';

const ROOT = new Directory(Paths.document, 'vass-library');
const ARTIFACTS = new Directory(ROOT, 'artifacts');
const INDEX = new File(ROOT, 'index.v1.json');
const MAX_ARTIFACTS_EXPOSED_TO_ASSISTANT = 20;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

let writeQueue: Promise<void> = Promise.resolve();

function emptyIndex(): LibraryIndex {
  return { schemaVersion: 1, artifacts: [] };
}

function normalizeText(value: unknown, maxLength: number): string {
  return typeof value === 'string'
    ? value.replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, maxLength)
    : '';
}

function normalizeSourceUrls(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  const unique = new Set<string>();
  for (const candidate of value) {
    const url = normalizeText(candidate, 2_000);
    if (/^https:\/\/[^\s]+$/i.test(url)) unique.add(url);
    if (unique.size >= 12) break;
  }
  return [...unique];
}

function normalizeKind(value: unknown): LibraryKind {
  return isLibraryKind(value) ? value : 'other';
}

function normalizeRevision(value: unknown): LibraryRevision | null {
  if (!value || typeof value !== 'object') return null;
  const source = value as Record<string, unknown>;
  const id = normalizeText(source.id, 64);
  const createdAt = normalizeText(source.createdAt, 64);
  if (!UUID.test(id) || !createdAt) return null;
  return {
    id,
    createdAt,
    note: normalizeText(source.note, 220) || null,
    byteLength: typeof source.byteLength === 'number' && Number.isFinite(source.byteLength)
      ? Math.max(0, Math.floor(source.byteLength))
      : 0,
  };
}

function normalizeArtifact(value: unknown): LibraryArtifact | null {
  if (!value || typeof value !== 'object') return null;
  const source = value as Record<string, unknown>;
  const id = normalizeText(source.id, 64);
  const title = normalizeText(source.title, 120);
  const createdAt = normalizeText(source.createdAt, 64);
  const updatedAt = normalizeText(source.updatedAt, 64);
  const currentRevisionId = normalizeText(source.currentRevisionId, 64);
  const revisions = Array.isArray(source.revisions)
    ? source.revisions.map(normalizeRevision).filter((revision): revision is LibraryRevision => revision !== null).slice(-50)
    : [];
  if (!UUID.test(id) || !title || !createdAt || !updatedAt || !UUID.test(currentRevisionId) || revisions.length === 0) return null;
  if (!revisions.some((revision) => revision.id === currentRevisionId)) return null;
  return {
    id,
    title,
    kind: normalizeKind(source.kind),
    summary: normalizeText(source.summary, 600),
    createdAt,
    updatedAt,
    currentRevisionId,
    sourceUrls: normalizeSourceUrls(source.sourceUrls),
    revisions,
    revisionCount: revisions.length,
  };
}

function createId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const randomHex = () => Math.floor(Math.random() * 0x1_0000).toString(16).padStart(4, '0');
  return `${randomHex()}${randomHex()}-${randomHex()}-4${randomHex().slice(1)}-a${randomHex().slice(1)}-${randomHex()}${randomHex()}${randomHex()}`;
}

function artifactDirectory(id: string): Directory {
  return new Directory(ARTIFACTS, id);
}

function revisionFile(artifactId: string, revisionId: string): File {
  return new File(artifactDirectory(artifactId), `${revisionId}.html`);
}

function ensureDirectories(): void {
  ROOT.create({ intermediates: true, idempotent: true });
  ARTIFACTS.create({ intermediates: true, idempotent: true });
}

async function readIndex(): Promise<LibraryIndex> {
  ensureDirectories();
  if (!INDEX.exists) return emptyIndex();
  try {
    const parsed = JSON.parse(await INDEX.text()) as { schemaVersion?: unknown; artifacts?: unknown };
    if (parsed.schemaVersion !== 1 || !Array.isArray(parsed.artifacts)) return emptyIndex();
    const artifacts = parsed.artifacts
      .map(normalizeArtifact)
      .filter((artifact): artifact is LibraryArtifact => artifact !== null);
    return { schemaVersion: 1, artifacts };
  } catch {
    // A document should remain recoverable even if a previous app kill caught
    // the tiny metadata index mid-write. The revision files themselves are not
    // touched here, so a future repair/import flow can still inspect them.
    return emptyIndex();
  }
}

async function writeIndex(index: LibraryIndex): Promise<void> {
  ensureDirectories();
  // move() mutates the File object's URI, so the temporary handle must be
  // created for each write instead of being retained as a module constant.
  const temporary = new File(ROOT, 'index.next.json');
  temporary.create({ intermediates: true, overwrite: true });
  temporary.write(JSON.stringify(index));
  await temporary.move(INDEX, { overwrite: true });
}

function enqueueWrite<T>(operation: () => Promise<T>): Promise<T> {
  const result = writeQueue.then(operation, operation);
  writeQueue = result.then(() => undefined, () => undefined);
  return result;
}

function toCatalogEntry(artifact: LibraryArtifact): LibraryCatalogEntry {
  return {
    id: artifact.id,
    title: artifact.title,
    kind: artifact.kind,
    summary: artifact.summary,
    updatedAt: artifact.updatedAt,
    revisionCount: artifact.revisions.length,
  };
}

export async function listLibraryArtifacts(): Promise<LibraryArtifact[]> {
  const index = await readIndex();
  return [...index.artifacts].sort((left, right) => right.updatedAt.localeCompare(left.updatedAt));
}

export async function getLibraryCatalogForAssistant(): Promise<LibraryCatalogEntry[]> {
  const items = await listLibraryArtifacts();
  return items.slice(0, MAX_ARTIFACTS_EXPOSED_TO_ASSISTANT).map(toCatalogEntry);
}

export async function readLibraryArtifactHtml(artifactId: string): Promise<string | null> {
  if (!UUID.test(artifactId)) return null;
  const index = await readIndex();
  const artifact = index.artifacts.find((item) => item.id === artifactId);
  if (!artifact) return null;
  return readLibraryArtifactRevisionHtml(artifact.id, artifact.currentRevisionId, artifact);
}

export async function readLibraryArtifactRevisionHtml(
  artifactId: string,
  revisionId: string,
  knownArtifact?: LibraryArtifact,
): Promise<string | null> {
  if (!UUID.test(artifactId) || !UUID.test(revisionId)) return null;
  const artifact = knownArtifact ?? (await readIndex()).artifacts.find((item) => item.id === artifactId);
  if (!artifact || !artifact.revisions.some((revision) => revision.id === revisionId)) return null;
  const file = revisionFile(artifact.id, revisionId);
  return file.exists ? file.text() : null;
}

export async function saveLibraryArtifact(draft: LibraryArtifactDraft): Promise<{ artifact: LibraryArtifact; created: boolean }> {
  return enqueueWrite(async () => {
    const html = sanitizeLibraryHtml(draft.html);
    const title = normalizeText(draft.title, 120);
    if (!title) throw new Error('У книги должно быть название.');

    const index = await readIndex();
    const existing = draft.artifactId && UUID.test(draft.artifactId)
      ? index.artifacts.find((item) => item.id === draft.artifactId)
      : undefined;
    const now = new Date().toISOString();
    const artifactId = existing?.id ?? createId();
    const revisionId = createId();
    const directory = artifactDirectory(artifactId);
    directory.create({ intermediates: true, idempotent: true });
    const file = revisionFile(artifactId, revisionId);
    file.create({ intermediates: true, overwrite: false });
    file.write(html);

    const revision: LibraryRevision = {
      id: revisionId,
      createdAt: now,
      note: normalizeText(draft.revisionNote, 220) || null,
      byteLength: html.length,
    };
    const revisions = [...(existing?.revisions ?? []), revision].slice(-50);
    const artifact: LibraryArtifact = {
      id: artifactId,
      title,
      kind: normalizeKind(draft.kind),
      summary: normalizeText(draft.summary, 600),
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
      currentRevisionId: revisionId,
      sourceUrls: normalizeSourceUrls(draft.sourceUrls),
      revisions,
      revisionCount: revisions.length,
    };

    const next: LibraryIndex = {
      schemaVersion: 1,
      artifacts: existing
        ? index.artifacts.map((item) => item.id === existing.id ? artifact : item)
        : [artifact, ...index.artifacts],
    };
    await writeIndex(next);
    return { artifact, created: !existing };
  });
}

export async function deleteLibraryArtifact(artifactId: string): Promise<boolean> {
  if (!UUID.test(artifactId)) return false;
  return enqueueWrite(async () => {
    const index = await readIndex();
    if (!index.artifacts.some((artifact) => artifact.id === artifactId)) return false;
    await writeIndex({ schemaVersion: 1, artifacts: index.artifacts.filter((artifact) => artifact.id !== artifactId) });
    const directory = artifactDirectory(artifactId);
    try {
      if (directory.exists) directory.delete();
    } catch {
      // The index is already authoritative. A later cleanup can remove an
      // orphaned local revision directory without reviving a deleted book.
    }
    return true;
  });
}
