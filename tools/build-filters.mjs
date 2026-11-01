// Compiles upstream filter lists into assets/filters.txt (+ gzip -> assets/filters.dat).
// Run: node tools/build-filters.mjs
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

const root = path.resolve(import.meta.dirname, '..');
const listsDir = path.join(root, 'tools', 'lists');
const assetsDir = path.join(root, 'assets');
fs.mkdirSync(assetsDir, { recursive: true });

const SOURCES = [
  'easylist.txt',
  'easyprivacy.txt',
  'antiadblock-adguard.txt',
  'quick-fixes.txt',
  'adservers.txt',
  'r1-extra.txt',
];

const seen = new Set();
const out = [];
const stats = { network: 0, cosmetic: 0, skipped: 0, total: 0 };

const isCosmetic = (l) => l.includes('##') || l.includes('#@#') || l.includes('#?#') || l.includes('#$#') || l.includes('#%#');

for (const name of SOURCES) {
  const file = path.join(listsDir, name);
  if (!fs.existsSync(file)) { console.warn('missing', name); continue; }
  const text = fs.readFileSync(file, 'utf8');
  for (let raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('!') || line.startsWith('[') || (line.startsWith('#') && !line.startsWith('##'))) continue;
    // unsupported syntaxes: scriptlets, procedural, style injection, html filters
    if (line.includes('##+js') || line.includes('#@#+js')) continue;
    if (line.includes('$$') || line.includes('#?#') || line.includes('#$#') || line.includes('#%#') || line.includes('##^')) { stats.skipped++; continue; }
    if (line.length > 400) { stats.skipped++; continue; }
    if (line.includes('#@#')) { stats.skipped++; continue; } // unhide rules: already handled by tighter list
    // drop rules whose options we cannot emulate safely at runtime
    const opts = line.includes('$') ? line.slice(line.indexOf('$')).toLowerCase() : '';
    if (/\$(domain|redirect|redirect-rule|csp|removeparam|replace|permissions|method|header|to|from|denyallow)\b/.test(opts)) { stats.skipped++; continue; }
    if (isCosmetic(line)) {
      const [domains, selector] = line.split('##');
      if (!selector || selector.length > 220) { stats.skipped++; continue; }
      if (!/^[#.\w\s\-\[\]="'*:()>,+~^$|]+$/.test(selector)) { stats.skipped++; continue; }
      stats.cosmetic++;
    } else {
      if (line.startsWith('/') && line.endsWith('/') && line.length > 2) { stats.skipped++; continue; } // regex rules (dropped: cost)
      stats.network++;
    }
    if (seen.has(line)) continue;
    seen.add(line);
    out.push(line);
    stats.total++;
  }
}

const text = out.join('\n');
fs.writeFileSync(path.join(assetsDir, 'filters.txt'), text);
fs.writeFileSync(path.join(assetsDir, 'filters.dat'), zlib.gzipSync(Buffer.from(text, 'utf8'), { level: 9 }));
console.log(stats, 'raw', (text.length / 1048576).toFixed(2) + 'MB', 'gz', (fs.statSync(path.join(assetsDir, 'filters.dat')).size / 1048576).toFixed(2) + 'MB');
