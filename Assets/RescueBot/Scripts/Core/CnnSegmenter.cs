using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace RescueBot.Core
{
    /// <summary>
    /// Pixel classifier (road / crossable debris / other) run by a small U-Net through Unity's Inference Engine.
    /// Model: Resources/rescue_seg.onnx, trained by Tools/cnn/train_cnn.py on ground-truth labels rendered from the simulator.
    /// Input: RGB 0..1, 1 x 3 x 96 x 160 (the 90 image rows plus 3 replicated rows top and bottom). Output: logits 1 x 3 x 96 x 160.
    /// </summary>
    public sealed class CnnSegmenter : ISegmenter, IDisposable
    {
        const int Pad = 3;
        public const string ModelName = "rescue_seg";
        readonly Worker worker;
        public ModelAsset Asset { get; }
        Tensor<float> input;
        float[] buf;
        double totalMs; int calls;
        public string Name { get; }
        public double AvgMs => calls > 0 ? totalMs / calls : 0;
        public int Calls => calls;

        public static CnnSegmenter Create()
        {
            var asset = Resources.Load<ModelAsset>(ModelName);
            if (asset == null) { Debug.LogWarning("RescueBot: model Resources/" + ModelName + ".onnx not found"); return null; }
            var model = ModelLoader.Load(asset);
            var backend = SystemInfo.supportsComputeShaders ? BackendType.GPUCompute : BackendType.CPU;
            return new CnnSegmenter(asset, model, backend);
        }

        CnnSegmenter(ModelAsset asset, Model model, BackendType backend)
        {
            Asset = asset;
            worker = new Worker(model, backend);
            Name = "CNN U-Net (" + backend + ")";
        }

        public void Segment(Color32[] img, int w, int h, byte[] seg)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int hp = h + 2 * Pad, plane = hp * w;
            if (buf == null || buf.Length != 3 * plane)
            {
                buf = new float[3 * plane];
                input?.Dispose();
                input = new Tensor<float>(new TensorShape(1, 3, hp, w), buf);
            }
            const float k = 1f / 255f;
            for (int r = 0; r < hp; r++)
            {
                int vt = Mathf.Clamp(r - Pad, 0, h - 1);          // top-down image row (edge rows are replicated)
                int src = (h - 1 - vt) * w, o = r * w;            // img is stored bottom-to-top
                for (int u = 0; u < w; u++)
                {
                    var c = img[src + u];
                    buf[o + u] = c.r * k; buf[plane + o + u] = c.g * k; buf[2 * plane + o + u] = c.b * k;
                }
            }
            input.Upload(buf);
            worker.Schedule(input);
            var outT = worker.PeekOutput() as Tensor<float>;
            using (var cpu = outT.ReadbackAndClone())
            {
                var a = cpu.DownloadToArray();
                for (int v = 0; v < h; v++)
                {
                    int o = (v + Pad) * w;
                    for (int u = 0; u < w; u++)
                    {
                        float l0 = a[o + u], l1 = a[plane + o + u], l2 = a[2 * plane + o + u];
                        seg[v * w + u] = l0 >= l1 && l0 >= l2 ? (byte)0 : l1 >= l2 ? (byte)1 : (byte)2;
                    }
                }
            }
            totalMs += sw.Elapsed.TotalMilliseconds; calls++;
        }

        public void Dispose() { input?.Dispose(); worker?.Dispose(); }
    }

    /// <summary>Selects the pixel classifier the rover's perception uses: classic colour rule or the CNN.</summary>
    public static class PerceptionSwitch
    {
        static CnnSegmenter cnn;
        public static bool UsingCnn => Perception.Segmenter != null;
        public static bool ModelAvailable => cnn != null || Resources.Load<Unity.InferenceEngine.ModelAsset>(CnnSegmenter.ModelName) != null;

        public static bool Set(bool useCnn)
        {
            if (!useCnn) { Perception.Segmenter = null; return true; }
            // a re-imported / retrained model must be picked up without a domain reload
            var asset = Resources.Load<ModelAsset>(CnnSegmenter.ModelName);
            if (cnn != null && cnn.Asset != asset) { cnn.Dispose(); cnn = null; }
            if (cnn == null) cnn = CnnSegmenter.Create();
            if (cnn == null) return false;
            Perception.Segmenter = cnn;
            return true;
        }

        public static string Describe()
        {
            if (Perception.Segmenter is CnnSegmenter c) return c.Name + (c.Calls > 0 ? $" · {c.AvgMs:0.0} ms/frame" : "");
            return "Classic colour rule";
        }

        public static void ResetStats() { }
    }
}
