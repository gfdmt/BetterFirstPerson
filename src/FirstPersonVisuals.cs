using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    // While a skill moves the whole body (flips, throws, dashes), the whole model is hidden.
    internal static class FirstPersonVisuals
    {
        private const int TileWidth = 64;
        private const int MaxTiles = 8;
        private const float ItemThresholdScale = 0.5f;
        private const float BodyFadeStart = 0.8f;
        private const float BodyFadeFull = 0.95f;
        private const float FadeOutTime = 0.1f;
        private const float FadeInTime = 0.25f;
        // the animator layer skills use for animations of the whole body (flips, throws, dashes)
        private const string FullBodyLayerName = "FullBody, Override";
        private const float SkillHideOutTime = 0.05f;
        // how long after a skill starts its animation still counts as that skill's
        private const float SkillAnimationDelay = 0.5f;

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
            // see-through glows (arrows of light, holograms): out of the way sooner, like item displays
            public bool fx;
            public bool wasDrawn;
            // switched on since the last measurement: kept hidden until it is measured
            public bool awaitingMeasure;
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

        // While a skill moves the whole body, the whole model is hidden: how visible it is (1 = only the
        // fading above applies), whether the full body layer plays, whether its animation is hidden.
        private static float modelAlpha = 1f;
        private static int fullBodyLayer = -1;
        private static bool fullBodyPlaying;
        private static bool skillHide;
        private static GenericSkill lastSkill;
        private static float lastSkillTime = -10f;
        private static bool skillStarted;
        private static int skillHides;
        private static float skillHiddenTime;
        private static int burstsHidden;
        private static readonly HashSet<int> strippedOverlays = new HashSet<int>();
        private static bool overlaysBroken;

        private static bool aimValid;
        private static float lastAimYaw;
        private static int aimFrames;
        private static int aimJumps;
        private static float aimTime;
        private static float aimSum;
        private static float aimMax;
        private static float swayMax;

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
                fullBodyLayer = animator.GetLayerIndex(FullBodyLayerName);
            }
            classifyFrame = Time.frameCount + 1;
            checkVisibleFrames = 0;
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
                // the flashes left out in first person come back in third person
                if (model && strippedOverlays.Count > 0) RefreshOverlays(model);
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
            aimValid = false;
            aimFrames = 0;
            aimJumps = 0;
            aimTime = 0f;
            aimSum = 0f;
            aimMax = 0f;
            swayMax = 0f;
            modelAlpha = 1f;
            fullBodyLayer = -1;
            fullBodyPlaying = false;
            skillHide = false;
            lastSkill = null;
            lastSkillTime = -10f;
            skillStarted = false;
            skillHides = 0;
            skillHiddenTime = 0f;
            burstsHidden = 0;
            strippedOverlays.Clear();
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

        // how far the animation aims the arms to the side of the body's facing, how often that jumps by more
        // than a degree from one frame to the next, and how far the arms sway (degrees in the log)
        public static void RecordAim(float aimYaw, float sway)
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f || deltaTime > 0.25f) return;
            if (aimValid && Mathf.Abs(Mathf.DeltaAngle(lastAimYaw, aimYaw)) > 1f) aimJumps++;
            lastAimYaw = aimYaw;
            aimValid = true;
            aimFrames++;
            aimTime += deltaTime;
            aimSum += Mathf.Abs(aimYaw) * deltaTime;
            aimMax = Mathf.Max(aimMax, Mathf.Abs(aimYaw));
            swayMax = Mathf.Max(swayMax, Mathf.Abs(sway));
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
            UpdateSkillHide(deltaTime);
            float start = Settings.FadeStart.Value;
            float full = Mathf.Max(start + 0.01f, Settings.FadeFull.Value);
            float followerAlpha = 1f;
            foreach (FadeGroup group in weaponGroups)
            {
                WatchSwitchOn(group, enabled);
                float scale = group.fx ? ItemThresholdScale : 1f;
                UpdateGroup(group, enabled, start * scale, full * scale, deltaTime);
                // lines and particles go with the solid parts of the weapon
                if (!group.fx) followerAlpha = Mathf.Min(followerAlpha, group.alpha);
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
            if (group.awaitingMeasure)
            {
                if (!enabled || mode != MeasureMode.Render || group.measured)
                {
                    // the first measurement since it was switched on decides at once
                    group.awaitingMeasure = false;
                    group.alpha = target;
                }
                else
                {
                    target = 0f;
                    group.alpha = 0f;
                }
            }
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

        // A glow that a skill switches on (Huntress' arrow cluster) stays hidden until it has been measured, so
        // it never flashes up in front of the eyes for the few frames a measurement takes. Parts that start
        // switched off may only get their materials when they come on.
        private static void WatchSwitchOn(FadeGroup group, bool enabled)
        {
            bool drawn = HasDrawable(group);
            if (drawn && !group.wasDrawn)
            {
                group.fx = IsEffectGroup(group);
                if (group.fx && enabled && mode == MeasureMode.Render)
                {
                    group.awaitingMeasure = true;
                    group.measured = false;
                    group.alpha = 0f;
                }
            }
            group.wasDrawn = drawn;
        }

        private static float ModelFade()
        {
            return model ? model.fade : 1f;
        }

        private static void Apply(FadeRenderer fr, float alpha)
        {
            alpha *= modelAlpha;
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
                if (fr.applied && fr.dither && fr.renderer) SetFade(fr, ModelFade() * group.alpha * modelAlpha);
        }

        // ---------------------------------------------------------------- skills that move the whole body

        // Flips, throws and dashes play on the animator's full body layer and were made for a camera behind
        // the survivor: in first person the body passes through the camera. While such an animation plays,
        // the whole model is hidden. It is decided when the animation starts, and again when another skill
        // starts during it: the primary attack keeps the model visible (melee swings), anything else hides it.
        private static void UpdateSkillHide(float deltaTime)
        {
            bool playing = FullBodyAnimationPlaying();
            if (playing && (!fullBodyPlaying || skillStarted))
            {
                bool fromSkill = lastSkill && Time.time - lastSkillTime < SkillAnimationDelay;
                bool primary = fromSkill && IsPrimary(lastSkill);
                bool hide = fullBodyPlaying ? skillHide || (fromSkill && !primary) : !primary;
                if (hide && !skillHide) skillHides++;
                skillHide = hide;
            }
            else if (!playing)
            {
                skillHide = false;
            }
            fullBodyPlaying = playing;
            skillStarted = false;
            float target = skillHide && Settings.HideBodyDuringSkills.Value ? 0f : 1f;
            modelAlpha = Mathf.MoveTowards(modelAlpha, target, deltaTime / (target < modelAlpha ? SkillHideOutTime : FadeInTime));
            if (modelAlpha < 0.999f) skillHiddenTime += deltaTime;
        }

        // the layer plays an animation: its state has clips, or it is blending into one that has
        private static bool FullBodyAnimationPlaying()
        {
            if (fullBodyLayer < 0 || !animator || !animator.isActiveAndEnabled || fullBodyLayer >= animator.layerCount) return false;
            if (animator.GetLayerWeight(fullBodyLayer) < 0.5f) return false;
            if (animator.GetCurrentAnimatorClipInfoCount(fullBodyLayer) > 0) return true;
            return animator.IsInTransition(fullBodyLayer) && animator.GetNextAnimatorClipInfoCount(fullBodyLayer) > 0;
        }

        private static bool IsPrimary(GenericSkill skill)
        {
            SkillLocator locator = body ? body.skillLocator : null;
            return locator && ReferenceEquals(skill, locator.primary);
        }

        public static void OnSkillExecuted(GenericSkill skill)
        {
            lastSkill = skill;
            lastSkillTime = Time.time;
            skillStarted = true;
        }

        // ---------------------------------------------------------------- flashes over the own model

        public static void CountBurst()
        {
            burstsHidden++;
        }

        // The game draws flashes (Huntress reappearing after her blink) and swelling shells (shields) over
        // the whole model, the face included, and in first person the face is right at the camera. Our own
        // model goes without the flashes that come and go and without anything that swells the mesh. Reads
        // fields the game keeps to itself: should that ever stop working, only this goes quiet.
        public static void StripOverlays(CharacterModel target)
        {
            if (overlaysBroken) return;
            try
            {
                StripOverlaysFrom(target);
            }
            catch (Exception exception)
            {
                overlaysBroken = true;
                Plugin.Log.LogWarning("Leaving flashes out of the own model stopped working: " + exception.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StripOverlaysFrom(CharacterModel target)
        {
            Material[] overlays = target.currentOverlays;
            int count = overlays != null ? Mathf.Min(target.activeOverlayCount, overlays.Length) : 0;
            if (count <= 0) return;
            List<TemporaryOverlayInstance> temporary = target.temporaryOverlays;
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                Material material = overlays[i];
                if (material && (material.IsKeywordEnabled("VERTEXOFFSET") || IsTemporary(temporary, material)))
                {
                    strippedOverlays.Add(material.GetInstanceID());
                    continue;
                }
                overlays[kept++] = material;
            }
            for (int i = kept; i < count; i++) overlays[i] = null;
            target.activeOverlayCount = kept;
        }

        private static bool IsTemporary(List<TemporaryOverlayInstance> temporary, Material material)
        {
            if (temporary == null) return false;
            foreach (TemporaryOverlayInstance overlay in temporary)
                if (overlay != null && ReferenceEquals(overlay.materialInstance, material)) return true;
            return false;
        }

        // the model puts its overlays together again on the next frame
        private static void RefreshOverlays(CharacterModel target)
        {
            if (overlaysBroken) return;
            try
            {
                ForceOverlayUpdate(target);
            }
            catch (Exception)
            {
                overlaysBroken = true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ForceOverlayUpdate(CharacterModel target)
        {
            target.forceUpdate = true;
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
            // clothes and bodies split from the head (Captain's coat and his body without the head): around the
            // middle of the body and reaching well below it. A weapon held across the chest is off to the side or
            // in front, and doesn't reach the legs.
            float centered = Mathf.Max(0.15f, 0.4f * (body ? body.radius : 0.5f));
            float belowMiddle = Mathf.Lerp(core.y, feet.y, 0.5f);
            int clothes = 0;
            foreach (Renderer renderer in renderers)
            {
                if (!(renderer is SkinnedMeshRenderer) || bodyRenderers.Contains(renderer)) continue;
                Bounds bounds = renderer.bounds;
                Vector3 offset = bounds.center - core;
                offset.y = 0f;
                // standing up like a body, not lying across like a rifle
                if (bounds.size.y < 0.8f * Mathf.Max(bounds.size.x, bounds.size.z)) continue;
                bounds.Expand(0.1f);
                if (!bounds.Contains(core) || offset.magnitude > centered) continue;
                if (bounds.min.y > belowMiddle && !bounds.Contains(headCenter)) continue;
                bodyRenderers.Add(renderer);
                clothes++;
            }
            if (clothes > 0) rule += " and " + clothes + " worn around the body";
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
            // loose meshes on one object make up one thing (Huntress' arrow quads): measured and faded together
            Dictionary<Transform, FadeGroup> looseGroups = new Dictionary<Transform, FadeGroup>();
            foreach (Renderer renderer in renderers)
            {
                if (bodyRenderers.Contains(renderer)) continue;
                FadeRenderer fr = MakeFadeRenderer(renderer);
                parts++;
                if (IsHeadgear(renderer))
                {
                    headgear.Add(fr);
                    sb.Append(" '").Append(renderer.name).Append("' (headgear, hidden)");
                    continue;
                }
                if (!fr.measurable)
                {
                    followers.Add(fr);
                    sb.Append(" '").Append(renderer.name).Append('\'').Append(Describe(fr));
                    continue;
                }
                Transform parent = renderer.transform.parent;
                bool loose = !fr.skinned && parent && parent != modelTransform;
                if (!loose || !looseGroups.TryGetValue(parent, out FadeGroup group))
                {
                    group = new FadeGroup { name = renderer.name };
                    weaponGroups.Add(group);
                    if (loose) looseGroups[parent] = group;
                }
                else if (group.renderers.Count == 1)
                {
                    group.name = parent.name;
                }
                group.renderers.Add(fr);
            }
            foreach (FadeGroup group in weaponGroups)
            {
                group.fx = IsEffectGroup(group);
                sb.Append(" '").Append(group.name).Append('\'');
                if (group.renderers.Count > 1)
                {
                    sb.Append(" [");
                    for (int i = 0; i < group.renderers.Count; i++)
                        sb.Append(i == 0 ? "" : ", ").Append(group.renderers[i].renderer.name);
                    sb.Append(']');
                }
                sb.Append(Describe(group.renderers[0]));
                if (group.fx) sb.Append(" (glow)");
                if (!HasDrawable(group)) sb.Append(" (off)");
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

        // drawn see-through, after the solid world (glowing quads, beams), and unable to dither
        private static bool IsEffectGroup(FadeGroup group)
        {
            foreach (FadeRenderer fr in group.renderers)
            {
                if (fr.dither || !fr.renderer) return false;
                Material[] materials = fr.renderer.sharedMaterials;
                if (materials.Length == 0) return false;
                foreach (Material material in materials)
                    if (!material || material.renderQueue <= (int)RenderQueue.GeometryLast) return false;
            }
            return group.renderers.Count > 0;
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
        // Until it is proven, rendering is checked against the box estimate: anything drawn at all (a weapon,
        // the body, an item) proves it. It is only given up when parts were clearly on screen for ten seconds of
        // checks (boxes around the camera don't count) and not a single pixel was ever drawn.
        private static void SelfCheck()
        {
            if (renderVerified) return;
            bool rendered = bodyGroup != null && bodyGroup.measured && bodyGroup.coverage > 0f;
            float box = 0f;
            foreach (FadeGroup group in weaponGroups)
            {
                box = Mathf.Max(box, group.boxCoverage);
                rendered |= group.measured && group.coverage > 0f;
            }
            foreach (FadeGroup group in items)
                rendered |= group.measured && group.coverage > 0f;
            if (rendered)
            {
                renderVerified = true;
                Plugin.Log.LogInfo("Rendered screen coverage works (checked against boxes for " + checkVisibleFrames + " frames)");
                return;
            }
            if (box < 0.05f) return;
            checkVisibleFrames++;
            if (checkVisibleFrames < 600) return;
            mode = MeasureMode.Box;
            Plugin.Log.LogWarning("Rendered screen coverage stayed empty while parts were on screen, using bounding boxes instead");
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

        // a box around the camera says nothing about how much of the screen the mesh in it covers: left out
        private static float BoxCoverage(FadeGroup group)
        {
            float coverage = 0f;
            foreach (FadeRenderer fr in group.renderers)
            {
                if (!fr.measurable || !IsDrawn(fr.renderer) || fr.renderer.bounds.Contains(boxCameraPosition)) continue;
                coverage = Mathf.Max(coverage, Coverage(fr));
            }
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
            if (weaponGroups.Count == 0 && items.Count == 0 && bodyGroup == null && swayTime < 1f && aimTime < 1f && skillHides == 0 && burstsHidden == 0) return;
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
            if (aimTime >= 1f && aimFrames > 0)
            {
                sb.Append("\n  arms aimed to the side of the body: avg ").Append((aimSum / aimTime).ToString("0.0"))
                    .Append(" deg, max ").Append(aimMax.ToString("0.0")).Append(" deg, jumps over 1 deg in ")
                    .Append((100f * aimJumps / aimFrames).ToString("0.0")).Append("% of frames; sway up to ")
                    .Append(swayMax.ToString("0.0")).Append(" deg (weapon sway ").Append(Settings.WeaponSway.Value.ToString("0.00")).Append(')');
                any = true;
            }
            if (skillHides > 0 || burstsHidden > 0 || strippedOverlays.Count > 0)
            {
                sb.Append("\n  model hidden for skills that move the whole body ").Append(skillHides).Append(" times, ")
                    .Append(skillHiddenTime.ToString("0.0")).Append(" s (")
                    .Append(fullBodyLayer >= 0 ? "full body layer " + fullBodyLayer : "no full body layer").Append("); bursts at the body hidden: ")
                    .Append(burstsHidden).Append("; flashes left out of the model: ").Append(strippedOverlays.Count);
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
