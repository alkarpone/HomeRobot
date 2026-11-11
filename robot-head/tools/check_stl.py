"""Check OpenSCAD ASCII STL topology and print bounds without dependencies."""
from collections import defaultdict
from pathlib import Path
import json


def inspect(path):
    vertices = {}
    faces = []
    triangle = []
    for line in path.read_text().splitlines():
        fields = line.split()
        if fields and fields[0] == "vertex":
            point = tuple(float(v) for v in fields[1:])
            triangle.append(vertices.setdefault(point, len(vertices)))
            if len(triangle) == 3:
                assert len(set(triangle)) == 3, f"Degenerate triangle: {path}"
                faces.append(tuple(triangle))
                triangle = []
    assert faces and not triangle, f"Empty or malformed ASCII STL: {path}"
    edges = defaultdict(list)
    adjacency = defaultdict(set)
    for index, face in enumerate(faces):
        for a, b in zip(face, face[1:] + face[:1]):
            edges[tuple(sorted((a, b)))].append((index, a, b))
    for uses in edges.values():
        assert len(uses) == 2, f"Non-manifold edge: {path}"
        (i, a, b), (j, c, d) = uses
        assert a == d and b == c, f"Inconsistent winding: {path}"
        adjacency[i].add(j)
        adjacency[j].add(i)
    seen = set()
    pending = [0]
    while pending:
        face = pending.pop()
        if face not in seen:
            seen.add(face)
            pending.extend(adjacency[face] - seen)
    assert len(seen) == len(faces), f"Disconnected triangle shells: {path}"
    # For a closed 2-manifold, every vertex link must be a single cycle.
    links = defaultdict(lambda: defaultdict(set))
    for a, b, c in faces:
        for v, p, q in [(a,b,c), (b,c,a), (c,a,b)]:
            links[v][p].add(q)
            links[v][q].add(p)
    for link in links.values():
        assert all(len(neighbors) == 2 for neighbors in link.values()), path
        reached, todo = set(), [next(iter(link))]
        while todo:
            v = todo.pop()
            if v not in reached:
                reached.add(v)
                todo.extend(link[v] - reached)
        assert len(reached) == len(link), f"Non-manifold vertex: {path}"
    points = list(vertices)
    lower = [min(p[i] for p in points) for i in range(3)]
    upper = [max(p[i] for p in points) for i in range(3)]
    bounds = [round(b-a, 4) for a, b in zip(lower, upper)]
    assert abs(lower[2]) < 0.001, f"Part not on print bed: {path}"
    assert all(0 < size < 240 for size in bounds), f"Print bounds exceeded: {path}"
    return {"file": path.name, "triangles": len(faces), "bounds_mm": bounds,
            "closed_connected_manifold": True, "consistent_winding": True}


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    names = ["front_bezel", "main_enclosure", "front_faceplate", "rear_cover", "esp32_mount",
             "oled_mount", "speaker_mount"]
    print(json.dumps([inspect(root / "stl" / f"{name}.stl") for name in names], indent=2))
