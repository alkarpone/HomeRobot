# Dimensions, assumptions and verification

All units are mm. X points right when looking at the face, Y points up from the
desk, Z points backwards from the faceplate. Body bounds: X ±55, Y 0–100, Z 0–86; the front bezel extends to Z -4,
for 90 mm total assembled depth. XY corners have 12 mm chamfer legs and 1 mm
edge softening (`corner_radius` retains its original parameter name). The front
bezel has a 2.4 mm perimeter bevel; the cover plates retain a 0.6 mm bevel.
The bottom has a long flat segment; the chamfered corners do not make it rock.

## Measurements required before the full print

| Parameter / feature | Provisional value | Required measurement |
|---|---|---|
| ESP assembly | 57 × 28 × 13 | Board outline, component heights, pins and clear edge strips |
| ESP rear support plane | (0,73,23) | Position the intact board/camera together |
| Native lens offset | (0,0,-20.6) from support plane | Actual lens centre and front plane relative to board; never a permissible ribbon extension |
| Camera body / opening | 16 × 16 × 8 / diameter 12 | Body, lens barrel, field of view, connector and ribbon exit |
| OLED PCB / depth | 35.4 × 33.5 / 5 | Include solder and headers; window alignment relative to PCB |
| OLED window | 30 × 16 centred | Visible glass and active area; update offset and size |
| OLED holes | Empty list | Populate measured local XY coordinates and diameter only |
| Speaker flange | Diameter 40, thickness 2 | Rim must have continuous load-bearing land |
| Speaker basket/magnet | Diameter 35, total depth 20 | All behind-flange sections must pass through the 35.8 ring bore |
| Microphone | Diameter 14, board thickness 1.6 assumed | Port position/side, pins, component clearance |
| Amplifier | 19 × 18 × 4 | Clear edge shelf and header orientation |
| USB | Left-pointing socket, within Y 61–85, Z 2.4–32.4 | Socket direction, both sockets if present, plug overmould and insertion sweep |
| Fasteners | M3 clearance 3.3, pilot 2.6 | Calibrate pilot for PLA and chosen thread-forming screw |

PCB hole coordinates are never inferred. Carrier screw positions belong to the
printed frame, outside the PCB, and are not electronic mounting holes. All PCB
frames have strap slots; prefer measured OLED holes with insulating washers
when they fall in the edge shelf. A hole in the open centre cannot retain a board:
change the frame shelf only after inspecting the measured hole pattern.

The camera cradle consists of two shallow C-shaped guides on the faceplate,
with top/bottom ribbon exits and 0.4 clearance per side. The ESP frame secures
the entire original carrier; the cradle only locates the camera body. Keep the
original connection intact. Do not use the screws to pull the camera into place.
If the native lens is not centred over the PCB, change `camera_native_offset`;
if necessary move `esp_position` and grow the enclosure. Do not move the lens
independently by extending or tensioning the ribbon. A loose camera requires a
small non-adhesive soft pad at its body, retained by the assembled PCB/cradle,
without contacting the lens or ribbon. Verify it cannot escape the shallow
cradle; increase guide depth only after measuring available component clearance.
No ribbon bend radius or free length has been supplied, so neither is certified.

## Default analytical review (not a rendered collision test)

- Front carrier extents: OLED Y 6.25–51.75; ESP Y 53–93. Gap 1.25.
- PCB envelopes: OLED Z 5–10; ESP Z 10–23. Faceplate ends at Z 2.4:
  respective front clearance 2.6 and 7.6. ESP frame ends at Z 26.
- Speaker ring spans Z 28–80. Front-carrier depth gap is 2; rear inner
  surface is Z 83.6, leaving 3.6. Speaker outer ring spans Y 19–71.
- Speaker flange is X 50.6–52.6; ring X 47.6–50.6; basket reaches X 32.6.
  The cavity is 105.2 wide. Speaker basket stays behind the front carriers.
- Microphone is on the left at Y 28, Z 52, approximately 105 mm from the
  right wall. Its 18 mm support annulus does not overlap the upper USB window.
  Acoustic isolation is not established by this separation; test feedback.
- Cover screw axes: X ±45, Y 10 and 90. Their 9.6 mm square rails are
  clipped to the faceted outer silhouette and join the diagonal corner walls.
  Front carrier maximum X extent is ±36.5, leaving 3.7 to the rails.
- Speaker screw circle radius 24 leaves 2.35 mm material outside a 3.3 mm
  shell hole and 2.7 outside the 2.6 mm ring pilot. Four outside-access screws
  clamp the flange between ring and shell. These are not speaker PCB holes.
- USB service opening is 24 high × 30 deep. The assumed socket is recessed
  approximately 24.1 mm from the left exterior. An ordinary short plug may
  require fingers inside the opening. Test with the actual cable: the opening
  alone does not guarantee connection. There is no assumed rear-facing USB
  port and no internal extension. Move/resize the window for actual sockets.
- Rear amplifier lies in a separate depth zone, approximately Z 77.6–81.6,
  centred at assembly X +20, Y 27. It clears the speaker's body in XY.
- The ring enters through the open rear, translates toward the right wall and
  captures the speaker rim. Front assembly enters along +Z; rear cover along
  -Z. The computed envelopes permit these routes without rigid-part overlap.
  Loose wires, screwdrivers and real connector sweeps still require a dry fit.

OpenSCAD assertions cover selected size and separation constraints. They are
not a general collision solver and are not a substitute for inspection after
parameter changes. OpenSCAD 2021.01 successfully ran the assertions and CGAL-rendered all seven
parts. A numerical tolerance of 1e-8 mm accommodates floating-point roundoff
at the flush camera plane. The independent ASCII STL check passed closed-edge
and vertex manifoldness, one connected triangle shell, consistent winding,
print-bed placement and bounds for all seven files (see `mesh-check.json`).
Assembly/exploded PNG previews were generated. Slicer checks and physical
fit tests have NOT run.
Geometry uses positive overlaps between attached features and epsilon-extended
subtractions to avoid intentional coincident-only attachments.

## Release checklist

1. Measure the items above and update parameters; inspect the intact camera
   in the proposed pose without tightening anything.
2. Render each selector with F6. Require one closed connected solid per STL,
   no render warnings and no slicer repair warnings. Check actual bed bounds.
3. Inspect assembly and exploded views; check every screw head and tool path.
4. Fit carriers to the unpowered electronics with insulation. Ensure straps
   touch clear PCB edges only; no glass, sensor, antenna, connectors or ribbon.
5. Plug and unplug every USB-C socket with the head fully assembled. Confirm
   the camera image is unobstructed and the ribbon retains its natural slack.
6. Test speaker rim capture without cone/basket pressure, microphone port
   alignment, wire clearance, stability, temperature and acoustic feedback.
7. Record real measurements and fit-test results before declaring ready to print
   repeatedly. The current artifact is an initial prototype only.

## Exterior revision checks

- The white bezel is a separate seventh connected solid, 4 mm thick. It contacts
  the faceplate at Z=0 and shares its four screw axes; front screws become M3 × 12.
- The bezel's clear main face is X ±42, starting at Y 12; the narrower camera
  turret extends to Y 87. The 30 × 16 OLED opening (Y 21–37) and centred camera
  opening (Y 67–79) are unobstructed by it. Field of view still needs a real test.
- Speaker grille recess is 0.4 mm deep, leaving 2.0 mm wall between its 2.8 mm
  acoustic holes. The 43 mm recessed disc stops before the radius-24 screw circle.
- Rear cover, front plate and shell all use the same revised faceted outline and
  screw coordinates. Replace all these parts together; do not mix earlier STLs.
- Cosmetic camera-well depth is 0.8 mm, leaving 1.6 mm material around the optical
  hole. The camera guides and electronics carriers retain their original datums.

## Snap-cover revision

Default rear retention and release geometry supersede the rear screw instructions
above; see `snap-cover.md`. Rear cover print height is 20.42 mm. Other component
datums and the 110 × 100 × 90 mm external envelope remain unchanged.
