import { Directory, File, Paths } from 'expo-file-system';
import { sanitizeLibraryHtml } from './libraryHtml';
import {
  isLibraryKind,
  type LibraryArtifact,
  type LibraryArtifactDraft,
  type LibraryAssistantCatalog,
  type LibraryCatalogEntry,
  type LibraryCatalogSection,
  type LibraryIndex,
  type LibraryKind,
  type LibraryRevision,
  type LibrarySection,
} from './types';

const ROOT = new Directory(Paths.document, 'vass-library');
const ARTIFACTS = new Directory(ROOT, 'artifacts');
const INDEX = new File(ROOT, 'index.v2.json');
const LEGACY_INDEX = new File(ROOT, 'index.v1.json');
const MAX_ARTIFACTS_EXPOSED_TO_ASSISTANT = 20;
const MAX_SECTIONS_EXPOSED_TO_ASSISTANT = 30;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

const DEFAULT_SECTION_BY_KIND: Record<LibraryKind, { id: string; title: string }> = {
  recipes: { id: '1c6b5221-0651-4ff1-8ef1-2ac0e205dca1', title: 'Кулинария' },
  restaurants: { id: '1c6b5222-0651-4ff1-8ef1-2ac0e205dca1', title: 'Гастрономия' },
  entertainment: { id: '1c6b5223-0651-4ff1-8ef1-2ac0e205dca1', title: 'Развлечения' },
  guide: { id: '1c6b5224-0651-4ff1-8ef1-2ac0e205dca1', title: 'Путешествия и гиды' },
  other: { id: '1c6b5225-0651-4ff1-8ef1-2ac0e205dca1', title: 'Без раздела' },
};

let writeQueue: Promise<void> = Promise.resolve();

function emptyIndex(): LibraryIndex {
  return { schemaVersion: 2, sections: [], artifacts: [] };
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

function normalizeSectionTitle(value: unknown): string {
  return normalizeText(value, 60);
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
  const sectionId = normalizeText(source.sectionId, 64);
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
    sectionId: UUID.test(sectionId) ? sectionId : '',
    sectionTitle: normalizeSectionTitle(source.sectionTitle),
    createdAt,
    updatedAt,
    currentRevisionId,
    sourceUrls: normalizeSourceUrls(source.sourceUrls),
    revisions,
    revisionCount: revisions.length,
  };
}

function normalizeSection(value: unknown): LibrarySection | null {
  if (!value || typeof value !== 'object') return null;
  const source = value as Record<string, unknown>;
  const id = normalizeText(source.id, 64);
  const title = normalizeSectionTitle(source.title);
  const createdAt = normalizeText(source.createdAt, 64);
  const updatedAt = normalizeText(source.updatedAt, 64);
  if (!UUID.test(id) || !title || !createdAt || !updatedAt) return null;
  return { id, title, createdAt, updatedAt };
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

function sameTitle(left: string, right: string): boolean {
  return left.localeCompare(right, 'ru-RU', { sensitivity: 'accent' }) === 0;
}

function findSectionByTitle(sections: LibrarySection[], title: string, exceptId?: string): LibrarySection | undefined {
  return sections.find((section) => section.id !== exceptId && sameTitle(section.title, title));
}

function createSection(title: string, now: string, id = createId()): LibrarySection {
  return { id, title, createdAt: now, updatedAt: now };
}

function defaultSectionFor(kind: LibraryKind, now: string): LibrarySection {
  const seed = DEFAULT_SECTION_BY_KIND[kind];
  return createSection(seed.title, now, seed.id);
}

function normalizeIndex(value: unknown): LibraryIndex | null {
  if (!value || typeof value !== 'object') return null;
  const source = value as Record<string, unknown>;
  if (!Array.isArray(source.artifacts)) return null;

  const artifacts = source.artifacts
    .map(normalizeArtifact)
    .filter((artifact): artifact is LibraryArtifact => artifact !== null);
  const sections = Array.isArray(source.sections)
    ? source.sections
      .map(normalizeSection)
      .filter((section): section is LibrarySection => section !== null)
    : [];
  const byId = new Map<string, LibrarySection>();
  const normalizedSections: LibrarySection[] = [];
  for (const section of sections) {
    if (byId.has(section.id) || findSectionByTitle(normalizedSections, section.title)) continue;
    byId.set(section.id, section);
    normalizedSections.push(section);
  }

  const assignedArtifacts = artifacts.map((artifact) => {
    const knownSection = artifact.sectionId ? byId.get(artifact.sectionId) : undefined;
    if (knownSection) return { ...artifact, sectionTitle: knownSection.title };
    const fallback = defaultSectionFor(artifact.kind, artifact.createdAt || artifact.updatedAt);
    const section = findSectionByTitle(normalizedSections, fallback.title)
      ?? (byId.has(fallback.id)
        ? createSection(fallback.title, fallback.createdAt)
        : fallback);
    if (!byId.has(section.id)) {
      byId.set(section.id, section);
      normalizedSections.push(section);
    }
    return { ...artifact, sectionId: section.id, sectionTitle: section.title };
  });

  return { schemaVersion: 2, sections: normalizedSections, artifacts: assignedArtifacts };
}

async function readIndex(): Promise<LibraryIndex> {
  ensureDirectories();
  const candidates = [INDEX, LEGACY_INDEX].filter((file) => file.exists);
  for (const candidate of candidates) {
    try {
      const index = normalizeIndex(JSON.parse(await candidate.text()));
      if (!index) continue;
      if (candidate.uri !== INDEX.uri) await writeIndex(index);
      return index;
    } catch {
      // Try the legacy index as a recovery source before declaring the local
      // library empty. Revision files themselves remain untouched.
    }
  }
  return emptyIndex();
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
    sectionTitle: artifact.sectionTitle,
    updatedAt: artifact.updatedAt,
    revisionCount: artifact.revisions.length,
  };
}

function sortArtifacts(items: LibraryArtifact[]): LibraryArtifact[] {
  return [...items].sort((left, right) => right.updatedAt.localeCompare(left.updatedAt));
}

function sortSections(items: LibrarySection[]): LibrarySection[] {
  return [...items].sort((left, right) => right.updatedAt.localeCompare(left.updatedAt) || left.title.localeCompare(right.title, 'ru-RU'));
}

export async function listLibraryArtifacts(): Promise<LibraryArtifact[]> {
  const index = await readIndex();
  return sortArtifacts(index.artifacts);
}

export async function listLibrarySections(): Promise<LibrarySection[]> {
  const index = await readIndex();
  return sortSections(index.sections);
}

export async function getLibraryOverview(): Promise<{ sections: LibrarySection[]; artifacts: LibraryArtifact[] }> {
  const index = await readIndex();
  return { sections: sortSections(index.sections), artifacts: sortArtifacts(index.artifacts) };
}

export async function getLibraryCatalogForAssistant(): Promise<LibraryAssistantCatalog> {
  const index = await readIndex();
  const sections: LibraryCatalogSection[] = sortSections(index.sections)
    .slice(0, MAX_SECTIONS_EXPOSED_TO_ASSISTANT)
    .map((section) => ({ id: section.id, title: section.title }));
  const titles = new Map(index.sections.map((section) => [section.id, section.title]));
  return {
    sections,
    artifacts: sortArtifacts(index.artifacts)
      .slice(0, MAX_ARTIFACTS_EXPOSED_TO_ASSISTANT)
      .map((artifact) => toCatalogEntry({ ...artifact, sectionTitle: titles.get(artifact.sectionId) ?? artifact.sectionTitle })),
  };
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
    const requestedSectionTitle = normalizeSectionTitle(draft.sectionTitle);
    const existingSection = existing ? index.sections.find((section) => section.id === existing.sectionId) : undefined;
    const defaultTitle = DEFAULT_SECTION_BY_KIND[normalizeKind(draft.kind)].title;
    const sectionTitle = requestedSectionTitle || existingSection?.title || defaultTitle;
    let section = findSectionByTitle(index.sections, sectionTitle);
    if (!section) {
      const defaultSection = defaultSectionFor(normalizeKind(draft.kind), now);
      section = sameTitle(defaultSection.title, sectionTitle) && !index.sections.some((item) => item.id === defaultSection.id)
        ? defaultSection
        : createSection(sectionTitle, now);
      index.sections.push(section);
    }
    section = { ...section, updatedAt: now };
    index.sections = index.sections.map((item) => item.id === section.id ? section : item);
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
      sectionId: section.id,
      sectionTitle: section.title,
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
      currentRevisionId: revisionId,
      sourceUrls: normalizeSourceUrls(draft.sourceUrls),
      revisions,
      revisionCount: revisions.length,
    };

    const next: LibraryIndex = {
      schemaVersion: 2,
      sections: index.sections,
      artifacts: existing
        ? index.artifacts.map((item) => item.id === existing.id ? artifact : item)
        : [artifact, ...index.artifacts],
    };
    await writeIndex(next);
    return { artifact, created: !existing };
  });
}

export async function createLibrarySection(titleInput: string): Promise<{ section: LibrarySection; created: boolean }> {
  return enqueueWrite(async () => {
    const title = normalizeSectionTitle(titleInput);
    if (!title) throw new Error('Введите название раздела.');
    const index = await readIndex();
    const existing = findSectionByTitle(index.sections, title);
    if (existing) return { section: existing, created: false };
    const section = createSection(title, new Date().toISOString());
    await writeIndex({ ...index, sections: [...index.sections, section] });
    return { section, created: true };
  });
}

export async function renameLibrarySection(sectionId: string, titleInput: string): Promise<LibrarySection> {
  if (!UUID.test(sectionId)) throw new Error('Раздел не найден.');
  return enqueueWrite(async () => {
    const title = normalizeSectionTitle(titleInput);
    if (!title) throw new Error('Введите название раздела.');
    const index = await readIndex();
    const current = index.sections.find((section) => section.id === sectionId);
    if (!current) throw new Error('Раздел не найден.');
    if (findSectionByTitle(index.sections, title, sectionId)) throw new Error('Раздел с таким названием уже есть.');
    const section = { ...current, title, updatedAt: new Date().toISOString() };
    await writeIndex({ ...index, sections: index.sections.map((item) => item.id === sectionId ? section : item) });
    return section;
  });
}

export async function moveLibraryArtifact(artifactId: string, sectionId: string): Promise<LibraryArtifact> {
  if (!UUID.test(artifactId) || !UUID.test(sectionId)) throw new Error('Книга или раздел не найдены.');
  return enqueueWrite(async () => {
    const index = await readIndex();
    const artifact = index.artifacts.find((item) => item.id === artifactId);
    const section = index.sections.find((item) => item.id === sectionId);
    if (!artifact || !section) throw new Error('Книга или раздел не найдены.');
    const now = new Date().toISOString();
    const moved = { ...artifact, sectionId: section.id, sectionTitle: section.title, updatedAt: now };
    const touchedSection = { ...section, updatedAt: now };
    await writeIndex({
      ...index,
      sections: index.sections.map((item) => item.id === section.id ? touchedSection : item),
      artifacts: index.artifacts.map((item) => item.id === artifact.id ? moved : item),
    });
    return moved;
  });
}

export async function deleteLibrarySection(sectionId: string): Promise<{ movedArtifactCount: number }> {
  if (!UUID.test(sectionId)) throw new Error('Раздел не найден.');
  return enqueueWrite(async () => {
    const index = await readIndex();
    const section = index.sections.find((item) => item.id === sectionId);
    if (!section) throw new Error('Раздел не найден.');
    const affected = index.artifacts.filter((artifact) => artifact.sectionId === sectionId);
    let sections = index.sections.filter((item) => item.id !== sectionId);
    const now = new Date().toISOString();
    let fallback = findSectionByTitle(sections, 'Без раздела');
    if (!fallback && affected.length > 0) {
      fallback = createSection('Без раздела', now);
      sections = [...sections, fallback];
    }
    const artifacts = affected.length === 0 || !fallback
      ? index.artifacts
      : index.artifacts.map((artifact) => artifact.sectionId === sectionId
        ? { ...artifact, sectionId: fallback!.id, sectionTitle: fallback!.title, updatedAt: now }
        : artifact);
    await writeIndex({ schemaVersion: 2, sections, artifacts });
    return { movedArtifactCount: affected.length };
  });
}

export async function deleteLibraryArtifact(artifactId: string): Promise<boolean> {
  if (!UUID.test(artifactId)) return false;
  return enqueueWrite(async () => {
    const index = await readIndex();
    if (!index.artifacts.some((artifact) => artifact.id === artifactId)) return false;
    await writeIndex({ ...index, artifacts: index.artifacts.filter((artifact) => artifact.id !== artifactId) });
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
