Bundled navigation data

Source: AIRAC 2609 (valid 03.09.2026 – 01.10.2026), airways and points exported from the MSFS 2024
navigation database into the workbook «AIRAC_2609_MSFS2024_трассы_и_точки.xlsx». Cycle, validity and
counts: airac/cycle.json.

Files (all built by tools/airac-import/build.js, do not edit by hand):
  fixes.dat.gz          "IDENT lat lon", one line per point (waypoints, VOR/DME, NDB, terminal points)
  airways.dat.gz        "AWY A alat alon B blat blon", one line per airway segment
  airac/airways.tsv.gz  the same segments with direction (F: only in sequence order, B: only against it,
                        empty: both ways), level (H high, L low, B both), MEA, MAA, course and distance
  airac/cycle.json      cycle metadata

New cycle: node tools/airac-import/build.js <workbook.xlsx>
