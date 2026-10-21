using System;
using System.Collections.Generic;
using UnityEngine;

namespace RescueBot.Core
{
    public enum Act { Straight, AvoidLeft, AvoidRight, Cross, Turn, Blocked }

    public struct Decision
    {
        public Act act;
        public float heading;       // rough direction of travel over the next second relative to the rover (rad, + = right)
        public float speed, yawRate;
        public float clearance;     // free travel distance along the chosen path (m)
        public float crossClearance;// distance to crossable debris on the chosen path (m)
        public bool newBlock; public bool hasBlock; public Vector2 blockPoint;     // world position of the nearest solid obstacle on the road ahead
        public bool hazardAhead; public Vector2 hazardPoint; // world position of crossable debris on the chosen path
        public float targetBearing;
    }

    /// <summary>
    /// Local planner (dynamic-window style). Works only from the camera scan - the base points of everything that is not road -
    /// plus a few seconds of remembered points so the rover can pass an obstacle that has left the field of view.
    /// It simulates a set of arcs (speed x yaw rate), rejects the ones that hit a solid obstacle (or the road edge) within the
    /// braking distance, and prefers the arc that gets closest to the lane target without driving over debris.
    /// </summary>
    public class Driver
    {
        public const float HalfW = 0.9f, Inflate = 0.2f, FrontLen = 1.3f;
        public const float VMax = 5f, MaxYaw = 1.7f, MemorySeconds = 10f, LongTravel = 6f;

        struct Pt { public Vector2 w; public byte cls; public float t; }
        readonly List<Pt> mem = new List<Pt>();
        float[] px = new float[4096], pz = new float[4096]; byte[] pc = new byte[4096];
        float[] sx = new float[4096], sz = new float[4096], cx = new float[1024], cz = new float[1024]; int[] cMem = new int[1024];
        int ns, nc; Vector2 cpos, cr, cf;
        readonly HashSet<long> cells = new HashSet<long>();
        public Func<Vector2, bool> onRoad, onRoadInner;
        public bool debugOn; public string debug = "";
        int pivotDir = 1; string dbgArcs = ""; bool commit, commitBlocked; float commitYaw, commitT;
        public int memoryCount => mem.Count;

        static readonly float[] Kappas = { -0.40f, -0.25f, -0.16f, -0.10f, -0.06f, -0.03f, -0.01f, 0f, 0.01f, 0.03f, 0.06f, 0.10f, 0.16f, 0.25f, 0.40f };
        const float PathLen = 16f, PathStep = 0.8f;
        static readonly float[] RetK = { -0.25f, -0.16f, -0.10f, -0.06f, 0.06f, 0.10f, 0.16f, 0.25f };

        public void CollectSolids(List<Vector2> into) { foreach (var p in mem) if (p.cls == 2) into.Add(p.w); }
        public void Reset() { mem.Clear(); pivotDir = 1; commit = false; }

        struct Arc { public float k, dFree, x, z, th, minD2, cost, crossDist; public bool collided, crossHit; public int crossIdx; }

        public Decision Decide(Pose pose, Scan scan, Vector2 target, float time)
        {
            var f = pose.Fwd; var r = pose.Right; cpos = pose.pos; cr = r; cf = f;
            mem.RemoveAll(p => time - p.t > MemorySeconds || (p.w - pose.pos).sqrMagnitude > 1600f);
            for (int u = 0; u < scan.n; u += 2)
                if (scan.cls[u] != 0 && scan.Z[u] < 30f)
                    mem.Add(new Pt { w = pose.pos + f * scan.Z[u] + r * scan.X[u], cls = scan.cls[u], t = time });
            int n = mem.Count;
            if (px.Length < n) { px = new float[n * 2]; pz = new float[n * 2]; pc = new byte[n * 2]; }
            ns = nc = 0; cells.Clear();
            for (int i = 0; i < n; i++)
            {
                var d = mem[i].w - pose.pos;
                px[i] = Vector2.Dot(d, r); pz[i] = Vector2.Dot(d, f); pc[i] = mem[i].cls;
                if (pz[i] < -4f) continue;
                long key = ((long)Mathf.FloorToInt(px[i] / 0.4f) + 1000) * 100000L + (Mathf.FloorToInt(pz[i] / 0.4f) + 1000) + (pc[i] == 1 ? 50000000000L : 0L);
                if (!cells.Add(key)) continue;
                if (pc[i] == 2) { if (onRoadInner != null && !onRoadInner(mem[i].w)) continue;
                    float rl = Mathf.Sqrt(px[i] * px[i] + pz[i] * pz[i]); float rx = px[i] / Mathf.Max(0.1f, rl), rz = pz[i] / Mathf.Max(0.1f, rl);
                    for (int sh = 0; sh < 3 && ns < sx.Length; sh++) { sx[ns] = px[i] + rx * sh * 0.8f; sz[ns] = pz[i] + rz * sh * 0.8f; ns++; } }
                else if (nc < cx.Length) { cx[nc] = px[i]; cz[nc] = pz[i]; cMem[nc] = i; nc++; }
            }

            var td = target - pose.pos;
            float tx = Vector2.Dot(td, r), tz = Vector2.Dot(td, f);
            float gamma = Mathf.Atan2(tx, tz);
            var dec = new Decision { targetBearing = gamma };
            FillBlock(ref dec, n);
            float tl = Mathf.Max(0.5f, Mathf.Sqrt(tx * tx + tz * tz)), ux = tx / tl, uz = tz / tl;
            float pivotW = (gamma >= 0f ? 1f : -1f) * MaxYaw;

            if (commit)
            {
                float dyaw = Mathf.DeltaAngle(pose.yaw * Mathf.Rad2Deg, commitYaw * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                if (Mathf.Abs(dyaw) < 0.25f || time > commitT) commit = false;
                else
                {
                    dec.act = commitBlocked ? Act.Blocked : Act.Turn; dec.speed = 0f; dec.yawRate = (dyaw >= 0f ? 1f : -1f) * (commitBlocked ? 1.2f : MaxYaw);
                    dec.heading = 0f; dec.clearance = commitBlocked ? 0f : 20f; dec.crossClearance = 99f;
                    return dec;
                }
            }
            if (Mathf.Abs(gamma) > 1.25f)
            {
                commit = true; commitBlocked = false; commitYaw = pose.yaw + gamma; commitT = time + 8f;
                // target is behind / far to the side: turn on the spot first
                dec.act = Act.Turn; dec.heading = gamma; dec.speed = 0f; dec.yawRate = pivotW; dec.clearance = 20f; dec.crossClearance = 99f;
                return dec;
            }

            Arc best = default, ideal = default; bool haveBest = false, haveIdeal = false;
            for (int b = 0; b < Kappas.Length + RetK.Length; b++)
            {
                bool ret = b >= Kappas.Length; if (b == 0) dbgArcs = "";
                var arc = Simulate(ret ? RetK[b - Kappas.Length] : Kappas[b], ux, uz, tx, tz, ret ? 1 : 0);
                float idealCost = arc.cost - (arc.crossHit ? 0.9f : 0f) - 0.5f * (1f - Mathf.Min(arc.dFree, 12f) / 12f) - (arc.collided ? 0.4f * (1f - Mathf.Min(arc.dFree, 12f) / 12f) : 0f);
                arc.cost += 0f;
                if (!haveIdeal || idealCost < ideal.cost) { ideal = arc; ideal.cost = idealCost; haveIdeal = true; }
                if (debugOn) dbgArcs += $"{(ret ? "S" : "")}{arc.k:0.00}:{arc.dFree:0} ";
                if (arc.dFree < LongTravel) continue;
                if (!haveBest || arc.cost < best.cost) { best = arc; haveBest = true; }
            }

            if (!haveBest || best.dFree < LongTravel)
            {
                // nothing is free for LongTravel metres: blocked. Look around by turning on the spot towards the more open side.
                if (debugOn) debug = $"BLOCKED mem={n} solid={ns} gamma={gamma * Mathf.Rad2Deg:0} arcs {dbgArcs}"; dec.act = Act.Blocked; dec.speed = 0f; dec.clearance = haveBest ? best.dFree : 0f; dec.crossClearance = 99f;
                dec.newBlock = true; float aB = BestEscape(gamma); pivotDir = aB >= 0f ? 1 : -1; commit = true; commitBlocked = true; commitYaw = pose.yaw + aB * Mathf.Deg2Rad; commitT = time + 8f;
                dec.yawRate = pivotDir * 1.2f; dec.heading = 0f;
                return dec;
            }

            float v = Mathf.Min(VMax, Mathf.Sqrt(2f * 4f * Mathf.Max(0f, best.dFree - 2f)));
            if (best.crossHit) v = Mathf.Min(v, 1.6f + Mathf.Max(0f, best.crossDist - 4f) * 0.4f);
            v = Mathf.Max(0.9f, v);
            float w = best.k * v;
            if (Mathf.Abs(w) > MaxYaw) { v = MaxYaw / Mathf.Abs(best.k); w = Mathf.Sign(best.k) * MaxYaw; }
            dec.speed = v; dec.yawRate = w;
            dec.heading = Mathf.Atan(best.k * 4f);
            dec.clearance = best.dFree; dec.crossClearance = 99f;
            if (best.crossHit)
            {
                dec.hazardAhead = true; dec.hazardPoint = mem[cMem[best.crossIdx]].w; dec.crossClearance = best.crossDist;
            }
            float dk = best.k - ideal.k;
            if (best.crossHit) dec.act = Act.Cross;
            else if (Mathf.Abs(dk) < 0.04f) dec.act = Act.Straight;
            else dec.act = dk < 0 ? Act.AvoidLeft : Act.AvoidRight;
            if (debugOn) debug = NearestDump() + $"mem={n} solid={ns} cross={nc} gamma={gamma * Mathf.Rad2Deg:0} pick k={best.k:0.00} v={v:0.0} dFree={best.dFree:0.0} cost={best.cost:0.00} ideal k={ideal.k:0.00} act={dec.act}";
            return dec;
        }

        float RayFree(float psi)
        {
            float sn = Mathf.Sin(psi), cs = Mathf.Cos(psi), R2 = (HalfW + Inflate) * (HalfW + Inflate);
            for (float s = 0.8f; s <= 12f; s += 0.5f)
            {
                float x = sn * s, z = cs * s;
                if (onRoad != null && !onRoad(cpos + cr * x + cf * z)) return s - 0.8f;
                for (int i = 0; i < ns; i += 3) { float dx = sx[i] - x, dz = sz[i] - z; if (dx * dx + dz * dz < R2) return s - 0.8f; }
            }
            return 12f;
        }
        float EscapeScore(float aDeg, float gd) => Mathf.Min(RayFree(aDeg * Mathf.Deg2Rad), 10f) - 0.02f * Mathf.Abs(aDeg) - 0.02f * Mathf.Abs(Mathf.DeltaAngle(aDeg, gd));
        float BestEscape(float gamma)
        {
            float gd = gamma * Mathf.Rad2Deg, best = 90f, bs = -1e9f;
            for (int a = -180; a <= 180; a += 15)
            {
                if (Mathf.Abs(a) < 20) continue;
                float s = EscapeScore(a, gd); if (s > bs) { bs = s; best = a; }
            }
            return best;
        }
        void ChoosePivot(float gamma)
        {
            float gd = gamma * Mathf.Rad2Deg, bestL = -1e9f, bestR = -1e9f;
            for (int a = 15; a <= 180; a += 15)
            {
                bestR = Mathf.Max(bestR, EscapeScore(a, gd));
                bestL = Mathf.Max(bestL, EscapeScore(-a, gd));
            }
            // positive angle = turn right (yaw increases); hysteresis so the direction does not flip every decision
            float r = bestR + (pivotDir > 0 ? 1.5f : 0f), l = bestL + (pivotDir < 0 ? 1.5f : 0f);
            pivotDir = r >= l ? 1 : -1;
        }
        string NearestDump()
        {
            var l = new List<Vector2>(); for (int i = 0; i < ns; i += 3) l.Add(new Vector2(sx[i], sz[i]));
            l.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));
            string s = "near:"; for (int i = 0; i < Mathf.Min(8, l.Count); i++) s += $" ({l[i].x:0.0},{l[i].y:0.0})"; return s + "\n";
        }
        Arc Simulate(float k, float ux, float uz, float tx, float tz, int mode)
        {
            float R = HalfW + Inflate, R2 = R * R, CR2 = (HalfW + 0.25f) * (HalfW + 0.25f);
            var a = new Arc { k = k, minD2 = 1e6f, crossIdx = -1, crossDist = 99f };
            float x = 0, z = 0, th = 0, s = 0, hold = 0f; int phase = 0;
            float xe = 0, ze = 0, the = 0; bool endSet = false;
            while (s < PathLen)
            {
                float kk = k;
                if (mode == 0) { if (Mathf.Abs(th) >= 1.0f) kk = 0f; }
                else
                {
                    if (phase == 0 && Mathf.Abs(th) >= 0.7f) { phase = 1; hold = 1.6f; }
                    if (phase == 1) { kk = 0f; hold -= PathStep; if (hold <= 0f) phase = 2; }
                    else if (phase == 2) { if (th * k > 0f) kk = -k; else { phase = 3; th = 0f; kk = 0f; } }
                    else if (phase == 3) kk = 0f;
                }
                float nth = th + kk * PathStep;
                float thm = th + kk * PathStep * 0.5f;
                float nx = x + Mathf.Sin(thm) * PathStep, nz = z + Mathf.Cos(thm) * PathStep;
                float ns2 = s + PathStep;
                bool hit = false;
                for (int q = -1; q <= 1 && !hit; q++)
                {
                    float ccx = nx + Mathf.Sin(nth) * q * 0.9f, ccz = nz + Mathf.Cos(nth) * q * 0.9f;
                    if (onRoad != null && !onRoad(cpos + cr * ccx + cf * ccz)) { hit = true; break; }
                    for (int i = 0; i < ns; i++)
                    {
                        float dx = sx[i] - ccx, dz = sz[i] - ccz, d2 = dx * dx + dz * dz;
                        if (d2 < a.minD2) a.minD2 = d2;
                        if (d2 < R2) { hit = true; break; }
                    }
                    if (!a.crossHit)
                        for (int i = 0; i < nc; i++)
                        {
                            float dx = cx[i] - ccx, dz = cz[i] - ccz;
                            if (dx * dx + dz * dz < CR2) { a.crossHit = true; a.crossIdx = i; a.crossDist = ns2; break; }
                        }
                }
                if (hit) { a.collided = true; break; }
                x = nx; z = nz; th = nth; s = ns2;
                if (!endSet && s >= 12f) { xe = x; ze = z; the = th; endSet = true; }
            }
            a.dFree = s;
            if (!endSet) { xe = x; ze = z; the = th; }
            a.x = xe; a.z = ze; a.th = the;
            float prog = Mathf.Clamp(xe * ux + ze * uz, -5f, 14f);
            float lat = Mathf.Abs(xe * uz - ze * ux);
            float bear = Mathf.Atan2(tx - xe, tz - ze);
            float ang = Mathf.Abs(Mathf.DeltaAngle(the * Mathf.Rad2Deg, bear * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
            float clearPen = Mathf.Max(0f, 2.6f - (Mathf.Sqrt(a.minD2) - R)) * 0.55f;
            a.cost = -0.1f * prog + 0.1f * lat + 0.5f * ang + clearPen + 0.5f * (1f - Mathf.Min(a.dFree, 12f) / 12f) + (a.crossHit ? 0.9f : 0f);
            return a;
        }
        /// <summary>Mean free distance of the rays around the given heading (used to choose a turning direction when boxed in).</summary>
        float Openness(float psi)
        {
            float sum = 0; int cnt = 0;
            for (float p = psi - 0.3f; p <= psi + 0.31f; p += 0.15f)
            {
                float s = Mathf.Sin(p), c = Mathf.Cos(p), best = 30f;
                for (int i = 0; i < ns; i++)
                {
                    float l = sx[i] * c - sz[i] * s; if (Mathf.Abs(l) > HalfW + Inflate) continue;
                    float sd = sx[i] * s + sz[i] * c; if (sd < 0f) continue;
                    if (sd < best) best = sd;
                }
                sum += best; cnt++;
            }
            return sum / Mathf.Max(1, cnt);
        }

        void FillBlock(ref Decision d, int n)
        {
            float best = 25f; int bi = -1;
            for (int i = 0; i < n; i++)
                if (pc[i] == 2 && pz[i] > 0f && Mathf.Abs(px[i]) < HalfW + 2.5f && pz[i] < best && (onRoadInner == null || onRoadInner(mem[i].w))) { best = pz[i]; bi = i; }
            if (bi >= 0) { d.hasBlock = true; d.blockPoint = mem[bi].w; }
        }
    }
}
