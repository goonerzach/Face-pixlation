// FacePixelator.cs
// BONELAB (Quest / LemonLoader + PC / MelonLoader) - BoneLib 3.x
//
// Attaches a point-filtered "mosaic block" to the head bone of EVERY RigManager in the scene:
// local player, Fusion player proxies, Timeline clones, NPC-style rigs. Because all of those are
// RigManagers, no hard Fusion reference is needed (Fusion stays a soft/no dependency).
//
// References needed: MelonLoader, BoneLib, Il2CppInterop.Runtime, Il2Cppmscorlib,
// Il2CppSLZ.Marrow, Il2CppSLZ.VRMK (Assembly-CSharp), UnityEngine.CoreModule,
// UnityEngine.PhysicsModule (for Collider), UnityEngine.AnimationModule (HumanBodyBones).
//
// NOTE: namespaces below are for BONELAB patch 4+ (Il2CppSLZ.*). On older builds change them to
//       SLZ.Marrow / SLZ.VRMK and drop the "Il2Cpp" prefix.

using System;
using System.Collections.Generic;
using BoneLib;
using BoneLib.BoneMenu;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(FacePixelator.FacePixelatorMod), "Face Pixelator", "1.0.0", "You")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace FacePixelator
{
    public class FacePixelatorMod : MelonMod
    {
        // ---------- Settings (live-updated by BoneMenu) ----------
        private static bool _enabled = true;
        private static bool _showOnLocalPlayer = false; // a block on your own face would block your view
        private static float _size = 0.22f;             // world-space edge length in meters
        private static int _grid = 6;                   // mosaic cells per side

        private const float ScanInterval = 1.0f;        // seconds; cheap enough for Quest
        private const float MinSize = 0.05f, MaxSize = 0.60f;
        private const int MinGrid = 2, MaxGrid = 16;

        // ---------- State ----------
        private sealed class Overlay
        {
            public GameObject Go;
            public Transform Head;
        }

        private static readonly Dictionary<int, Overlay> Overlays = new Dictionary<int, Overlay>();
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly List<int> ToRemove = new List<int>();

        private static Texture2D _tex;
        private static Material _mat;
        private static float _nextScan;
        private static bool _rescanRequested;

        // =====================================================================
        public override void OnInitializeMelon()
        {
            try { SetupBoneMenu(); }
            catch (Exception e) { LoggerInstance.Error($"BoneMenu setup failed: {e}"); }
        }

        public override void OnLateInitializeMelon()
        {
            // Avatar swaps -> rescan next tick so the block re-attaches to the new head bone.
            try { Hooking.OnSwitchAvatarPostfix += _ => _rescanRequested = true; }
            catch (Exception e) { LoggerInstance.Warning($"Avatar hook unavailable: {e.Message}"); }

            try { Hooking.OnLevelInitialized += _ => { ClearAll(); _rescanRequested = true; }; }
            catch (Exception e) { LoggerInstance.Warning($"Level hook unavailable: {e.Message}"); }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName) => ClearAll();

        public override void OnDeinitializeMelon() => ClearAll();

        public override void OnUpdate()
        {
            if (!_rescanRequested && Time.unscaledTime < _nextScan) return;
            _rescanRequested = false;
            _nextScan = Time.unscaledTime + ScanInterval;

            try { Scan(); }
            catch (Exception e) { LoggerInstance.Warning($"Scan failed: {e.Message}"); }
        }

        // =====================================================================
        // BoneMenu
        // =====================================================================
        private void SetupBoneMenu()
        {
            Page page = Page.Root.CreatePage("Face Pixelator", Color.cyan);

            page.CreateBool("Enabled", Color.green, _enabled, v =>
            {
                _enabled = v;
                _rescanRequested = true;
            });

            page.CreateFloat("Pixel Block Size", Color.white, _size, 0.01f, MinSize, MaxSize, v =>
            {
                _size = v;
                _rescanRequested = true; // re-applies scale on next tick
            });

            page.CreateInt("Pixel Grid", Color.yellow, _grid, 1, MinGrid, MaxGrid, v =>
            {
                _grid = v;
                RebuildTexture();
            });

            page.CreateBool("Show On Local Player", Color.gray, _showOnLocalPlayer, v =>
            {
                _showOnLocalPlayer = v;
                _rescanRequested = true;
            });
        }

        // =====================================================================
        // Core logic
        // =====================================================================
        private static void Scan()
        {
            EnsureAssets();
            Seen.Clear();

            var rigs = Object.FindObjectsOfType<RigManager>();
            int localId = 0;
            var local = Player.RigManager;
            if (local != null) localId = local.GetInstanceID();

            foreach (var rig in rigs)
            {
                if (rig == null) continue;

                Transform head = GetHead(rig);
                if (head == null) continue;

                int id = rig.GetInstanceID();
                Seen.Add(id);

                bool isLocal = id == localId;
                bool shouldShow = _enabled && (!isLocal || _showOnLocalPlayer);

                Overlays.TryGetValue(id, out Overlay ov);

                // Avatar changed (new head bone) or overlay got destroyed -> rebuild
                if (ov != null && (ov.Go == null || ov.Head != head))
                {
                    Destroy(ov);
                    Overlays.Remove(id);
                    ov = null;
                }

                if (!shouldShow)
                {
                    if (ov != null) ov.Go.SetActive(false);
                    continue;
                }

                if (ov == null)
                {
                    ov = Create(rig, head);
                    if (ov == null) continue;
                    Overlays[id] = ov;
                }

                Apply(ov, rig);
                ov.Go.SetActive(true);
            }

            // Despawned clones / disconnected Fusion players / unloaded rigs
            ToRemove.Clear();
            foreach (var kv in Overlays)
                if (!Seen.Contains(kv.Key)) ToRemove.Add(kv.Key);

            foreach (int id in ToRemove)
            {
                Destroy(Overlays[id]);
                Overlays.Remove(id);
            }
        }

        private static Transform GetHead(RigManager rig)
        {
            try
            {
                // Avatar head bone is the right anchor (follows the visible head, works for proxies/clones)
                var avatar = rig.avatar;
                if (avatar != null && avatar.animator != null)
                {
                    var t = avatar.animator.GetBoneTransform(HumanBodyBones.Head);
                    if (t != null) return t;
                }
            }
            catch { /* fall through */ }

            try { return rig.physicsRig != null ? rig.physicsRig.m_head : null; }
            catch { return null; }
        }

        private static Overlay Create(RigManager rig, Transform head)
        {
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "FacePixelator_Block";

                // Physics-free: never let the block collide with the rig
                var col = go.GetComponent<Collider>();
                if (col != null) Object.Destroy(col);

                var r = go.GetComponent<Renderer>();
                if (r != null)
                {
                    r.sharedMaterial = _mat;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                }

                go.transform.SetParent(head, false);
                return new Overlay { Go = go, Head = head };
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Face Pixelator] Create failed: {e.Message}");
                return null;
            }
        }

        // Keeps world-space size constant even on scaled avatars; offsets block toward the face.
        private static void Apply(Overlay ov, RigManager rig)
        {
            var t = ov.Go.transform;
            var head = ov.Head;

            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;

            Vector3 ls = head.lossyScale;
            if (ls.x < 1e-4f || ls.y < 1e-4f || ls.z < 1e-4f) return;
            t.localScale = new Vector3(_size / ls.x, _size / ls.y, _size / ls.z);

            // Head-bone axes differ between avatars, so derive the offset from the upright rig axes
            // and convert into head-local space.
            Transform rt = rig.transform;
            Vector3 world = head.position + rt.up * (_size * 0.30f) + rt.forward * (_size * 0.35f);
            t.localPosition = head.InverseTransformPoint(world);
        }

        // =====================================================================
        // Assets
        // =====================================================================
        private static void EnsureAssets()
        {
            if (_tex == null) RebuildTexture();
            if (_mat == null) _mat = BuildMaterial(_tex);
        }

        private static void RebuildTexture()
        {
            int n = Mathf.Clamp(_grid, MinGrid, MaxGrid);
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point; // hard pixel edges = the "pixelated" look
            tex.wrapMode = TextureWrapMode.Clamp;

            var rng = new System.Random(1337);
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                byte v = (byte)rng.Next(90, 235);
                px[i] = new Color32(v, (byte)(v * 0.82f), (byte)(v * 0.70f), 255);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);

            var old = _tex;
            _tex = tex;
            if (_mat != null) _mat.mainTexture = _tex;
            if (old != null) Object.Destroy(old);
        }

        private static Material BuildMaterial(Texture tex)
        {
            // Shader.Find can fail on Quest if a shader was stripped, so try several unlit options.
            string[] candidates =
            {
                "Universal Render Pipeline/Unlit",
                "Unlit/Texture",
                "Sprites/Default",
                "UI/Default",
                "Standard"
            };

            Shader sh = null;
            foreach (var name in candidates)
            {
                sh = Shader.Find(name);
                if (sh != null) break;
            }

            Material m;
            if (sh != null)
            {
                m = new Material(sh);
            }
            else
            {
                // Last resort: clone any material already used by a rig renderer
                m = null;
                var anyRenderer = Object.FindObjectOfType<RigManager>()?.GetComponentInChildren<Renderer>();
                if (anyRenderer != null && anyRenderer.sharedMaterial != null)
                    m = new Material(anyRenderer.sharedMaterial);
                if (m == null)
                    m = new Material(Shader.Find("Hidden/InternalErrorShader"));
            }

            m.mainTexture = tex;
            m.SetTexture("_BaseMap", tex); // URP property name
            return m;
        }

        // =====================================================================
        // Cleanup
        // =====================================================================
        private static void Destroy(Overlay ov)
        {
            if (ov?.Go != null) Object.Destroy(ov.Go);
        }

        private static void ClearAll()
        {
            foreach (var kv in Overlays) Destroy(kv.Value);
            Overlays.Clear();
            Seen.Clear();
            // _tex/_mat are kept so re-attaching after a level load stays cheap
        }
    }
}
