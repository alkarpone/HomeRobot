# Press-to-release service cover

The default `rear_attachment="snap"` replaces the four rear screws with two
integral cantilever hooks at the top/bottom centre of the rear cover. The front
bezel, electronics and speaker still use screws. Routine internal access needs
only rear-cover removal. All seven STL filenames stay the same; replace the
shell and rear cover together. `rear_attachment="screws"` restores screw retention
and omits the hooks, guide lips and release windows; export both matching parts.

## Use

Align the short top/bottom guide lips, press the rear cover straight in and
check both hooks have engaged. To open, lift the robot, press each hook inward
through its small top/bottom opening and gently pull that edge of the cover
back. Release the hooks sequentially, keeping the first edge slightly withdrawn.
A blunt plastic pick or fingernail helps reach the recessed hooks. Do not pull
against a locked hook or insert a metal tool beside powered electronics.
Disconnect power first. Wiring needs service slack because the amplifier is on
the removable cover. Nothing is glued and there are no loose latch pieces.

## Default geometry

- Two beams: free length 18, thickness 1.2, width 10 mm; tapered 3 mm root.
- Neutral beam-to-shell clearance 0.4 mm; outward hook projection 1.4 mm.
  Engagement into the 2.4 mm wall is 1.0 mm. To clear it, press approximately
  1.2 mm inward, allowing print variation. Do not press farther than needed.
- In rear-cover print coordinates the beam extends Z 2.4–20.4, the square
  retaining shoulder is at Z 17.4. Its insertion ramp spans Z 18.4–20.4.
- In assembled coordinates the matching windows span Z 65.2–69.0; the hook
  shoulder is at Z 68.6, leaving 0.4 mm axial release clearance. Window width
  10.8 mm gives 0.4 mm lateral clearance each side.
- Beam outer face: top Y 97.2, bottom Y 2.8. Hook reaches Y 98.6 / 1.4;
  it stays inside the external 0–100 mm envelope. Release requires access from
  above and underneath, so lift the head before opening.
- The flexure zones are at X ±5, near Y 3 and 97, Z 66–84. Front boards end at
  Z 26. The speaker ring ends at Y 71 and starts at Y 19. The rear amplifier is
  near X +20, Y 27. These nominal envelopes leave room for the hooks and their
  inward movement. Keep wires out of the marked flexure zones.
- Simple beam theory estimates about 0.67% root surface strain for 1.2 mm
  deflection: 1.5 × 1.2 × 1.2 / 18². Root shape, layer adhesion and material
  fatigue mean this is only a screening estimate, not a lifetime prediction.

## Print and fit test

Print rear cover outer face down as exported, with beams rising vertically.
Its new bounds are 110 × 100 × 20.42 mm. The ramps are self-supporting; inspect
root bonding and use normal slow outer-wall speeds. Default PLA is appropriate
for a first gentle fit test; PETG is preferable for repeated flexing, with its
own clearance test. Do not promise repeated cycling until physically tested.
Shell release windows add approximately 10.8 mm bridges; inspect the slice and
use local support if your profile cannot bridge them cleanly.

First test the cover against an empty shell. Check easy insertion, positive
retention, top/bottom release and absence of stress whitening. Start with a few
gentle cycles. If too tight, increase `snap_gap` or reduce `snap_projection`,
then re-export matching shell/cover. Do not sand the beam root thinner. A loose
cover may need less gap or projection adjustment after measuring the print.
The mesh checks do not verify spring force, tactile feedback or fatigue life.

See `rear_cover.png` for the generated OpenSCAD detail. The studio render from
the previous exterior revision does not show these new service features.
