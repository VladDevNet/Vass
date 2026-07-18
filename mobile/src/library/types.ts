export const LIBRARY_KINDS = ['recipes', 'restaurants', 'entertainment', 'guide', 'other'] as const;

export type LibraryKind = typeof LIBRARY_KINDS[number];

export interface LibraryCatalogEntry {
  id: string;
  title: string;
  kind: LibraryKind;
  summary: string;
  updatedAt: string;
  revisionCount: number;
}

export interface LibraryRevision {
  id: string;
  createdAt: string;
  note: string | null;
  byteLength: number;
}

export interface LibraryArtifact extends LibraryCatalogEntry {
  createdAt: string;
  currentRevisionId: string;
  sourceUrls: string[];
  revisions: LibraryRevision[];
}

export interface LibraryIndex {
  schemaVersion: 1;
  artifacts: LibraryArtifact[];
}

export interface LibraryArtifactDraft {
  artifactId?: string | null;
  title: string;
  kind: LibraryKind;
  html: string;
  summary?: string | null;
  sourceUrls?: string[] | null;
  revisionNote?: string | null;
}

export const LIBRARY_KIND_LABELS: Record<LibraryKind, string> = {
  recipes: 'Рецепты',
  restaurants: 'Рестораны',
  entertainment: 'Развлечения',
  guide: 'Подборки и гиды',
  other: 'Другое',
};

export function isLibraryKind(value: unknown): value is LibraryKind {
  return typeof value === 'string' && (LIBRARY_KINDS as readonly string[]).includes(value);
}
