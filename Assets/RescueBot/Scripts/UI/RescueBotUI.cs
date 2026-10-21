using System;
using System.Collections.Generic;
using System.Linq;
using RescueBot.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace RescueBot.UI
{
    /// <summary>
    /// UI Toolkit interface built in code. Left: scene (grid X/Y, spacing, severity, new scene), run (start, stop, loop, speed),
    /// camera view, display and language. Right: the rover's camera image, what the perception made of it, decision and trip statistics.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(UIDocument))]
    public class RescueBotUI : MonoBehaviour
    {
        public AppController app;

        UIDocument doc;
        VisualElement root, left, right;
        Label perceptionLabel, stateLabel, decisionLabel, tripLabel, totalLabel, recentLabel, cityLabel, hintLabel;
        Image camImage, segImage;
        Texture2D segTex;
        float nextRefresh;

        static readonly Color Panel = new Color(0.05f, 0.07f, 0.10f, 0.88f);
        static readonly Color Accent = new Color(0.35f, 0.85f, 1f);
        static readonly Color Dim = new Color(0.68f, 0.72f, 0.78f);

        void OnEnable()
        {
            doc = GetComponent<UIDocument>();
            Build();
            if (app != null)
            {
                app.onChanged -= Build; app.onChanged += Build;
                if (app.rig != null) app.rig.isOverUI = IsOverUI;
            }
        }

        void OnDisable() { if (app != null) app.onChanged -= Build; }

        // ------------------------------------------------------------------ helpers

        static VisualElement Box(Color bg, float pad = 10)
        {
            var v = new VisualElement();
            v.style.backgroundColor = bg;
            v.style.paddingLeft = v.style.paddingRight = pad; v.style.paddingTop = v.style.paddingBottom = pad;
            v.style.borderTopLeftRadius = v.style.borderTopRightRadius = v.style.borderBottomLeftRadius = v.style.borderBottomRightRadius = 8;
            return v;
        }

        static Label Text(string s, int size = 13, Color? c = null, bool bold = false)
        {
            var l = new Label(s);
            l.style.fontSize = size; l.style.color = c ?? new Color(0.92f, 0.94f, 0.97f);
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginBottom = 2;
            return l;
        }

        static VisualElement Row(bool wrap = true)
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row;
            if (wrap) r.style.flexWrap = Wrap.Wrap;
            r.style.marginBottom = 4;
            r.style.alignItems = Align.Center;
            return r;
        }

        static Button Btn(string s, Action a, bool on = false, float minW = 0, bool big = false)
        {
            var b = new Button(a) { text = s };
            b.style.fontSize = big ? 15 : 12;
            b.style.marginLeft = 0; b.style.marginRight = 4; b.style.marginTop = 2; b.style.marginBottom = 2;
            b.style.paddingLeft = big ? 12 : 7; b.style.paddingRight = big ? 12 : 7; b.style.paddingTop = big ? 7 : 3; b.style.paddingBottom = big ? 7 : 3;
            b.style.backgroundColor = on ? new Color(0.05f, 0.5f, 0.7f) : new Color(0.15f, 0.19f, 0.25f);
            b.style.color = Color.white;
            b.style.borderTopWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = b.style.borderRightWidth = 0;
            b.style.borderTopLeftRadius = b.style.borderTopRightRadius = b.style.borderBottomLeftRadius = b.style.borderBottomRightRadius = 5;
            if (minW > 0) b.style.minWidth = minW;
            return b;
        }

        static void Section(VisualElement parent, string title)
        {
            var l = Text(title, 12, Accent, true);
            l.style.marginTop = 10; l.style.marginBottom = 4;
            l.style.borderBottomWidth = 1; l.style.borderBottomColor = new Color(0.35f, 0.85f, 1f, 0.25f);
            parent.Add(l);
        }

        static void Check(VisualElement parent, string label, Func<bool> get, Action<bool> set)
        {
            var t = new Toggle(Loc.T(label)) { value = get() };
            t.style.fontSize = 12; t.style.color = Color.white; t.style.marginBottom = 0; t.style.marginTop = 0;
            t.labelElement.style.minWidth = 150; t.labelElement.style.color = new Color(0.86f, 0.9f, 0.95f);
            t.RegisterValueChangedCallback(e => set(e.newValue));
            parent.Add(t);
        }

        static VisualElement Chip(string s, Color c)
        {
            var r = new VisualElement(); r.style.flexDirection = FlexDirection.Row; r.style.alignItems = Align.Center; r.style.marginRight = 8; r.style.marginBottom = 2;
            var sw = new VisualElement(); sw.style.width = 10; sw.style.height = 10; sw.style.backgroundColor = c; sw.style.marginRight = 4;
            sw.style.borderTopLeftRadius = sw.style.borderTopRightRadius = sw.style.borderBottomLeftRadius = sw.style.borderBottomRightRadius = 5;
            r.Add(sw); r.Add(Text(s, 11, new Color(0.86f, 0.9f, 0.95f)));
            return r;
        }

        static void Stepper(VisualElement parent, string name, int value, int min, int max, Action<int> set)
        {
            var r = Row();
            var l = Text(Loc.T(name), 12, Dim); l.style.width = 130; r.Add(l);
            r.Add(Btn("−", () => set(Mathf.Max(min, value - 1)), false, 26));
            var v = Text(value.ToString(), 14, Color.white, true); v.style.width = 30; v.style.unityTextAlign = TextAnchor.MiddleCenter; r.Add(v);
            r.Add(Btn("+", () => set(Mathf.Min(max, value + 1)), false, 26));
            parent.Add(r);
        }

        // ------------------------------------------------------------------ build

        public void Build()
        {
            if (doc == null) doc = GetComponent<UIDocument>();
            root = doc != null ? doc.rootVisualElement : null;
            if (root == null || app == null || app.sim == null) return;
            var cfg = app.config;
            root.Clear();
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Ignore;

            left = Box(Panel, 12);
            left.style.position = Position.Absolute; left.style.left = 10; left.style.top = 10; left.style.bottom = 10; left.style.width = 340;
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.style.flexGrow = 1;
            left.Add(scroll); root.Add(left);
            var lc = scroll.contentContainer;

            lc.Add(Text("RescueBot", 22, Color.white, true));
            lc.Add(Text(Loc.T("Camera-guided disaster rescue rover"), 12, Dim));

            Section(lc, Loc.T("Scene"));
            Stepper(lc, "Grid X (blocks)", cfg.nx, 3, 9, v => app.SetGrid(v, cfg.ny));
            Stepper(lc, "Grid Y (blocks)", cfg.ny, 3, 9, v => app.SetGrid(cfg.nx, v));
            lc.Add(Text(Loc.T("Road spacing (m)"), 12, Dim));
            var sp = Row();
            foreach (var s in new[] { 40, 60, 80, 100 }) { int ss = s; sp.Add(Btn(s.ToString(), () => app.SetSpacing(ss), Mathf.Approximately(cfg.spacing, s), 38)); }
            lc.Add(sp);
            lc.Add(Text(Loc.T("Disaster severity"), 12, Dim));
            var sv = Row();
            foreach (var s in new[] { 0, 25, 50, 75, 100 }) { int ss = s; sv.Add(Btn(s + "%", () => app.SetSeverity(ss / 100f), Mathf.Abs(cfg.severity * 100f - s) < 1f, 44)); }
            lc.Add(sv);
            Check(lc, "Keep every junction reachable", () => cfg.ensureReachable, x => app.SetEnsureReachable(x));
            var nr = Row();
            nr.Add(Btn("✚ " + Loc.T("New scene"), () => app.NewScene(), false, 0, true));
            cityLabel = Text("", 11, Dim); nr.Add(cityLabel);
            lc.Add(nr);

            Section(lc, Loc.T("Run"));
            var rr = Row();
            rr.Add(Btn("▶ " + Loc.T("Start"), () => app.StartRun(), app.running, 80, true));
            rr.Add(Btn("■ " + Loc.T("Stop"), () => app.StopRun(), !app.running, 80, true));
            lc.Add(rr);
            Check(lc, "Loop (new start and goal after arrival)", () => app.loop, x => app.SetLoop(x));
            lc.Add(Text(Loc.T("Speed"), 12, Dim));
            var spd = Row();
            foreach (var s in new[] { 1, 2, 5, 10, 30 }) { int ss = s; spd.Add(Btn($"{s}x", () => { app.speed = ss; Build(); }, Mathf.Approximately(app.speed, s), 40)); }
            lc.Add(spd);
            stateLabel = Text("", 13, Color.white, true); lc.Add(stateLabel);

            Section(lc, Loc.T("Vision model"));
            var pm = Row();
            pm.Add(Btn(Loc.T("Classic (colour rule)"), () => app.SetPerception(false), !PerceptionSwitch.UsingCnn));
            pm.Add(Btn(Loc.T("CNN (U-Net)"), () => { if (!app.SetPerception(true)) perceptionLabel.text = Loc.T("CNN model missing"); }, PerceptionSwitch.UsingCnn));
            lc.Add(pm);
            perceptionLabel = Text("", 10, Dim); lc.Add(perceptionLabel);

            Section(lc, Loc.T("Camera view"));
            var vr = Row();
            vr.Add(Btn(Loc.T("Overview"), () => app.SetView(ViewMode.Overview), app.rig.mode == ViewMode.Overview));
            vr.Add(Btn(Loc.T("Top"), () => app.SetView(ViewMode.Top), app.rig.mode == ViewMode.Top));
            vr.Add(Btn(Loc.T("Chase"), () => app.SetView(ViewMode.Chase), app.rig.mode == ViewMode.Chase));
            vr.Add(Btn(Loc.T("Onboard"), () => app.SetView(ViewMode.Onboard), app.rig.mode == ViewMode.Onboard));
            lc.Add(vr);
            Check(lc, "Show route", () => app.view.showRoute, x => app.view.showRoute = x);
            Check(lc, "Show decision line", () => app.view.showScan, x => app.view.showScan = x);
            hintLabel = Text(Loc.T("Drag: rotate · Right-drag: pan · Wheel: zoom"), 10, Dim); lc.Add(hintLabel);

            Section(lc, Loc.T("Legend"));
            var lg = Row();
            lg.Add(Chip(Loc.T("Road"), new Color(0.3f, 0.3f, 0.35f)));
            lg.Add(Chip(Loc.T("Crossable debris"), new Color(1f, 0.78f, 0f)));
            lg.Add(Chip(Loc.T("Solid obstacle"), new Color(0.8f, 0.28f, 0.24f)));
            lg.Add(Chip(Loc.T("Start point"), new Color(0.2f, 0.9f, 0.3f)));
            lg.Add(Chip(Loc.T("Goal"), new Color(1f, 0.2f, 0.4f)));
            lg.Add(Chip(Loc.T("Route"), new Color(0.1f, 0.9f, 1f)));
            lc.Add(lg);

            Section(lc, Loc.T("Settings"));
            var lr = Row();
            lr.Add(Btn("한국어", () => { Loc.Korean = true; Build(); }, Loc.Korean));
            lr.Add(Btn("English", () => { Loc.Korean = false; Build(); }, !Loc.Korean));
            lc.Add(lr);

            // ---------------- right panel
            right = Box(Panel, 10);
            right.style.position = Position.Absolute; right.style.right = 10; right.style.top = 10; right.style.bottom = 10; right.style.width = 360;
            root.Add(right);
            var rs = new ScrollView(ScrollViewMode.Vertical); rs.style.flexGrow = 1; right.Add(rs);
            var rc = rs.contentContainer;
            rc.Add(Text(Loc.T("Rover camera"), 13, Accent, true));
            camImage = new Image { scaleMode = ScaleMode.ScaleToFit };
            camImage.style.width = 336; camImage.style.height = 189; camImage.style.backgroundColor = Color.black;
            if (app.view.camRT != null) camImage.image = app.view.camRT;
            rc.Add(camImage);
            var cm = app.sim.cam;
            if (segTex == null || segTex.width != cm.w) { segTex = new Texture2D(cm.w, cm.h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave }; }
            var t0 = Text(Loc.T("Perception"), 13, Accent, true); t0.style.marginTop = 6; rc.Add(t0);
            segImage = new Image { scaleMode = ScaleMode.ScaleToFit, image = segTex };
            segImage.style.width = 336; segImage.style.height = 189; segImage.style.backgroundColor = Color.black;
            rc.Add(segImage);
            rc.Add(Text(Loc.T("road · debris (crossable) · solid · heading"), 10, Dim));
            var d0 = Text(Loc.T("Decision"), 13, Accent, true); d0.style.marginTop = 6; rc.Add(d0);
            decisionLabel = Text("", 12); rc.Add(decisionLabel);
            var t1 = Text(Loc.T("Trip"), 13, Accent, true); t1.style.marginTop = 6; rc.Add(t1);
            tripLabel = Text("", 12); rc.Add(tripLabel);
            var t2 = Text(Loc.T("Totals"), 13, Accent, true); t2.style.marginTop = 6; rc.Add(t2);
            totalLabel = Text("", 12); rc.Add(totalLabel);
            var t3 = Text(Loc.T("Recent trips"), 13, Accent, true); t3.style.marginTop = 6; rc.Add(t3);
            recentLabel = Text("", 11); rc.Add(recentLabel);
            nextRefresh = 0;
            Refresh();
        }

        public bool IsOverUI(Vector2 screenPos)
        {
            if (root == null || root.panel == null) return false;
            var p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screenPos.x, Screen.height - screenPos.y));
            return (left != null && left.worldBound.Contains(p)) || (right != null && right.worldBound.Contains(p));
        }

        // ------------------------------------------------------------------ refresh

        static string ActName(Act a)
        {
            switch (a)
            {
                case Act.Straight: return Loc.T("Go straight");
                case Act.AvoidLeft: return Loc.T("Avoid left");
                case Act.AvoidRight: return Loc.T("Avoid right");
                case Act.Cross: return Loc.T("Cross debris");
                case Act.Turn: return Loc.T("Turning");
                default: return Loc.T("Blocked");
            }
        }

        static string StateName(SimState s) => Loc.T(s.ToString());

        void Update()
        {
            if (app == null || app.sim == null || root == null) return;
            if (app.rig != null && root.layout.width > 0 && Screen.width > 0)
            {
                float k = Screen.width / root.layout.width;
                app.rig.panelLeft = (left.layout.xMax) * k; app.rig.panelRight = (root.layout.width - right.layout.xMin) * k;
            }
            if (Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = Time.realtimeSinceStartup + 0.1f;
            Refresh();
        }

        void Refresh()
        {
            var sim = app.sim; if (sim == null || stateLabel == null) return;
            var city = sim.city;
            cityLabel.text = $"{Loc.T("seed")} {city.cfg.seed} · {city.obstacles.Count} {Loc.T("obstacles")} · {city.blockedEdges} {Loc.T("blocked roads")} · {city.roughEdges} {Loc.T("rough roads")}";
            if (perceptionLabel != null && (PerceptionSwitch.UsingCnn || perceptionLabel.text.Length == 0 || perceptionLabel.text == "Classic colour rule")) perceptionLabel.text = PerceptionSwitch.Describe();
            stateLabel.text = $"{StateName(sim.state)}  ·  {sim.time:0}{Loc.T("s")}" + (app.running ? "" : "  (" + Loc.T("Stop") + ")");
            var d = sim.decision;
            decisionLabel.text = $"{Loc.T("action")}: {ActName(d.act)}\n{Loc.T("heading")}: {d.heading * Mathf.Rad2Deg:+0;-0;0}°   {Loc.T("clearance")}: {Mathf.Min(d.clearance, 99f):0} m\n{Loc.T("speed")}: {sim.v:0.0} m/s";
            var t = sim.trip;
            string res = sim.state == SimState.Arrived ? "  ✔" : sim.state == SimState.Failed ? "  ✖ " + Loc.T(t.reason) : "";
            tripLabel.text = $"#{sim.tripCounter}  {sim.startNode} → {sim.goalNode}{res}\n{Loc.T("time")}: {sim.TripTime:0}{Loc.T("s")}   {Loc.T("distance")}: {t.dist:0} m\n{Loc.T("replans")}: {t.replans}   {Loc.T("blocked roads found")}: {t.blockedEvents}\n{Loc.T("debris crossed")}: {sim.CrossedNow}   {Loc.T("collisions")}: {t.collisions}\n{Loc.T("ideal path cost")}: {t.oracleCost:0}";
            float eff = sim.tSuccess > 0 ? sim.tEffSum / sim.tSuccess : 0f;
            totalLabel.text = $"{Loc.T("trips")}: {sim.tTrips}   {Loc.T("success")}: {sim.tSuccess}   {Loc.T("failed")}: {sim.tFail}\n{Loc.T("avg time")}: {(sim.tTrips > 0 ? sim.tTime / sim.tTrips : 0):0}{Loc.T("s")}   {Loc.T("efficiency")}: {eff * 100:0}%\n{Loc.T("collisions")}: {sim.tCollisions}   {Loc.T("replans")}: {sim.tReplans}";
            var recent = sim.history.Skip(Math.Max(0, sim.history.Count - 6)).Reverse()
                .Select(h => $"#{h.index} {h.start}→{h.goal} {(h.success ? "✔" : "✖")} {h.time:0}{Loc.T("s")} {h.dist:0}m {(h.success ? (h.efficiency * 100).ToString("0") + "%" : Loc.T(h.reason))}");
            recentLabel.text = string.Join("\n", recent);
            if (camImage != null && app.view.camRT != null && camImage.image != app.view.camRT) camImage.image = app.view.camRT;
            UpdateSeg(sim);
        }

        void UpdateSeg(RescueSim sim)
        {
            var sc = sim.scan; if (sc == null || segTex == null || segTex.width != sc.w) return;
            int w = sc.w, h = sc.h;
            var px = new Color32[w * h];
            var road = new Color32(58, 60, 72, 255); var cross = new Color32(255, 200, 0, 255); var other = new Color32(120, 60, 55, 255);
            for (int v = 0; v < h; v++)
                for (int u = 0; u < w; u++)
                {
                    byte c = sc.seg[v * w + u];
                    px[(h - 1 - v) * w + u] = c == 0 ? road : c == 1 ? cross : other;
                }
            for (int u = 0; u < w; u++)
            {
                int r = sc.row[u];
                if (r < 0) continue;
                var col = sc.cls[u] == 1 ? new Color32(255, 255, 120, 255) : new Color32(255, 255, 255, 255);
                for (int k = 0; k < 2; k++) { int rr = Mathf.Min(h - 1, r + k); px[(h - 1 - rr) * w + u] = col; }
            }
            int hu = Mathf.Clamp(Mathf.RoundToInt(w * 0.5f + sim.cam.F * Mathf.Tan(Mathf.Clamp(sim.decision.heading, -1.5f, 1.5f))), 0, w - 1);
            for (int v = h - 14; v < h; v++) px[(h - 1 - v) * w + hu] = new Color32(60, 255, 90, 255);
            segTex.SetPixels32(px); segTex.Apply(false);
        }
    }
}
