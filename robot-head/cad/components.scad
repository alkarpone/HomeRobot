// All printable modules are single connected solids. Electronics are preview only.
module rounded_outline(w,h,r) {
    // Octagonal silhouette with broad facets and slightly softened edges.
    offset(r=outline_softening) offset(delta=-outline_softening)
        polygon([[-w/2+r,0],[w/2-r,0],[w/2,r],[w/2,h-r],
                 [w/2-r,h],[-w/2+r,h],[-w/2,h-r],[-w/2,r]]);
}
module bezel_aperture() {
    // Wide dark face with a narrower camera turret, following the reference.
    face_half=enclosure_width/2-13;
    bottom=12;
    shoulder=camera_position.y-12;
    top=camera_position.y+14;
    turret=14;
    chamfer=4;
    polygon([[-face_half+chamfer,bottom],[face_half-chamfer,bottom],
        [face_half,bottom+chamfer],[face_half,shoulder-chamfer],
        [face_half-chamfer,shoulder],[camera_position.x+turret,shoulder],
        [camera_position.x+turret,top-chamfer],[camera_position.x+turret-chamfer,top],
        [camera_position.x-turret+chamfer,top],[camera_position.x-turret,top-chamfer],
        [camera_position.x-turret,shoulder],[-face_half+chamfer,shoulder],
        [-face_half,shoulder-chamfer],[-face_half,bottom+chamfer]]);
}
module front_bezel() {
    difference() {
        hull() {
            linear_extrude(eps) offset(delta=-bezel_bevel)
                rounded_outline(enclosure_width,enclosure_height,corner_radius);
            translate([0,0,bezel_bevel]) linear_extrude(bezel_thickness-bezel_bevel)
                rounded_outline(enclosure_width,enclosure_height,corner_radius);
        }
        translate([0,0,-eps]) linear_extrude(bezel_thickness+2*eps) bezel_aperture();
        cover_points() bore(screw_diameter,bezel_thickness);
    }
}
module plate(w,h,t,r=corner_radius) {
    // Small perimeter bevel, with a flat printable face.
    hull() {
        linear_extrude(eps) offset(delta=-edge_bevel) rounded_outline(w,h,r);
        translate([0,0,edge_bevel]) linear_extrude(t-edge_bevel)
            rounded_outline(w,h,r);
    }
}
module bore(d,h) { translate([0,0,-eps]) cylinder(d=d,h=h+2*eps); }
module slot(length,width,height) {
    hull() for(x=[-(length-width)/2,(length-width)/2])
        translate([x,0,0]) bore(width,height);
}
module cover_points() {
    for(x=[-cover_x,cover_x],y=cover_y) translate([x,y,0]) children();
}
// Local XY is side-panel Z/Y; local positive Z points outward (+X).
module right_side(p) { translate(p) rotate([0,90,0]) children(); }
module mounting_boss(h) {
    difference() { cylinder(d=boss_diameter,h=h); bore(pilot_diameter,h); }
}
module frame(s,holes=[]) {
    f=frame_size(s);
    difference() {
        translate([-f.x/2,-f.y/2,0]) cube([f.x,f.y,mount_thickness]);
        // Narrow edge shelf supports PCB; the component field stays open.
        translate([-(s.x-4)/2,-(s.y-4)/2,-eps])
            cube([s.x-4,s.y-4,mount_thickness+2*eps]);
        for(p=mount_points(s)) translate([p.x,p.y,0]) bore(screw_diameter,mount_thickness);
        for(x=[-1,1],y=[-1,1])
            translate([x*(s.x/2-2),y*(s.y/2+3),0]) slot(4,2,mount_thickness);
        for(p=holes) translate([p.x,p.y,0]) bore(oled_hole_diameter,mount_thickness);
    }
}
module front_standoffs(pos,s) {
    for(p=mount_points(s)) translate([pos.x+p.x,pos.y+p.y,wall_thickness-eps])
        mounting_boss(pos.z-wall_thickness+eps);
}
module front_faceplate() {
    difference() {
        union() {
            plate(enclosure_width,enclosure_height,wall_thickness);
            front_standoffs(esp_position,esp_size);
            front_standoffs(oled_position,oled_size);
            // Loose camera-body locator on the same datum as the complete PCB carrier.
            // Native offset drives both this pocket and the optical opening.
            translate([camera_position.x,camera_position.y,wall_thickness-eps])
                linear_extrude(3+eps) difference() {
                    square([camera_body_size.x+2*pcb_clearance+4,camera_body_size.y+2*pcb_clearance+4],center=true);
                    square([camera_body_size.x+2*pcb_clearance,camera_body_size.y+2*pcb_clearance],center=true);
                    // Open top and bottom for the existing ribbon; no closed cable slot.
                    square([camera_body_size.x-4,camera_body_size.y+10],center=true);
                }
        }
        cover_points() bore(screw_diameter,wall_thickness);
        // Shallow chamfered camera well; the circular optical opening remains through.
        translate([camera_position.x,camera_position.y,0]) hull() {
            translate([0,-10,-eps]) linear_extrude(eps) rounded_outline(20,20,3);
            translate([0,-9,0.8]) linear_extrude(eps) rounded_outline(18,18,2);
        }
        translate([camera_position.x,camera_position.y,0]) bore(camera_opening_diameter,wall_thickness);
        translate([oled_position.x+oled_window_offset.x-oled_window.x/2,
                   oled_position.y+oled_window_offset.y-oled_window.y/2,-eps])
            cube([oled_window.x,oled_window.y,wall_thickness+2*eps]);
    }
}
// Mirror a top-edge feature onto the bottom edge without changing its Z datum.
module rear_edges() {
    children();
    translate([0,enclosure_height,0]) mirror([0,1,0]) children();
}
module rear_snap() {
    outer=enclosure_height-wall_thickness-snap_gap;
    end=wall_thickness+snap_length;
    // Long beam with a wider, tapered root; no detached clip to lose.
    translate([-snap_width/2,outer-snap_thickness,wall_thickness-eps])
        cube([snap_width,snap_thickness,snap_length+eps]);
    hull() {
        translate([-snap_width/2,outer-snap_thickness-1.2,wall_thickness-eps])
            cube([snap_width,snap_thickness+1.2,eps]);
        translate([-snap_width/2,outer-snap_thickness,wall_thickness+3])
            cube([snap_width,snap_thickness,eps]);
    }
    // Square retaining shoulder followed by a 45-degree insertion ramp.
    translate([-snap_width/2,outer-eps,end-3])
        cube([snap_width,snap_projection+eps,1]);
    hull() {
        translate([-snap_width/2,outer-eps,end-2])
            cube([snap_width,snap_projection+eps,eps]);
        translate([-snap_width/2,outer-eps,end])
            cube([snap_width,eps,eps]);
    }
}
module rear_guides() {
    outer=enclosure_height-wall_thickness-snap_gap;
    // Short guide lips leave a large free flexure zone around each latch.
    for(x=[-35,12]) translate([x,outer-1.4,wall_thickness-eps])
        cube([23,1.4,3+eps]);
}
module rear_snap_windows() {
    end=wall_thickness+snap_length;
    rear_edges() translate([-snap_width/2-snap_window_clearance,
        enclosure_height-wall_thickness-eps,
        enclosure_depth-end-snap_window_clearance])
        cube([snap_width+2*snap_window_clearance,wall_thickness+2*eps,
              3+2*snap_window_clearance]);
}
module rear_cover() {
    difference() {
        union() {
            plate(enclosure_width,enclosure_height,wall_thickness);
            if(rear_attachment=="snap") rear_edges() { rear_snap(); rear_guides(); }
            // Integral amplifier edge shelf, retained by two replaceable nylon ties.
            translate([amp_position.x,amp_position.y,wall_thickness-eps])
                difference() {
                    cube([amp_size.x+6,amp_size.y+6,4],center=true);
                    cube([amp_size.x-4,amp_size.y-4,10],center=true);
                }
        }
        if(rear_attachment=="screws") cover_points() bore(screw_diameter,wall_thickness);
        for(x=[-1,1],y=[-1,1]) translate([amp_position.x+x*(amp_size.x/2+1),
            amp_position.y+y*5,0]) rotate([0,0,90]) slot(5,2,wall_thickness+4);
        for(x=[-12,-6,0,6,12]) translate([x,enclosure_height-16,0])
            rotate([0,0,90]) slot(12,2,wall_thickness);
    }
}
module speaker_mount() {
    difference() {
        cylinder(d=speaker_diameter+2*speaker_ring_width,h=mount_thickness);
        bore(speaker_body_diameter+2*speaker_clearance,mount_thickness);
        for(a=[0:90:270]) rotate([0,0,a]) translate([speaker_screw_radius,0,0])
            bore(pilot_diameter,mount_thickness);
    }
}
module main_enclosure() {
    difference() {
        union() {
            translate([0,0,wall_thickness]) linear_extrude(enclosure_depth-2*wall_thickness)
                difference() {
                    rounded_outline(enclosure_width,enclosure_height,corner_radius);
                    offset(delta=-wall_thickness) rounded_outline(enclosure_width,enclosure_height,corner_radius);
                }
            // Continuous square corner rails overlap shell; accessible pilots at both ends.
            intersection() {
                for(x=[-cover_x,cover_x],y=cover_y)
                    translate([x-4.8,y-4.8,wall_thickness])
                        cube([9.6,9.6,enclosure_depth-2*wall_thickness]);
                linear_extrude(enclosure_depth)
                    rounded_outline(enclosure_width,enclosure_height,corner_radius);
            }
            // Microphone support annulus, against the opposite wall.
            translate(mic_position) rotate([0,-90,0]) translate([0,0,-2])
                difference() { cylinder(d=mic_diameter+4,h=2+eps); bore(mic_diameter-4,2+eps); }
        }
        cover_points() {
            translate([0,0,wall_thickness-eps]) bore(pilot_diameter,cover_engagement);
            translate([0,0,enclosure_depth-wall_thickness-cover_engagement]) bore(pilot_diameter,cover_engagement+eps);
        }
        if(rear_attachment=="snap") rear_snap_windows();
        // Grille holes have short 2.4 mm bridges in the prescribed print orientation.
        right_side(speaker_position) {
            // Shallow circular inset makes the grille read as a separate disc.
            translate([0,0,wall_thickness-grille_recess]) bore(speaker_diameter+3,grille_recess+eps);
            for(u=[-16:4:16],v=[-16:4:16])
                if(u*u+v*v<18*18) translate([u,v,-eps]) bore(2.8,wall_thickness+eps);
        }
        right_side(speaker_position) for(a=[0:90:270]) rotate([0,0,a])
            translate([speaker_screw_radius,0,-eps]) bore(screw_diameter,wall_thickness+eps);
        translate(mic_position) rotate([0,-90,0]) {
            translate([0,0,-3]) bore(mic_port_diameter,wall_thickness+3);
            for(u=[-1,1],v=[-1,1]) translate([u*(mic_diameter/2+1),v*3,-3])
                slot(3,1.6,wall_thickness+3);
        }
        // Large service window accepts the original plug directly, no ribbon extension.
        translate([-enclosure_width/2-eps,usb_window_y-usb_window_height/2,
                   usb_window_z-usb_window_depth/2])
            cube([wall_thickness+2*eps,usb_window_height,usb_window_depth]);
    }
}
module electronics() {
    color("green") translate([esp_position.x-esp_size.x/2,esp_position.y-esp_size.y/2,esp_position.z-esp_size.z]) cube(esp_size);
    color("black") translate([camera_position.x-camera_body_size.x/2,camera_position.y-camera_body_size.y/2,camera_position.z]) cube(camera_body_size);
    color("navy") translate([oled_position.x-oled_size.x/2,oled_position.y-oled_size.y/2,oled_position.z-oled_size.z]) cube(oled_size);
    color("dimgray") right_side(speaker_position) {
        translate([0,0,-speaker_flange_thickness]) cylinder(d=speaker_diameter,h=speaker_flange_thickness);
        translate([0,0,-speaker_depth]) cylinder(d=speaker_body_diameter,h=speaker_depth-speaker_flange_thickness);
    }
    color("teal") translate(mic_position) rotate([0,-90,0]) translate([0,0,-3.6]) cylinder(d=mic_diameter,h=1.6);
    color("green") translate([0,0,enclosure_depth]) rotate([0,180,0])
        translate([amp_position.x-amp_size.x/2,amp_position.y-amp_size.y/2,wall_thickness+2]) cube(amp_size);
}

// Display and fasteners are explanatory preview objects, never STL contents.
module preview_details() {
    if(show_hardware) {
        color("silver") cover_points() translate([0,0,-bezel_thickness-1.5])
            difference() {
                cylinder(d=5.5,h=1.5);
                translate([-1.7,-0.4,-eps]) cube([3.4,0.8,0.6]);
                translate([-0.4,-1.7,-eps]) cube([0.8,3.4,0.6]);
            }
        color("silver") right_side(speaker_position)
            for(a=[0:90:270]) rotate([0,0,a]) translate([speaker_screw_radius,0,wall_thickness])
                cylinder(d=5.5,h=1.5);
    }
    // Actual OLED-size mock screen; does not imply a larger physical display.
    color([0.02,0.04,0.08]) translate([oled_position.x-oled_window.x/2,
        oled_position.y-oled_window.y/2,wall_thickness+0.3])
        cube([oled_window.x,oled_window.y,0.1]);
    if(show_face_expression) color([0.55,0.9,1])
        for(eye=[-1,1],i=[-2:2])
            translate([oled_position.x+eye*7+i*1.5-0.7,
                oled_position.y+2-abs(i)*1.5,wall_thickness+0.15]) cube([1.4,1.4,0.1]);
    color([0.12,0.14,0.18]) translate([camera_position.x,camera_position.y,0.9])
        difference() { cylinder(d=11,h=1.4); bore(7.5,1.4); }
    color([0.08,0.18,0.27]) translate([camera_position.x,camera_position.y,1.2]) cylinder(d=7.5,h=0.2);
}
