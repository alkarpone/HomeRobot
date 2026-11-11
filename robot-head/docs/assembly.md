# Assembly and service

## Hardware

- Four M3 × 12 pan-head thread-forming screws for the front bezel and faceplate
  (4 bezel + 2.4 faceplate, about 5.6 engagement into an 8-deep pilot).
- No rear screws for the default snap cover. Optional screw mode needs four
  M3 × 8 rear screws (2.4 cover, about 5.6 engagement).
- Eight M3 × 6 screws for ESP/OLED frames (3 frame, about 3 engagement).
- Four M3 × 7 screws for speaker retention (2.4 wall + 2 flange spacing +
  about 2.6 ring engagement); measure actual stack. A 3 mm ring is intentionally
  through-drilled. If using M3 × 8, verify the protruding tip clears everything.
- Thin insulating washers/pads, approximately eight narrow nylon cable ties
  for boards/microphone/amplifier. Use replaceable or releasable ties; no glue.
- Optional measured OLED screws with insulating washers and nuts, sized to the
  real PCB holes. They are not automatically M3.

Pilots are intended for careful thread forming in PLA, not unmodelled heat-set
inserts. Test one first, tighten by hand and avoid repeated aggressive torque.
For frequent servicing, revise the bosses for measured inserts in a later fit
revision. Do not place metal washers on exposed electronic conductors.

## Order

1. Deburr prints and check pilot/clearance diameters. Verify the default
   dimensions against the real modules before powering anything.
2. Keep ESP and camera connected in their native arrangement. Place the camera
   body gently in the faceplate guides and its lens at the opening. Place the
   ESP frame over the PCB rear edges and fasten to the four faceplate posts.
   Two ties run through top/bottom frame slots and around clear PCB edge strips,
   away from the camera, ribbon, antenna and USB. Add thin captured insulation
   where necessary; do not compress components. The rear component field is open.
3. Fit OLED similarly. Prefer its real mounting holes if their measured positions
   lie on the shelf. Otherwise use edge ties only if they clear the glass and
   active area. Adjust support-plane depth to remove excessive axial play; do
   not use screw force against the display glass.
4. Feed the microphone ties through the small side slots with the shell still
   open. Seat the module on its annular support, port facing the acoustic hole.
   Ties retain its edges. Check the actual port offset and component side;
   if not central, move the hole before printing. Keep wires away from the speaker.
5. Insert speaker through the rear with its cone facing the right grille. Its
   front rim rests against the inside wall. Slide the retaining ring over the
   magnet/basket from behind, onto the rear of the flange. Align the four printed
   pilots and install the four screws from outside. The flange is sandwiched;
   neither the cone nor the magnet carries clamping force. If the basket does
   not pass the ring bore, revise it; do not force or grind the speaker.
6. Tie the amplifier to the integral rear-cover edge shelf, solder side clear
   of plastic and wiring accessible from inside. Route speaker leads along the
   right wall and microphone leads left; provide service loops and strain relief.
7. Install the front assembly from the front, place the separate white bezel over
   the black faceplate, and fasten both with the four M3 × 12 front screws.
   The largest frame is smaller than the front aperture between corner rails.
   Verify both USB connector access and natural camera ribbon slack now.
8. Connect wires with power off, keep leads clear of all screw paths, then align the rear guide lips and press the cover in until both hooks engage.
   Keep wiring out of the top/bottom flexure zones (see `snap-cover.md`). The cover flips around Y in the
   assembly, so the amplifier appears on the right when viewed from the front.
9. Stand on the flat bottom. Test USB insertion/removal, camera view, OLED,
   microphone pickup and speaker at low volume. Check stability with the cable
   attached, thermal behaviour and audio feedback before extended use.

For service release the top/bottom rear hooks as described in `snap-cover.md`, disconnect wiring, then remove the front
module. Side screw heads stay accessible externally. Releasing the front module
must not pull on the short camera ribbon. Cut/replace ties only with the board
removed and protected. A physical fit test is still required for tool access
and actual tolerances; no hardware assembly has been performed for this design.
