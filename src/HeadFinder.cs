using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    // A point that moves with the head, and how big the head is
    internal sealed class HeadAnchor
    {
        public Transform Transform;
        public Vector3 LocalCenter;
        public float LocalRadius;
        public float FallbackRadius;
        public string Source;
        // the skeleton's head bone, when there is one: things attached to it are headgear
        public Transform Bone;
        // eye bones: the camera never goes behind them (faces that stick out of the head's shape, like Operator's)
        public Transform[] Eyes = new Transform[0];

        public Vector3 Center => Transform.TransformPoint(LocalCenter);

        // how far the frontmost eye is ahead of the head's center along a direction
        public float EyesAhead(Vector3 center, Vector3 direction)
        {
            float ahead = 0f;
            foreach (Transform eye in Eyes)
                if (eye) ahead = Mathf.Max(ahead, Vector3.Dot(eye.position - center, direction));
            return ahead;
        }

        public float Radius
        {
            get
            {
                if (LocalRadius <= 0f) return FallbackRadius;
                Vector3 scale = Transform.lossyScale;
                return LocalRadius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            }
        }
    }

    internal static class HeadFinder
    {
        // Looks for, in this order: the configured path, the head weak point snipers aim at (it has the
        // head's shape and size), a head center marker, a head bone, the model's own "Head" entry, a neck
        // bone, and at last the chest.
        public static HeadAnchor Find(CharacterBody body, Transform model, string configuredPath)
        {
            float fallbackRadius = Mathf.Clamp(body.radius * 0.3f, 0.1f, 0.6f);
            if (!string.IsNullOrEmpty(configuredPath) && configuredPath.Trim().Length > 0)
            {
                Transform target = model.Find(configuredPath.Trim());
                if (target) return Anchor(target, fallbackRadius, "head path '" + configuredPath.Trim() + "'");
                Plugin.Log.LogWarning("Head path '" + configuredPath + "' is not in " + model.name + ", looking for the head by itself");
            }

            HurtBoxGroup hurtBoxes = model.GetComponent<HurtBoxGroup>();
            if (hurtBoxes && hurtBoxes.hurtBoxes != null)
            {
                foreach (HurtBox hurtBox in hurtBoxes.hurtBoxes)
                    if (hurtBox && hurtBox.isSniperTarget && hurtBox != hurtBoxes.mainHurtBox)
                        return Anchor(hurtBox.transform, fallbackRadius, "head weak point '" + hurtBox.name + "'");
            }

            Transform found = Search(model, name => name == "headcenter");
            if (found) return Anchor(found, fallbackRadius, "head center '" + found.name + "'");
            found = Search(model, name => name == "head" || name == "head_m" || name.EndsWith(":head", StringComparison.Ordinal) || name.EndsWith(" head", StringComparison.Ordinal));
            if (found) return Anchor(found, fallbackRadius, "head bone '" + found.name + "'");

            ChildLocator locator = model.GetComponent<ChildLocator>();
            found = locator ? locator.FindChild("Head") : null;
            if (found) return Anchor(found, fallbackRadius, "model's head '" + found.name + "'");
            found = Search(model, name => name == "neck" || name == "neck_m");
            if (found) return Anchor(found, fallbackRadius, "neck bone '" + found.name + "'");
            found = locator ? locator.FindChild("Chest") : null;
            if (found) return Anchor(found, fallbackRadius, "chest '" + found.name + "'");
            return null;
        }

        private static HeadAnchor Anchor(Transform transform, float fallbackRadius, string source)
        {
            HeadAnchor anchor = new HeadAnchor { Transform = transform, FallbackRadius = fallbackRadius, Source = source };
            // weak points and ragdoll bones carry a collider of the head's shape
            if (transform.TryGetComponent(out SphereCollider sphere))
            {
                anchor.LocalCenter = sphere.center;
                anchor.LocalRadius = sphere.radius;
            }
            else if (transform.TryGetComponent(out CapsuleCollider capsule))
            {
                anchor.LocalCenter = capsule.center;
                anchor.LocalRadius = capsule.radius;
            }
            else if (transform.TryGetComponent(out BoxCollider box))
            {
                anchor.LocalCenter = box.center;
                anchor.LocalRadius = 0.5f * Mathf.Min(box.size.x, Mathf.Min(box.size.y, box.size.z));
            }
            anchor.Bone = HeadBone(transform);
            anchor.Eyes = FindEyes(anchor.Bone);
            return anchor;
        }

        private static bool IsHeadBoneName(string name)
        {
            string n = name.ToLowerInvariant();
            return n == "head" || n == "head_m" || n == "headbone" || n.EndsWith(":head", StringComparison.Ordinal) || n.EndsWith(" head", StringComparison.Ordinal);
        }

        // the anchor itself, its parent (weak points hang off the head), or a sibling (some hang off the neck)
        private static Transform HeadBone(Transform anchor)
        {
            if (IsHeadBoneName(anchor.name)) return anchor;
            Transform parent = anchor.parent;
            if (!parent) return null;
            if (IsHeadBoneName(parent.name)) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform sibling = parent.GetChild(i);
                if (sibling != anchor && IsHeadBoneName(sibling.name)) return sibling;
            }
            return null;
        }

        private static Transform[] FindEyes(Transform headBone)
        {
            if (!headBone) return new Transform[0];
            List<Transform> eyes = new List<Transform>();
            foreach (Transform t in headBone.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (!n.Contains("eye") || n.Contains("lid") || n.Contains("brow") || n.Contains("lash") || n.EndsWith("_end", StringComparison.Ordinal)) continue;
                eyes.Add(t);
                if (eyes.Count >= 16) break;
            }
            return eyes.ToArray();
        }

        // breadth first, so the bone closest to the model root wins over a same-named attachment deeper down
        internal static Transform Search(Transform root, Func<string, bool> matches)
        {
            Queue<Transform> queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Transform current = queue.Dequeue();
                if (current != root && matches(current.name.ToLowerInvariant())) return current;
                for (int i = 0; i < current.childCount; i++) queue.Enqueue(current.GetChild(i));
            }
            return null;
        }
    }
}
