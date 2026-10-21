using System;
using System.Collections.Generic;
using RescueBot.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Pose = RescueBot.Core.Pose;

namespace RescueBot.World
{
    /// <summary>Appends boxes to one combined mesh (used for buildings, sidewalks and road markings).</summary>
    public class MeshBuilder
    {
        readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        readonly List<int> t = new List<int>();
        public int Count => v.Count;

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 nrm)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), nrm) < 0f) { var tmp = b; b = d; d = tmp; }
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d); n.Add(nrm); n.Add(nrm); n.Add(nrm); n.Add(nrm);
            t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        public void Box(Vector3 center, Vector3 size, float yawDeg, bool bottom = false)
        {
            float r = yawDeg * Mathf.Deg2Rad, s = Mathf.Sin(r), c = Mathf.Cos(r);
            Vector3 right = new Vector3(c, 0, -s), up = Vector3.up, fwd = new Vector3(s, 0, c);
            Vector3 h = size * 0.5f;
            Vector3 P(float sx, float sy, float sz) => center + right * (sx * h.x) + up * (sy * h.y) + fwd * (sz * h.z);
            Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), up);
            Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), right);
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), -right);
            Quad(P(-1, -1, 1), P(-1, 1, 1), P(1, 1, 1), P(1, -1, 1), fwd);
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), -fwd);
            if (bottom) Quad(P(-1, -1, -1), P(-1, -1, 1), P(1, -1, 1), P(1, -1, -1), -up);
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>
    /// Builds the 3D city (roads, sidewalks, buildings, disaster obstacles), the rover, the route overlay and the
    /// rover's camera (rendered into a small RenderTexture that the perception reads).
    /// Generated objects are DontSave; the scene only holds this component.
    /// </summary>
    [ExecuteAlways]
    public class CityView : MonoBehaviour
    {
        public const int OverlayLayer = 30;
        public Shader litShader, unlitShader;
        public RescueSim sim;
        public bool showRoute = true, showScan = true, hideOverlays;
        public Camera perceptionCam;
        public RenderTexture camRT;

        public bool IsBuilt => root != null;
        public bool Visible { get => root != null && root.activeSelf; set { if (root != null) root.SetActive(value); } }
        GameObject root, rover, routeGo, goalGo, startGo, headingGo;
        LineRenderer routeLine, headingLine;
        Texture2D readTex;
        Mesh cube, cylinder;
        readonly Dictionary<int, Material> mats = new Dictionary<int, Material>();
        Material overlayMat;

        static readonly Color[] BuildingPalette =
        {
            new Color(0.78f, 0.66f, 0.52f), new Color(0.72f, 0.50f, 0.40f), new Color(0.50f, 0.58f, 0.68f), new Color(0.55f, 0.65f, 0.50f),
            new Color(0.82f, 0.72f, 0.50f), new Color(0.62f, 0.35f, 0.30f), new Color(0.62f, 0.50f, 0.60f), new Color(0.60f, 0.62f, 0.45f),
        };
        static readonly Color[] CarPalette =
        {
            new Color(0.80f, 0.12f, 0.12f), new Color(0.15f, 0.25f, 0.70f), new Color(0.95f, 0.92f, 0.78f),
            new Color(0.15f, 0.55f, 0.25f), new Color(0.55f, 0.20f, 0.60f), new Color(0.10f, 0.55f, 0.60f),
        };
        public static readonly Color Asphalt = new Color(0.22f, 0.22f, 0.22f), Marking = new Color(0.40f, 0.40f, 0.40f),
            Sidewalk = new Color(0.78f, 0.70f, 0.55f), Ground = new Color(0.45f, 0.50f, 0.35f), Sky = new Color(0.50f, 0.70f, 0.92f);

        Material Mat(Color c, float smooth = 0.05f)
        {
            int key = ((int)(c.r * 255) << 16) | ((int)(c.g * 255) << 8) | (int)(c.b * 255) | ((int)(smooth * 100) << 24);
            if (mats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(litShader != null ? litShader : Shader.Find("Universal Render Pipeline/Lit")) { hideFlags = HideFlags.DontSave };
            m.SetColor("_BaseColor", c); m.color = c;
            m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", 0f);
            mats[key] = m; return m;
        }

        Material Overlay(Color c)
        {
            var m = new Material(unlitShader != null ? unlitShader : Shader.Find("Universal Render Pipeline/Unlit")) { hideFlags = HideFlags.DontSave };
            m.SetColor("_BaseColor", c); m.color = c; return m;
        }

        void EnsureMeshes()
        {
            if (cube == null) { var g = GameObject.CreatePrimitive(PrimitiveType.Cube); cube = g.GetComponent<MeshFilter>().sharedMesh; DestroyImmediate(g); }
            if (cylinder == null) { var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder); cylinder = g.GetComponent<MeshFilter>().sharedMesh; DestroyImmediate(g); }
        }

        GameObject Part(Transform parent, Mesh mesh, Material m, Vector3 pos, Vector3 scale, Quaternion rot, string name = "p", int layer = 0)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave, layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos; go.transform.localRotation = rot; go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
            return go;
        }

        GameObject Combined(Transform parent, MeshBuilder mb, Material m, string name)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mb.ToMesh(name);
            go.AddComponent<MeshRenderer>().sharedMaterial = m;
            return go;
        }

        // ------------------------------------------------------------------ build

        public void Clear()
        {
            if (root != null) DestroyImmediate(root);
            root = null; mats.Clear();
        }

        public void Build(RescueSim s)
        {
            Clear();
            EnsureMeshes();
            sim = s;
            var city = s.city;
            root = new GameObject("CityRoot") { hideFlags = HideFlags.DontSave };
            var T = root.transform;
            float W = city.cfg.roadWidth;

            // ground + asphalt
            float pad = 120f;
            var groundMb = new MeshBuilder();
            groundMb.Box(new Vector3(city.width * 0.5f, -0.3f, city.depth * 0.5f), new Vector3(city.width + 2 * pad, 0.2f, city.depth + 2 * pad), 0);
            Combined(T, groundMb, Mat(Ground, 0f), "Ground");
            var roadMb = new MeshBuilder();
            foreach (var e in city.edges)
            {
                var c = (city.nodes[e.a] + city.nodes[e.b]) * 0.5f;
                bool hz = e.horizontal;
                roadMb.Box(new Vector3(c.x, -0.05f, c.y), new Vector3(hz ? e.length + W : W, 0.1f, hz ? W : e.length + W), 0);
            }
            Combined(T, roadMb, Mat(Asphalt, 0f), "Roads");

            // dashed centre lines (dim grey: still reads as road)
            var markMb = new MeshBuilder();
            foreach (var e in city.edges)
            {
                for (float t = 8f; t < e.length - 8f; t += 6f)
                {
                    var p = city.EdgePoint(e, t + 1.5f, 0f);
                    markMb.Box(new Vector3(p.x, 0.012f, p.y), new Vector3(e.horizontal ? 3f : 0.18f, 0.02f, e.horizontal ? 0.18f : 3f), 0);
                }
            }
            Combined(T, markMb, Mat(Marking, 0f), "Markings");

            // sidewalk slabs per block
            var slabMb = new MeshBuilder();
            float hw = W * 0.5f;
            for (int bj = 0; bj < city.cfg.ny; bj++)
                for (int bi = 0; bi < city.cfg.nx; bi++)
                {
                    float x0 = bi * city.cfg.spacing + hw, x1 = (bi + 1) * city.cfg.spacing - hw, z0 = bj * city.cfg.spacing + hw, z1 = (bj + 1) * city.cfg.spacing - hw;
                    slabMb.Box(new Vector3((x0 + x1) * 0.5f, 0.1f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, 0.2f, z1 - z0), 0);
                }
            Combined(T, slabMb, Mat(Sidewalk, 0f), "Sidewalks");

            // buildings, one combined mesh per colour
            var byColor = new MeshBuilder[BuildingPalette.Length];
            foreach (var b in city.buildings)
            {
                int k = b.variant % BuildingPalette.Length;
                if (byColor[k] == null) byColor[k] = new MeshBuilder();
                byColor[k].Box(new Vector3(b.center.x, 0.2f + b.height * 0.5f, b.center.y), new Vector3(b.size.x, b.height, b.size.y), 0);
            }
            for (int k = 0; k < byColor.Length; k++) if (byColor[k] != null) Combined(T, byColor[k], Mat(BuildingPalette[k], 0.1f), "Buildings" + k);

            // obstacles
            var obsRoot = new GameObject("Obstacles") { hideFlags = HideFlags.DontSave }; obsRoot.transform.SetParent(T, false);
            foreach (var o in city.obstacles) BuildObstacle(obsRoot.transform, o);

            BuildMarkers(T, city);
            BuildRover(T);
            BuildCamera();
            Render(0f);
        }

        void BuildObstacle(Transform parent, Obstacle o)
        {
            var go = new GameObject($"{o.kind}{o.id}") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(o.pos.x, 0.2f, o.pos.y);
            go.transform.rotation = Quaternion.Euler(0, o.yaw, 0);
            var rnd = new System.Random(o.id * 977 + 13);
            float R() => (float)rnd.NextDouble();
            if (o.kind == ObsKind.Car)
            {
                var col = CarPalette[o.variant % CarPalette.Length];
                Part(go.transform, cube, Mat(col, 0.3f), new Vector3(0, 0.55f, 0), new Vector3(o.size.x, 0.7f, o.size.y), Quaternion.identity, "body");
                Part(go.transform, cube, Mat(new Color(0.10f, 0.14f, 0.30f), 0.5f), new Vector3(0, 1.15f, -0.2f), new Vector3(o.size.x * 0.85f, 0.55f, o.size.y * 0.5f), Quaternion.identity, "cabin");
                var wm = Mat(new Color(0.12f, 0.12f, 0.2f), 0f);
                foreach (var sx in new[] { -1f, 1f }) foreach (var sz in new[] { -1f, 1f })
                    Part(go.transform, cube, wm, new Vector3(sx * o.size.x * 0.5f, 0.3f, sz * o.size.y * 0.32f), new Vector3(0.25f, 0.6f, 0.7f), Quaternion.identity, "wheel");
            }
            else if (o.kind == ObsKind.Rubble)
            {
                Part(go.transform, cube, Mat(Color.HSVToRGB(24f / 360f, 0.42f, 0.42f), 0f), new Vector3(0, 0.2f, 0), new Vector3(o.size.x, 0.5f, o.size.y), Quaternion.identity, "mound");
                int cnt = Mathf.Clamp(Mathf.RoundToInt(o.size.x * o.size.y / 2.5f), 6, 20);
                for (int i = 0; i < cnt; i++)
                {
                    float hue = (20f + R() * 7f) / 360f;
                    var col = Color.HSVToRGB(hue, 0.35f + R() * 0.2f, 0.35f + R() * 0.25f);
                    float sx = 0.9f + R() * 1.8f, sz = 0.9f + R() * 1.8f;
                    float px = (R() - 0.5f) * (o.size.x - sx), pz = (R() - 0.5f) * (o.size.y - sz);
                    float edge = 1f - Mathf.Min(1f, (Mathf.Abs(px) / (o.size.x * 0.5f) + Mathf.Abs(pz) / (o.size.y * 0.5f)) * 0.5f);
                    float h = Mathf.Max(0.5f, o.height * (0.35f + 0.65f * edge) * (0.7f + 0.3f * R()));
                    Part(go.transform, cube, Mat(col, 0f), new Vector3(px, h * 0.5f - 0.05f, pz), new Vector3(sx, h, sz), Quaternion.Euler((R() - 0.5f) * 20f, R() * 90f, (R() - 0.5f) * 20f), "chunk");
                }
            }
            else
            {
                int cnt = Mathf.Clamp(Mathf.RoundToInt(o.size.x * o.size.y / 1.6f), 4, 14);
                for (int i = 0; i < cnt; i++)
                {
                    float hue = (38f + R() * 14f) / 360f;
                    var col = Color.HSVToRGB(hue, 0.85f, 0.85f + R() * 0.12f);
                    float sx = 0.5f + R() * 1.0f, sz = 0.4f + R() * 0.9f, h = 0.12f + R() * (o.height - 0.1f);
                    float px = (R() - 0.5f) * (o.size.x - sx), pz = (R() - 0.5f) * (o.size.y - sz);
                    Part(go.transform, cube, Mat(col, 0.1f), new Vector3(px, h * 0.5f - 0.05f, pz), new Vector3(sx, h, sz), Quaternion.Euler((R() - 0.5f) * 14f, R() * 180f, (R() - 0.5f) * 14f), "debris");
                }
            }
        }

        void BuildMarkers(Transform T, City city)
        {
            routeGo = new GameObject("Route") { hideFlags = HideFlags.DontSave, layer = OverlayLayer }; routeGo.transform.SetParent(T, false);
            routeLine = routeGo.AddComponent<LineRenderer>();
            SetupLine(routeLine, new Color(0.1f, 0.9f, 1f), 0.6f);
            headingGo = new GameObject("Heading") { hideFlags = HideFlags.DontSave, layer = OverlayLayer }; headingGo.transform.SetParent(T, false);
            headingLine = headingGo.AddComponent<LineRenderer>();
            SetupLine(headingLine, new Color(0.3f, 1f, 0.3f), 0.35f);
            goalGo = new GameObject("Goal") { hideFlags = HideFlags.DontSave, layer = OverlayLayer }; goalGo.transform.SetParent(T, false);
            Part(goalGo.transform, cylinder, Overlay(new Color(1f, 0.2f, 0.4f)), new Vector3(0, 15f, 0), new Vector3(2.2f, 15f, 2.2f), Quaternion.identity, "goal", OverlayLayer);
            Part(goalGo.transform, cylinder, Overlay(new Color(1f, 0.2f, 0.4f)), new Vector3(0, 0.1f, 0), new Vector3(12f, 0.05f, 12f), Quaternion.identity, "goalRing", OverlayLayer);
            startGo = new GameObject("Start") { hideFlags = HideFlags.DontSave, layer = OverlayLayer }; startGo.transform.SetParent(T, false);
            Part(startGo.transform, cylinder, Overlay(new Color(0.2f, 0.9f, 0.3f)), new Vector3(0, 0.1f, 0), new Vector3(10f, 0.05f, 10f), Quaternion.identity, "startRing", OverlayLayer);
        }

        void SetupLine(LineRenderer l, Color c, float w)
        {
            l.sharedMaterial = Overlay(c); l.startWidth = l.endWidth = w; l.useWorldSpace = true; l.numCapVertices = 2; l.numCornerVertices = 2;
            l.shadowCastingMode = ShadowCastingMode.Off; l.receiveShadows = false; l.alignment = LineAlignment.View;
        }

        void BuildRover(Transform T)
        {
            rover = new GameObject("Rover") { hideFlags = HideFlags.DontSave, layer = OverlayLayer }; rover.transform.SetParent(T, false);
            var body = Mat(new Color(0.95f, 0.55f, 0.08f), 0.2f); var dark = Mat(new Color(0.12f, 0.12f, 0.14f), 0f);
            var q = Quaternion.identity; int L = OverlayLayer; var tr = rover.transform;
            Part(tr, cube, body, new Vector3(0, 0.75f, -0.1f), new Vector3(1.5f, 0.6f, 2.6f), q, "body", L);
            Part(tr, cube, body, new Vector3(0, 1.2f, -0.5f), new Vector3(1.2f, 0.35f, 1.4f), q, "top", L);
            foreach (float sx in new[] { -0.85f, 0.85f })
            {
                Part(tr, cube, dark, new Vector3(sx, 0.4f, 0), new Vector3(0.45f, 0.8f, 3.1f), q, "track", L);
                for (int i = -1; i <= 1; i++) Part(tr, cylinder, Mat(new Color(0.35f, 0.35f, 0.38f), 0.2f), new Vector3(sx * 1.1f, 0.4f, i * 0.95f), new Vector3(0.7f, 0.12f, 0.7f), Quaternion.Euler(0, 0, 90), "wheel", L);
            }
            Part(tr, cube, dark, new Vector3(0, 1.3f, 0.4f), new Vector3(0.12f, 0.5f, 0.12f), q, "mast", L);
            Part(tr, cube, Mat(new Color(0.2f, 0.2f, 0.25f), 0.3f), new Vector3(0, 1.55f, 0.4f), new Vector3(0.34f, 0.2f, 0.3f), q, "camera", L);
            Part(tr, cylinder, Overlay(new Color(1f, 0.1f, 0.1f)), new Vector3(0, 1.5f, -0.9f), new Vector3(0.25f, 0.1f, 0.25f), q, "beacon", L);
        }

        void BuildCamera()
        {
            var cm = sim.cam;
            if (camRT == null || camRT.width != cm.w || camRT.height != cm.h)
            {
                if (camRT != null) { camRT.Release(); DestroyImmediate(camRT); }
                camRT = new RenderTexture(cm.w, cm.h, 24, RenderTextureFormat.ARGB32) { name = "RoverCamRT", filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
                camRT.Create();
            }
            if (readTex == null || readTex.width != cm.w) readTex = new Texture2D(cm.w, cm.h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            if (perceptionCam == null)
            {
                var go = new GameObject("RoverCamera") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(root.transform, false);
                perceptionCam = go.AddComponent<Camera>();
                var d = go.AddComponent<UniversalAdditionalCameraData>();
                d.renderPostProcessing = false; d.antialiasing = AntialiasingMode.None; d.renderShadows = true;
            }
            else if (perceptionCam.transform.parent != root.transform) perceptionCam.transform.SetParent(root.transform, false);
            perceptionCam.enabled = false;
            perceptionCam.targetTexture = camRT;
            perceptionCam.clearFlags = CameraClearFlags.SolidColor; perceptionCam.backgroundColor = Sky;
            perceptionCam.cullingMask = ~(1 << OverlayLayer);
            perceptionCam.fieldOfView = 2f * Mathf.Atan(cm.h * 0.5f / cm.F) * Mathf.Rad2Deg;
            perceptionCam.aspect = cm.w / (float)cm.h;
            perceptionCam.nearClipPlane = 0.3f; perceptionCam.farClipPlane = 400f;
            sim.grab = Grab;
        }

        public void PlaceCamera(Pose p)
        {
            var cm = sim.cam;
            var f = p.Fwd;
            perceptionCam.transform.position = new Vector3(p.pos.x + f.x * cm.fwdOffset, cm.height, p.pos.y + f.y * cm.fwdOffset);
            perceptionCam.transform.rotation = Quaternion.Euler(cm.pitchDeg, p.yaw * Mathf.Rad2Deg, 0);
        }

        public Color32[] Grab(Pose p)
        {
            if (perceptionCam == null) return null;
            PlaceCamera(p);
            perceptionCam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = camRT;
            readTex.ReadPixels(new Rect(0, 0, camRT.width, camRT.height), 0, 0, false);
            RenderTexture.active = prev;
            return readTex.GetPixels32();
        }

        /// <summary>
        /// Ground-truth class image(s) for training: re-renders the same poses with every surface replaced by a flat unlit colour
        /// of its albedo class (0 road, 1 crossable debris, 2 other incl. sky). Rows bottom-to-top like <see cref="Grab"/>; value in the R/B channels.
        /// </summary>
        public readonly Dictionary<string, int> labelInfo = new Dictionary<string, int>();
        public Color32[][] GrabLabelsBatch(IList<Pose> poses)
        {
            labelInfo.Clear();
            var res = new Color32[poses.Count][];
            if (perceptionCam == null || root == null) return res;
            var rends = root.GetComponentsInChildren<MeshRenderer>(true);
            var orig = new Material[rends.Length][];
            var lc = new[] { new Color(0, 0, 0), new Color(1, 0, 0), new Color(0, 0, 1) };
            var lab = new[] { Overlay(lc[0]), Overlay(lc[1]), Overlay(lc[2]) };
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i]; if (r.gameObject.layer == OverlayLayer) continue;
                orig[i] = r.sharedMaterials;
                var m = orig[i].Length > 0 ? orig[i][0] : null;
                // truth by object type, not by colour: asphalt + markings are road, hazard debris is crossable, everything else is other
                string nm = r.gameObject.name;
                byte cls = nm == "Roads" || nm == "Markings" ? Perception.Road : nm == "debris" ? Perception.Cross : Perception.Other;
                string key = nm + ":" + cls; labelInfo.TryGetValue(key, out int nn); labelInfo[key] = nn + 1;
                r.sharedMaterial = lab[cls];
            }
            var bg = perceptionCam.backgroundColor; perceptionCam.backgroundColor = lc[2];
            var shadowsPrev = perceptionCam.GetUniversalAdditionalCameraData().renderShadows;
            perceptionCam.GetUniversalAdditionalCameraData().renderShadows = false;
            for (int k = 0; k < poses.Count; k++) res[k] = Grab(poses[k]);
            perceptionCam.GetUniversalAdditionalCameraData().renderShadows = shadowsPrev;
            perceptionCam.backgroundColor = bg;
            for (int i = 0; i < rends.Length; i++) if (orig[i] != null) rends[i].sharedMaterials = orig[i];
            foreach (var m in lab) DestroyImmediate(m);
            return res;
        }

        // ------------------------------------------------------------------ per frame

        public Vector3 RoverPos => sim != null ? new Vector3(sim.pose.pos.x, 0, sim.pose.pos.y) : Vector3.zero;

        public void Render(float frac)
        {
            if (!IsBuilt || sim == null) return;
            var p = sim.pose; var city = sim.city;
            float bump = sim.bump, tt = (float)sim.time;
            float rx = bump > 0.05f ? Mathf.Sin(tt * 11f) * 3f * Mathf.Min(1f, bump) : 0f;
            float rz = bump > 0.05f ? Mathf.Sin(tt * 8f + 1f) * 3f * Mathf.Min(1f, bump) : 0f;
            rover.transform.position = new Vector3(p.pos.x, 0.2f + (bump > 0.05f ? Mathf.Abs(Mathf.Sin(tt * 9f)) * 0.08f : 0f), p.pos.y);
            rover.transform.rotation = Quaternion.Euler(rx, p.yaw * Mathf.Rad2Deg, rz);
            rover.SetActive(sim.state != SimState.Idle || sim.tripCounter > 0);

            bool active = sim.tripCounter > 0;
            goalGo.SetActive(active); startGo.SetActive(active);
            if (active)
            {
                var g = city.nodes[sim.goalNode]; goalGo.transform.position = new Vector3(g.x, 0.2f, g.y);
                var s = city.nodes[sim.startNode]; startGo.transform.position = new Vector3(s.x, 0.2f, s.y);
            }
            routeLine.enabled = showRoute && !hideOverlays && active && sim.pts.Count > 1;
            if (routeLine.enabled)
            {
                routeLine.positionCount = sim.pts.Count;
                for (int i = 0; i < sim.pts.Count; i++) routeLine.SetPosition(i, new Vector3(sim.pts[i].x, 0.35f, sim.pts[i].y));
            }
            headingLine.enabled = showScan && !hideOverlays && active && sim.state == SimState.Driving;
            if (headingLine.enabled)
            {
                float h = p.yaw + sim.decision.heading;
                var d = new Vector3(Mathf.Sin(h), 0, Mathf.Cos(h));
                var o = new Vector3(p.pos.x, 0.5f, p.pos.y);
                headingLine.positionCount = 2; headingLine.SetPosition(0, o); headingLine.SetPosition(1, o + d * Mathf.Clamp(sim.decision.clearance, 3f, 25f));
            }
        }

        void OnDestroy() { Clear(); if (camRT != null) { camRT.Release(); DestroyImmediate(camRT); } }
    }
}
