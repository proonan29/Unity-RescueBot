using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RescueBot.Core
{
    public enum SimState { Idle, Driving, Arrived, Failed }

    /// <summary>What the rover has learned about the roads from its camera (the city map itself is known, obstacles are not).</summary>
    public class Knowledge
    {
        public bool[] blocked, rough;
        public Knowledge(int edges) { blocked = new bool[edges]; rough = new bool[edges]; }
        public float Cost(Edge e) => blocked[e.id] ? float.PositiveInfinity : e.length + (rough[e.id] ? City.RoughPenalty : 0f);
    }

    public class TripStats
    {
        public int index, start, goal;
        public bool success; public string reason = "";
        public float time, dist, oracleCost, actualCost, efficiency;
        public int replans, blockedEvents, crossings, collisions;
    }

    /// <summary>
    /// Deterministic rover simulation on a City: fixed time step, kinematic tracked rover, camera-only perception,
    /// global route on the intersection graph (replanned when the camera finds a blocked or rough road).
    /// The camera image is supplied by the view through <see cref="grab"/>.
    /// </summary>
    public class RescueSim
    {
        public const float Dt = 0.05f, DecisionDt = 0.2f, LaneOffset = 3.0f, ArriveRadius = 6f, PauseAfterTrip = 1.5f;
        public const float Accel = 3f, Decel = 8f;

        public City city;
        public CamModel cam = new CamModel();
        public Driver driver = new Driver();
        public Func<Pose, Color32[]> grab;
        public Knowledge know;
        public bool loop = true;
        public int tripCounter;

        // rover state
        public Pose pose;
        public float v, yawRate, time;
        public SimState state = SimState.Idle;
        public Decision decision;
        public Scan scan;
        public bool onDebris;
        public float bump;

        // trip
        public int startNode, goalNode;
        public List<int> route = new List<int>(), oracleRoute = new List<int>();
        public List<Vector2> pts = new List<Vector2>();
        int seg;
        public TripStats trip = new TripStats();
        public List<TripStats> history = new List<TripStats>();
        public float tripStart;
        float decisionTimer, pauseTimer; public float stuckTimer; Vector2 stuckRef;
        int blockedTicks, noBlockTicks; readonly List<float> blockEpis = new List<float>(); float reverseTimer, nextRev = 8f, latchTime = -100f; Vector2 latchPt; bool wasColliding, retried;
        readonly HashSet<int> crossed = new HashSet<int>();
        public string message = "";
        public bool logging; public List<string> log = new List<string>();
        void Log(string s) { if (logging && log.Count < 4000) log.Add($"{time,6:0.0}s {s}"); }
        string P() => $"pos=({pose.pos.x:0},{pose.pos.y:0}) yaw={pose.yaw * Mathf.Rad2Deg:0}";
        System.Random rng;
        public int Seed;

        // totals
        public int tTrips, tSuccess, tFail, tCollisions, tReplans, tCrossings;
        public float tTime, tDist, tEffSum;

        public RescueSim(City c, int seed)
        {
            city = c; Seed = seed; rng = new System.Random(seed * 104729 + 17);
            know = new Knowledge(c.edges.Count);
        }

        public Vector2 GoalPos => city.nodes[goalNode];
        public float TripTime => time - tripStart;
        public int CrossedNow => crossed.Count;

        // ------------------------------------------------------------------ trips

        public void StartTrip()
        {
            tripCounter++;
            int n = city.nodes.Length, s = 0, g = 0; List<int> orc = null; float oc = 0;
            int minBlocks = Mathf.Max(2, (city.cfg.nx + city.cfg.ny) / 2);
            for (int attempt = 0; attempt < 80; attempt++)
            {
                s = rng.Next(0, n); g = rng.Next(0, n);
                int di = Mathf.Abs(s % city.nodesX - g % city.nodesX) + Mathf.Abs(s / city.nodesX - g / city.nodesX);
                if (di < minBlocks && attempt < 60) continue;
                orc = city.Route(s, g, e => e.TruthCost, out oc);
                if (orc != null && s != g) break;
            }
            startNode = s; goalNode = g;
            oracleRoute = orc ?? new List<int>();
            trip = new TripStats { index = tripCounter, start = s, goal = g, oracleCost = oc };
            crossed.Clear(); Array.Clear(know.blocked, 0, know.blocked.Length); stuckTimer = 0; blockEpis.Clear(); reverseTimer = 0f; nextRev = 8f; blockedTicks = 0; retried = false; wasColliding = false; v = 0; yawRate = 0;
            tripStart = time; decisionTimer = 0; driver.Reset(); driver.onRoad = p => city.OnRoad(p, 0.6f); driver.onRoadInner = p => city.OnRoad(p, 1.4f); noBlockTicks = 0; message = "";
            var r = city.Route(s, g, know.Cost, out float kc);
            if (r == null || r.Count < 2) { Finish(false, "no route"); return; }
            route = r;
            var d0 = (city.nodes[r[1]] - city.nodes[r[0]]).normalized;
            pose = new Pose { pos = city.nodes[s] + City.Right(d0) * LaneOffset, yaw = Mathf.Atan2(d0.x, d0.y) };
            BuildPolyline(pose.pos, r);
            state = SimState.Driving;
        }

        void Finish(bool ok, string reason)
        {
            Log($"FINISH {(ok ? "ok" : "FAIL")} {reason} {P()}"); trip.success = ok; trip.reason = reason; trip.time = time - tripStart;
            trip.crossings = crossed.Count;
            trip.actualCost = trip.dist + crossed.Count * City.RoughPenalty;
            trip.efficiency = ok && trip.actualCost > 1f && trip.oracleCost > 0 ? Mathf.Min(1f, trip.oracleCost / trip.actualCost) : 0f;
            history.Add(trip);
            tTrips++; if (ok) { tSuccess++; tEffSum += trip.efficiency; } else tFail++;
            tTime += trip.time; tDist += trip.dist; tCollisions += trip.collisions; tReplans += trip.replans; tCrossings += trip.crossings;
            state = ok ? SimState.Arrived : SimState.Failed; v = 0; yawRate = 0; pauseTimer = PauseAfterTrip;
            message = ok ? "arrived" : reason;
        }

        // ------------------------------------------------------------------ route polyline

        /// <summary>Lane-following polyline through the route; route[0] is the node the path starts from.</summary>
        void BuildPolyline(Vector2 start, List<int> rt)
        {
            pts.Clear(); pts.Add(start); seg = 0;
            if (rt.Count < 2) return;
            var din = (city.nodes[rt[1]] - city.nodes[rt[0]]).normalized;
            for (int i = 1; i < rt.Count; i++)
            {
                var node = city.nodes[rt[i]];
                var dout = i + 1 < rt.Count ? (city.nodes[rt[i + 1]] - node).normalized : din;
                float dot = Vector2.Dot(din, dout);
                if (dot > 0.99f) pts.Add(node + City.Right(din) * LaneOffset);
                else if (dot < -0.99f) { pts.Add(node + City.Right(din) * LaneOffset); pts.Add(node + City.Right(dout) * LaneOffset); }
                else pts.Add(node + (City.Right(din) + City.Right(dout)) * LaneOffset);
                din = dout;
            }
        }

        Vector2 LookaheadTarget()
        {
            if (pts.Count < 2) return pose.pos + pose.Fwd * 10f;
            while (seg < pts.Count - 2)
            {
                var a = pts[seg]; var b = pts[seg + 1]; var ab = b - a;
                float l2 = ab.sqrMagnitude;
                if (l2 < 1e-4f || Vector2.Dot(pose.pos - a, ab) / l2 >= 1f || (b - pose.pos).magnitude < 3f) seg++; else break;
            }
            var a0 = pts[seg]; var ab0 = pts[seg + 1] - a0;
            float ll = ab0.sqrMagnitude;
            float s = ll < 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(pose.pos - a0, ab0) / ll);
            var cur = a0 + ab0 * s;
            float remain = Mathf.Clamp(3f + 0.8f * v, 5f, 9f);
            int i = seg;
            while (true)
            {
                var nxt = pts[i + 1]; float d = (nxt - cur).magnitude;
                if (d >= remain || i + 1 >= pts.Count - 1) { cur += (nxt - cur).normalized * Mathf.Min(remain, d); break; }
                remain -= d; cur = nxt; i++;
            }
            return cur;
        }

        // ------------------------------------------------------------------ replanning

        void RePlan()
        {
            trip.replans++; Log($"REPLAN {P()}");
            float nd; int nn = city.NearestNode(pose.pos, out nd);
            if (nd < 8f)
            {
                var r = city.Route(nn, goalNode, know.Cost, out float tot);
                if (r == null) { NoRoute(); return; }
                route = r;
                if (r.Count < 2) { pts.Clear(); pts.Add(pose.pos); pts.Add(GoalPos); seg = 0; return; }
                BuildPolyline(pose.pos, r);
                return;
            }
            var e = city.NearestEdge(pose.pos, out float t, out float dd);
            bool ab = Vector2.Dot(pose.Fwd, e.dir) > 0f;
            int u = ab ? e.a : e.b, vv = ab ? e.b : e.a;
            float toV = ab ? e.length - t : t, toU = e.length - toV;
            float costF = float.PositiveInfinity, costB = float.PositiveInfinity;
            List<int> rF = null, rB = null;
            if (!know.blocked[e.id]) { rF = city.Route(vv, goalNode, know.Cost, out float tf); if (rF != null) costF = toV + (know.rough[e.id] ? City.RoughPenalty : 0f) + tf; }
            rB = city.Route(u, goalNode, know.Cost, out float tb); if (rB != null) costB = toU + 12f + tb;
            if (rF == null && rB == null) { NoRoute(); return; }
            List<int> list;
            if (costF <= costB) { list = new List<int> { u }; list.AddRange(rF); }
            else { list = new List<int> { vv }; list.AddRange(rB); }
            route = list;
            Log($"  route {(costF <= costB ? "forward" : "U-turn")} via road {e.id} u={u} v={vv}: {string.Join(">", list)}");
            BuildPolyline(pose.pos, list);
        }

        void NoRoute()
        {
            if (!retried)
            {
                retried = true; Array.Clear(know.blocked, 0, know.blocked.Length); // look again
                RePlan(); return;
            }
            Finish(false, "no route");
        }

        // ------------------------------------------------------------------ stepping

        public void Tick()
        {
            time += Dt;
            if (state == SimState.Idle) return;
            if (state != SimState.Driving)
            {
                pauseTimer -= Dt;
                if (pauseTimer <= 0 && loop) StartTrip();
                return;
            }
            decisionTimer -= Dt;
            if (decisionTimer <= 0) { decisionTimer = DecisionDt; Decide(); if (state != SimState.Driving) return; }

            // speed / terrain
            var f = pose.Fwd;
            var deb = city.DebrisAt(pose.pos) ?? city.DebrisAt(pose.pos + f * 1.0f) ?? city.DebrisAt(pose.pos - f * 1.0f);
            onDebris = deb != null;
            float target = decision.speed, yawCmd = decision.yawRate;
            if (reverseTimer > 0f) { reverseTimer -= Dt; target = -1.2f; yawCmd = 0f; }
            if (onDebris) target = Mathf.Min(target, 1.6f);
            float dv = target - v;
            v += Mathf.Clamp(dv, -Decel * Dt, Accel * Dt);
            yawRate += Mathf.Clamp(yawCmd - yawRate, -6f * Dt, 6f * Dt);
            pose.yaw += yawRate * Dt;
            if (pose.yaw > Mathf.PI) pose.yaw -= 2 * Mathf.PI; else if (pose.yaw < -Mathf.PI) pose.yaw += 2 * Mathf.PI;
            f = pose.Fwd;
            var np = pose.pos + f * v * Dt;
            bool hit = Mathf.Abs(v) > 0.01f && Violations(np, f) > Violations(pose.pos, f);
            if (hit)
            {
                if (!wasColliding) { trip.collisions++; Log($"COLLISION [{hitDesc}] {P()} act={decision.act} v={v:0.0} head={decision.heading * Mathf.Rad2Deg:0} clr={decision.clearance:0.0}"); }
                wasColliding = true; if (v > 0f) reverseTimer = 1.5f; else reverseTimer = 0f; v = 0;
            }
            else { wasColliding = false; pose.pos = np; trip.dist += v * Dt; }
            var cd = city.DebrisAt(pose.pos);
            if (cd != null) crossed.Add(cd.id);
            bump = onDebris ? Mathf.Abs(v) : bump * 0.9f;

            if ((pose.pos - GoalPos).magnitude < ArriveRadius) { Finish(true, "arrived"); return; }

            stuckTimer += Dt;
            if ((pose.pos - stuckRef).magnitude > 3f) { stuckRef = pose.pos; stuckTimer = 0; nextRev = 8f; }
            else if (stuckTimer > nextRev) { nextRev += 8f; reverseTimer = 2.5f; }
            if (stuckTimer > 12f && time - latchTime < 25f)
            {
                var le = city.NearestEdge(latchPt, out float lt, out float ld);
                latchTime = -100f;
                if (!know.blocked[le.id]) { know.blocked[le.id] = true; trip.blockedEvents++; Log($"BLOCKED(stuck) road {le.id} truth={le.truth} dist={ld:0.0} pt=({latchPt.x:0},{latchPt.y:0}) {P()}"); }
                RePlan(); if (state == SimState.Driving) blockedTicks = 0;
            }
            if (stuckTimer > 30f) { Finish(false, "stuck"); return; }
            float limit = 90f + Mathf.Max(trip.oracleCost, 100f) / 1.2f;
            if (time - tripStart > limit) Finish(false, "timeout");
        }

        string hitDesc = "";
        int Violations(Vector2 p, Vector2 f)
        {
            int n = 0;
            for (int k = -1; k <= 1; k++)
            {
                var c = p + f * (k * 0.9f);
                if (!city.OnRoad(c, 0.6f)) { hitDesc = "OFFROAD"; n++; continue; }
                var so = city.SolidHit(c, Driver.HalfW);
                if (so != null)
                {
                    var rel = so.pos - pose.pos; hitDesc = $"{so.kind} rel=({Vector2.Dot(rel, pose.Right):0.0},{Vector2.Dot(rel, pose.Fwd):0.0}) size=({so.size.x:0.0}x{so.size.y:0.0})"; n++;
                }
            }
            return n;
        }

        readonly List<Vector2> solidTmp = new List<Vector2>();
        /// <summary>Widest free lateral gap (m, between inflated obstacle points) across the road around pt, from the camera memory.</summary>
        float LateralGap(Vector2 pt, Edge e)
        {
            solidTmp.Clear(); driver.CollectSolids(solidTmp);
            var a = city.nodes[e.a]; var dir = e.dir; var perp = new Vector2(dir.y, -dir.x);
            var c = a + dir * Vector2.Dot(pt - a, dir);
            float hw = city.cfg.roadWidth * 0.5f - 0.6f;
            var iv = new List<Vector2>();
            foreach (var p in solidTmp)
            {
                var d = p - c; if (Mathf.Abs(Vector2.Dot(d, dir)) > 3.5f) continue;
                float l = Vector2.Dot(d, perp); iv.Add(new Vector2(l - 1.1f, l + 1.1f));
            }
            iv.Sort((x, y) => x.x.CompareTo(y.x));
            float cur = -hw, best = 0f;
            foreach (var v2 in iv) { if (v2.x > cur) best = Mathf.Max(best, v2.x - cur); cur = Mathf.Max(cur, v2.y); }
            return Mathf.Max(best, hw - cur);
        }
        bool BlockConfirmed()
        {
            if (!decision.hasBlock) return false;
            var be = city.NearestEdge(decision.blockPoint, out float bt, out float bd);
            float gap = LateralGap(decision.blockPoint, be);
            return gap < 2.4f ? blockedTicks >= 8 : blockedTicks >= 20;
        }
        void Decide()
        {
            if (grab == null) { decision = new Decision { act = Act.Blocked }; return; }
            var img = grab(pose);
            scan = Perception.Analyze(img, cam, scan);
            var tgt = LookaheadTarget();
            decision = driver.Decide(pose, scan, tgt, time);

            bool replan = false;
            if (decision.hazardAhead && decision.act == Act.Cross && decision.crossClearance < 10f)
            {
                var he = city.NearestEdge(decision.hazardPoint, out float ht, out float hd);
                if (hd < 8f && !know.rough[he.id] && !know.blocked[he.id]) { know.rough[he.id] = true; replan = true; Log($"ROUGH road {he.id} truth={he.truth} {P()}"); }
            }
            if (decision.act == Act.Blocked && decision.newBlock)
            {
                blockEpis.Add(time); blockEpis.RemoveAll(x => time - x > 30f); blockedTicks = blockEpis.Count * 4;
                if (decision.hasBlock) { latchPt = decision.blockPoint; latchTime = time; }
                if (!decision.hasBlock) { noBlockTicks++; if (noBlockTicks > 3) { noBlockTicks = 0; Log($"BOXED IN, replanning {P()}"); replan = true; } }
                else if (BlockConfirmed())
                {
                    var pt = decision.hasBlock ? decision.blockPoint : pose.pos + pose.Fwd * 8f;
                    var be = city.NearestEdge(pt, out float bt, out float bd);
                    if (!know.blocked[be.id]) { know.blocked[be.id] = true; blockEpis.Clear(); trip.blockedEvents++; replan = true; Log($"BLOCKED road {be.id} truth={be.truth} dist={bd:0.0} pt=({pt.x:0},{pt.y:0}) {P()} gap={LateralGap(pt, be):0.0} ticks={blockedTicks} hasBlock={decision.hasBlock} gamma={decision.targetBearing * Mathf.Rad2Deg:0}"); }
                    else { blockedTicks = 0; blockEpis.Clear(); }
                }
            }
            else if (decision.act != Act.Blocked && v > 2f) noBlockTicks = 0;
            if (replan) { RePlan(); if (state == SimState.Driving) { blockedTicks = 0; } }
        }

        public void Advance(float seconds)
        {
            int n = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < n; i++) Tick();
        }

        // ------------------------------------------------------------------ reporting

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"city {city.cfg.nx}x{city.cfg.ny} blocks, spacing {city.cfg.spacing:0}m, severity {city.cfg.severity:0.00}, seed {city.cfg.seed}: obstacles={city.obstacles.Count} blockedRoads={city.blockedEdges} roughRoads={city.roughEdges} downgraded={city.downgraded}");
            sb.AppendLine($"t={time:0}s state={state} trip#{tripCounter} {startNode}->{goalNode} act={decision.act} v={v:0.0} dist={trip.dist:0}m replans={trip.replans} collisions={trip.collisions}");
            float avgEff = tSuccess > 0 ? tEffSum / tSuccess : 0f;
            sb.AppendLine($"totals trips={tTrips} ok={tSuccess} fail={tFail} avgTime={(tTrips > 0 ? tTime / tTrips : 0):0}s eff={avgEff * 100:0}% collisions={tCollisions} replans={tReplans} crossings={tCrossings}");
            return sb.ToString();
        }
    }
}
