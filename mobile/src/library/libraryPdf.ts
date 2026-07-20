import { Directory, File, Paths } from 'expo-file-system';
import * as Print from 'expo-print';
import * as Sharing from 'expo-sharing';
import { Platform } from 'react-native';

const EXPORT_DIRECTORY = new Directory(Paths.cache, 'vass-library-exports');
const EXPORT_MAX_AGE_MS = 24 * 60 * 60 * 1000;

function cleanFileStem(title: string): string {
  const normalized = title
    .replace(/[\\/:*?"<>|]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 80);
  return normalized || 'Книга Vass';
}

function withA4PageLayout(html: string): string {
  const printStyles = `
    <style>
      @page { size: A4; margin: 14mm; }
      body { padding: 0; background: #ffffff; }
      main, article, section { max-width: none; }
    </style>`;
  return html.includes('</head>') ? html.replace('</head>', `${printStyles}</head>`) : `${printStyles}${html}`;
}

function cleanupOldExports(): void {
  try {
    if (!EXPORT_DIRECTORY.exists) return;
    const cutoff = Date.now() - EXPORT_MAX_AGE_MS;
    for (const entry of EXPORT_DIRECTORY.list()) {
      if (!(entry instanceof File) || entry.extension.toLowerCase() !== '.pdf') continue;
      const modifiedAt = entry.modificationTime;
      if (typeof modifiedAt !== 'number' || modifiedAt < cutoff) entry.delete();
    }
  } catch {
    // A cache cleanup must never prevent sharing the newly generated PDF.
  }
}

export async function shareLibraryRevisionAsPdf(title: string, html: string): Promise<void> {
  if (!html.trim()) throw new Error('Книга ещё не открыта. Попробуйте ещё раз через секунду.');
  if (Platform.OS === 'web') throw new Error('Отправка PDF доступна в приложении для Android или iPhone.');
  if (!await Sharing.isAvailableAsync()) throw new Error('На этом устройстве недоступна отправка файлов.');

  EXPORT_DIRECTORY.create({ intermediates: true, idempotent: true });
  cleanupOldExports();

  const printed = await Print.printToFileAsync({
    html: withA4PageLayout(html),
    width: 595,
    height: 842,
    textZoom: 100,
  });
  const source = new File(printed.uri);
  if (!source.exists || !source.size) throw new Error('Не удалось подготовить PDF книги.');

  const destination = new File(EXPORT_DIRECTORY, `${Date.now()}-${cleanFileStem(title)}.pdf`);
  await source.move(destination, { overwrite: true });
  await Sharing.shareAsync(destination.uri, {
    mimeType: 'application/pdf',
    dialogTitle: `Отправить PDF: ${title}`,
  });
}
