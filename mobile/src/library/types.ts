export const LIBRARY_KINDS = ['recipes', 'restaurants', 'entertainment', 'guide', 'other'] as const;

export type LibraryKind = typeof LIBRARY_KINDS[number];

export interface LibrarySection {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
}

export interface LibraryCatalogSection {
  id: string;
  title: string;
}

export interface LibraryCatalogEntry {
  id: string;
  title: string;
  kind: LibraryKind;
  summary: string;
  sectionTitle: string;
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
  sectionId: string;
  createdAt: string;
  currentRevisionId: string;
  sourceUrls: string[];
  revisions: LibraryRevision[];
}

export interface LibraryIndex {
  schemaVersion: 2;
  sections: LibrarySection[];
  artifacts: LibraryArtifact[];
}

export interface LibraryAssistantCatalog {
  sections: LibraryCatalogSection[];
  artifacts: LibraryCatalogEntry[];
}

export interface LibraryArtifactDraft {
  artifactId?: string | null;
  title: string;
  kind: LibraryKind;
  html: string;
  sectionTitle?: string | null;
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
