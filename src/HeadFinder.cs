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

        public Vector3 Center => Transform.TransformPoint(LocalCenter);

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
            return anchor;
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
