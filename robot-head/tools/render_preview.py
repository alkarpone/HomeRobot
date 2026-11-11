"""Blender studio render from the exported default STLs; no shape substitution.

Run: Blender --background --python robot-head/tools/render_preview.py
Display pixels, lens and screw heads are illustrative purchased hardware only.
Coordinates below match the documented default parameters, not arbitrary overrides.
"""
from pathlib import Path
from math import pi
import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[1]
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)

def material(name, color, metallic=0, roughness=0.4, emission=0):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1)
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = (*color, 1)
    shader.inputs['Metallic'].default_value = metallic
    shader.inputs['Roughness'].default_value = roughness
    if emission:
        shader.inputs['Emission Color'].default_value = (*color, 1)
        shader.inputs['Emission Strength'].default_value = emission
    return mat

white = material('Warm white PLA', (0.82,0.84,0.87), roughness=0.48)
black = material('Charcoal PLA', (0.028,0.034,0.042), roughness=0.5)
metal = material('Steel screw heads - illustrative', (0.4,0.43,0.48), 0.8,0.28)
glass = material('OLED dark glass - illustrative', (0.008,0.017,0.03), 0.2,0.15)
lens = material('Camera glass - illustrative', (0.02,0.07,0.1), 0.55,0.1)
eye = material('OLED pixels - illustrative', (0.36,0.74,1), emission=3)
bpy.ops.object.empty_add()
head = bpy.context.object
head.name = 'Default CAD assembly, Y-up converted to Z-up'
head.rotation_euler.x = pi/2

def assign(obj, mat, pos=(0,0,0)):
    obj.parent = head
    obj.location = Vector(pos)/100
    obj.data.materials.append(mat)
    return obj

def mesh(name, mat, pos=(0,0,0), rotation=(0,0,0)):
    bpy.ops.wm.stl_import(filepath=str(ROOT/'stl'/f'{name}.stl'), global_scale=0.01)
    obj = assign(bpy.context.object, mat, pos)
    obj.name = name
    obj.rotation_euler = rotation
    return obj

mesh('main_enclosure', white, (0,0,2.4))
mesh('front_bezel', white, (0,0,-4))
mesh('front_faceplate', black)
mesh('rear_cover', white, (0,0,86), (0,pi,0))
mesh('esp32_mount', black, (0,73,23))
mesh('oled_mount', black, (0,29,10))
mesh('speaker_mount', black, (47.6,45,54), (0,pi/2,0))

def box(name, pos, size, mat):
    bpy.ops.mesh.primitive_cube_add()
    obj = assign(bpy.context.object, mat, pos)
    obj.name = name
    obj.dimensions = Vector(size)/100
    return obj

def cylinder(name, pos, diameter, depth, mat, rotation=(0,0,0)):
    bpy.ops.mesh.primitive_cylinder_add(vertices=64, radius=diameter/200, depth=depth/100)
    obj=assign(bpy.context.object,mat,pos)
    obj.name=name
    obj.rotation_euler=rotation
    return obj

box('OLED glass, true 30 x 16 mm window', (0,29,2.9), (30,16,0.5), glass)
for side in [-1,1]:
    for i in range(-2,3):
        box('Illustrative OLED pixel', (side*7+i*1.5,31-abs(i)*1.5,2.6), (1.4,1.4,0.1), eye)
cylinder('Lens barrel', (0,73,1.8), 11,1.7,black)
cylinder('Lens glass', (0,73,0.85), 7.5,0.15,lens)
for x in [-45,45]:
    for y in [10,90]:
        cylinder('M3 front head', (x,y,-4.7),5.5,1.4,metal)
        box('Screw drive visual', (x,y,-5.41), (3.2,0.65,0.04),black)
        box('Screw drive visual', (x,y,-5.41), (0.65,3.2,0.04),black)
for y,z in [(45,30),(45,78),(21,54),(69,54)]:
    cylinder('M3 speaker head', (55.7,y,z),5.5,1.4,metal,(0,pi/2,0))
cylinder('Speaker dark backing - envelope', (50.6,45,54),38,1,black,(0,pi/2,0))

floor=material('Studio floor',(0.72,0.75,0.79),roughness=0.75)
bpy.ops.mesh.primitive_plane_add(size=200, location=(0,0,-0.005))
bpy.context.object.data.materials.append(floor)
scene=bpy.context.scene
scene.render.engine='CYCLES'
scene.cycles.device='CPU'
scene.cycles.samples=40
scene.cycles.use_denoising=True
scene.world.color=(0.3,0.3,0.3)

def aim(obj, target):
    obj.rotation_euler=(Vector(target)-obj.location).to_track_quat('-Z','Y').to_euler()

for pos,power,size in [((1.3,2.0,3.5),450,2.5),((-2,1,1.8),230,2),((0,-2,2.8),350,2)]:
    bpy.ops.object.light_add(type='AREA',location=pos)
    light=bpy.context.object
    light.data.energy=power
    light.data.shape='DISK'
    light.data.size=size
    aim(light,(0,-0.4,0.5))
bpy.ops.object.camera_add(location=(2.2,3.4,1.85))
scene.camera=bpy.context.object
aim(scene.camera,(0,-0.35,0.48))
scene.camera.data.type='ORTHO'
scene.camera.data.ortho_scale=1.85
scene.render.resolution_x=1600
scene.render.resolution_y=1400
scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'
scene.render.filepath=str(ROOT/'docs'/'studio-preview.png')
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'docs'/'studio-preview.blend'))
bpy.ops.render.render(write_still=True)
