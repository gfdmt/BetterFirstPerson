using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    // The player's own muzzle flashes appear right in front of the camera in first person; they are shrunk
    // (or hidden) there. Bursts that go off at the middle of the player's body (Huntress' blink) would fill
    // the view from inside; they are hidden. EffectComponent.Reset runs on an effect's first spawn and every
    // time a pooled effect is reused: the change is undone before the game sets the effect up for its new
    // use, and decided again for that use.
    internal static class MuzzleFlashes
    {
        // how close to the middle of the body or the head a burst goes off, in meters
        private const float BurstReach = 0.3f;

        internal static int adjustedCount;
        private static readonly Dictionary<int, bool> muzzleChildren = new Dictionary<int, bool>();
        private static FirstPersonSession cachedFor;

        public static void Install()
        {
            On.RoR2.EffectComponent.Reset += Reset;
        }

        private static void Reset(On.RoR2.EffectComponent.orig_Reset orig, EffectComponent self)
        {
            MuzzleFlashAdjustment adjustment = null;
            if (adjustedCount > 0)
            {
                adjustment = self.GetComponent<MuzzleFlashAdjustment>();
                if (adjustment) adjustment.Restore();
            }
            orig(self);
            FirstPersonSession session = FirstPersonController.Session;
            if (session == null) return;
            if (IsOwnMuzzleFlash(session, self))
            {
                float size = Mathf.Clamp01(Settings.MuzzleFlashSize.Value);
                if (size >= 0.999f) return;
                adjustment = Adjustment(self, adjustment);
                if (size <= 0.001f) adjustment.Hide();
                else adjustment.Shrink(size);
            }
            else if (Settings.HideOwnBursts.Value && IsBurstAtBody(session, self))
            {
                Adjustment(self, adjustment).Hide();
                FirstPersonVisuals.CountBurst();
            }
        }

        private static MuzzleFlashAdjustment Adjustment(EffectComponent effect, MuzzleFlashAdjustment known)
        {
            if (known) return known;
            MuzzleFlashAdjustment adjustment = effect.GetComponent<MuzzleFlashAdjustment>();
            return adjustment ? adjustment : effect.gameObject.AddComponent<MuzzleFlashAdjustment>();
        }

        // set off in the middle of the body (its center or its core: Huntress' special blinks from one, her
        // utility from the other) or at the head, by the player's own skills or nobody in particular; effects
        // that belong to something else, or hits on the body's surface, stay
        private static bool IsBurstAtBody(FirstPersonSession session, EffectComponent effect)
        {
            EffectData data = effect.effectData;
            CharacterBody body = session.Body;
            if (data == null || !body) return false;
            GameObject root = data.rootObject;
            if (root && !ReferenceEquals(root, session.BodyObject)) return false;
            Vector3 position = effect.transform.position;
            if (Near(position, body.corePosition) || Near(position, session.BodyObject.transform.position)) return true;
            HeadAnchor head = session.Head;
            return head != null && head.Transform && Near(position, head.Center);
        }

        private static bool Near(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude < BurstReach * BurstReach;
        }

        private static bool IsOwnMuzzleFlash(FirstPersonSession session, EffectComponent effect)
        {
            EffectData data = effect.effectData;
            if (data == null || !ReferenceEquals(data.rootObject, session.BodyObject) || data.modelChildIndexIsHurtBoxIndex) return false;
            int index = data.modelChildIndex;
            if (index < 0) return false;
            if (!ReferenceEquals(cachedFor, session))
            {
                cachedFor = session;
                muzzleChildren.Clear();
            }
            if (!muzzleChildren.TryGetValue(index, out bool isMuzzle))
            {
                string childName = session.Locator ? session.Locator.FindChildName(index) : null;
                isMuzzle = childName != null && childName.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0;
                muzzleChildren[index] = isMuzzle;
            }
            return isMuzzle;
        }
    }

    // undoable changes to one pooled effect instance: hidden renderers or a smaller size
    internal sealed class MuzzleFlashAdjustment : MonoBehaviour
    {
        private static readonly List<ParticleSystem> buffer = new List<ParticleSystem>();
        private readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        private readonly List<ParticleSystem> rescaled = new List<ParticleSystem>();
        private readonly List<ParticleSystemScalingMode> scalingModes = new List<ParticleSystemScalingMode>();
        private Vector3 originalScale;
        private bool scaled;
        private bool counted;

        public void Hide()
        {
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                renderer.enabled = false;
                hiddenRenderers.Add(renderer);
            }
            Count();
        }

        public void Shrink(float size)
        {
            // child particle systems only follow the root's scale in Hierarchy mode; switch the ones where that
            // changes nothing else (unscaled parents), the others keep their vanilla size
            buffer.Clear();
            GetComponentsInChildren(true, buffer);
            foreach (ParticleSystem system in buffer)
            {
                ParticleSystem.MainModule main = system.main;
                if (main.scalingMode == ParticleSystemScalingMode.Hierarchy || system.transform == transform) continue;
                Transform parent = system.transform.parent;
                Vector3 parentScale = parent ? parent.lossyScale : Vector3.one;
                if (Mathf.Abs(parentScale.x - 1f) > 0.01f || Mathf.Abs(parentScale.y - 1f) > 0.01f || Mathf.Abs(parentScale.z - 1f) > 0.01f) continue;
                rescaled.Add(system);
                scalingModes.Add(main.scalingMode);
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            buffer.Clear();
            originalScale = transform.localScale;
            transform.localScale = originalScale * size;
            scaled = true;
            Count();
        }

        public void Restore()
        {
            if (!counted) return;
            foreach (Renderer renderer in hiddenRenderers)
                if (renderer) renderer.enabled = true;
            hiddenRenderers.Clear();
            if (scaled)
            {
                transform.localScale = originalScale;
                scaled = false;
            }
            for (int i = 0; i < rescaled.Count; i++)
            {
                if (!rescaled[i]) continue;
                ParticleSystem.MainModule main = rescaled[i].main;
                main.scalingMode = scalingModes[i];
            }
            rescaled.Clear();
            scalingModes.Clear();
            counted = false;
            MuzzleFlashes.adjustedCount--;
        }

        private void Count()
        {
            if (counted) return;
            counted = true;
            MuzzleFlashes.adjustedCount++;
        }

        private void OnDestroy()
        {
            if (!counted) return;
            counted = false;
            MuzzleFlashes.adjustedCount--;
        }
    }
}
