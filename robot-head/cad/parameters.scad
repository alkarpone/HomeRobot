// Millimetres. Electronics dimensions are UNVERIFIED envelopes, not hole patterns.
part = "assembly"; // assembly, exploded, or any of the seven STL names
show_electronics = true;
preview_upright = false; // Rotate assembly views only; individual STL orientation is unchanged.
$fn = 64;
eps = 0.02;
enclosure_width = 110;
enclosure_height = 100;
enclosure_depth = 86;
wall_thickness = 2.4;
corner_radius = 12; // Chamfer leg length; retained parameter name for compatibility.
outline_softening = 1;
bezel_thickness = 4;
bezel_bevel = 2.4;
grille_recess = 0.4;
show_hardware = true;
show_face_expression = true; // Illustration only; not printable geometry or firmware.
edge_bevel = 0.6;
pcb_clearance = 0.4;
screw_diameter = 3.3;
pilot_diameter = 2.6; // M3 thread-forming pilot: calibrate on a coupon
boss_diameter = 8;
cover_engagement = 8;
mount_thickness = 3;

// Coordinates: X right, Y up, Z from front to rear. Desktop is Y=0.
esp_size = [57,28,13];
esp_position = [0,73,23]; // rear PCB support plane, complete assembly moves together
// Measure lens relative to PCB centre and rear support plane in native configuration.
// This is NOT a cable reach allowance. Never stretch the ribbon to match this value.
camera_native_offset = [0,0,-20.6];
camera_position = esp_position + camera_native_offset;
camera_opening_diameter = 12;
camera_body_size = [16,16,8];
oled_size = [35.4,33.5,5];
oled_position = [0,29,10];
oled_window = [30,16];
oled_window_offset = [0,0];
// Populate ONLY from measured PCB holes, relative to PCB centre. Empty = tie retention.
oled_holes = [];
oled_hole_diameter = 2.2;

speaker_diameter = 40;
speaker_depth = 20;
speaker_body_diameter = 35; // MUST measure basket/magnet behind the flange
speaker_flange_thickness = 2;
speaker_position = [enclosure_width/2-wall_thickness,45,54];
speaker_clearance = 0.4;
speaker_ring_width = 6;
speaker_screw_radius = speaker_diameter/2+4;
mic_diameter = 14;
mic_position = [-enclosure_width/2+wall_thickness,28,52];
mic_port_diameter = 3;
amp_size = [19,18,4];
amp_position = [-20,27]; // rear-cover print coordinates; mirrored in assembly
usb_window_height = 24;
usb_window_depth = 30;
usb_window_y = esp_position.y;
usb_window_z = wall_thickness+usb_window_depth/2;
// Designed enclosure mounting coordinates; never interpreted as PCB holes.
cover_x = enclosure_width/2-10;
cover_y = [10,enclosure_height-10];
function frame_size(s) = [s.x+16,s.y+12];
function mount_points(s) = [for(x=[-1,1],y=[-1,1]) [x*(s.x/2+4),y*(s.y/2)]];

// Service cover: top/bottom press-to-release cantilevers; screws remain optional.
rear_attachment = "snap"; // snap or screws
snap_length = 18;
snap_thickness = 1.2;
snap_width = 10;
snap_gap = 0.4;
snap_projection = 1.4;
snap_window_clearance = 0.4;
