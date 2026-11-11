include <parameters.scad>
include <components.scad>

module checks() {
    assert(rear_attachment=="snap" || rear_attachment=="screws");
    assert(snap_length>=15 && snap_thickness>=1 && snap_gap>=0.3);
    assert(snap_projection>snap_gap && snap_projection<2);
    assert(wall_thickness+snap_length<enclosure_depth/2);
    assert(bezel_thickness>bezel_bevel && bezel_bevel>0);
    assert(grille_recess>=0 && wall_thickness-grille_recess>=1.8);
    assert(camera_position.y+14<enclosure_height-6,"Camera turret exceeds bezel");
    assert(wall_thickness>=2 && corner_radius>wall_thickness);
    assert(edge_bevel>0 && edge_bevel<wall_thickness);
    assert(enclosure_width<240 && enclosure_height<240 && enclosure_depth<240,"Build envelope exceeds conservative P1S allowance");
    assert(esp_position.y-frame_size(esp_size).y/2 > oled_position.y+frame_size(oled_size).y/2,"Front brackets overlap");
    assert(esp_position.y+frame_size(esp_size).y/2 < enclosure_height-wall_thickness);
    assert(oled_position.y-frame_size(oled_size).y/2 > wall_thickness);
    assert(esp_position.z-esp_size.z>wall_thickness && oled_position.z-oled_size.z>wall_thickness);
    assert(speaker_position.z-speaker_diameter/2-speaker_ring_width > esp_position.z+mount_thickness,"Speaker ring overlaps front assembly");
    assert(speaker_body_diameter+2*speaker_clearance < speaker_diameter-2,"No flange capture land");
    assert(speaker_position.z+speaker_diameter/2+speaker_ring_width < enclosure_depth-wall_thickness);
    assert(abs(camera_position.x-esp_position.x)<esp_size.x/2 && abs(camera_position.y-esp_position.y)<esp_size.y/2,"Native camera offset outside assumed assembly");
    assert(abs(esp_position.x)+frame_size(esp_size).x/2 < cover_x-4.8,"ESP frame hits corner rails");
    assert(abs(oled_position.x)+frame_size(oled_size).x/2 < cover_x-4.8,"OLED frame hits corner rails");
    assert(camera_position.y-camera_body_size.y/2-pcb_clearance-2 > oled_position.y+frame_size(oled_size).y/2,"Camera guide hits OLED carrier");
    assert(speaker_position.y-speaker_diameter/2-speaker_ring_width>wall_thickness);
    assert(speaker_position.y+speaker_diameter/2+speaker_ring_width<enclosure_height-wall_thickness);
    // Allow floating-point roundoff when the lens plane is flush with the wall.
    assert(camera_position.z>=wall_thickness-1e-8,"Lens must not penetrate faceplate");
    children();
}
module assembly(explode=0) {
    color([0.86,0.87,0.89]) main_enclosure();
    color([0.92,0.93,0.95]) translate([0,0,-bezel_thickness-2*explode]) front_bezel();
    color([0.09,0.10,0.12]) translate([0,0,-explode]) front_faceplate();
    color([0.86,0.87,0.89]) translate([0,0,enclosure_depth+explode]) rotate([0,180,0]) rear_cover();
    color("orange") translate(esp_position+[0,0,explode/2]) frame(esp_size);
    color("orange") translate(oled_position+[0,0,explode/2]) frame(oled_size,oled_holes);
    color("orange") right_side(speaker_position+[explode,0,0])
        translate([0,0,-speaker_flange_thickness-mount_thickness]) speaker_mount();
    if(show_electronics && explode==0) %electronics();
    if(explode==0 && $preview) preview_details();
}
checks() {
    if(part=="assembly") rotate([preview_upright ? 90 : 0,0,0]) assembly();
    else if(part=="exploded") rotate([preview_upright ? 90 : 0,0,0]) assembly(35);
    // Export positions put the broad flat face on the XY print bed at Z=0.
    else if(part=="main_enclosure") translate([0,0,-wall_thickness]) main_enclosure();
    else if(part=="front_bezel") front_bezel();
    else if(part=="front_faceplate") front_faceplate();
    else if(part=="rear_cover") rear_cover();
    else if(part=="esp32_mount") frame(esp_size);
    else if(part=="oled_mount") frame(oled_size,oled_holes);
    else if(part=="speaker_mount") speaker_mount();
    else assert(false,str("Unknown part: ",part));
}
