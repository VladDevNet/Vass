const MAX_LIBRARY_HTML_LENGTH = 96_000;

function stripCodeFence(value: string): string {
  const trimmed = value.trim();
  if (!trimmed.startsWith('```')) return trimmed;
  const firstBreak = trimmed.indexOf('\n');
  const lastFence = trimmed.lastIndexOf('```');
  return firstBreak >= 0 && lastFence > firstBreak
    ? trimmed.slice(firstBreak + 1, lastFence).trim()
    : trimmed;
}

function sanitizeCss(value: string): string {
  return value
    .replace(/@import\s+(?:url\([^)]*\)|[^;]+);?/gi, '')
    .replace(/url\s*\([^)]*\)/gi, 'none')
    .replace(/expression\s*\([^)]*\)/gi, '')
    .replace(/-moz-binding\s*:[^;}]+;?/gi, '')
    .replace(/behavior\s*:[^;}]+;?/gi, '');
}

// The model is intentionally allowed to design a document freely, but the
// reader is never allowed to execute code, fetch resources, submit forms, or
// escape into another page. This is a belt-and-suspenders sanitization layer;
// LibraryReaderScreen also disables JavaScript and navigation in WebView.
export function sanitizeLibraryHtml(rawHtml: string): string {
  const input = stripCodeFence(rawHtml).replace(/\u0000/g, '');
  if (input.length < 32 || input.length > MAX_LIBRARY_HTML_LENGTH) {
    throw new Error('Документ библиотеки имеет недопустимый размер.');
  }

  const styles: string[] = [];
  let body = input.replace(/<style\b[^>]*>([\s\S]*?)<\/style\s*>/gi, (_match, css: string) => {
    const sanitized = sanitizeCss(css);
    if (sanitized.trim()) styles.push(sanitized);
    return '';
  });

  body = body
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<(script|iframe|object|embed|frame|frameset|form|input|button|textarea|select|option|audio|video|source|track|svg|math)\b[^>]*>[\s\S]*?<\/\1\s*>/gi, '')
    .replace(/<(script|iframe|object|embed|frame|frameset|form|input|button|textarea|select|option|audio|video|source|track|link|base|meta|svg|math)\b[^>]*\/?\s*>/gi, '')
    .replace(/<\/?(?:html|head|body|title|!doctype)[^>]*>/gi, '')
    .replace(/\son[a-z]+\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)/gi, '')
    .replace(/\s(?:href|src|action|formaction|poster|data)\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)/gi, '')
    .replace(/\sstyle\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)/gi, '')
    .replace(/javascript\s*:/gi, '')
    .trim();

  const visibleText = body.replace(/<[^>]+>/g, '').replace(/&nbsp;/gi, ' ').trim();
  if (!visibleText) throw new Error('Документ библиотеки не содержит читаемого содержания.');

  const baseStyles = `
    :root { color-scheme: light; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      padding: 28px 20px 44px;
      color: #15213a;
      background: #f7f8fb;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      font-size: 17px;
      line-height: 1.58;
    }
    main, article, section { max-width: 760px; margin: 0 auto; }
    h1, h2, h3 { color: #12213b; line-height: 1.2; margin: 1.35em 0 .55em; }
    h1 { font-size: 2rem; margin-top: 0; }
    h2 { font-size: 1.42rem; }
    h3 { font-size: 1.1rem; }
    p, li { color: #334155; }
    ul, ol { padding-left: 1.35em; }
    table { width: 100%; border-collapse: collapse; margin: 1.15em 0; background: #ffffff; }
    th, td { border: 1px solid #dbe3ef; padding: .72em; text-align: left; vertical-align: top; }
    th { color: #183153; background: #eaf0f8; }
    blockquote { margin: 1em 0; padding: .8em 1em; border-left: 4px solid #5d8ac7; background: #eef4fb; }
    .card, .callout { padding: 1em; border: 1px solid #dbe3ef; border-radius: 8px; background: #ffffff; margin: .9em 0; }
    a { color: inherit; text-decoration: none; }
  `;

  return `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; media-src 'none'; font-src 'none'; connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'"><style>${baseStyles}\n${styles.join('\n')}</style></head><body>${body}</body></html>`;
}
