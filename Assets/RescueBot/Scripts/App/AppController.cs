using System;
using RescueBot.Core;
using RescueBot.World;
using UnityEngine;

namespace RescueBot
{
    /// <summary>
    /// Owns the city + simulation, advances it in play mode, and exposes the actions used by the UI and the CLI:
    /// new scene (city), grid size, start / stop, loop, speed and camera view.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    public class AppController : MonoBehaviour
    {
        public CityView view;
        public CameraRig rig;
        public CityConfig config = new CityConfig();
        public float speed = 1f;
        public bool running = false;
        public bool loop = true;

        [NonSerialized] public RescueSim sim;
        public event Action onChanged;
        double acc;
        public float stepMs;

        public bool Ready => sim != null && view != null && view.IsBuilt;

        void OnEnable() => EnsureBuilt();

        public void EnsureBuilt()
        {
            if (view == null || rig == null) return;
            if (sim == null) sim = new RescueSim(new City(config), config.seed) { loop = loop };
            if (!view.IsBuilt || view.sim != sim) { view.Build(sim); rig.Frame(sim.city.width, sim.city.depth); }
            rig.rover = RoverInfo;
        }

        (Vector3, float, bool) RoverInfo()
        {
            if (sim == null || sim.tripCounter == 0) return (Vector3.zero, 0f, false);
            return (new Vector3(sim.pose.pos.x, 0, sim.pose.pos.y), sim.pose.yaw * Mathf.Rad2Deg, true);
        }

        void Update()
        {
            if (sim == null || view == null || !view.IsBuilt) EnsureBuilt();
            if (sim == null) return;
            sim.loop = loop;
            if (Application.isPlaying && running)
            {
                acc += Time.unscaledDeltaTime * speed;
                int n = (int)(acc / RescueSim.Dt);
                acc -= n * RescueSim.Dt;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int done = 0;
                for (; done < n; done++)
                {
                    sim.Tick();
                    if (sw.Elapsed.TotalMilliseconds > 16) break;
                }
                if (done < n) acc = 0; // cannot keep up: drop the backlog
                stepMs = (float)sw.Elapsed.TotalMilliseconds;
            }
            view.Render(0f);
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Generate a new disaster city (new seed unless one is given) and place the rover on a new trip.</summary>
        public void NewScene(int seed = -1, bool autoStart = true)
        {
            var c = config.Clone();
            c.seed = seed >= 0 ? seed : new System.Random().Next(1, 1000000);
            Rebuild(c, autoStart);
        }

        public void Rebuild(CityConfig c, bool autoStart = true)
        {
            config = c.Clone();
            sim = new RescueSim(new City(config), config.seed) { loop = loop };
            acc = 0;
            view.Build(sim);
            rig.Frame(sim.city.width, sim.city.depth);
            rig.rover = RoverInfo;
            if (autoStart) sim.StartTrip();
            view.Render(0f);
            onChanged?.Invoke();
        }

        public void SetGrid(int nx, int ny) { var c = config.Clone(); c.nx = Mathf.Clamp(nx, 3, 9); c.ny = Mathf.Clamp(ny, 3, 9); Rebuild(c, running || (sim != null && sim.tripCounter > 0)); }
        public void SetSpacing(float s) { var c = config.Clone(); c.spacing = Mathf.Clamp(s, 40f, 120f); Rebuild(c, running || sim.tripCounter > 0); }
        public void SetSeverity(float s) { var c = config.Clone(); c.severity = Mathf.Clamp01(s); Rebuild(c, running || sim.tripCounter > 0); }
        public void SetEnsureReachable(bool b) { var c = config.Clone(); c.ensureReachable = b; Rebuild(c, running || sim.tripCounter > 0); }

        public void StartRun()
        {
            EnsureBuilt();
            if (sim.state == SimState.Idle || sim.state == SimState.Arrived || sim.state == SimState.Failed)
                if (sim.state == SimState.Idle || !loop) sim.StartTrip();
            running = true; onChanged?.Invoke();
        }

        public void StopRun() { running = false; onChanged?.Invoke(); }

        /// <summary>Choose the perception model: false = classic colour rule, true = CNN (U-Net via Inference Engine).</summary>
        public bool SetPerception(bool cnn) { bool ok = PerceptionSwitch.Set(cnn); onChanged?.Invoke(); return ok; }
        public void SetLoop(bool b) { loop = b; if (sim != null) sim.loop = b; onChanged?.Invoke(); }
        public void SetView(ViewMode m) { rig.SetMode(m); view.hideOverlays = m == ViewMode.Onboard; onChanged?.Invoke(); }

        public void Advance(float seconds)
        {
            EnsureBuilt();
            if (sim.state == SimState.Idle) sim.StartTrip();
            sim.Advance(seconds);
            view.Render(0f);
        }
    }
}
