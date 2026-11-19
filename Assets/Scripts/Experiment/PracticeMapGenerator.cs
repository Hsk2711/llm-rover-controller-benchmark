// PracticeMapGenerator: makes a random PRACTICE map from a seed (5-15 box obstacles, sometimes including a small
// U-shape). It is used ONLY to show the chatbots an example map when asking them for controller code (Phase 6); it is
// never one of the four test maps. The same seed always gives the same map (seeded System.Random, never UnityEngine.Random).
// Every box is kept off the start and goal zones, and a grid flood-fill proves that a free path from the start zone
// centre to the goal zone centre still exists for a rover-sized body; a box that would block it is rejected.
using System.Collections.Generic;
using UnityEngine;

public static class PracticeMapGenerator
{
    // Practice-map settings (these are NOT study parameters; the arena, zone and obstacle sizes come from SimConfig).
    const int MinBoxes = 5, MaxBoxes = 15;      // total boxes, a U-shape counts as 3
    const float MinSide = 0.3f, MaxSide = 0.6f; // single box side lengths (m)
    const double UShapeChance = 0.35;           // chance that a map contains one small U-shape
    const float ZoneMargin = 0.3f;              // boxes stay at least this far away from the start / goal zone squares (m)
    const float MinBoxGap = 0.05f;              // boxes never overlap: at least this gap between two obstacles (m)
    const float PathClearance = 0.2f;           // flood-fill: the rover is treated as a disc this big (rover half-diagonal is 0.125 m)
    const float GridCell = 0.05f;               // flood-fill grid cell (m)
    const int AttemptsPerPlacement = 300;

    /// <summary>Build a practice map. The result is a runtime-only MapDefinition (not saved as an asset).</summary>
    public static MapDefinition Generate(int seed, SimConfig config)
    {
        var rng = new System.Random(seed);
        var map = ScriptableObject.CreateInstance<MapDefinition>();
        map.name = "Practice_" + seed;

        float half = config.arenaSize * 0.5f;
        float zoneCentre = half - config.zoneSize * 0.5f;
        map.startZoneCentre = new Vector2(-zoneCentre, -zoneCentre);     // bottom-left
        map.goalZoneCentre = new Vector2(zoneCentre, zoneCentre);        // top-right

        int target = rng.Next(MinBoxes, MaxBoxes + 1);                   // 5..15
        bool withU = rng.NextDouble() < UShapeChance;

        var boxes = map.obstacles;
        float zoneHalf = config.zoneSize * 0.5f;
        Vector2[] startSquare = Square(map.startZoneCentre, zoneHalf);
        Vector2[] goalSquare = Square(map.goalZoneCentre, zoneHalf);

        if (withU)
        {
            for (int attempt = 0; attempt < AttemptsPerPlacement; attempt++)
            {
                var u = RandomU(rng, half, config.wallThickness);
                if (TryAdd(boxes, u, half, startSquare, goalSquare, map)) break;
            }
        }

        int guard = 0;
        while (boxes.Count < target && guard++ < AttemptsPerPlacement * MaxBoxes)
        {
            var single = new List<BoxObstacle> { RandomBox(rng, half) };
            TryAdd(boxes, single, half, startSquare, goalSquare, map);
        }
        return map;
    }

    static BoxObstacle RandomBox(System.Random rng, float half)
    {
        float margin = MaxSide;
        return new BoxObstacle
        {
            position = new Vector2(Range(rng, -half + margin, half - margin), Range(rng, -half + margin, half - margin)),
            rotationDeg = Range(rng, 0f, 180f),
            size = new Vector2(Range(rng, MinSide, MaxSide), Range(rng, MinSide, MaxSide)),
        };
    }

    // A small U: back wall and two side walls (thickness t), opening towards -a. Inside width 0.7-1.0 m, depth 0.5-0.8 m.
    static List<BoxObstacle> RandomU(System.Random rng, float half, float t)
    {
        float w = Range(rng, 0.7f, 1.0f);
        float depth = Range(rng, 0.5f, 0.8f);
        float theta = Range(rng, 0f, 360f);
        Vector2 c = new Vector2(Range(rng, -half + 1.2f, half - 1.2f), Range(rng, -half + 1.2f, half - 1.2f));
        float r = theta * Mathf.Deg2Rad;
        Vector2 a = new Vector2(Mathf.Cos(r), Mathf.Sin(r));     // the U's own x axis: points to the closed end
        Vector2 b = new Vector2(-Mathf.Sin(r), Mathf.Cos(r));    // the U's own z axis: across the opening
        return new List<BoxObstacle>
        {
            new BoxObstacle { position = c + a * (depth * 0.5f + t * 0.5f), rotationDeg = theta, size = new Vector2(t, w) },
            new BoxObstacle { position = c + a * (t * 0.5f) + b * (w * 0.5f + t * 0.5f), rotationDeg = theta, size = new Vector2(depth + t, t) },
            new BoxObstacle { position = c + a * (t * 0.5f) - b * (w * 0.5f + t * 0.5f), rotationDeg = theta, size = new Vector2(depth + t, t) },
        };
    }

    // Add the group of boxes only if all of them are inside the arena, clear of both zones and of the existing boxes,
    // and a free path from start to goal still exists afterwards.
    static bool TryAdd(List<BoxObstacle> boxes, List<BoxObstacle> group, float half,
                       Vector2[] startSquare, Vector2[] goalSquare, MapDefinition map)
    {
        foreach (var g in group)
        {
            Vector2[] c = Corners(g);
            foreach (var p in c) if (Mathf.Abs(p.x) > half - 0.02f || Mathf.Abs(p.y) > half - 0.02f) return false;
            if (PolygonDistance(c, startSquare) < ZoneMargin) return false;
            if (PolygonDistance(c, goalSquare) < ZoneMargin) return false;
            foreach (var other in boxes) if (PolygonDistance(c, Corners(other)) < MinBoxGap) return false;
        }
        var test = new List<BoxObstacle>(boxes);
        test.AddRange(group);
        if (!PathExists(test, map.startZoneCentre, map.goalZoneCentre, half)) return false;
        boxes.AddRange(group);
        return true;
    }

    // ---- free-path check: grid flood-fill -------------------------------------------------------------------------
    static bool PathExists(List<BoxObstacle> boxes, Vector2 start, Vector2 goal, float half)
    {
        int n = Mathf.RoundToInt(2f * half / GridCell);
        var blocked = new bool[n * n];
        for (int iz = 0; iz < n; iz++)
            for (int ix = 0; ix < n; ix++)
            {
                var p = new Vector2(-half + (ix + 0.5f) * GridCell, -half + (iz + 0.5f) * GridCell);
                bool b = Mathf.Abs(p.x) > half - PathClearance || Mathf.Abs(p.y) > half - PathClearance;
                for (int k = 0; !b && k < boxes.Count; k++) b = DistanceToBox(p, boxes[k]) < PathClearance;
                blocked[iz * n + ix] = b;
            }

        int Cell(Vector2 p) => Mathf.Clamp((int)((p.y + half) / GridCell), 0, n - 1) * n + Mathf.Clamp((int)((p.x + half) / GridCell), 0, n - 1);
        int s = Cell(start), g = Cell(goal);
        if (blocked[s] || blocked[g]) return false;

        var seen = new bool[n * n];
        var queue = new Queue<int>();
        queue.Enqueue(s);
        seen[s] = true;
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (cur == g) return true;
            int cx = cur % n, cz = cur / n;
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = cz + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= n || nz >= n) continue;
                int ni = nz * n + nx;
                if (blocked[ni] || seen[ni]) continue;
                seen[ni] = true;
                queue.Enqueue(ni);
            }
        }
        return false;
    }

    // ---- geometry helpers -----------------------------------------------------------------------------------------
    static float Range(System.Random rng, float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

    static Vector2[] Square(Vector2 c, float h) =>
        new[] { c + new Vector2(-h, -h), c + new Vector2(h, -h), c + new Vector2(h, h), c + new Vector2(-h, h) };

    static Vector2[] Corners(BoxObstacle b)
    {
        float r = b.rotationDeg * Mathf.Deg2Rad;
        Vector2 ax = new Vector2(Mathf.Cos(r), Mathf.Sin(r)) * (b.size.x * 0.5f);
        Vector2 az = new Vector2(-Mathf.Sin(r), Mathf.Cos(r)) * (b.size.y * 0.5f);
        return new[] { b.position - ax - az, b.position + ax - az, b.position + ax + az, b.position - ax + az };
    }

    // Distance from a point to a (rotated) box; 0 when the point is inside.
    static float DistanceToBox(Vector2 p, BoxObstacle b)
    {
        float r = b.rotationDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(r), sin = Mathf.Sin(r);
        Vector2 d = p - b.position;
        float lx = d.x * cos + d.y * sin;           // point in the box's own axes
        float lz = -d.x * sin + d.y * cos;
        float dx = Mathf.Max(Mathf.Abs(lx) - b.size.x * 0.5f, 0f);
        float dz = Mathf.Max(Mathf.Abs(lz) - b.size.y * 0.5f, 0f);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Smallest distance between two convex polygons (0 if they overlap or touch).
    static float PolygonDistance(Vector2[] a, Vector2[] b)
    {
        if (!SeparatedBySomeEdge(a, b) && !SeparatedBySomeEdge(b, a)) return 0f;
        float best = float.MaxValue;
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
            {
                best = Mathf.Min(best, PointToSegment(a[i], b[j], b[(j + 1) % b.Length]));
                best = Mathf.Min(best, PointToSegment(b[j], a[i], a[(i + 1) % a.Length]));
            }
        return best;
    }

    // true if some edge of 'a' has all of 'b' strictly on its outer side (a separating axis)
    static bool SeparatedBySomeEdge(Vector2[] a, Vector2[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            Vector2 e = a[(i + 1) % a.Length] - a[i];
            Vector2 normal = new Vector2(e.y, -e.x);                         // outward for a counter-clockwise polygon
            float aMax = float.MinValue, bMin = float.MaxValue;
            foreach (var p in a) aMax = Mathf.Max(aMax, Vector2.Dot(normal, p));
            foreach (var p in b) bMin = Mathf.Min(bMin, Vector2.Dot(normal, p));
            if (bMin > aMax + 1e-5f) return true;
        }
        return false;
    }

    static float PointToSegment(Vector2 p, Vector2 s0, Vector2 s1)
    {
        Vector2 d = s1 - s0;
        float t = d.sqrMagnitude < 1e-12f ? 0f : Mathf.Clamp01(Vector2.Dot(p - s0, d) / d.sqrMagnitude);
        return Vector2.Distance(p, s0 + d * t);
    }
}
