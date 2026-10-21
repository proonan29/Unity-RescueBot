using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RescueBot.Core;
using RescueBot.UI;
using RescueBot.World;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace RescueBot.EditorTools
{
    /// <summary>Scene setup, app control and headless evaluation: `unity command rb_*`.</summary>
    public static class RescueCommands
    {
        const string ScenePath = "Assets/RescueBot/Scenes/RescueBot.unity";
        const string UiFolder = "Assets/RescueBot/UI";
        const string PanelSettingsPath = UiFolder + "/RescueBotPanelSettings.asset";
        const string ThemePath = UiFolder + "/RescueBotTheme.tss";
        const string TextSettingsPath = UiFolder + "/RescueBotTextSettings.asset";
        const string FontPath = UiFolder + "/Fonts/NotoSansKR-Regular.otf";
        const string FontAssetPath = UiFolder + "/Fonts/NotoSansKR-Regular SDF.asset";

        static PanelSettings EnsurePanelSettings()
        {
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (ps != null) return ps;
            ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            AssetDatabase.CreateAsset(ps, PanelSettingsPath);
            return ps;
        }

        [CliCommand("rb_setup_scene", "Create the RescueBot scene: light, camera rig, city view, app controller and UI. Saves the scene.")]
        public static string SetupScene([CliArg("nx", "Blocks in X (3-9)")] int nx = 5,
                                        [CliArg("ny", "Blocks in Y (3-9)")] int ny = 5,
                                        [CliArg("severity", "Disaster severity 0..1")] float severity = 0.5f,
                                        [CliArg("seed", "City seed")] int seed = 1,
                                        [CliArg("spacing", "Road spacing (m)")] float spacing = 60f,
                                        [CliArg("scene_path", "Scene asset path")] string scenePath = ScenePath)
        {
            if (Application.isPlaying) return "error: exit play mode first";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath));
            PlayerSettings.runInBackground = true;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.2f; sun.color = Color.white; sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.55f;
            sunGo.transform.rotation = Quaternion.Euler(52f, -38f, 0f);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.52f, 0.52f, 0.52f);
            RenderSettings.skybox = null;
            RenderSettings.fog = false;

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = CityView.Sky;
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false; camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing; camData.stopNaN = true;
            var rig = camGo.AddComponent<CameraRig>();

            var viewGo = new GameObject("CityView");
            var view = viewGo.AddComponent<CityView>();
            view.litShader = Shader.Find("Universal Render Pipeline/Lit");
            view.unlitShader = Shader.Find("Universal Render Pipeline/Unlit");

            var appGo = new GameObject("App");
            appGo.SetActive(false);
            var app = appGo.AddComponent<AppController>();
            app.view = view; app.rig = rig;
            app.config = new CityConfig { nx = Mathf.Clamp(nx, 3, 9), ny = Mathf.Clamp(ny, 3, 9), severity = Mathf.Clamp01(severity), seed = seed, spacing = spacing };

            var uiGo = new GameObject("UI");
            uiGo.SetActive(false);
            var docu = uiGo.AddComponent<UIDocument>();
            docu.panelSettings = EnsurePanelSettings();
            var ui = uiGo.AddComponent<RescueBotUI>(); ui.app = app;
            SetupFont(false);
            appGo.SetActive(true);
            uiGo.SetActive(true);
            app.EnsureBuilt();

            EditorSceneManager.SaveScene(scene, scenePath);
            var list = EditorBuildSettings.scenes.Where(s => s.path != scenePath && !s.path.EndsWith("SampleScene.unity")).ToList();
            list.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
            AssetDatabase.SaveAssets();
            return $"scene={scenePath} build_ms={sw.ElapsedMilliseconds}\n{app.sim.Report()}";
        }

        [CliCommand("rb_setup_font", "Create the Noto Sans KR dynamic font asset and register it as a UI fallback font.")]
        public static string SetupFont([CliArg("force", "Recreate the font asset")] bool force = false)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null) return $"error: font not found: {FontPath}";
            var fa = AssetDatabase.LoadAssetAtPath<FontAsset>(FontAssetPath);
            if (fa != null && force) { AssetDatabase.DeleteAsset(FontAssetPath); fa = null; }
            if (fa == null)
            {
                fa = FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
                if (fa == null) return "error: CreateFontAsset failed";
                fa.name = "NotoSansKR-Regular SDF";
                fa.isMultiAtlasTexturesEnabled = true;
                AssetDatabase.CreateAsset(fa, FontAssetPath);
                if (fa.atlasTextures != null) foreach (var tex in fa.atlasTextures) if (tex != null) { tex.name = fa.name + " Atlas"; AssetDatabase.AddObjectToAsset(tex, fa); }
                if (fa.material != null) { fa.material.name = fa.name + " Material"; AssetDatabase.AddObjectToAsset(fa.material, fa); }
                EditorUtility.SetDirty(fa);
            }
            var ts = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(TextSettingsPath);
            if (ts == null) { ts = ScriptableObject.CreateInstance<PanelTextSettings>(); AssetDatabase.CreateAsset(ts, TextSettingsPath); }
            if (ts.fallbackFontAssets == null) ts.fallbackFontAssets = new List<FontAsset>();
            if (!ts.fallbackFontAssets.Contains(fa)) ts.fallbackFontAssets.Add(fa);
            ts.clearDynamicDataOnBuild = true;
            EditorUtility.SetDirty(ts);
            var ps = EnsurePanelSettings();
            ps.textSettings = ts;
            EditorUtility.SetDirty(ps);
            AssetDatabase.SaveAssets();
            return $"font={FontAssetPath} hangul={fa.HasCharacter('한', true, true)}";
        }

        // ------------------------------------------------------------------ app control

        static AppController App(out string error)
        {
            error = null;
            var app = UnityEngine.Object.FindAnyObjectByType<AppController>();
            if (app == null) { error = "error: no AppController in the active scene (run rb_setup_scene)"; return null; }
            app.EnsureBuilt();
            if (!app.Ready) { error = "error: app not ready"; return null; }
            return app;
        }

        static void MarkDirty(AppController app) { app.view.Render(0f); app.rig.Place(); if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(app.gameObject.scene); }
        static void RefreshUI() { var ui = UnityEngine.Object.FindAnyObjectByType<RescueBotUI>(); if (ui != null) ui.Build(); }

        [CliCommand("rb_status", "Simulation state and statistics of the running app (edit or play mode).")]
        public static string Status()
        {
            var app = App(out var err); if (app == null) return err;
            return $"play={Application.isPlaying} running={app.running} loop={app.loop} speed={app.speed} view={app.rig.mode}\n" + app.sim.Report();
        }

        [CliCommand("rb_new", "Generate a new city (scene) and start a trip. seed < 0 = random.")]
        public static string New([CliArg("nx", "Blocks in X (3-9)")] int nx = 0,
                                 [CliArg("ny", "Blocks in Y (3-9)")] int ny = 0,
                                 [CliArg("severity", "Disaster severity 0..1, <0 = unchanged")] float severity = -1f,
                                 [CliArg("seed", "Seed, <0 = random")] int seed = -1,
                                 [CliArg("spacing", "Road spacing, 0 = unchanged")] float spacing = 0f,
                                 [CliArg("advance", "Seconds to simulate right away")] float advance = 0f)
        {
            var app = App(out var err); if (app == null) return err;
            var c = app.config.Clone();
            if (nx > 0) c.nx = Mathf.Clamp(nx, 3, 9);
            if (ny > 0) c.ny = Mathf.Clamp(ny, 3, 9);
            if (severity >= 0) c.severity = Mathf.Clamp01(severity);
            if (spacing > 0) c.spacing = spacing;
            c.seed = seed >= 0 ? seed : new System.Random().Next(1, 1000000);
            app.Rebuild(c, true);
            if (advance > 0) app.Advance(advance);
            RefreshUI(); MarkDirty(app);
            return app.sim.Report();
        }

        [CliCommand("rb_advance", "Advance the simulation by N seconds (renders the rover camera each decision).")]
        public static string Advance([CliArg("seconds", "Simulated seconds")] float seconds = 20f)
        {
            var app = App(out var err); if (app == null) return err;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            app.Advance(seconds);
            RefreshUI();
            return $"advanced {seconds}s in {sw.ElapsedMilliseconds} ms\n" + app.sim.Report();
        }

        [CliCommand("rb_set", "Set run state, loop, speed, view (overview|top|chase|onboard), language (ko|en). Empty = unchanged.")]
        public static string Set([CliArg("running", "true/false")] string running = "",
                                 [CliArg("loop", "true/false")] string loop = "",
                                 [CliArg("speed", "Speed multiplier, 0 = unchanged")] float speed = 0f,
                                 [CliArg("view", "overview|top|chase|onboard")] string view = "",
                                 [CliArg("lang", "ko|en")] string lang = "",
                                 [CliArg("show_route", "true/false")] string showRoute = "")
        {
            var app = App(out var err); if (app == null) return err;
            if (running != "") app.running = running == "true";
            if (loop != "") app.SetLoop(loop == "true");
            if (speed > 0) app.speed = speed;
            if (view != "") app.SetView(view.StartsWith("t") ? ViewMode.Top : view.StartsWith("c") ? ViewMode.Chase : view.StartsWith("on") || view.StartsWith("r") ? ViewMode.Onboard : ViewMode.Overview);
            if (lang != "") Loc.Korean = lang == "ko";
            if (showRoute != "") app.view.showRoute = showRoute == "true";
            RefreshUI(); MarkDirty(app);
            return $"running={app.running} loop={app.loop} speed={app.speed} view={app.rig.mode} lang={(Loc.Korean ? "ko" : "en")}";
        }

        // ------------------------------------------------------------------ headless evaluation

        sealed class Harness : IDisposable
        {
            public GameObject go; public CityView view; public RescueSim sim;
            readonly List<CityView> hidden = new List<CityView>();
            public Harness(CityConfig cfg)
            {
                // the open scene may hold its own city at the same coordinates: hide it so the camera only sees this one
                foreach (var cv in UnityEngine.Object.FindObjectsByType<CityView>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (cv.Visible) { cv.Visible = false; hidden.Add(cv); }
                go = new GameObject("RB_Harness") { hideFlags = HideFlags.HideAndDontSave };
                view = go.AddComponent<CityView>();
                view.litShader = Shader.Find("Universal Render Pipeline/Lit");
                view.unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                sim = new RescueSim(new City(cfg), cfg.seed) { loop = true };
                view.Build(sim);
            }
            public void Dispose() { foreach (var cv in hidden) if (cv != null) cv.Visible = true; view.Clear(); if (view.camRT != null) { view.camRT.Release(); UnityEngine.Object.DestroyImmediate(view.camRT); } UnityEngine.Object.DestroyImmediate(go); }
        }

        [CliCommand("rb_sim_run", "Headless: run N trips on a generated city and print the statistics (uses the real camera + perception).")]
        public static string SimRun([CliArg("nx", "Blocks in X")] int nx = 5,
                                    [CliArg("ny", "Blocks in Y")] int ny = 5,
                                    [CliArg("severity", "Disaster severity 0..1")] float severity = 0.5f,
                                    [CliArg("seed", "City seed")] int seed = 1,
                                    [CliArg("spacing", "Road spacing")] float spacing = 60f,
                                    [CliArg("trips", "Trips to run")] int trips = 5,
                                    [CliArg("max_seconds", "Wall-clock limit (s)")] float maxWall = 45f)
        {
            var cfg = new CityConfig { nx = nx, ny = ny, severity = severity, seed = seed, spacing = spacing };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var h = new Harness(cfg))
            {
                var sim = h.sim;
                sim.StartTrip();
                while (sim.history.Count < trips && sw.Elapsed.TotalSeconds < maxWall) sim.Advance(5f);
                var sb = new StringBuilder();
                sb.AppendLine(sim.Report());
                foreach (var t in sim.history)
                    sb.AppendLine($"  #{t.index,2} {t.start,3}->{t.goal,3} {(t.success ? "ok  " : "FAIL")} {t.reason,-8} time={t.time,5:0}s dist={t.dist,5:0}m oracle={t.oracleCost,5:0} eff={t.efficiency * 100,3:0}% replans={t.replans} blockedFound={t.blockedEvents} crossed={t.crossings} coll={t.collisions}");
                sb.AppendLine($"wall={sw.ElapsedMilliseconds} ms simTime={sim.time:0}s (x{sim.time * 1000f / Mathf.Max(1, sw.ElapsedMilliseconds):0.0})");
                return sb.ToString();
            }
        }

        [CliCommand("rb_trace", "Headless: run trips and print the event log (blocked/rough marks with ground truth, replans, collisions).")]
        public static string Trace([CliArg("nx", "Blocks in X")] int nx = 5,
                                   [CliArg("ny", "Blocks in Y")] int ny = 5,
                                   [CliArg("severity", "Severity")] float severity = 0.5f,
                                   [CliArg("seed", "Seed")] int seed = 1,
                                   [CliArg("trips", "Trips")] int trips = 1,
                                   [CliArg("skip", "Skip the first N log lines")] int skip = 0,
                                   [CliArg("lines", "Max lines")] int lines = 80,
                                   [CliArg("until", "Simulate until this time (s) in the first trip, then save the camera png")] float until = 0f,
                                   [CliArg("png", "png name prefix")] string png = "trace",
                                   [CliArg("trip", "Which trip to inspect with --until (1-based)")] int trip = 1,
                                   [CliArg("stuck", "Run until the rover has not moved for 12 s, then save pngs")] bool stuck = false,
                                   [CliArg("timeline", "Print a state line every N seconds until --until")] float timeline = 0f)
        {
            var cfg = new CityConfig { nx = nx, ny = ny, severity = severity, seed = seed };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var h = new Harness(cfg))
            {
                var sim = h.sim; sim.logging = true; sim.StartTrip();
                if (stuck)
                {
                    while (sim.stuckTimer < 12f && sw.Elapsed.TotalSeconds < 50) sim.Advance(0.1f);
                    sim.driver.debugOn = true; sim.Advance(0.25f);
                    return SavePngs(h.view, sim, png) + "\n" + sim.driver.debug + "\n" + string.Join("\n", sim.log.Skip(Mathf.Max(0, sim.log.Count - 4)));
                }
                if (until > 0f)
                {
                    while (sim.history.Count < trip - 1 && sw.Elapsed.TotalSeconds < 40) sim.Advance(0.5f);
                    until += sim.time;
                    var tl = new StringBuilder();
                    while (sim.time < until && sw.Elapsed.TotalSeconds < 40)
                    {
                        sim.Advance(timeline > 0f ? timeline : Mathf.Min(1f, until - sim.time));
                        if (timeline > 0f) tl.AppendLine($"t={sim.time:0.0} pos=({sim.pose.pos.x:0.0},{sim.pose.pos.y:0.0}) yaw={sim.pose.yaw * Mathf.Rad2Deg:0} v={sim.v:0.0} act={sim.decision.act} clr={sim.decision.clearance:0.0}");
                    }
                    if (timeline > 0f) return tl.ToString();
                    sim.driver.debugOn = true; sim.Advance(0.25f);
                    string r = SavePngs(h.view, sim, png) + "\n" + sim.driver.debug;
                    return r + "\n" + string.Join("\n", sim.log.Skip(Mathf.Max(0, sim.log.Count - 6)));
                }
                while (sim.history.Count < trips && sw.Elapsed.TotalSeconds < 40) sim.Advance(5f);
                return $"trips={sim.history.Count} log={sim.log.Count}\n" + string.Join("\n", sim.log.Skip(skip).Take(lines));
            }
        }
        [CliCommand("rb_city_info", "Print the ground truth of a generated city: roads with obstacles, blocked and rough roads, connectivity.")]
        public static string CityInfo([CliArg("nx", "Blocks in X")] int nx = 5,
                                      [CliArg("ny", "Blocks in Y")] int ny = 5,
                                      [CliArg("severity", "Severity 0..1")] float severity = 0.5f,
                                      [CliArg("seed", "Seed")] int seed = 1,
                                      [CliArg("reachable", "Keep connected")] bool reachable = true,
                                      [CliArg("road", "Dump the gap profile of this road id")] int road = -1)
        {
            var c = new City(new CityConfig { nx = nx, ny = ny, severity = severity, seed = seed, ensureReachable = reachable });
            if (road >= 0) return c.GapProfile(c.edges[road]);
            var sb = new StringBuilder();
            sb.AppendLine($"nodes={c.nodes.Length} roads={c.edges.Count} obstacles={c.obstacles.Count} (cars {c.obstacles.Count(o => o.kind == ObsKind.Car)}, rubble {c.obstacles.Count(o => o.kind == ObsKind.Rubble)}, debris {c.obstacles.Count(o => o.kind == ObsKind.Debris)}) blocked={c.blockedEdges} rough={c.roughEdges} downgraded={c.downgraded} buildings={c.buildings.Count}");
            int unreachable = 0;
            for (int i = 1; i < c.nodes.Length; i++) { var r = c.Route(0, i, e => e.TruthCost, out _); if (r == null) unreachable++; }
            sb.AppendLine($"nodes unreachable from node 0: {unreachable}");
            foreach (var e in c.edges.Where(e => e.truth != EdgeStatus.Clear))
                sb.AppendLine($"  road {e.id,3} {e.a}-{e.b} {e.truth,-7} rough={e.roughCount} obs={e.obstacles.Count}");
            return sb.ToString();
        }

        static void SaveTop(RescueSim sim, string name)
        {
            var go = new GameObject("topcam") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 28f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.2f, 0.25f, 0.2f);
            go.transform.position = new Vector3(sim.pose.pos.x, 100f, sim.pose.pos.y); go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var rt = RenderTexture.GetTemporary(640, 640, 24); cam.targetTexture = rt; cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(640, 640, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, 640, 640), 0, 0); tex.Apply();
            RenderTexture.active = prev; cam.targetTexture = null; RenderTexture.ReleaseTemporary(rt);
            Directory.CreateDirectory("Screenshots");
            File.WriteAllBytes($"Screenshots/{name}_top.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex); UnityEngine.Object.DestroyImmediate(go);
        }

        static string SavePngs(CityView view, RescueSim sim, string name)
        {
            SaveTop(sim, name);
            var img = view.Grab(sim.pose);
            var scan = Perception.Analyze(img, sim.cam);
            int w = sim.cam.w, h = sim.cam.h;
            var a = new Texture2D(w, h, TextureFormat.RGBA32, false); a.SetPixels32(img); a.Apply();
            var b = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int v = 0; v < h; v++) for (int u = 0; u < w; u++) { byte c = scan.seg[v * w + u]; px[(h - 1 - v) * w + u] = c == 0 ? new Color32(58, 60, 72, 255) : c == 1 ? new Color32(255, 200, 0, 255) : new Color32(120, 60, 55, 255); }
            for (int u = 0; u < w; u++) if (scan.row[u] >= 0) px[(h - 1 - scan.row[u]) * w + u] = new Color32(255, 255, 255, 255);
            b.SetPixels32(px); b.Apply();
            Directory.CreateDirectory("Screenshots");
            File.WriteAllBytes($"Screenshots/{name}_cam.png", a.EncodeToPNG());
            File.WriteAllBytes($"Screenshots/{name}_seg.png", b.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
            return $"saved {name} t={sim.time:0.0} pos=({sim.pose.pos.x:0},{sim.pose.pos.y:0}) yaw={sim.pose.yaw * Mathf.Rad2Deg:0} act={sim.decision.act}";
        }

        [CliCommand("rb_cam_png", "Save the rover camera image and the perception result of the current app state to Screenshots/.")]
        public static string CamPng([CliArg("name", "File prefix")] string name = "cam")
        {
            var app = App(out var err); if (app == null) return err;
            var sim = app.sim;
            if (sim.tripCounter == 0) sim.StartTrip();
            var img = app.view.Grab(sim.pose);
            var scan = Perception.Analyze(img, sim.cam);
            int w = sim.cam.w, h = sim.cam.h;
            var a = new Texture2D(w, h, TextureFormat.RGBA32, false); a.SetPixels32(img); a.Apply();
            var b = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int v = 0; v < h; v++) for (int u = 0; u < w; u++) { byte c = scan.seg[v * w + u]; px[(h - 1 - v) * w + u] = c == 0 ? new Color32(58, 60, 72, 255) : c == 1 ? new Color32(255, 200, 0, 255) : new Color32(120, 60, 55, 255); }
            for (int u = 0; u < w; u++) if (scan.row[u] >= 0) px[(h - 1 - scan.row[u]) * w + u] = new Color32(255, 255, 255, 255);
            b.SetPixels32(px); b.Apply();
            Directory.CreateDirectory("Screenshots");
            File.WriteAllBytes($"Screenshots/{name}_cam.png", a.EncodeToPNG());
            File.WriteAllBytes($"Screenshots/{name}_seg.png", b.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
            var sb = new StringBuilder($"saved Screenshots/{name}_cam.png, {name}_seg.png roadFraction={scan.roadFraction:0.00}\n");
            for (int u = 0; u < w; u += 10) sb.Append($"u{u}:{(scan.cls[u] == 0 ? "free" : scan.cls[u] == 1 ? "X" : "S")}@{scan.Z[u]:0}/{scan.X[u]:0.0} ");
            return sb.ToString();
        }
    }
}
