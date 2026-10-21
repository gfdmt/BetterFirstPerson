using System;
using System.Collections.Generic;
using System.Text;
using RoR2;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterFirstPerson
{
    // Keeps the player's own weapon parts, gadgets and item displays from covering the first person view.
    //
    // Fading uses the game's dither fade (_Fade) where the material supports it; once a part is faded out,
    // or right away for materials that can't dither, it is moved to a layer the first person camera doesn't
    // draw. How much of the screen each part covers is measured by drawing it into a tiny render texture
    // that is read back asynchronously: a few draw calls, no GPU stall. Where that is not available or does
    // not work, the coverage is estimated from bounding boxes instead.
    // The body itself is measured too and only gets out of the way when it blocks nearly the whole view.
    internal static class FirstPersonVisuals
    {
        private const int TileWidth = 64;
        private const int MaxTiles = 8;
        private const float ItemThresholdScale = 0.5f;
        private const float BodyFadeStart = 0.8f;
        private const float BodyFadeFull = 0.95f;
        private const float FadeOutTime = 0.1f;
        private const float FadeInTime = 0.25f;

        private sealed class FadeRenderer
        {
            public Renderer renderer;
            public SkinnedMeshRenderer skinned;
            public MeshFilter meshFilter;
            // a mesh: it can be drawn for the coverage and hidden on the layer
            public bool measurable;
            // its materials dither with _Fade
            public bool dither;
            public bool layerSafe;
            public int subMeshCount = 1;
            public bool applied;
            public bool hidden;
            public int originalLayer;
        }

        private sealed class FadeGroup
        {
            public string name;
            public bool isItem;
            // worn on the head (hats, glasses, visors): never wanted in front of the eyes
            public bool headgear;
            public ItemDisplay itemDisplay;
            public readonly List<FadeRenderer> renderers = new List<FadeRenderer>();
            public float coverage;
            public float boxCoverage;
            public bool measured;
            public float alpha = 1f;
            public float time;
            public float coverageSum;
            public float coverageMax;
            public float fadedTime;
            public readonly float[] histogram = new float[10];
        }

        private sealed class PendingReadback
        {
            public readonly FadeGroup[] mapping = new FadeGroup[MaxTiles];
            public int tiles;
            public int height;
            public int generation;
            public int frame;
            public bool busy;
            public Action<AsyncGPUReadbackRequest> callback;
        }

        private enum MeasureMode
        {
            Render,
            Box
        }

        // weapon parts that are meshes, each faded on its own (a gauntlet can cover the screen while the
        // jumpsuit around the waist doesn't)
        private static readonly List<FadeGroup> weaponGroups = new List<FadeGroup>();
        // particles and lines of the weapon: hidden while any weapon part is faded out
        private static readonly List<FadeRenderer> followers = new List<FadeRenderer>();
        // the head's own extra meshes and effects (Captain's cap, Operator's eye glows): always hidden
        private static readonly List<FadeRenderer> headgear = new List<FadeRenderer>();
        private static readonly List<FadeGroup> items = new List<FadeGroup>();
        private static readonly Dictionary<ItemDisplay, FadeGroup> itemGroups = new Dictionary<ItemDisplay, FadeGroup>();
        private static readonly Dictionary<SkinnedMeshRenderer, bool> boundsOverrides = new Dictionary<SkinnedMeshRenderer, bool>();
        private static readonly HashSet<int> describedBodies = new HashSet<int>();
        private static readonly List<ItemDisplay> displayBuffer = new List<ItemDisplay>();
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static readonly int fadeId = Shader.PropertyToID("_Fade");
        private static readonly PendingReadback[] readbacks = CreateReadbacks(6);
        private static readonly Vector3[] corners = new Vector3[8];
        private static readonly Vector2[] points = new Vector2[24];
        private static readonly Vector2[] hull = new Vector2[48];
        private static readonly Vector2[] clipA = new Vector2[64];
        private static readonly Vector2[] clipB = new Vector2[64];

        private static FirstPersonSession session;
        private static CharacterBody body;
        private static CharacterModel model;
        private static Transform modelTransform;
        private static Animator animator;
        private static AnimatorCullingMode animatorCulling;
        private static Camera camera;
        private static string bodyName = "";
        private static FadeGroup bodyGroup;
        private static int classifyFrame = -1;
        private static int lastFrame = -1;
        private static float nextItemRefresh;
        private static float nextStatsLog;
        private static int generation;
        private static bool failed;
        private static int hideLayer = -1;

        private static MeasureMode mode = MeasureMode.Render;
        private static bool renderResourcesTried;
        private static bool renderVerified;
        private static int checkVisibleFrames;
        private static int checkRenderedFrames;
        private static Material coverageMaterial;
        private static RenderTexture coverageTexture;
        private static CommandBuffer commandBuffer;
        private static int tileHeight;
        private static int readbackCursor;
        private static int weaponCursor;
        private static int itemCursor;

        private static Vector3 boxCameraPosition;
        private static Quaternion boxToCamera;
        private static float boxTanH;
        private static float boxTanV;
        private static float boxNear;

        private static bool swayValid;
        private static float swayTime;
        private static float swayHead;
        private static float swayCamera;
        private static Vector3 lastHeadOffset;
        private static Vector3 lastCameraOffset;

        // ---------------------------------------------------------------- lifecycle

        public static void Begin(FirstPersonSession newSession)
        {
            End();
            failed = false;
            session = newSession;
            body = session.Body;
            camera = session.Camera;
            bodyName = session.BodyName;
            hideLayer = session.HideLayer;
            modelTransform = session.Model;
            model = session.CharacterModel;
            if (!model || model.baseRendererInfos == null)
            {
                model = null;
                return;
            }
            // bounds of the current pose instead of the bind pose, for sorting out the body and for box estimates
            foreach (CharacterModel.RendererInfo info in model.baseRendererInfos)
                if (info.renderer is SkinnedMeshRenderer skinned) OverrideBoundsUpdate(skinned);
            // hidden parts must keep animating and skinning, they are still measured
            animator = modelTransform.GetComponent<Animator>();
            if (animator)
            {
                animatorCulling = animator.cullingMode;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            classifyFrame = Time.frameCount + 1;
            checkVisibleFrames = 0;
            checkRenderedFrames = 0;
        }

        public static void End()
        {
            LogStats(true);
            generation++;
            try
            {
                RestoreAll();
                foreach (KeyValuePair<SkinnedMeshRenderer, bool> pair in boundsOverrides)
                    if (pair.Key) pair.Key.updateWhenOffscreen = pair.Value;
                if (animator) animator.cullingMode = animatorCulling;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning("Restoring first person visuals failed: " + exception.Message);
            }
            boundsOverrides.Clear();
            weaponGroups.Clear();
            followers.Clear();
            headgear.Clear();
            items.Clear();
            itemGroups.Clear();
            bodyGroup = null;
            session = null;
            body = null;
            model = null;
            modelTransform = null;
            animator = null;
            camera = null;
            classifyFrame = -1;
            lastFrame = -1;
            swayValid = false;
            swayTime = 0f;
            swayHead = 0f;
            swayCamera = 0f;
        }

        // the game can quit while first person is on, End is not called then
        public static void OnQuit()
        {
            LogStats(true);
        }

        private static void RestoreAll()
        {
            foreach (FadeGroup group in weaponGroups) RestoreGroup(group);
            foreach (FadeRenderer fr in followers)
                if (fr.applied && fr.renderer) Restore(fr);
            foreach (FadeRenderer fr in headgear)
                if (fr.applied && fr.renderer) Restore(fr);
            if (bodyGroup != null) RestoreGroup(bodyGroup);
            foreach (FadeGroup group in items) RestoreGroup(group);
        }

        private static void Fail(Exception exception)
        {
            Plugin.Log.LogError("First person visuals stopped until first person is entered again: " + exception);
            failed = true;
            try
            {
                RestoreAll();
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- per frame

        // Camera.onPreCull: the camera, animation and skinning of this frame are final here
        public static void OnPreCull(Camera cam)
        {
            if (!ReferenceEquals(cam, camera) || failed || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            try
            {
                if (!model) return;
                if (classifyFrame >= 0)
                {
                    if (Time.frameCount < classifyFrame) return;
                    classifyFrame = -1;
                    Classify();
                }
                UpdateFade(cam);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        // CharacterModel.UpdateMaterials writes _Fade to every renderer before each render; put ours back
        public static void AfterModelMaterialsUpdated(CharacterModel updated)
        {
            if (!ReferenceEquals(updated, model) || failed) return;
            foreach (FadeGroup group in weaponGroups) ReapplyFade(group);
            if (bodyGroup != null) ReapplyFade(bodyGroup);
            foreach (FadeGroup group in items) ReapplyFade(group);
            foreach (FadeRenderer fr in headgear)
                if (fr.applied && fr.dither && fr.renderer) SetFade(fr, 0f);
        }

        // how much the head moves relative to the body and how much of that reaches the camera (cm/s in the log)
        public static void RecordSway(Vector3 headOffset, Vector3 cameraOffset, float deltaTime)
        {
            if (swayValid && deltaTime > 0f && deltaTime < 0.25f)
            {
                swayHead += (headOffset - lastHeadOffset).magnitude;
                swayCamera += (cameraOffset - lastCameraOffset).magnitude;
                swayTime += deltaTime;
            }
            lastHeadOffset = headOffset;
            lastCameraOffset = cameraOffset;
            swayValid = true;
        }

        private static void UpdateFade(Camera cam)
        {
            float deltaTime = Time.deltaTime;
            if (Time.time >= nextItemRefresh)
            {
                nextItemRefresh = Time.time + 0.5f;
                RefreshItemDisplays();
            }
            bool enabled = Settings.FadeWeapon.Value;
            if (enabled && (mode == MeasureMode.Box || !renderVerified))
            {
                PrepareBox(cam);
                foreach (FadeGroup group in weaponGroups) group.boxCoverage = BoxCoverage(group);
                if (mode == MeasureMode.Box)
                {
                    if (bodyGroup != null) bodyGroup.boxCoverage = 0f;
                    foreach (FadeGroup group in items) group.boxCoverage = BoxCoverage(group);
                }
                else
                {
                    SelfCheck();
                }
            }
            float start = Settings.FadeStart.Value;
            float full = Mathf.Max(start + 0.01f, Settings.FadeFull.Value);
            float followerAlpha = 1f;
            foreach (FadeGroup group in weaponGroups)
            {
                UpdateGroup(group, enabled, start, full, deltaTime);
                followerAlpha = Mathf.Min(followerAlpha, group.alpha);
            }
            foreach (FadeRenderer fr in followers)
                if (fr.renderer) Apply(fr, followerAlpha);
            foreach (FadeRenderer fr in headgear)
                if (fr.renderer) Apply(fr, 0f);
            // a box can't tell the body from the air around it, only rendering can
            if (bodyGroup != null) UpdateGroup(bodyGroup, enabled && mode == MeasureMode.Render, BodyFadeStart, BodyFadeFull, deltaTime);
            foreach (FadeGroup group in items) UpdateGroup(group, enabled, start * ItemThresholdScale, full * ItemThresholdScale, deltaTime);
            if (enabled && mode == MeasureMode.Render) RequestCoverage(cam);
            if (Time.time >= nextStatsLog)
            {
                nextStatsLog = Time.time + 60f;
                LogStats(false);
            }
        }

        private static void UpdateGroup(FadeGroup group, bool enabled, float start, float full, float deltaTime)
        {
            if (group.headgear)
            {
                group.alpha = 0f;
                foreach (FadeRenderer fr in group.renderers)
                    if (fr.renderer) Apply(fr, 0f);
                return;
            }
            float coverage = !enabled ? 0f : mode == MeasureMode.Box ? group.boxCoverage : group.measured ? group.coverage : 0f;
            float target = coverage <= start ? 1f : coverage >= full ? 0f : 1f - (coverage - start) / (full - start);
            float duration = target < group.alpha ? FadeOutTime : FadeInTime;
            group.alpha = Mathf.MoveTowards(group.alpha, target, deltaTime / duration);
            if (enabled)
            {
                group.time += deltaTime;
                group.coverageSum += coverage * deltaTime;
                if (coverage > group.coverageMax) group.coverageMax = coverage;
                group.histogram[Mathf.Clamp((int)(coverage * 10f), 0, 9)] += deltaTime;
                if (group.alpha < 0.99f) group.fadedTime += deltaTime;
            }
            foreach (FadeRenderer fr in group.renderers)
                if (fr.renderer) Apply(fr, group.alpha);
        }

        private static float ModelFade()
        {
            return model ? model.fade : 1f;
        }

        private static void Apply(FadeRenderer fr, float alpha)
        {
            if (alpha >= 0.999f)
            {
                if (fr.applied) Restore(fr);
                return;
            }
            if (fr.dither) SetFade(fr, ModelFade() * alpha);
            // dithered parts disappear for good when fully faded; parts without dither at half way
            SetHidden(fr, alpha <= (fr.dither ? 0.02f : 0.5f));
            fr.applied = true;
        }

        private static void Restore(FadeRenderer fr)
        {
            if (fr.dither) SetFade(fr, ModelFade());
            SetHidden(fr, false);
            fr.applied = false;
        }

        private static void SetFade(FadeRenderer fr, float fade)
        {
            fr.renderer.GetPropertyBlock(block);
            block.SetFloat(fadeId, fade);
            fr.renderer.SetPropertyBlock(block);
        }

        // Meshes go to a layer the first person camera doesn't draw: they stay rendered for the coverage
        // measurement (which ignores layers) and come back by themselves. Particles, lines and objects with
        // colliders are switched off instead.
        private static void SetHidden(FadeRenderer fr, bool hide)
        {
            if (hide == fr.hidden) return;
            fr.hidden = hide;
            if (fr.measurable && fr.layerSafe && hideLayer >= 0)
            {
                GameObject gameObject = fr.renderer.gameObject;
                if (hide)
                {
                    fr.originalLayer = gameObject.layer;
                    gameObject.layer = hideLayer;
                }
                else if (gameObject.layer == hideLayer)
                {
                    gameObject.layer = fr.originalLayer;
                }
            }
            else
            {
                fr.renderer.forceRenderingOff = hide;
            }
        }

        private static void RestoreGroup(FadeGroup group)
        {
            foreach (FadeRenderer fr in group.renderers)
                if (fr.applied && fr.renderer) Restore(fr);
            group.alpha = 1f;
        }

        private static void ReapplyFade(FadeGroup group)
        {
            foreach (FadeRenderer fr in group.renderers)
                if (fr.applied && fr.dither && fr.renderer) SetFade(fr, ModelFade() * group.alpha);
        }

        // ---------------------------------------------------------------- sorting out the model

        private static void Classify()
        {
            Transform headTransform = session.Head.Transform;
            Vector3 headCenter = headTransform ? session.Head.Center : modelTransform.position;
            Vector3 core = body ? body.corePosition : headCenter;
            List<Renderer> renderers = new List<Renderer>();
            foreach (CharacterModel.RendererInfo info in model.baseRendererInfos)
                if (info.renderer && !renderers.Contains(info.renderer)) renderers.Add(info.renderer);

            // the body mesh wraps the head, the chest and reaches the feet; weapons, packs and gadgets don't.
            // Bone lists can't tell: some survivors skin every mesh to the whole skeleton.
            HashSet<Renderer> bodyRenderers = new HashSet<Renderer>();
            Vector3 feet = body ? body.footPosition + Vector3.up * 0.1f : core;
            string rule = "pose bounds";
            foreach (Renderer renderer in renderers)
            {
                if (!(renderer is SkinnedMeshRenderer)) continue;
                Bounds bounds = renderer.bounds;
                bounds.Expand(0.1f);
                if (bounds.Contains(headCenter) && bounds.Contains(core) && bounds.Contains(feet)) bodyRenderers.Add(renderer);
            }
            if (bodyRenderers.Count == 0)
            {
                // hovering or legless survivors
                rule = "pose bounds without feet";
                foreach (Renderer renderer in renderers)
                {
                    if (!(renderer is SkinnedMeshRenderer)) continue;
                    Bounds bounds = renderer.bounds;
                    bounds.Expand(0.1f);
                    if (bounds.Contains(headCenter) && bounds.Contains(core)) bodyRenderers.Add(renderer);
                }
            }
            if (bodyRenderers.Count == 0 && headTransform)
            {
                rule = "head bone";
                foreach (Renderer renderer in renderers)
                    if (renderer is SkinnedMeshRenderer skinned && UsesBone(skinned, headTransform)) bodyRenderers.Add(renderer);
            }
            if (bodyRenderers.Count == 0)
            {
                rule = "largest mesh";
                Renderer largest = null;
                float largestVolume = 0f;
                foreach (Renderer renderer in renderers)
                {
                    if (!(renderer is SkinnedMeshRenderer)) continue;
                    Vector3 size = renderer.bounds.size;
                    float volume = size.x * size.y * size.z;
                    if (!largest || volume > largestVolume)
                    {
                        largest = renderer;
                        largestVolume = volume;
                    }
                }
                if (largest) bodyRenderers.Add(largest);
            }

            StringBuilder sb = new StringBuilder("First person visuals for " + bodyName + ", body by " + rule + ":");
            bodyGroup = new FadeGroup { name = "body" };
            foreach (Renderer renderer in renderers)
            {
                if (!bodyRenderers.Contains(renderer)) continue;
                FadeRenderer fr = MakeFadeRenderer(renderer);
                bodyGroup.renderers.Add(fr);
                sb.Append(" '").Append(renderer.name).Append('\'').Append(Describe(fr));
            }
            if (bodyGroup.renderers.Count == 0) bodyGroup = null;
            sb.Append("; weapon:");
            int parts = 0;
            foreach (Renderer renderer in renderers)
            {
                if (bodyRenderers.Contains(renderer)) continue;
                FadeRenderer fr = MakeFadeRenderer(renderer);
                if (IsHeadgear(renderer))
                {
                    headgear.Add(fr);
                    sb.Append(" '").Append(renderer.name).Append("' (headgear, hidden)");
                    parts++;
                    continue;
                }
                if (fr.measurable)
                {
                    FadeGroup group = new FadeGroup { name = renderer.name };
                    group.renderers.Add(fr);
                    weaponGroups.Add(group);
                }
                else
                {
                    followers.Add(fr);
                }
                parts++;
                sb.Append(" '").Append(renderer.name).Append('\'').Append(Describe(fr));
            }
            if (parts == 0) sb.Append(" none");
            RefreshItemDisplays();
            nextItemRefresh = Time.time + 0.5f;
            nextStatsLog = Time.time + 60f;
            sb.Append("; coverage by ").Append(mode == MeasureMode.Render ? "rendering" : "bounding boxes");
            if (hideLayer >= 0) sb.Append(", hidden on layer ").Append(hideLayer);
            if (body && describedBodies.Add(body.GetInstanceID())) Plugin.Log.LogInfo(sb.ToString());
        }

        // hangs off the head bone, or is a small mesh sitting right on the head
        private static bool IsHeadgear(Renderer renderer)
        {
            HeadAnchor head = session != null ? session.Head : null;
            if (head == null || !head.Transform || !renderer) return false;
            if (head.Bone && renderer.transform.IsChildOf(head.Bone)) return true;
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) return false;
            Bounds bounds = renderer.bounds;
            float radius = head.Radius;
            return bounds.extents.magnitude < 2.5f * radius && (bounds.center - head.Center).magnitude < 1.5f * radius;
        }

        private static string Describe(FadeRenderer fr)
        {
            if (!fr.measurable) return " (follows)";
            string how = fr.dither ? " (dither" : " (no dither";
            return fr.layerSafe ? how + ")" : how + ", collider)";
        }

        private static bool UsesBone(SkinnedMeshRenderer skinned, Transform head)
        {
            Transform headParent = head.parent;
            foreach (Transform bone in skinned.bones)
                if (bone && (bone == head || bone == headParent)) return true;
            return false;
        }

        private static FadeRenderer MakeFadeRenderer(Renderer renderer)
        {
            FadeRenderer fr = new FadeRenderer { renderer = renderer, skinned = renderer as SkinnedMeshRenderer };
            Mesh mesh = null;
            if (fr.skinned)
            {
                // always skinned and with current bounds, also while faded out, hidden or off screen
                OverrideBoundsUpdate(fr.skinned);
                mesh = fr.skinned.sharedMesh;
            }
            else if (renderer is MeshRenderer)
            {
                fr.meshFilter = renderer.GetComponent<MeshFilter>();
                mesh = fr.meshFilter ? fr.meshFilter.sharedMesh : null;
            }
            if (mesh)
            {
                fr.measurable = true;
                fr.subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                fr.dither = MaterialsDither(renderer);
                fr.layerSafe = !renderer.GetComponent<Collider>();
            }
            return fr;
        }

        // the hopoo shaders only dither with the DITHER keyword on (and _FadeOn where it exists)
        private static bool MaterialsDither(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length == 0) return false;
            foreach (Material material in materials)
            {
                if (!material || !material.HasProperty(fadeId) || !material.IsKeywordEnabled("DITHER")) return false;
                if (material.HasProperty("_FadeOn") && material.GetInt("_FadeOn") == 0) return false;
            }
            return true;
        }

        private static void OverrideBoundsUpdate(SkinnedMeshRenderer skinned)
        {
            if (!skinned || boundsOverrides.ContainsKey(skinned)) return;
            boundsOverrides[skinned] = skinned.updateWhenOffscreen;
            skinned.updateWhenOffscreen = true;
        }

        private static void RefreshItemDisplays()
        {
            if (!model) return;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                FadeGroup group = items[i];
                if (group.itemDisplay) continue;
                foreach (FadeRenderer fr in group.renderers)
                    if (!ReferenceEquals(fr.skinned, null)) boundsOverrides.Remove(fr.skinned);
                itemGroups.Remove(group.itemDisplay);
                items.RemoveAt(i);
            }
            displayBuffer.Clear();
            model.GetComponentsInChildren(true, displayBuffer);
            foreach (ItemDisplay display in displayBuffer)
            {
                if (!display || display.rendererInfos == null || itemGroups.ContainsKey(display)) continue;
                FadeGroup group = new FadeGroup { name = display.name.Replace("(Clone)", ""), isItem = true, itemDisplay = display };
                bool onHead = true;
                foreach (CharacterModel.RendererInfo info in display.rendererInfos)
                {
                    if (!info.renderer) continue;
                    group.renderers.Add(MakeFadeRenderer(info.renderer));
                    onHead &= IsHeadgear(info.renderer);
                }
                group.headgear = onHead && group.renderers.Count > 0;
                itemGroups[display] = group;
                items.Add(group);
            }
            displayBuffer.Clear();
        }

        // ---------------------------------------------------------------- rendered coverage

        private static PendingReadback[] CreateReadbacks(int count)
        {
            PendingReadback[] result = new PendingReadback[count];
            for (int i = 0; i < count; i++)
            {
                PendingReadback pending = new PendingReadback();
                pending.callback = request => OnReadback(request, pending);
                result[i] = pending;
            }
            return result;
        }

        private static bool IsDrawn(Renderer renderer)
        {
            return renderer && renderer.enabled && renderer.gameObject.activeInHierarchy;
        }

        private static bool HasDrawable(FadeGroup group)
        {
            foreach (FadeRenderer fr in group.renderers)
                if (fr.measurable && IsDrawn(fr.renderer)) return true;
            return false;
        }

        // Shader.Find is redirected to the game's addressable shaders by RoR2BepInExPack and throws for
        // Unity's built-in ones, so look through the shaders that are already loaded instead
        private static Shader FindLoadedShader()
        {
            string[] names = { "Hidden/InternalErrorShader", "Hidden/Internal-Colored", "Unlit/Color" };
            Shader[] loaded = Resources.FindObjectsOfTypeAll<Shader>();
            foreach (string name in names)
                foreach (Shader shader in loaded)
                    if (shader && shader.name == name && shader.isSupported) return shader;
            return null;
        }

        private static bool EnsureRenderResources(Camera cam)
        {
            if (!coverageMaterial)
            {
                if (renderResourcesTried)
                {
                    mode = MeasureMode.Box;
                    return false;
                }
                renderResourcesTried = true;
                Shader shader = null;
                try
                {
                    if (SystemInfo.supportsAsyncGPUReadback) shader = FindLoadedShader();
                }
                catch (Exception exception)
                {
                    Plugin.Log.LogWarning("Looking up a shader for screen coverage failed: " + exception.Message);
                }
                if (!shader)
                {
                    mode = MeasureMode.Box;
                    Plugin.Log.LogWarning("Rendered screen coverage is not available (async readback " + SystemInfo.supportsAsyncGPUReadback + "), using bounding boxes");
                    return false;
                }
                coverageMaterial = new Material(shader) { name = "BetterFirstPerson coverage", hideFlags = HideFlags.HideAndDontSave };
                SetIfPresent(coverageMaterial, "_Color", 1f);
                SetIfPresent(coverageMaterial, "_SrcBlend", (float)BlendMode.One);
                SetIfPresent(coverageMaterial, "_DstBlend", (float)BlendMode.Zero);
                SetIfPresent(coverageMaterial, "_ZWrite", 1f);
                SetIfPresent(coverageMaterial, "_ZTest", (float)CompareFunction.LessEqual);
                SetIfPresent(coverageMaterial, "_Cull", (float)CullMode.Back);
                commandBuffer = new CommandBuffer { name = "BetterFirstPerson coverage" };
                Plugin.Log.LogInfo("Screen coverage is drawn with " + shader.name);
            }
            int height = Mathf.Clamp(Mathf.RoundToInt(TileWidth / Mathf.Max(0.2f, cam.aspect)), 8, 160);
            if (!coverageTexture || !coverageTexture.IsCreated() || height != tileHeight)
            {
                if (coverageTexture)
                {
                    coverageTexture.Release();
                    UnityEngine.Object.Destroy(coverageTexture);
                }
                tileHeight = height;
                coverageTexture = new RenderTexture(TileWidth * MaxTiles, tileHeight, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "BetterFirstPerson coverage",
                    hideFlags = HideFlags.HideAndDontSave
                };
                coverageTexture.Create();
            }
            return true;
        }

        private static void SetIfPresent(Material material, string property, float value)
        {
            if (!material.HasProperty(property)) return;
            if (property == "_Color") material.SetColor(property, Color.white);
            else material.SetFloat(property, value);
        }

        private static void AddTile(PendingReadback slot, ref int tiles, FadeGroup group)
        {
            if (tiles >= MaxTiles || group.headgear) return;
            if (HasDrawable(group)) slot.mapping[tiles++] = group;
            else group.coverage = 0f;
        }

        // every weapon part, the body, and the remaining tiles for item displays (taking turns), each drawn
        // into its own 64 px wide tile
        private static void RequestCoverage(Camera cam)
        {
            if (!EnsureRenderResources(cam)) return;
            PendingReadback slot = readbacks[readbackCursor];
            if (slot.busy)
            {
                // a lost callback must not stop the measuring for good
                if (Time.frameCount - slot.frame < 120) return;
                slot.busy = false;
            }
            int tiles = 0;
            int weaponCount = weaponGroups.Count;
            int scanned = 0;
            while (scanned < weaponCount && tiles < MaxTiles - 1)
            {
                AddTile(slot, ref tiles, weaponGroups[(weaponCursor + scanned) % weaponCount]);
                scanned++;
            }
            if (weaponCount > 0) weaponCursor = (weaponCursor + scanned) % weaponCount;
            if (bodyGroup != null) AddTile(slot, ref tiles, bodyGroup);
            int itemCount = items.Count;
            scanned = 0;
            while (scanned < itemCount && tiles < MaxTiles)
            {
                AddTile(slot, ref tiles, items[(itemCursor + scanned) % itemCount]);
                scanned++;
            }
            if (itemCount > 0) itemCursor = (itemCursor + scanned) % itemCount;
            if (tiles == 0) return;

            commandBuffer.Clear();
            commandBuffer.SetRenderTarget(coverageTexture);
            commandBuffer.ClearRenderTarget(true, true, Color.clear);
            commandBuffer.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
            for (int t = 0; t < tiles; t++)
            {
                commandBuffer.SetViewport(new Rect(t * TileWidth, 0f, TileWidth, tileHeight));
                foreach (FadeRenderer fr in slot.mapping[t].renderers)
                {
                    if (!fr.measurable || !IsDrawn(fr.renderer)) continue;
                    // front and back faces: from inside a mesh (a hood, a rifle stock) it covers everything
                    for (int s = 0; s < fr.subMeshCount; s++)
                        commandBuffer.DrawRenderer(fr.renderer, coverageMaterial, s, 0);
                    commandBuffer.SetInvertCulling(true);
                    for (int s = 0; s < fr.subMeshCount; s++)
                        commandBuffer.DrawRenderer(fr.renderer, coverageMaterial, s, 0);
                    commandBuffer.SetInvertCulling(false);
                }
            }
            RenderTexture previous = RenderTexture.active;
            Graphics.ExecuteCommandBuffer(commandBuffer);
            RenderTexture.active = previous;

            slot.tiles = tiles;
            slot.height = tileHeight;
            slot.generation = generation;
            slot.frame = Time.frameCount;
            slot.busy = true;
            readbackCursor = (readbackCursor + 1) % readbacks.Length;
            AsyncGPUReadback.Request(coverageTexture, 0, slot.callback);
        }

        private static void OnReadback(AsyncGPUReadbackRequest request, PendingReadback pending)
        {
            pending.busy = false;
            try
            {
                if (request.hasError || pending.generation != generation) return;
                NativeArray<uint> data = request.GetData<uint>();
                int width = TileWidth * MaxTiles;
                int height = pending.height;
                if (data.Length < width * height) return;
                float pixels = TileWidth * height;
                for (int t = 0; t < pending.tiles; t++)
                {
                    int covered = 0;
                    for (int y = 0; y < height; y++)
                    {
                        int index = y * width + t * TileWidth;
                        int end = index + TileWidth;
                        for (; index < end; index++)
                            if (data[index] != 0u) covered++;
                    }
                    FadeGroup group = pending.mapping[t];
                    group.coverage = covered / pixels;
                    group.measured = true;
                }
            }
            catch (Exception exception)
            {
                if (mode == MeasureMode.Render)
                {
                    mode = MeasureMode.Box;
                    Plugin.Log.LogWarning("Reading rendered screen coverage failed, using bounding boxes: " + exception.Message);
                }
            }
            finally
            {
                Array.Clear(pending.mapping, 0, MaxTiles);
            }
        }

        // until it is proven, check rendering against the box estimate: if a weapon part's box is clearly on
        // screen for a while but nothing ever gets drawn, rendering doesn't work here
        private static void SelfCheck()
        {
            if (renderVerified) return;
            float box = 0f;
            bool rendered = false;
            foreach (FadeGroup group in weaponGroups)
            {
                box = Mathf.Max(box, group.boxCoverage);
                if (group.measured && group.coverage > 0f) rendered = true;
            }
            if (box < 0.05f) return;
            checkVisibleFrames++;
            if (rendered) checkRenderedFrames++;
            if (checkVisibleFrames < 90) return;
            if (checkRenderedFrames > 0)
            {
                renderVerified = true;
                Plugin.Log.LogInfo("Rendered screen coverage works (weapon drawn in " + checkRenderedFrames + " of " + checkVisibleFrames + " checked frames)");
            }
            else
            {
                mode = MeasureMode.Box;
                Plugin.Log.LogWarning("Rendered screen coverage stayed empty while the weapon was on screen, using bounding boxes instead");
            }
        }

        // ---------------------------------------------------------------- box estimate

        private static void PrepareBox(Camera cam)
        {
            Transform transform = cam.transform;
            boxCameraPosition = transform.position;
            boxToCamera = Quaternion.Inverse(transform.rotation);
            boxTanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            boxTanH = boxTanV * cam.aspect;
            boxNear = cam.nearClipPlane;
        }

        private static float BoxCoverage(FadeGroup group)
        {
            float coverage = 0f;
            foreach (FadeRenderer fr in group.renderers)
                if (fr.measurable && IsDrawn(fr.renderer)) coverage = Mathf.Max(coverage, Coverage(fr));
            return coverage;
        }

        // Fraction of the screen covered by the renderer's box: corners in front of the near plane plus
        // the box edges cut at the near plane, projected, convex hull, clipped to the viewport.
        private static float Coverage(FadeRenderer fr)
        {
            if (!FillBox(fr)) return 0f;
            for (int i = 0; i < 8; i++) corners[i] = boxToCamera * (corners[i] - boxCameraPosition);
            int count = 0;
            for (int i = 0; i < 8; i++)
                if (corners[i].z >= boxNear) points[count++] = Project(corners[i]);
            for (int i = 0; i < 8; i++)
            {
                for (int bit = 1; bit < 8; bit <<= 1)
                {
                    if ((i & bit) != 0) continue;
                    int j = i | bit;
                    float za = corners[i].z;
                    float zb = corners[j].z;
                    if ((za >= boxNear) == (zb >= boxNear)) continue;
                    Vector3 cut = Vector3.LerpUnclamped(corners[i], corners[j], (boxNear - za) / (zb - za));
                    cut.z = boxNear;
                    points[count++] = Project(cut);
                }
            }
            if (count < 3) return 0f;
            int hullCount = ConvexHull(count);
            return hullCount < 3 ? 0f : ClippedArea(hullCount);
        }

        private static bool FillBox(FadeRenderer fr)
        {
            Renderer renderer = fr.renderer;
            if (fr.meshFilter && fr.meshFilter.sharedMesh)
            {
                FillCorners(fr.meshFilter.sharedMesh.bounds, renderer.transform);
                return true;
            }
            Bounds world = renderer.bounds;
            if (world.size.sqrMagnitude < 1e-6f) return false;
            if (fr.skinned)
            {
                // the box in root bone space is tighter for long weapons; use it when it matches this pose
                FillCorners(fr.skinned.localBounds, fr.skinned.rootBone ? fr.skinned.rootBone : renderer.transform);
                Vector3 min = corners[0];
                Vector3 max = corners[0];
                for (int i = 1; i < 8; i++)
                {
                    min = Vector3.Min(min, corners[i]);
                    max = Vector3.Max(max, corners[i]);
                }
                Vector3 tolerance = new Vector3(0.05f, 0.05f, 0.05f);
                if (Vector3.Max(world.min, min - tolerance) == world.min && Vector3.Min(world.max, max + tolerance) == world.max) return true;
            }
            FillCorners(world, null);
            return true;
        }

        private static void FillCorners(Bounds bounds, Transform transform)
        {
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 local = new Vector3(
                    center.x + ((i & 1) != 0 ? extents.x : -extents.x),
                    center.y + ((i & 2) != 0 ? extents.y : -extents.y),
                    center.z + ((i & 4) != 0 ? extents.z : -extents.z));
                corners[i] = transform ? transform.TransformPoint(local) : local;
            }
        }

        private static Vector2 Project(Vector3 point)
        {
            return new Vector2(0.5f + 0.5f * point.x / (point.z * boxTanH), 0.5f + 0.5f * point.y / (point.z * boxTanV));
        }

        private static float Cross(Vector2 origin, Vector2 a, Vector2 b)
        {
            return (a.x - origin.x) * (b.y - origin.y) - (a.y - origin.y) * (b.x - origin.x);
        }

        // Andrew's monotone chain over points[0..count), result in hull, returns the vertex count
        private static int ConvexHull(int count)
        {
            for (int i = 1; i < count; i++)
            {
                Vector2 key = points[i];
                int j = i - 1;
                while (j >= 0 && (points[j].x > key.x || (points[j].x == key.x && points[j].y > key.y)))
                {
                    points[j + 1] = points[j];
                    j--;
                }
                points[j + 1] = key;
            }
            int k = 0;
            for (int i = 0; i < count; i++)
            {
                while (k >= 2 && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0f) k--;
                hull[k++] = points[i];
            }
            for (int i = count - 2, lower = k + 1; i >= 0; i--)
            {
                while (k >= lower && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0f) k--;
                hull[k++] = points[i];
            }
            return k - 1;
        }

        private static float ClippedArea(int count)
        {
            Array.Copy(hull, clipA, count);
            count = ClipEdge(clipA, count, clipB, true, 0f, true);
            count = ClipEdge(clipB, count, clipA, true, 1f, false);
            count = ClipEdge(clipA, count, clipB, false, 0f, true);
            count = ClipEdge(clipB, count, clipA, false, 1f, false);
            if (count < 3) return 0f;
            float area = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = clipA[i];
                Vector2 b = clipA[(i + 1) % count];
                area += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(area) * 0.5f;
        }

        private static int ClipEdge(Vector2[] input, int count, Vector2[] output, bool alongX, float bound, bool keepGreater)
        {
            if (count == 0) return 0;
            int written = 0;
            Vector2 previous = input[count - 1];
            bool previousInside = Inside(previous, alongX, bound, keepGreater);
            for (int i = 0; i < count; i++)
            {
                Vector2 current = input[i];
                bool currentInside = Inside(current, alongX, bound, keepGreater);
                if (currentInside != previousInside)
                {
                    float a = alongX ? previous.x : previous.y;
                    float b = alongX ? current.x : current.y;
                    output[written++] = Vector2.LerpUnclamped(previous, current, (bound - a) / (b - a));
                }
                if (currentInside) output[written++] = current;
                previous = current;
                previousInside = currentInside;
            }
            return written;
        }

        private static bool Inside(Vector2 point, bool alongX, float bound, bool keepGreater)
        {
            float value = alongX ? point.x : point.y;
            return keepGreater ? value >= bound : value <= bound;
        }

        // ---------------------------------------------------------------- diagnostics

        private static void LogStats(bool final)
        {
            if (weaponGroups.Count == 0 && items.Count == 0 && bodyGroup == null && swayTime < 1f) return;
            StringBuilder sb = new StringBuilder("Screen coverage " + (final ? "summary" : "so far") + " for " + bodyName
                + " (" + (mode == MeasureMode.Render ? "rendered" : "bounding boxes") + ", fade from " + Settings.FadeStart.Value.ToString("0.00")
                + " to " + Settings.FadeFull.Value.ToString("0.00") + ", items at half, body from " + BodyFadeStart.ToString("0.00") + "):");
            bool any = false;
            foreach (FadeGroup group in weaponGroups) any |= AppendStats(sb, group);
            if (bodyGroup != null) any |= AppendStats(sb, bodyGroup);
            foreach (FadeGroup group in items)
                if (group.coverageMax >= 0.02f) any |= AppendStats(sb, group);
            if (swayTime >= 1f)
            {
                sb.Append("\n  head movement relative to the body ").Append((100f * swayHead / swayTime).ToString("0"))
                    .Append(" cm/s, reaching the camera ").Append((100f * swayCamera / swayTime).ToString("0"))
                    .Append(" cm/s (stabilization ").Append(Settings.Stabilization.Value.ToString("0.00")).Append(')');
                any = true;
            }
            if (any) Plugin.Log.LogInfo(sb.ToString());
        }

        private static bool AppendStats(StringBuilder sb, FadeGroup group)
        {
            if (group.time < 1f) return false;
            sb.Append("\n  ").Append(group.isItem ? "item '" : ReferenceEquals(group, bodyGroup) ? "" : "part '").Append(group.name)
                .Append(group.isItem || !ReferenceEquals(group, bodyGroup) ? "'" : "")
                .Append(": avg ").Append((group.coverageSum / group.time).ToString("0.00"))
                .Append(" max ").Append(group.coverageMax.ToString("0.00"))
                .Append(", faded ").Append((100f * group.fadedTime / group.time).ToString("0")).Append("% of ").Append(group.time.ToString("0")).Append(" s")
                .Append(", % of time per tenth of the screen:");
            foreach (float seconds in group.histogram)
                sb.Append(' ').Append((100f * seconds / group.time).ToString("0"));
            return true;
        }
    }
}
