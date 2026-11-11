# Exported prototype meshes

All seven ASCII STL files were exported with OpenSCAD 2021.01 from the default
parameters on 2026-11-11. Each passed the dependency-free mesh check in
`../tools/check_stl.py`: one closed connected manifold, consistent winding,
Z=0 print-bed placement and bounds below 240 mm on each axis.

See `../docs/mesh-check.json` for measured mesh bounds and triangle counts.
These checks do not establish real electronics fit. Measure the camera/PCB,
USB and speaker as described in `../docs/dimensions.md` before printing.
Re-export all files after parameter changes using `../README.md`.

Exterior revision: also print `front_bezel.stl` in white and use M3 × 12 front
screws. Shell and cover screw locations changed; do not mix earlier revisions.

Current default uses snap rear retention: replace `main_enclosure.stl` and
`rear_cover.stl` together. See `../docs/snap-cover.md`; rear screws are omitted.
