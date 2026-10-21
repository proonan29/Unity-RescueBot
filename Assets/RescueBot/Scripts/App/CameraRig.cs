using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace RescueBot
{
    public enum ViewMode { Overview, Top, Chase, Onboard }

    /// <summary>
    /// Camera with four views: city overview (orbit), top-down follow, chase and the rover's onboard camera.
    /// Left-drag rotates, right-drag pans (overview), wheel zooms. The projection is shifted so the view centre sits between the UI panels.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(Camera))]
    public class CameraRig : MonoBehaviour
    {
        public ViewMode mode = ViewMode.Overview;
        public Vector3 focus = new Vector3(150, 0, 150);
        public float yaw = 20, pitch = 55, distance = 400;
        public float panelLeft, panelRight;
        public Func<Vector2, bool> isOverUI;
        public Func<(Vector3 pos, float yawDeg, bool ok)> rover;
        [NonSerialized] public Vector2 mouse;

        Camera cam;
        Vector2 last;
        bool pressed, dragging, panDrag;
        float chaseYaw, chasePitch = 22, chaseDist = 16, topDist = 90;
        public Camera Cam => cam != null ? cam : cam = GetComponent<Camera>();

        void LateUpdate()
        {
            if (Application.isPlaying) HandleInput();
            Place();
        }

        void HandleInput()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m == null) return;
            Vector2 pos = m.position.ReadValue();
            mouse = pos;
            bool overUI = isOverUI != null && isOverUI(pos);
            bool l = m.leftButton.isPressed, r = m.rightButton.isPressed || m.middleButton.isPressed;
            if ((m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame || m.middleButton.wasPressedThisFrame) && !overUI)
            { pressed = true; dragging = false; last = pos; panDrag = !m.leftButton.wasPressedThisFrame; }
            if (pressed && (l || r))
            {
                var d = pos - last; last = pos; dragging = true;
                if (mode == ViewMode.Overview)
                {
                    if (panDrag) Pan(d);
                    else { yaw += d.x * 0.25f; pitch = Mathf.Clamp(pitch - d.y * 0.2f, 8f, 89f); }
                }
                else if (mode == ViewMode.Chase && !panDrag) { chaseYaw += d.x * 0.3f; chasePitch = Mathf.Clamp(chasePitch - d.y * 0.2f, 5f, 80f); }
            }
            if (pressed && !l && !r) { pressed = false; dragging = false; }
            float wheel = m.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f && !overUI)
            {
                float k = wheel > 0 ? 0.88f : 1.13f;
                if (mode == ViewMode.Overview) distance = Mathf.Clamp(distance * k, 20f, 1600f);
                else if (mode == ViewMode.Top) topDist = Mathf.Clamp(topDist * k, 25f, 600f);
                else if (mode == ViewMode.Chase) chaseDist = Mathf.Clamp(chaseDist * k, 6f, 80f);
            }
#endif
        }

        void Pan(Vector2 d)
        {
            var c = Cam;
            float wpp = distance * Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2 / Mathf.Max(1, c.pixelHeight);
            var right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
            var fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            focus -= (right * d.x + fwd * d.y / Mathf.Max(0.3f, Mathf.Sin(pitch * Mathf.Deg2Rad))) * wpp;
        }

        public void Place()
        {
            var c = Cam;
            c.ResetProjectionMatrix();
            c.orthographic = false;
            var (rp, ry, ok) = rover != null ? rover() : (Vector3.zero, 0f, false);
            var mm = mode;
            if (!ok && mm != ViewMode.Overview) mm = ViewMode.Overview;
            switch (mm)
            {
                case ViewMode.Overview:
                {
                    var rot = Quaternion.Euler(pitch, yaw, 0);
                    transform.position = focus + rot * new Vector3(0, 0, -distance);
                    transform.rotation = rot;
                    c.fieldOfView = 40f; c.nearClipPlane = Mathf.Max(0.5f, distance * 0.004f); c.farClipPlane = 4000;
                    break;
                }
                case ViewMode.Top:
                    transform.position = rp + new Vector3(0, topDist, 0);
                    transform.rotation = Quaternion.Euler(90, ry, 0);
                    c.fieldOfView = 50f; c.nearClipPlane = 1f; c.farClipPlane = 2000;
                    break;
                case ViewMode.Chase:
                {
                    float k = Application.isPlaying ? 1 - Mathf.Exp(-Time.unscaledDeltaTime * 4) : 1;
                    float target = ry + 0;
                    chaseYaw = Mathf.LerpAngle(chaseYaw, target, k);
                    var rot = Quaternion.Euler(chasePitch, chaseYaw, 0);
                    transform.position = rp + Vector3.up * 1.2f + rot * new Vector3(0, 0, -chaseDist);
                    transform.rotation = rot;
                    c.fieldOfView = 60f; c.nearClipPlane = 0.3f; c.farClipPlane = 1500;
                    break;
                }
                case ViewMode.Onboard:
                    transform.position = rp + Quaternion.Euler(0, ry, 0) * new Vector3(0, 1.5f, 0.9f);
                    transform.rotation = Quaternion.Euler(14f, ry, 0);
                    c.fieldOfView = 60f; c.nearClipPlane = 0.3f; c.farClipPlane = 1500;
                    break;
            }
            if (Screen.width > 0 && (panelLeft > 0 || panelRight > 0))
            {
                float f = (panelLeft - panelRight) / Mathf.Max(1, c.pixelWidth);
                var p = c.projectionMatrix;
                p.m02 = -f;
                c.projectionMatrix = p;
            }
        }

        public void SetMode(ViewMode m) { mode = m; }

        public void Frame(float width, float depth)
        {
            focus = new Vector3(width * 0.5f, 0, depth * 0.5f);
            float span = Mathf.Max(width, depth) + 120f;
            yaw = 0; pitch = 62; distance = span * 1.15f;
        }
    }
}
