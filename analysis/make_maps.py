"""make_maps.py: defines the four TEST maps of the study, checks every rule of the map table, then writes them as Unity
MapDefinition assets (Assets/Maps/*.asset) and a preview picture (docs/figures/maps_overview.png).
Run from the project folder:  venv/Scripts/python.exe analysis/make_maps.py
Coordinates: metres, x = east, z = north, arena centre (0,0), arena -3..+3. Box = (x, z, rotation_deg, size_x, size_z);
positive rotation = counter-clockwise seen from above (= LEFT), size_x runs along the box's own x axis.
The script stops with an error if a rule is broken, so the assets are never written from a wrong map."""
import math
import random
import sys
from collections import deque
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MAP_DIR = ROOT / "Assets" / "Maps"
SCRIPT_GUID = "7c1e5b0a93d44f2e8a6b2d9f4c3e1a70"          # MapDefinition.cs.meta

HALF = 3.0                                                # arena spans -HALF..+HALF
ZONE = 1.0                                                # zone size (SimConfig.zoneSize)
START, GOAL = (-2.5, -2.5), (2.5, 2.5)                    # bottom-left, top-right
T = 0.1                                                   # thin-wall thickness (SimConfig.wallThickness)
ROVER_R = 0.125                                           # rover treated as a disc (half of its 0.25 m diagonal)


# ---------------------------------------------------------------- geometry
def corners(b):
    x, z, deg, sx, sz = b
    r = math.radians(deg)
    ax = (math.cos(r) * sx / 2, math.sin(r) * sx / 2)
    az = (-math.sin(r) * sz / 2, math.cos(r) * sz / 2)
    return [(x - ax[0] - az[0], z - ax[1] - az[1]), (x + ax[0] - az[0], z + ax[1] - az[1]),
            (x + ax[0] + az[0], z + ax[1] + az[1]), (x - ax[0] + az[0], z - ax[1] + az[1])]


def square(c, h=ZONE / 2):
    return [(c[0] - h, c[1] - h), (c[0] + h, c[1] - h), (c[0] + h, c[1] + h), (c[0] - h, c[1] + h)]


def pt_seg(p, a, b):
    dx, dz = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dz * dz
    t = 0 if l2 == 0 else max(0, min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dz) / l2))
    return math.hypot(p[0] - (a[0] + t * dx), p[1] - (a[1] + t * dz))


def separated(a, b):
    for i in range(4):
        e = (a[(i + 1) % 4][0] - a[i][0], a[(i + 1) % 4][1] - a[i][1])
        n = (e[1], -e[0])
        amax = max(n[0] * p[0] + n[1] * p[1] for p in a)
        bmin = min(n[0] * p[0] + n[1] * p[1] for p in b)
        if bmin > amax + 1e-9:
            return True
    return False


def poly_dist(a, b):
    if not separated(a, b) and not separated(b, a):
        return 0.0
    return min(min(pt_seg(a[i], b[j], b[(j + 1) % 4]) for i in range(4) for j in range(4)),
               min(pt_seg(b[j], a[i], a[(i + 1) % 4]) for i in range(4) for j in range(4)))


def dist_to_box(p, b):
    x, z, deg, sx, sz = b
    r = math.radians(deg)
    dx, dz = p[0] - x, p[1] - z
    lx = dx * math.cos(r) + dz * math.sin(r)
    lz = -dx * math.sin(r) + dz * math.cos(r)
    return math.hypot(max(abs(lx) - sx / 2, 0), max(abs(lz) - sz / 2, 0))


def wall_gap(b):
    """Smallest gap between a box and the four arena walls."""
    c = corners(b)
    return min(HALF + min(p[0] for p in c), HALF - max(p[0] for p in c),
               HALF + min(p[1] for p in c), HALF - max(p[1] for p in c))


def path_exists(boxes, clearance=ROVER_R, cell=0.05):
    n = round(2 * HALF / cell)
    blocked = [[False] * n for _ in range(n)]
    for iz in range(n):
        for ix in range(n):
            p = (-HALF + (ix + .5) * cell, -HALF + (iz + .5) * cell)
            blocked[iz][ix] = (abs(p[0]) > HALF - clearance or abs(p[1]) > HALF - clearance
                               or any(dist_to_box(p, b) < clearance for b in boxes))
    cellof = lambda p: (int((p[0] + HALF) / cell), int((p[1] + HALF) / cell))
    s, g = cellof(START), cellof(GOAL)
    if blocked[s[1]][s[0]] or blocked[g[1]][g[0]]:
        return False
    seen, q = {s}, deque([s])
    while q:
        c = q.popleft()
        if c == g:
            return True
        for d in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, nz = c[0] + d[0], c[1] + d[1]
            if 0 <= nx < n and 0 <= nz < n and not blocked[nz][nx] and (nx, nz) not in seen:
                seen.add((nx, nz))
                q.append((nx, nz))
    return False


def shortest_path(boxes, clearance=0.10, cell=0.05):
    """Shortest collision-free path from the start-zone centre to the goal-zone centre.
    The rover is a point that must stay at least `clearance` away from every box and every arena wall. Dijkstra on a grid
    (16 neighbours), then straightened by line-of-sight shortcuts. Returns (grid length, straightened length in metres,
    the straightened path as a list of (x, z) points)."""
    import heapq
    import numpy as np
    n = round(2 * HALF / cell)
    xs = -HALF + (np.arange(n) + .5) * cell
    X, Z = np.meshgrid(xs, xs)                             # X[iz][ix], Z[iz][ix]
    blocked = (np.abs(X) > HALF - clearance) | (np.abs(Z) > HALF - clearance)
    for x, z, deg, sx, sz in boxes:
        r = math.radians(deg)
        dx, dz = X - x, Z - z
        lx = dx * math.cos(r) + dz * math.sin(r)
        lz = -dx * math.sin(r) + dz * math.cos(r)
        blocked |= np.hypot(np.maximum(np.abs(lx) - sx / 2, 0), np.maximum(np.abs(lz) - sz / 2, 0)) < clearance

    def cell_of(p):
        return min(n - 1, int((p[1] + HALF) / cell)), min(n - 1, int((p[0] + HALF) / cell))   # (iz, ix)

    def centre(c):
        return (-HALF + (c[1] + .5) * cell, -HALF + (c[0] + .5) * cell)

    def visible(p, q):                                     # straight segment p-q stays in free cells
        k = max(1, int(math.hypot(q[0] - p[0], q[1] - p[1]) / (cell / 2)))
        for i in range(k + 1):
            c = cell_of((p[0] + (q[0] - p[0]) * i / k, p[1] + (q[1] - p[1]) * i / k))
            if blocked[c]:
                return False
        return True

    s, g = cell_of(START), cell_of(GOAL)
    if blocked[s] or blocked[g]:
        sys.exit("shortest_path: start or goal is blocked")
    moves = [(a, b) for a in range(-2, 3) for b in range(-2, 3) if (a, b) != (0, 0) and math.gcd(abs(a), abs(b)) == 1]
    dist = {s: 0.0}
    prev = {}
    heap = [(0.0, s)]
    while heap:
        d, c = heapq.heappop(heap)
        if c == g:
            break
        if d > dist.get(c, 1e18):
            continue
        for a, b in moves:
            nc = (c[0] + a, c[1] + b)
            if not (0 <= nc[0] < n and 0 <= nc[1] < n) or blocked[nc]:
                continue
            if max(abs(a), abs(b)) > 1 or (a != 0 and b != 0):
                if not visible(centre(c), centre(nc)):
                    continue
            nd = d + math.hypot(a, b) * cell
            if nd < dist.get(nc, 1e18):
                dist[nc] = nd
                prev[nc] = c
                heapq.heappush(heap, (nd, nc))
    if g not in dist:
        sys.exit("shortest_path: no path")
    cells = [g]
    while cells[-1] != s:
        cells.append(prev[cells[-1]])
    cells.reverse()
    pts = [START] + [centre(c) for c in cells[1:-1]] + [GOAL]
    grid_len = sum(math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(pts, pts[1:]))
    out, i = [pts[0]], 0                                   # straighten: jump to the farthest visible point
    while i < len(pts) - 1:
        j = len(pts) - 1
        while j > i + 1 and not visible(pts[i], pts[j]):
            j -= 1
        out.append(pts[j])
        i = j
    return grid_len, sum(math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(out, out[1:])), out


def stats(boxes):
    """(min gap between two boxes, min gap box-to-wall, min gap box-to-zone)"""
    cs = [corners(b) for b in boxes]
    bb = min((poly_dist(cs[i], cs[j]) for i in range(len(cs)) for j in range(i + 1, len(cs))), default=99)
    bw = min((wall_gap(b) for b in boxes), default=99)
    bz = min(min(poly_dist(c, square(START)), poly_dist(c, square(GOAL))) for c in cs)
    return bb, bw, bz


# ---------------------------------------------------------------- the four maps
def map_m1():
    """Open: 5 boxes 0.4-0.6 m, wide free paths."""
    return [(-1.2, -1.0, 30, 0.5, 0.5), (1.1, 1.2, -20, 0.6, 0.4), (-1.4, 1.3, 45, 0.5, 0.5),
            (1.3, -1.2, 10, 0.4, 0.6), (0.1, -0.1, 60, 0.5, 0.5)]


def map_m2():
    """Cluttered: 18 boxes 0.3-0.6 m, every gap (box-box, box-wall, box-zone) at least 0.5 m."""
    rng = random.Random(20270)
    min_gap = 0.52                                         # a little over 0.5 so rounding can never break the rule
    boxes = []
    for _ in range(200000):
        if len(boxes) == 18:
            break
        b = (rng.uniform(-HALF + .3, HALF - .3), rng.uniform(-HALF + .3, HALF - .3), rng.uniform(0, 90),
             rng.uniform(.3, .6), rng.uniform(.3, .6))
        c = corners(b)
        if wall_gap(b) < min_gap:
            continue
        if min(poly_dist(c, square(START)), poly_dist(c, square(GOAL))) < min_gap:
            continue
        if any(poly_dist(c, corners(o)) < min_gap for o in boxes):
            continue
        boxes.append(b)
    if len(boxes) != 18:
        sys.exit("M2: could not place 18 boxes")
    return boxes


def map_m3():
    """Corridor: a right-angle staircase (zig-zag) corridor 0.5 m wide and 5.5 m long, built from thin walls, between two
    sealed corner rooms (1.75 m x 1.75 m, containing the start / goal zones). The corridor is 0.5 m wide between the INNER
    faces of the walls. Everything outside the rooms and the corridor is sealed off, so the corridor is the only way."""
    cell, n = 0.25, 24
    room = 1.75                                            # side of the two corner rooms
    free = [[False] * n for _ in range(n)]                 # free[i][j]: cell column i (x), row j (z)

    def fill(x0, x1, z0, z1):
        for i in range(n):
            for j in range(n):
                cx, cz = -HALF + (i + .5) * cell, -HALF + (j + .5) * cell
                if x0 < cx < x1 and z0 < cz < z1:
                    free[i][j] = True

    fill(-HALF, -HALF + room, -HALF, -HALF + room)         # start room (bottom-left corner)
    fill(HALF - room, HALF, HALF - room, HALF)             # goal room (top-right corner)
    # centre line of the corridor: leaves the start room through its north side, enters the goal room through its south
    # side; the shape is the same after a 180 degree turn (fair for the two ends). Legs: N .75, E 1.5, N 1, E 1.5, N .75.
    way = [(-1.5, -1.25), (-1.5, -0.5), (0.0, -0.5), (0.0, 0.5), (1.5, 0.5), (1.5, 1.25)]
    for a, b in zip(way, way[1:]):                         # 0.5 m wide lanes along the centre line
        fill(min(a[0], b[0]) - .25, max(a[0], b[0]) + .25, min(a[1], b[1]) - .25, max(a[1], b[1]) + .25)

    def is_free(i, j):
        return 0 <= i < n and 0 <= j < n and free[i][j]

    # boundary edges between a free cell and a solid cell (the arena walls themselves need no extra wall)
    edges = {}                                             # (orientation, line index, normal sign) -> [start indices]
    for i in range(n):
        for j in range(n):
            if not free[i][j]:
                continue
            for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ni, nj = i + dx, j + dz
                if not (0 <= ni < n and 0 <= nj < n) or free[ni][nj]:
                    continue
                if dz != 0:
                    edges.setdefault(("h", j + 1 if dz > 0 else j, dz), []).append(i)
                else:
                    edges.setdefault(("v", i + 1 if dx > 0 else i, dx), []).append(j)

    def free_count(vi, vj):                                # free cells among the 4 around grid vertex (vi, vj)
        return sum(is_free(a, b) for a in (vi - 1, vi) for b in (vj - 1, vj))

    boxes = []
    for (ori, line, sign), starts in edges.items():
        starts.sort()
        runs, s = [], starts[0]
        for prev, cur in zip(starts, starts[1:] + [None]):
            if cur != prev + 1:
                runs.append((s, prev + 1))
                s = cur
        for a, b in runs:                                  # run from grid index a to b along the line
            ext_a = ext_b = 0.0
            for end, which in ((a, "a"), (b, "b")):
                vi, vj = (end, line) if ori == "h" else (line, end)
                on_edge = vi in (0, n) or vj in (0, n)
                if free_count(vi, vj) == 1 and not on_edge:        # convex corner of the free space: cover it
                    if which == "a":
                        ext_a = T
                    else:
                        ext_b = T
            length = (b - a) * cell + ext_a + ext_b
            mid = -HALF + (a + b) / 2 * cell + (ext_b - ext_a) / 2
            fixed = -HALF + line * cell + sign * T / 2
            boxes.append((mid, fixed, 0, length, T) if ori == "h" else (fixed, mid, 0, T, length))
    return boxes, way


def map_m4():
    """Trap: a U-shaped dead end (inside 1.5 m wide, 1.2 m deep) on the start-goal line, opening towards the start,
    plus 3 extra boxes."""
    w, depth = 1.5, 1.2
    g = math.sqrt(0.5)                                     # unit vector (g, g): along the start->goal diagonal
    ax, az = (g, g), (-g, g)                               # U's own axes (rotation 45 deg): depth axis, width axis
    c = (0.0, 0.0)                                         # centre of the inside of the U (the diagonal passes through it)

    def at(da, dz):
        return (c[0] + ax[0] * da + az[0] * dz, c[1] + ax[1] * da + az[1] * dz)

    back = at(depth / 2 + T / 2, 0) + (45, T, w)                           # closed end, on the goal side
    side1 = at(T / 2, w / 2 + T / 2) + (45, depth + T, T)
    side2 = at(T / 2, -(w / 2 + T / 2)) + (45, depth + T, T)
    u = [back, side1, side2]
    extra = [(-1.3, 1.1, 25, 0.5, 0.5), (1.3, -1.1, -25, 0.5, 0.5), (-0.7, -2.0, 70, 0.4, 0.6)]
    return u + extra, len(u)


# ---------------------------------------------------------------- checks
def check(cond, text):
    print(("  ok    " if cond else "  FAIL  ") + text)
    if not cond:
        sys.exit("map rule broken: " + text)


def inside_arena(boxes):
    return all(abs(p[0]) <= HALF + 1e-9 and abs(p[1]) <= HALF + 1e-9 for b in boxes for p in corners(b))


def sides_in(boxes, lo, hi):
    return all(lo - 1e-9 <= s <= hi + 1e-9 for b in boxes for s in b[3:5])


def verify_all():
    maps = {}
    print("M1_Open")
    m1 = map_m1()
    bb, bw, bz = stats(m1)
    check(len(m1) == 5, "5 boxes")
    check(sides_in(m1, .4, .6), "all sides 0.4-0.6 m")
    check(inside_arena(m1), "inside the arena")
    check(bz > 0.5, "clear of both zones (min gap %.2f m)" % bz)
    check(bb >= 0.8 and bw >= 0.8, "wide free paths: min gap box-box %.2f m, box-wall %.2f m" % (bb, bw))
    check(path_exists(m1), "free path start -> goal")
    maps["M1_Open"] = m1

    print("M2_Cluttered")
    m2 = map_m2()
    bb, bw, bz = stats(m2)
    check(len(m2) == 18, "18 boxes")
    check(sides_in(m2, .3, .6), "all sides 0.3-0.6 m")
    check(inside_arena(m2), "inside the arena")
    check(min(bb, bw, bz) >= 0.5, "gaps never below 0.5 m: box-box %.3f, box-wall %.3f, box-zone %.3f" % (bb, bw, bz))
    check(path_exists(m2), "free path start -> goal")
    maps["M2_Cluttered"] = m2

    print("M3_Corridor")
    m3, way = map_m3()
    check(inside_arena(m3), "inside the arena")
    cs = [corners(b) for b in m3]
    check(min(poly_dist(c, square(START, ZONE / 2 - 1e-3)) for c in cs) > 0.0 and
          min(poly_dist(c, square(GOAL, ZONE / 2 - 1e-3)) for c in cs) > 0.0,
          "no wall enters the start or goal zone (walls may touch their edges)")
    # corridor width: along the centre line the nearest wall must be 0.25 m away (corridor 0.5 m wide), never less
    pts = []
    for a, b in zip(way, way[1:]):
        k = max(1, int(math.hypot(b[0] - a[0], b[1] - a[1]) / 0.05))
        pts += [(a[0] + (b[0] - a[0]) * i / k, a[1] + (b[1] - a[1]) * i / k) for i in range(k + 1)]
    clear = [min(dist_to_box(p, b) for b in m3) for p in pts]
    check(abs(min(clear) - 0.25) < 1e-6, "corridor is 0.5 m wide (nearest wall to centre line: %.3f m, mean %.3f m)"
          % (min(clear), sum(clear) / len(clear)))
    check(path_exists(m3), "free path start -> goal along the corridor")
    corridor_len = sum(math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(way, way[1:]))
    check(5.0 <= corridor_len <= 6.0, "corridor centre line is %.1f m long (target 5-6 m), %d wall boxes" % (corridor_len, len(m3)))
    maps["M3_Corridor"] = m3

    print("M4_Trap")
    m4, nu = map_m4()
    u, extra = m4[:nu], m4[nu:]
    check(len(extra) == 3 and sides_in(extra, .3, .6), "3 extra boxes, sides 0.3-0.6 m")
    check(inside_arena(m4), "inside the arena")
    bb, bw, bz = stats(m4)
    check(bz > 0.5, "clear of both zones (min gap %.2f m)" % bz)
    ucs = [corners(b) for b in u]
    egap = min(poly_dist(corners(e), c) for e in extra for c in ucs)
    check(egap >= 0.5 and min(wall_gap(e) for e in extra) >= 0.5,
          "extra boxes clear of the U (%.2f m) and the walls" % egap)
    # the opening faces the start: the closed end (back wall) is on the goal side of the U centre
    check(u[0][0] > 0 and u[0][1] > 0, "closed end of the U is on the goal side (%.2f, %.2f)" % (u[0][0], u[0][1]))
    # walking the straight start->goal line, the first thing met is the inside face of the closed end,
    # i.e. the line enters through the opening and runs into the dead end
    line = [(-2.5 + 5.0 * k / 500, -2.5 + 5.0 * k / 500) for k in range(501)]
    first = next((p for p in line if any(dist_to_box(p, b) < 1e-9 for b in u)), None)
    inner_face = 0.6 / math.sqrt(2)                        # half the inside depth, as an x = z coordinate
    check(first is not None and abs(first[0] - inner_face) < 0.02,
          "the start-goal line runs into the closed end of the U (first contact x = z = %.2f)" % first[0])
    check(path_exists(m4), "a free path start -> goal still exists around the U")
    maps["M4_Trap"] = m4
    return maps


# ---------------------------------------------------------------- output
def fmt(v):
    s = ("%.4f" % v).rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def write_asset(name, boxes):
    lines = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!114 &11400000", "MonoBehaviour:",
             "  m_ObjectHideFlags: 0", "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
             "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: 0}", "  m_Enabled: 1", "  m_EditorHideFlags: 0",
             "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % SCRIPT_GUID, "  m_Name: " + name,
             "  m_EditorClassIdentifier: Assembly-CSharp::MapDefinition",
             "  startZoneCentre: {x: %s, y: %s}" % (fmt(START[0]), fmt(START[1])),
             "  goalZoneCentre: {x: %s, y: %s}" % (fmt(GOAL[0]), fmt(GOAL[1])), "  obstacles:"]
    for x, z, deg, sx, sz in boxes:
        lines += ["  - position: {x: %s, y: %s}" % (fmt(x), fmt(z)), "    rotationDeg: " + fmt(deg),
                  "    size: {x: %s, y: %s}" % (fmt(sx), fmt(sz))]
    (MAP_DIR / (name + ".asset")).write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def draw_map(ax, name, boxes, path, length):
    from matplotlib.patches import Polygon, Rectangle
    ax.add_patch(Rectangle((-HALF, -HALF), 2 * HALF, 2 * HALF, fill=False, lw=2.5, color="black"))
    ax.add_patch(Rectangle((START[0] - .5, START[1] - .5), 1, 1, color="#4caf50", alpha=.40))
    ax.add_patch(Rectangle((GOAL[0] - .5, GOAL[1] - .5), 1, 1, color="#2196f3", alpha=.40))
    for b in boxes:
        ax.add_patch(Polygon(corners(b), color="#707070"))
    ax.plot([p[0] for p in path], [p[1] for p in path], ls="--", lw=1.6, color="#e65100", label="shortest path %.2f m" % length)
    ax.text(START[0], START[1] - .38, "START", ha="center", fontsize=8, color="#1b5e20")
    ax.text(GOAL[0], GOAL[1] + .30, "GOAL", ha="center", fontsize=8, color="#0d47a1")
    ax.set_xlim(-HALF - .15, HALF + .15)
    ax.set_ylim(-HALF - .15, HALF + .15)
    ax.set_aspect("equal")
    ax.set_title("%s  (%d boxes)" % (name, len(boxes)), fontsize=11)
    ax.set_xlabel("x (m)")
    ax.legend(loc="lower right", fontsize=8, framealpha=.9)


def draw(maps, paths, lengths):
    """Quick schematic of the four maps with their shortest paths (docs/figures/maps_overview.png). The paper's Figure 2
    pictures are rendered by Unity instead (docs/figures/maps/)."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    out_dir = ROOT / "docs" / "figures"
    out_dir.mkdir(parents=True, exist_ok=True)
    fig, axes = plt.subplots(1, 4, figsize=(18, 4.9))
    for ax, (name, boxes) in zip(axes, maps.items()):
        draw_map(ax, name, boxes, paths[name], lengths[name])
    axes[0].set_ylabel("z (m)")
    fig.tight_layout()
    fig.savefig(out_dir / "maps_overview.png", dpi=130)
    plt.close(fig)
    print("preview written to", out_dir / "maps_overview.png")


MAX_PATH = 8.0        # m: a controller at half speed (0.15 m/s) must still be able to finish within 60 s


def path_table(maps):
    """Shortest collision-free path (0.10 m clearance) start centre -> goal centre, for every map."""
    straight = math.hypot(GOAL[0] - START[0], GOAL[1] - START[1])
    print()
    print("Shortest collision-free path, start-zone centre -> goal-zone centre (0.10 m clearance)")
    print("%-14s %9s %9s %7s %10s %10s" % ("map", "grid (m)", "short (m)", "x line", "t @0.30", "t @0.15"))
    result, paths = {}, {}
    for name, boxes in maps.items():
        grid, short, paths[name] = shortest_path(boxes)
        result[name] = short
        print("%-14s %9.2f %9.2f %7.2f %8.0f s %8.0f s" % (name, grid, short, short / straight, short / 0.30, short / 0.15))
    print("straight line start-goal = %.2f m;  limit = %.1f m" % (straight, MAX_PATH))
    return result, paths


if __name__ == "__main__":
    all_maps = verify_all()
    lengths, paths = path_table(all_maps)
    too_long = [k for k, v in lengths.items() if v > MAX_PATH]
    if too_long:
        sys.exit("path longer than %.1f m: %s" % (MAX_PATH, ", ".join(too_long)))
    MAP_DIR.mkdir(parents=True, exist_ok=True)
    for map_name, map_boxes in all_maps.items():
        write_asset(map_name, map_boxes)
        print("wrote", MAP_DIR / (map_name + ".asset"))
    draw(all_maps, paths, lengths)
