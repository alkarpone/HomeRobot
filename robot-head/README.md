# HomeRobot stationary head — fit-test prototype

Parametric OpenSCAD enclosure, isolated from application and firmware sources.
Seven printable parts, **110 × 100 × 90 mm (width × height × depth)**, 2.4 mm walls,
flat bottom, recessed black faceplate, white faceted shell and a separate white bezel. No motion hardware or glue.
The larger envelope separates the complete camera carrier above the OLED and
keeps the speaker ring behind the front carriers.

**This is a preliminary fit-test design, not a measured production enclosure.**
Read [dimensions](docs/dimensions.md) before printing. In particular, measure the
camera's native position, USB direction and speaker basket. The CAD cannot
establish the short ribbon's compatibility from PCB dimensions alone.

## Open and export

Install OpenSCAD from the [official downloads](https://openscad.org/downloads.html).
On macOS, copy the application into Applications. Open `cad/robot_head.scad`.
Select `part="assembly"` or `part="exploded"`; F5 shows the assembly and F6 renders
solid geometry. Translucent electronics are F5 reference envelopes only.
Do not print the complete assembly as a fused object.

OpenSCAD 2021.01 was found on the Desktop and used to export all seven STLs.
[Assembly preview](docs/assembly.png), [exploded view](docs/exploded.png) and
[mesh check results](docs/mesh-check.json) are included. All seven meshes passed
closed-manifold, connectedness, winding and print-bounds checks. Physical fit
and slicer validation remain pending. Re-export after changing measurements:

```sh
OPENSCAD="$HOME/Desktop/OpenSCAD-2021.01.app/Contents/MacOS/OpenSCAD"
# On Linux or another PATH installation: OPENSCAD=openscad
for part in main_enclosure front_bezel front_faceplate rear_cover esp32_mount oled_mount speaker_mount; do
  "$OPENSCAD" -o "stl/$part.stl" -D "part=\"$part\"" cad/robot_head.scad || exit 1
done
"$OPENSCAD" -o docs/assembly.png --imgsize=1400,1000 --viewall --autocenter \
  --camera=0,0,0,70,0,145,300 -D 'preview_upright=true' -D 'part="assembly"' cad/robot_head.scad
"$OPENSCAD" -o docs/exploded.png --imgsize=1400,1000 --viewall --autocenter \
  --camera=0,0,0,70,0,145,300 -D 'preview_upright=true' -D 'part="exploded"' cad/robot_head.scad
```

See the [OpenSCAD CLI reference](https://files.openscad.org/documentation/manual/Using_OpenSCAD_in_a_command_line_environment.html).
PNG rendering requires graphics support. Export selectors already orient each part
flat on the XY bed at Z=0. Assembly coordinates instead use Y as vertical.

## Printing

PLA, 0.4 mm nozzle, 0.16 mm layers, 3 perimeters and 15–20% infill as a starting
profile. Thin walls may be entirely filled by the slicer. Use a brim on the tall
shell if needed. Inspect every layer around side openings and mounting posts.

| Part | Bed face | Conservative bounds, mm | Supports |
|---|---|---|---|
| main_enclosure | Front open rim | 110 × 100 × 81.2 | Local support at USB window roof likely needed (24 mm bridge) |
| front_bezel | Outer face | 110 × 100 × 4 | None; 45-degree perimeter bevel |
| front_faceplate | Outer face | 110 × 100 × 23 | Normally none; upright posts |
| rear_cover | Outer face | 110 × 100 × 20.42 | Normally none; inspect beam roots |
| esp32_mount | Flat frame | 73 × 40 × 3 | None |
| oled_mount | Flat frame | 51.4 × 45.5 × 3 | None |
| speaker_mount | Flat ring | 52 × 52 × 3 | None |

Every default part fits well inside the P1S's nominal 256 mm cube
([manufacturer guide](https://cdn1.bambulab.com/documentation/quick-start-2024-01-17/Quick-Start-P1S_en.pdf)).
Print the two carriers and speaker ring first for cheap fit checks. Only then
print the faceplate, inspect the assembled front module, and print the shell.
See [assembly](docs/assembly.md) for hardware and installation order.

Repeat mesh verification with `python3 tools/check_stl.py`. This checks the exported
ASCII meshes, not real electronics fit or assembly collisions.

## Reference-inspired exterior revision

The exterior now follows the supplied reference's faceted white housing, dark
recessed face with a raised camera silhouette, visible corner screws and circular
side grille. A separate 4 mm white bezel uses the same front screws; this avoids
requiring AMS or painting. The original six-part assembly remains 86 mm deep;
the bezel adds 4 mm. The microphone stays on the opposite side from the speaker.

The real 1.3-inch OLED remains a 30 × 16 mm visible window. The reference image's
much larger-looking screen and apparent 70 mm head height are not adopted as
measured dimensions. The original native camera/PCB relationship remains a
fit-test requirement. No electronic component was shrunk to improve appearance.

[Studio preview](docs/studio-preview.png) is rendered in Blender from the actual
exported STLs. Display pixels, lens and screw heads illustrate purchased hardware;
they are not additional printable parts and do not change firmware. The reproducible
`tools/render_preview.py` uses the documented default assembly coordinates.
OpenSCAD previews can show coplanar display artifacts; the STL/Blender render is
also provided to inspect the exterior without those preview artifacts.

## Tool-free rear access

The current rear cover uses two integral press-to-release hooks and short guide
lips, with release windows in the shell. Front/internal screws remain. See
[operation, clearances and print fit test](docs/snap-cover.md) and the
[rear-cover detail](docs/rear_cover.png). Print the new shell and cover together.
For the previous screw style use `rear_attachment="screws"` and export both.
The studio image predates this latch revision; current OpenSCAD views and STLs
include the latches. The rear-cover print height is now 20.42 mm.
