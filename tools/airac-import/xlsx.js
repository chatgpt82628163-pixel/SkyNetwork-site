// Minimal reader for the AIRAC workbook: unzips it with the system `unzip` and walks the sheet XML row by row.
// Only what the export uses: inline strings and plain numbers, no shared strings.
'use strict';
const { execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

function unpack(xlsx) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'airac-'));
  execFileSync('unzip', ['-q', '-o', xlsx, 'xl/workbook.xml', 'xl/worksheets/*', '-d', dir]);
  return dir;
}

function sheetFiles(dir) {
  const wb = fs.readFileSync(path.join(dir, 'xl', 'workbook.xml'), 'utf8');
  const names = [...wb.matchAll(/<sheet name="([^"]+)" sheetId="(\d+)"/g)].map(m => m[1]);
  const out = {};
  names.forEach((n, i) => { out[n] = path.join(dir, 'xl', 'worksheets', `sheet${i + 1}.xml`); });
  return out;
}

const unescape = s => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const colIndex = ref => { let n = 0; for (const ch of ref.match(/^[A-Z]+/)[0]) n = n * 26 + ch.charCodeAt(0) - 64; return n - 1; };

// Yields each row as an array of cell values (strings or numbers, undefined for empty cells); the header row included.
function* rows(file) {
  const xml = fs.readFileSync(file, 'utf8');
  const rowRe = /<row[^>]*>([\s\S]*?)<\/row>/g;
  const cellRe = /<c r="([A-Z]+)\d+"([^>]*?)(?:\/>|>([\s\S]*?)<\/c>)/g;
  let m;
  while ((m = rowRe.exec(xml))) {
    const row = [];
    let c;
    cellRe.lastIndex = 0;
    while ((c = cellRe.exec(m[1]))) {
      const body = c[3] || '';
      let v;
      if (/t="inlineStr"/.test(c[2])) { const t = body.match(/<t[^>]*>([\s\S]*?)<\/t>/); v = t ? unescape(t[1]) : ''; }
      else { const t = body.match(/<v>([\s\S]*?)<\/v>/); v = t ? Number(t[1]) : undefined; }
      row[colIndex(c[1])] = v;
    }
    yield row;
  }
}

module.exports = { unpack, sheetFiles, rows };
