using System;
using System.Collections.Generic;
using System.IO;
using RescueBot.Core;
using RescueBot.World;
using Unity.Pipeline.Commands;
using UnityEngine;
using Pose = RescueBot.Core.Pose;

namespace RescueBot.EditorTools
{
    /// <summary>Training data for the CNN segmenter: rover camera images + ground-truth class images.</summary>
    public static class DatasetCommands
    {
        [CliCommand("rb_dump_dataset", "Headless: write rover camera images (img/) and ground-truth class images (lbl/, value 0 road 1 crossable 2 other in the R/B channels) for CNN training.")]
        public static string Dump([CliArg("nx", "Blocks in X")] int nx = 5,
                                  [CliArg("ny", "Blocks in Y")] int ny = 5,
                                  [CliArg("severity", "Severity 0..1")] float severity = 0.6f,
                                  [CliArg("seed", "City seed")] int seed = 1,
                                  [CliArg("spacing", "Road spacing")] float spacing = 60f,
                                  [CliArg("drive", "Samples taken while the classical rover drives")] int drive = 120,
                                  [CliArg("rand", "Samples at random poses (near obstacles / anywhere on roads)")] int rand = 120,
                                  [CliArg("out", "Output folder")] string outDir = "Dataset",
                                  [CliArg("raw", "Also save the raw label render (lblraw/) for debugging")] bool raw = false,
                                  [CliArg("light", "0 = standard scene lighting, 1 = lighting randomised per seed")] int light = 1)
        {
            var cfg = new CityConfig { nx = nx, ny = ny, severity = severity, seed = seed, spacing = spacing };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Directory.CreateDirectory(outDir + "/img"); Directory.CreateDirectory(outDir + "/lbl");
            using (var h = new RescueCommands.Harness(cfg, light > 0 ? seed : 0))
            {
                var sim = h.sim; var city = sim.city;
                var poses = new List<Pose>();
                sim.StartTrip();
                while (poses.Count < drive && sw.Elapsed.TotalSeconds < 60) { sim.Advance(0.9f); poses.Add(sim.pose); }
                var rnd = new System.Random(seed * 7919 + 13);
                for (int k = 0; k < rand; k++)
                {
                    Vector2 pos, dir; float yaw;
                    if (rnd.NextDouble() < 0.65 && city.obstacles.Count > 0)
                    {
                        var o = city.obstacles[rnd.Next(city.obstacles.Count)];
                        var e = city.NearestEdge(o.pos, out float t, out float d);
                        dir = e.dir * (rnd.Next(2) == 0 ? 1f : -1f);
                        var perp = new Vector2(dir.y, -dir.x);
                        pos = o.pos - dir * (4f + (float)rnd.NextDouble() * 22f) + perp * (-4.2f + (float)rnd.NextDouble() * 8.4f);
                    }
                    else
                    {
                        var e = city.edges[rnd.Next(city.edges.Count)];
                        dir = e.dir * (rnd.Next(2) == 0 ? 1f : -1f);
                        var perp = new Vector2(dir.y, -dir.x);
                        pos = city.nodes[e.a] + e.dir * ((float)rnd.NextDouble() * e.length) + perp * (-4.5f + (float)rnd.NextDouble() * 9f);
                    }
                    yaw = Mathf.Atan2(dir.x, dir.y) + ((float)rnd.NextDouble() - 0.5f) * 1.2f;
                    poses.Add(new Pose { pos = pos, yaw = yaw });
                }
                int w = sim.cam.w, hh = sim.cam.h;
                var imgs = new Color32[poses.Count][];
                for (int k = 0; k < poses.Count; k++) imgs[k] = h.view.Grab(poses[k]);
                var labs = h.view.GrabLabelsBatch(poses);
                var tex = new Texture2D(w, hh, TextureFormat.RGBA32, false);
                var px = new Color32[w * hh];
                for (int k = 0; k < poses.Count; k++)
                {
                    string name = $"s{seed}_{nx}x{ny}_{k:D3}.png";
                    tex.SetPixels32(imgs[k]); tex.Apply(); File.WriteAllBytes($"{outDir}/img/{name}", tex.EncodeToPNG());
                    for (int i = 0; i < px.Length; i++)
                    {
                        var c = labs[k][i];
                        byte cls = c.r > 127 && c.b < 127 ? (byte)1 : c.b > 127 ? (byte)2 : (byte)0;
                        px[i] = new Color32(cls, cls, cls, 255);
                    }
                    tex.SetPixels32(px); tex.Apply(); File.WriteAllBytes($"{outDir}/lbl/{name}", tex.EncodeToPNG());
                    if (raw) { Directory.CreateDirectory(outDir + "/lblraw"); tex.SetPixels32(labs[k]); tex.Apply(); File.WriteAllBytes($"{outDir}/lblraw/{name}", tex.EncodeToPNG()); }
                }
                UnityEngine.Object.DestroyImmediate(tex);
                string info = "";
                if (raw) foreach (var kv in h.view.labelInfo) info += "\n  " + kv.Key + " x" + kv.Value;
                return $"dumped {poses.Count} samples seed={seed} {nx}x{ny} sev={severity} wall={sw.ElapsedMilliseconds}ms{info}";
            }
        }
    }
}
