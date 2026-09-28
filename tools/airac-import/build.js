// Builds the site's bundled nav data from the AIRAC workbook (sheets «Трассы», «Точки», «Инфо»):
//   Nav/fixes.dat.gz          "IDENT lat lon"                         — every point, read by NavData
//   Nav/airways.dat.gz        "AWY A alat alon B blat blon"           — every airway segment, read by NavData
//   Nav/airac/airways.tsv.gz  the same segments with what the planner needs: direction, level, MEA, MAA, course, distance
//   Nav/airac/cycle.json      cycle, validity and counts
// Usage: node tools/airac-import/build.js <workbook.xlsx> [site project dir]
'use strict';
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const { unpack, sheetFiles, rows } = require('./xlsx');

const [xlsx, siteDir = path.join(__dirname, '..', '..', 'src', 'SkyNetwork.Site')] = process.argv.slice(2);
if (!xlsx) { console.error('usage: node build.js <workbook.xlsx> [site project dir]'); process.exit(2); }

// A segment longer than this is two airways that happen to share a name, not one leg.
const MaxSegmentNm = 1200;

const dir = unpack(xlsx);
const sheets = sheetFiles(dir);
for (const name of ['Трассы', 'Точки', 'Инфо'])
  if (!sheets[name]) throw new Error(`sheet «${name}» not found`);

const round = (x, d) => Number(x.toFixed(d));
const nm = (lat1, lon1, lat2, lon2) => {
  const r = Math.PI / 180, dLat = (lat2 - lat1) * r, dLon = (lon2 - lon1) * r;
  const a = Math.sin(dLat / 2) ** 2 + Math.cos(lat1 * r) * Math.cos(lat2 * r) * Math.sin(dLon / 2) ** 2;
  return 2 * 3440.065 * Math.asin(Math.sqrt(Math.min(1, a)));
};
const validPos = (lat, lon) => typeof lat === 'number' && typeof lon === 'number' && Math.abs(lat) <= 90 && Math.abs(lon) <= 180;

// ---- info ----
const info = {};
for (const r of rows(sheets['Инфо'])) if (typeof r[0] === 'string' && r[1] !== undefined) info[r[0]] = String(r[1]);
const cycle = info['AIRAC цикл'];
if (!/^\d{4}$/.test(cycle || '')) throw new Error('no AIRAC cycle on sheet «Инфо»');

// ---- points ----
// Col: 0 type, 1 ident, 2 name, 3 region, 4 area, 5 lat, 6 lon, 7 freq, 8 class, 9 airport
const fixes = new Map(); // "IDENT lat lon" → true, the same name at the same place once
const pointTypes = {};
let first = true;
for (const r of rows(sheets['Точки'])) {
  if (first) { first = false; continue; }
  const [type, ident, , , , lat, lon] = r;
  if (typeof ident !== 'string' || !/^[A-Z0-9]{1,7}$/.test(ident) || !validPos(lat, lon)) continue;
  fixes.set(`${ident} ${round(lat, 4)} ${round(lon, 4)}`, true);
  pointTypes[type] = (pointTypes[type] || 0) + 1;
}

// ---- airways ----
// Col: 0 airway, 1 seq, 2 fix, 3 region, 4 area, 5 lat, 6 lon, 7 route type, 8 level (H/L/B), 9 direction (F/B),
//      10 min alt 1, 11 min alt 2, 12 max alt, 13 course from, 14 course to, 15 distance, 16 description code.
// The level, direction and altitudes of a record are those of the segment from its fix to the next one (ARINC 424).
// The second character of the description code is 'E' at the end of a continuous airway: no segment after it.
const legacy = new Set();
const full = [];
let prev = null, skippedLong = 0;
first = true;
for (const r of rows(sheets['Трассы'])) {
  if (first) { first = false; continue; }
  const [awy, seq, fix, region, , lat, lon, , level, direction, min1, min2, max, course, , dist, desc] = r;
  const cur = { awy, seq, fix, region, lat, lon, level, direction, min1, min2, max, course, dist, desc: desc || '' };
  const ok = typeof awy === 'string' && typeof fix === 'string' && validPos(lat, lon);
  if (ok && prev && prev.awy === awy && seq > prev.seq && prev.desc[1] !== 'E') {
    const d = nm(prev.lat, prev.lon, lat, lon);
    if (d > MaxSegmentNm) skippedLong++;
    else if (d > 0) {
      const a = `${prev.fix} ${round(prev.lat, 4)} ${round(prev.lon, 4)}`, b = `${fix} ${round(lat, 4)} ${round(lon, 4)}`;
      legacy.add(`${awy} ${a} ${b}`);
      full.push([awy, prev.seq, prev.fix, prev.region, round(prev.lat, 6), round(prev.lon, 6), fix, region, round(lat, 6), round(lon, 6),
        prev.direction || '', prev.level || '', prev.min1 ?? '', prev.max ?? '', prev.course ?? '', prev.dist ?? round(d, 1)].join('\t'));
      // Airway fixes are points too, whatever the points sheet says.
      fixes.set(a, true); fixes.set(b, true);
    }
  }
  prev = ok ? cur : null;
}

// ---- write ----
const gz = (file, lines) => {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, zlib.gzipSync(lines.join('\n') + '\n', { level: 9 }));
  console.log(`${path.relative(process.cwd(), file)}: ${lines.length} lines, ${(fs.statSync(file).size / 1e6).toFixed(1)} MB`);
};
const nav = path.join(siteDir, 'Nav');
gz(path.join(nav, 'fixes.dat.gz'), [...fixes.keys()].sort());
gz(path.join(nav, 'airways.dat.gz'), [...legacy].sort());
gz(path.join(nav, 'airac', 'airways.tsv.gz'),
  ['airway\tseq\tfrom\tfrom_region\tfrom_lat\tfrom_lon\tto\tto_region\tto_lat\tto_lon\tdirection\tlevel\tmin_alt\tmax_alt\tcourse\tdistance_nm', ...full]);
const meta = {
  cycle,
  revision: info['Ревизия'] || null,
  valid: info['Действует'] || null,
  parsed: info['Дата парсинга'] || null,
  source: 'MSFS 2024 navigation data (AIRAC ' + cycle + ')',
  fixes: fixes.size,
  segments: full.length,
  airways: new Set(full.map(l => l.split('\t')[0])).size,
  pointTypes,
  skippedLongSegments: skippedLong,
};
fs.writeFileSync(path.join(nav, 'airac', 'cycle.json'), JSON.stringify(meta, null, 2) + '\n');
console.log(meta);
fs.rmSync(dir, { recursive: true, force: true });
