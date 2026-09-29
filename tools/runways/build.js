// Builds src/SkyNetwork.Site/Nav/runways.csv.gz from OurAirports runways.csv (public domain,
// https://davidmegginson.github.io/ourairports-data/runways.csv): the runways of the airports the site knows
// (wwwroot/data/airports.json), for approximate airport diagrams and runway choice when OpenStreetMap has none yet.
// Line: ICAO,LE,HE,lat1,lon1,lat2,lon2,lengthM,widthM,surface,exact — "exact" 0 when the ends were not in the data
// and the runway was laid through the airport's reference point along its heading.
// Usage: node tools/runways/build.js <runways.csv> [site project dir]
'use strict';
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const [csv, siteDir = path.join(__dirname, '..', '..', 'src', 'SkyNetwork.Site')] = process.argv.slice(2);
if (!csv) { console.error('usage: node build.js <runways.csv> [site project dir]'); process.exit(2); }

const airports = JSON.parse(fs.readFileSync(path.join(siteDir, 'wwwroot', 'data', 'airports.json'), 'utf8'));

function parseCsv(text) {
  const rows = []; let row = [], field = '', q = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (q) { if (c === '"') { if (text[i + 1] === '"') { field += '"'; i++; } else q = false; } else field += c; }
    else if (c === '"') q = true;
    else if (c === ',') { row.push(field); field = ''; }
    else if (c === '\n') { row.push(field.replace(/\r$/, '')); rows.push(row); row = []; field = ''; }
    else field += c;
  }
  if (field || row.length) { row.push(field); rows.push(row); }
  return rows;
}

const RAD = Math.PI / 180;
function dest(lat, lon, brg, m) {
  const d = m / 6371000, b = brg * RAD, p1 = lat * RAD, l1 = lon * RAD;
  const p2 = Math.asin(Math.sin(p1) * Math.cos(d) + Math.cos(p1) * Math.sin(d) * Math.cos(b));
  const l2 = l1 + Math.atan2(Math.sin(b) * Math.sin(d) * Math.cos(p1), Math.cos(d) - Math.sin(p1) * Math.sin(p2));
  return [p2 / RAD, ((l2 / RAD + 540) % 360) - 180];
}
const num = s => s === '' || s == null ? null : Number(s);
const r6 = x => Number(x.toFixed(6));

const rows = parseCsv(fs.readFileSync(csv, 'utf8'));
const h = rows.shift();
const col = Object.fromEntries(h.map((n, i) => [n, i]));
const out = [];
let exact = 0, laid = 0;
for (const r of rows) {
  const icao = (r[col.airport_ident] || '').toUpperCase();
  const apt = airports[icao];
  if (!apt || r[col.closed] === '1') continue;
  const le = (r[col.le_ident] || '').toUpperCase(), he = (r[col.he_ident] || '').toUpperCase();
  const surface = (r[col.surface] || '').toUpperCase();
  if (/^H/.test(le) || /H$/.test(le) && /H$/.test(he) || /WATER|^W$/.test(surface)) continue;   // helipads and water lanes
  const lengthM = Math.round((num(r[col.length_ft]) || 0) * 0.3048), widthM = Math.round((num(r[col.width_ft]) || 45 / 0.3048) * 0.3048);
  let lat1 = num(r[col.le_latitude_deg]), lon1 = num(r[col.le_longitude_deg]);
  let lat2 = num(r[col.he_latitude_deg]), lon2 = num(r[col.he_longitude_deg]);
  let isExact = 1;
  if (lat1 == null || lon1 == null || lat2 == null || lon2 == null) {
    // No ends: through the reference point, along the true heading or the number (magnetic, close enough).
    let hdg = num(r[col.le_heading_degT]);
    const n = parseInt(le, 10);
    if (hdg == null && !isNaN(n)) hdg = n * 10;
    if (hdg == null || lengthM < 100) continue;
    [lat1, lon1] = dest(apt[0], apt[1], (hdg + 180) % 360, lengthM / 2);
    [lat2, lon2] = dest(apt[0], apt[1], hdg, lengthM / 2);
    isExact = 0; laid++;
  } else exact++;
  out.push([icao, le, he, r6(lat1), r6(lon1), r6(lat2), r6(lon2), lengthM, widthM, surface.replace(/,/g, ' '), isExact].join(','));
}
out.sort();
const file = path.join(siteDir, 'Nav', 'runways.csv.gz');
fs.writeFileSync(file, zlib.gzipSync(out.join('\n') + '\n', { level: 9 }));
console.log(`${out.length} runways (${exact} with their ends, ${laid} laid through the airport) → ${path.relative(process.cwd(), file)}, ${(fs.statSync(file).size / 1e6).toFixed(2)} MB`);
