using System;
using UnityEngine;

namespace RescueBot.Core
{
    public struct Pose
    {
        public Vector2 pos; public float yaw; // yaw in radians, forward = (sin, cos) on the (x, z) plane
        public Vector2 Fwd => new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
        public Vector2 Right => new Vector2(Mathf.Cos(yaw), -Mathf.Sin(yaw));
    }

    /// <summary>Pinhole camera on the rover: intrinsics + the ground-plane back projection of every pixel.</summary>
    public class CamModel
    {
        public int w = 160, h = 90;
        public float hfov = 110f, height = 1.5f, pitchDeg = 22f, fwdOffset = 0.4f, maxRange = 60f;
        public float F;
        float[] gD, gX;

        public CamModel() { Init(); }

        public void Init()
        {
            F = (w * 0.5f) / Mathf.Tan(hfov * 0.5f * Mathf.Deg2Rad);
            gD = new float[w * h]; gX = new float[w * h];
            float p = pitchDeg * Mathf.Deg2Rad, sp = Mathf.Sin(p), cp = Mathf.Cos(p);
            for (int v = 0; v < h; v++)
                for (int u = 0; u < w; u++)
                {
                    float a = (u + 0.5f - w * 0.5f) / F, b = (v + 0.5f - h * 0.5f) / F;
                    float down = sp + b * cp;
                    int i = v * w + u;
                    if (down <= 1e-4f) { gD[i] = -1; continue; }
                    float t = height / down;
                    float D = t * (cp - b * sp);
                    if (D <= 0.01f || D > maxRange) { gD[i] = -1; continue; }
                    gD[i] = D; gX[i] = t * a;
                }
        }

        /// <summary>Forward distance (from the camera) and lateral offset of the ground seen at pixel (u, v from top). false = no ground hit within range.</summary>
        public bool Ground(int u, int v, out float D, out float X)
        {
            int i = v * w + u; D = gD[i]; X = gX[i]; return D > 0;
        }

        public float ColumnBearing(int u) => Mathf.Atan((u + 0.5f - w * 0.5f) / F);
    }

    /// <summary>
    /// What the rover reads from one 2D camera image: for every image column the first non-road pixel above the bottom edge,
    /// projected onto the ground plane (a "visual range scan"), classified as drivable-over debris or solid.
    /// </summary>
    public class Scan
    {
        public int n, w, h;
        public float[] X, Z;       // blocker position in the rover frame (x right, z forward) per column
        public byte[] cls;         // 0 free to max range, 1 crossable debris, 2 solid
        public byte[] seg;         // per pixel class (row 0 = top): 0 road, 1 crossable, 2 other
        public int[] row;          // first non-road row per column (-1 = free)
        public float roadFraction; // share of road pixels in the lower half
    }

    public static class Perception
    {
        public const byte Road = 0, Cross = 1, Other = 2;

        /// <summary>Pixel colour -> class. Asphalt is grey (low saturation, mid value); hazard debris is saturated yellow/orange.</summary>
        public static byte ClassOf(Color32 c)
        {
            int mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            float v = mx / 255f;
            float s = mx == 0 ? 0f : (mx - mn) / (float)mx;
            if (s < 0.13f && v > 0.07f && v < 0.62f) return Road;
            if (s > 0.55f && v > 0.2f)
            {
                float d = mx - mn, hue;
                if (mx == c.r) hue = 60f * (((c.g - c.b) / d) % 6f);
                else if (mx == c.g) hue = 60f * (((c.b - c.r) / d) + 2f);
                else hue = 60f * (((c.r - c.g) / d) + 4f);
                if (hue < 0) hue += 360f;
                if (hue >= 33f && hue <= 70f) return Cross;
            }
            return Other;
        }

        /// <summary>Image rows are given bottom-to-top (Texture2D order); the result uses top-to-bottom rows.</summary>
        public static Scan Analyze(Color32[] img, CamModel cam, Scan reuse = null)
        {
            int w = cam.w, h = cam.h;
            var sc = reuse != null && reuse.n == w ? reuse : new Scan { n = w, w = w, h = h, X = new float[w], Z = new float[w], cls = new byte[w], seg = new byte[w * h], row = new int[w] };
            int roadLow = 0, lowCount = 0;
            for (int v = 0; v < h; v++)
            {
                int src = (h - 1 - v) * w;
                for (int u = 0; u < w; u++)
                {
                    byte c = ClassOf(img[src + u]);
                    sc.seg[v * w + u] = c;
                    if (v >= h / 2) { lowCount++; if (c == Road) roadLow++; }
                }
            }
            sc.roadFraction = lowCount > 0 ? roadLow / (float)lowCount : 0f;
            for (int u = 0; u < w; u++)
            {
                int hit = -1;
                for (int v = h - 1; v >= 0; v--)
                {
                    if (sc.seg[v * w + u] == Road) continue;
                    // need two consecutive non-road rows (rejects 1-pixel speckle and edge blending)
                    if (v == 0 || sc.seg[(v - 1) * w + u] != Road) { hit = v; break; }
                }
                sc.row[u] = -1;
                if (hit < 0) { Free(sc, cam, u); continue; }
                // ground point of the last road pixel below the blocker (the obstacle's near base)
                int gv = Mathf.Min(h - 1, hit + 1);
                float D, X;
                if (!cam.Ground(u, gv, out D, out X)) { Free(sc, cam, u); continue; }
                int crossVotes = 0, k = 0;
                for (int v = hit; v >= 0 && k < 3; v--, k++) if (sc.seg[v * w + u] == Cross) crossVotes++;
                sc.cls[u] = crossVotes >= 2 ? Cross : (byte)2;
                if (sc.cls[u] == Cross) sc.cls[u] = 1; else sc.cls[u] = 2;
                sc.X[u] = X; sc.Z[u] = D + cam.fwdOffset; sc.row[u] = hit;
            }
            return sc;
        }

        static void Free(Scan sc, CamModel cam, int u)
        {
            sc.cls[u] = 0; sc.row[u] = -1;
            float th = cam.ColumnBearing(u);
            sc.Z[u] = cam.maxRange + cam.fwdOffset; sc.X[u] = Mathf.Tan(th) * cam.maxRange;
        }
    }
}
