using System;
using System.Collections.Generic;
using UnityEngine;

namespace RescueBot.Core
{
    public enum ObsKind { Car, Debris, Rubble }
    public enum EdgeStatus { Clear, Rough, Blocked }

    [Serializable]
    public class CityConfig
    {
        public int nx = 5, ny = 5;          // blocks (grid cells) in x / z; intersections = (n+1)
        public float spacing = 60f;         // road centre to road centre (m)
        public float roadWidth = 12f;
        public float severity = 0.5f;       // 0..1 disaster intensity
        public int seed = 1;
        public bool ensureReachable = true; // downgrade blockages until every intersection can be reached
        public CityConfig Clone() => (CityConfig)MemberwiseClone();
    }

    /// <summary>Disaster obstacle: Car (solid), Rubble (solid, building collapse), Debris (low, can be driven over).</summary>
    public class Obstacle
    {
        public ObsKind kind;
        public Vector2 pos;      // world x, z
        public float yaw;        // degrees about Y, local forward = (sin, cos)
        public Vector2 size;     // x = width (local right), y = length (local forward)
        public float height;
        public int variant;
        public int edge;
        public int id;
        public bool Solid => kind != ObsKind.Debris;
        public float Radius => 0.5f * Mathf.Sqrt(size.x * size.x + size.y * size.y);

        public Vector2 ToLocal(Vector2 p)
        {
            float r = yaw * Mathf.Deg2Rad, s = Mathf.Sin(r), c = Mathf.Cos(r);
            var d = p - pos;
            return new Vector2(d.x * c - d.y * s, d.x * s + d.y * c);
        }

        /// <summary>Distance from p to the rectangle (0 when inside).</summary>
        public float Distance(Vector2 p)
        {
            var l = ToLocal(p);
            float dx = Mathf.Max(Mathf.Abs(l.x) - size.x * 0.5f, 0f);
            float dy = Mathf.Max(Mathf.Abs(l.y) - size.y * 0.5f, 0f);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
    }

    public struct Building { public Vector2 center, size; public float height; public int variant; }

    public class Edge
    {
        public int id, a, b;
        public float length;
        public Vector2 dir;
        public bool horizontal;
        public List<Obstacle> obstacles = new List<Obstacle>();
        public EdgeStatus truth;
        public int roughCount;
        public float TruthCost => truth == EdgeStatus.Blocked ? float.PositiveInfinity : length + roughCount * City.RoughPenalty;
    }

    /// <summary>
    /// Grid city: (nx+1) x (ny+1) intersections joined by straight roads. A disaster scatters obstacles on the roads.
    /// Deterministic from CityConfig.seed.
    /// </summary>
    public class City
    {
        public const float RobotGap = 4.0f;      // narrowest gap the rover fits through (m)
        public const float RoughPenalty = 30f;   // metres-equivalent cost of one debris crossing
        public const float NodeClear = 14f;      // obstacle free zone around intersections

        public readonly CityConfig cfg;
        public int nodesX, nodesY;
        public Vector2[] nodes;
        public List<Edge> edges = new List<Edge>();
        public List<int>[] adj;
        public List<Obstacle> obstacles = new List<Obstacle>();
        public List<Building> buildings = new List<Building>();
        public float width, depth;
        public int blockedEdges, roughEdges, downgraded;
        System.Random rng;

        public int Node(int i, int j) => j * nodesX + i;
        public Vector2 Center => new Vector2(width * 0.5f, depth * 0.5f);
        public static Vector2 Right(Vector2 d) => new Vector2(d.y, -d.x);

        public City(CityConfig c)
        {
            cfg = c.Clone();
            cfg.nx = Mathf.Clamp(cfg.nx, 1, 12); cfg.ny = Mathf.Clamp(cfg.ny, 1, 12);
            nodesX = cfg.nx + 1; nodesY = cfg.ny + 1;
            width = cfg.nx * cfg.spacing; depth = cfg.ny * cfg.spacing;
            rng = new System.Random(cfg.seed * 7919 + cfg.nx * 131 + cfg.ny * 17);
            nodes = new Vector2[nodesX * nodesY];
            adj = new List<int>[nodes.Length];
            for (int j = 0; j < nodesY; j++)
                for (int i = 0; i < nodesX; i++) { nodes[Node(i, j)] = new Vector2(i * cfg.spacing, j * cfg.spacing); adj[Node(i, j)] = new List<int>(); }
            for (int j = 0; j < nodesY; j++)
                for (int i = 0; i < nodesX; i++)
                {
                    if (i + 1 < nodesX) AddEdge(Node(i, j), Node(i + 1, j), true);
                    if (j + 1 < nodesY) AddEdge(Node(i, j), Node(i, j + 1), false);
                }
            BuildBuildings();
            foreach (var e in edges) Populate(e);
            foreach (var e in edges) Analyze(e);
            if (cfg.ensureReachable) EnsureReachable();
            int id = 0;
            foreach (var e in edges) foreach (var o in e.obstacles) { o.id = id++; obstacles.Add(o); }
            foreach (var e in edges) { if (e.truth == EdgeStatus.Blocked) blockedEdges++; else if (e.truth == EdgeStatus.Rough) roughEdges++; }
        }

        void AddEdge(int a, int b, bool horizontal)
        {
            var e = new Edge { id = edges.Count, a = a, b = b, horizontal = horizontal };
            var d = nodes[b] - nodes[a];
            e.length = d.magnitude; e.dir = d / e.length;
            edges.Add(e); adj[a].Add(e.id); adj[b].Add(e.id);
        }

        public int Other(Edge e, int n) => e.a == n ? e.b : e.a;
        public Vector2 EdgePoint(Edge e, float t, float lat) => nodes[e.a] + e.dir * t + Right(e.dir) * lat;
        float Rnd(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        // ------------------------------------------------------------------ buildings

        void BuildBuildings()
        {
            float hw = cfg.roadWidth * 0.5f + 3.5f; // road half width + sidewalk
            for (int bj = 0; bj < cfg.ny; bj++)
                for (int bi = 0; bi < cfg.nx; bi++)
                {
                    float x0 = bi * cfg.spacing + hw, x1 = (bi + 1) * cfg.spacing - hw;
                    float z0 = bj * cfg.spacing + hw, z1 = (bj + 1) * cfg.spacing - hw;
                    int cols = rng.Next(2, 4), rows = rng.Next(1, 3);
                    float gap = 2.5f;
                    float cw = (x1 - x0 - gap * (cols - 1)) / cols, rh = (z1 - z0 - gap * (rows - 1)) / rows;
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            if (rng.NextDouble() < 0.08) continue; // empty lot
                            float w = cw * Rnd(0.8f, 1f), d = rh * Rnd(0.8f, 1f);
                            var ctr = new Vector2(x0 + c * (cw + gap) + cw * 0.5f, z0 + r * (rh + gap) + rh * 0.5f);
                            buildings.Add(new Building { center = ctr, size = new Vector2(w, d), height = Rnd(6f, 24f), variant = rng.Next(0, 8) });
                        }
                }
        }

        // ------------------------------------------------------------------ disaster

        void Populate(Edge e)
        {
            float sev = Mathf.Clamp01(cfg.severity);
            if (sev <= 0f || e.length < 2 * NodeClear + 6f) return;
            if (rng.NextDouble() < sev * 0.28) AddBlock(e);
            float expected = sev * 2.8f;
            int n = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
            for (int k = 0; k < n; k++) TryAdd(e);
        }

        float BaseYaw(Edge e) => Mathf.Atan2(e.dir.x, e.dir.y) * Mathf.Rad2Deg;

        void TryAdd(Edge e)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double r = rng.NextDouble();
                var o = new Obstacle { edge = e.id, variant = rng.Next(0, 6) };
                float by = BaseYaw(e);
                if (r < 0.40)
                {
                    o.kind = ObsKind.Car; o.size = new Vector2(1.9f, 4.5f); o.height = 1.5f;
                    o.yaw = by + (rng.NextDouble() < 0.7 ? Rnd(-22f, 22f) : 90f + Rnd(-25f, 25f)) + (rng.NextDouble() < 0.5 ? 0f : 180f);
                }
                else if (r < 0.75)
                {
                    o.kind = ObsKind.Debris; o.size = new Vector2(Rnd(1.8f, 4.2f), Rnd(1.2f, 3.2f)); o.height = 0.35f; o.yaw = Rnd(0f, 360f);
                }
                else
                {
                    o.kind = ObsKind.Rubble; o.size = new Vector2(Rnd(3f, 7f), Rnd(2.5f, 5f)); o.height = Rnd(1.6f, 3f); o.yaw = by + Rnd(-35f, 35f);
                }
                float rad = o.Radius;
                float lat = Rnd(-(cfg.roadWidth * 0.5f - rad * 0.75f), cfg.roadWidth * 0.5f - rad * 0.75f);
                if (cfg.roadWidth * 0.5f - rad * 0.75f < 0) lat = 0;
                float t = Rnd(NodeClear + rad, e.length - NodeClear - rad);
                o.pos = EdgePoint(e, t, lat);
                bool ok = true;
                foreach (var q in e.obstacles) if ((q.pos - o.pos).magnitude < (q.Radius + rad) * 0.9f) { ok = false; break; }
                if (!ok) continue;
                e.obstacles.Add(o);
                return;
            }
        }

        void AddBlock(Edge e)
        {
            float by = BaseYaw(e);
            float t = Rnd(NodeClear + 5f, e.length - NodeClear - 5f);
            double kind = rng.NextDouble();
            if (kind < 0.25)
            {
                // scattered debris band across the whole road: the rover can drive over it
                e.obstacles.Add(new Obstacle { edge = e.id, kind = ObsKind.Debris, pos = EdgePoint(e, t, 0f), yaw = by, size = new Vector2(cfg.roadWidth + 1f, Rnd(2f, 3.2f)), height = 0.4f, variant = rng.Next(0, 6) });
            }
            else if (kind < 0.62)
            {
                // collapsed building: rubble pile across the whole road
                e.obstacles.Add(new Obstacle { edge = e.id, kind = ObsKind.Rubble, pos = EdgePoint(e, t, 0f), yaw = by, size = new Vector2(cfg.roadWidth + 1.5f, Rnd(3f, 5f)), height = Rnd(2f, 3.2f), variant = rng.Next(0, 6) });
            }
            else
            {
                // pile-up: cars across the road
                foreach (float lat in new[] { -3.6f, 0f, 3.6f })
                    e.obstacles.Add(new Obstacle { edge = e.id, kind = ObsKind.Car, pos = EdgePoint(e, t + Rnd(-1f, 1f), lat), yaw = by + 90f + Rnd(-12f, 12f), size = new Vector2(1.9f, 4.5f), height = 1.5f, variant = rng.Next(0, 6) });
            }
        }

        public string GapProfile(Edge e)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"road {e.id} {e.a}-{e.b} len={e.length:0} truth={e.truth} obstacles={e.obstacles.Count}");
            foreach (var o in obstacles) if (Vector2.Distance(o.pos, nodes[e.a] + e.dir * (e.length * 0.5f)) < e.length * 0.6f) sb.AppendLine($"  GLOBAL id={o.id} edge={o.edge} {o.kind} pos=({o.pos.x:0.0},{o.pos.y:0.0}) yaw={o.yaw:0}");
            foreach (var o in e.obstacles) sb.AppendLine($"  {o.kind} pos=({o.pos.x:0.0},{o.pos.y:0.0}) t={Vector2.Dot(o.pos - nodes[e.a], e.dir):0.0} yaw={o.yaw:0} size=({o.size.x:0.0}x{o.size.y:0.0})");
            float lo = -cfg.roadWidth * 0.5f + 0.3f, hi = cfg.roadWidth * 0.5f - 0.3f; const float step = 0.25f; int nl = Mathf.FloorToInt((hi - lo) / step) + 1;
            for (float t = NodeClear; t <= e.length - NodeClear; t += 2f)
            {
                int run = 0, best = 0;
                for (int k = 0; k < nl; k++)
                {
                    var p = EdgePoint(e, t, lo + k * step); bool solid = false;
                    foreach (var o in e.obstacles) { if (o.Solid && o.Distance(p) <= 0f) solid = true; }
                    if (!solid) { run++; best = Mathf.Max(best, run); } else run = 0;
                }
                sb.AppendLine($"  t={t:0} widest free gap={best * step:0.0}");
            }
            return sb.ToString();
        }
        /// <summary>Ground-truth passability of a road: widest free gap per cross-section.</summary>
        void Analyze(Edge e)
        {
            e.truth = EdgeStatus.Clear; e.roughCount = 0;
            if (e.obstacles.Count == 0) return;
            float lo = -cfg.roadWidth * 0.5f + 0.3f, hi = cfg.roadWidth * 0.5f - 0.3f;
            const float step = 0.25f;
            int nl = Mathf.FloorToInt((hi - lo) / step) + 1;
            var roughT = new List<float>();
            bool blocked = false;
            for (float t = NodeClear; t <= e.length - NodeClear && !blocked; t += 0.5f)
            {
                int runSolid = 0, bestSolid = 0, runAll = 0, bestAll = 0;
                for (int k = 0; k < nl; k++)
                {
                    var p = EdgePoint(e, t, lo + k * step);
                    bool solid = false, debris = false;
                    foreach (var o in e.obstacles)
                    {
                        if ((o.pos - p).sqrMagnitude > (o.Radius + 0.1f) * (o.Radius + 0.1f)) continue;
                        if (o.Distance(p) <= 0f) { if (o.Solid) solid = true; else debris = true; }
                    }
                    if (!solid) { runSolid++; bestSolid = Mathf.Max(bestSolid, runSolid); } else runSolid = 0;
                    if (!solid && !debris) { runAll++; bestAll = Mathf.Max(bestAll, runAll); } else runAll = 0;
                }
                if (bestSolid * step < RobotGap) blocked = true;
                else if (bestAll * step < RobotGap) roughT.Add(t);
            }
            if (blocked) { e.truth = EdgeStatus.Blocked; return; }
            if (roughT.Count == 0) return;
            e.truth = EdgeStatus.Rough;
            foreach (var o in e.obstacles)
            {
                if (o.Solid) continue;
                float ot = Vector2.Dot(o.pos - nodes[e.a], e.dir);
                foreach (var t in roughT) if (Mathf.Abs(t - ot) < o.Radius + 0.5f) { e.roughCount++; break; }
            }
            if (e.roughCount == 0) e.roughCount = 1;
        }

        void EnsureReachable()
        {
            var parent = new int[nodes.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var blocked = new List<Edge>();
            foreach (var e in edges)
            {
                if (e.truth == EdgeStatus.Blocked) blocked.Add(e);
                else parent[Find(e.a)] = Find(e.b);
            }
            // random order
            for (int i = blocked.Count - 1; i > 0; i--) { int j = rng.Next(0, i + 1); var tmp = blocked[i]; blocked[i] = blocked[j]; blocked[j] = tmp; }
            foreach (var e in blocked)
            {
                if (Find(e.a) == Find(e.b)) continue;
                parent[Find(e.a)] = Find(e.b);
                // replace the blockage by a low debris band that can be driven over
                e.obstacles.Clear();
                float t = e.length * 0.5f;
                e.obstacles.Add(new Obstacle { edge = e.id, kind = ObsKind.Debris, pos = EdgePoint(e, t, 0f), yaw = BaseYaw(e), size = new Vector2(cfg.roadWidth + 1f, Rnd(2f, 3f)), height = 0.4f, variant = rng.Next(0, 6) });
                Analyze(e);
                downgraded++;
            }
            // blocked edges that were not needed stay blocked (alternative routes exist)
        }

        // ------------------------------------------------------------------ geometry queries

        public bool OnRoad(Vector2 p, float margin = 0f)
        {
            float hw = cfg.roadWidth * 0.5f - margin;
            int ix = Mathf.Clamp(Mathf.RoundToInt(p.x / cfg.spacing), 0, cfg.nx);
            int iz = Mathf.Clamp(Mathf.RoundToInt(p.y / cfg.spacing), 0, cfg.ny);
            if (Mathf.Abs(p.x - ix * cfg.spacing) <= hw && p.y >= -hw && p.y <= depth + hw) return true;
            if (Mathf.Abs(p.y - iz * cfg.spacing) <= hw && p.x >= -hw && p.x <= width + hw) return true;
            return false;
        }

        public int NearestNode(Vector2 p, out float dist)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < nodes.Length; i++) { float d = (nodes[i] - p).sqrMagnitude; if (d < bd) { bd = d; best = i; } }
            dist = Mathf.Sqrt(bd); return best;
        }

        /// <summary>Closest road segment; t = distance along the edge from node a.</summary>
        public Edge NearestEdge(Vector2 p, out float t, out float dist)
        {
            Edge best = null; float bd = float.MaxValue; t = 0;
            foreach (var e in edges)
            {
                float s = Mathf.Clamp(Vector2.Dot(p - nodes[e.a], e.dir), 0f, e.length);
                float d = (nodes[e.a] + e.dir * s - p).sqrMagnitude;
                if (d < bd) { bd = d; best = e; t = s; }
            }
            dist = Mathf.Sqrt(bd); return best;
        }

        public Obstacle SolidHit(Vector2 p, float radius)
        {
            foreach (var o in obstacles)
            {
                if (!o.Solid) continue;
                if ((o.pos - p).sqrMagnitude > (o.Radius + radius) * (o.Radius + radius)) continue;
                if (o.Distance(p) < radius) return o;
            }
            return null;
        }

        public Obstacle DebrisAt(Vector2 p)
        {
            foreach (var o in obstacles)
            {
                if (o.kind != ObsKind.Debris) continue;
                if ((o.pos - p).sqrMagnitude > (o.Radius + 0.5f) * (o.Radius + 0.5f)) continue;
                if (o.Distance(p) < 0.4f) return o;
            }
            return null;
        }

        // ------------------------------------------------------------------ routing

        /// <summary>Dijkstra over intersections. cost returns +inf for impassable roads. Returns the node list from..to or null.</summary>
        public List<int> Route(int from, int to, Func<Edge, float> cost, out float total)
        {
            int n = nodes.Length;
            var dist = new float[n]; var prev = new int[n]; var done = new bool[n];
            for (int i = 0; i < n; i++) { dist[i] = float.PositiveInfinity; prev[i] = -1; }
            dist[from] = 0;
            for (int it = 0; it < n; it++)
            {
                int u = -1; float bd = float.PositiveInfinity;
                for (int i = 0; i < n; i++) if (!done[i] && dist[i] < bd) { bd = dist[i]; u = i; }
                if (u < 0) break;
                done[u] = true;
                if (u == to) break;
                foreach (int ei in adj[u])
                {
                    var e = edges[ei];
                    float c = cost(e);
                    if (float.IsPositiveInfinity(c)) continue;
                    int v = Other(e, u);
                    if (dist[u] + c < dist[v]) { dist[v] = dist[u] + c; prev[v] = u; }
                }
            }
            total = dist[to];
            if (float.IsPositiveInfinity(total)) return null;
            var path = new List<int>();
            for (int x = to; x != -1; x = prev[x]) path.Add(x);
            path.Reverse();
            return path;
        }

        public Edge EdgeBetween(int a, int b)
        {
            foreach (int ei in adj[a]) { var e = edges[ei]; if (Other(e, a) == b) return e; }
            return null;
        }
    }
}
