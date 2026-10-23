using System;
using System.Collections.Generic;
using System.Text;
using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    // Lowers the arms and the weapon they hold, after each frame's animation, by turning them together around
    // the shoulders: a "low ready" pose that clears the middle of the view. Some weapons hang off the hips
    // on their own bones (Railgunner's rifle, Huntress' bow, Bandit's shotgun); they get the same turn as the
    // arms while a hand is on them, so the hands stay on the grip. The same turn lets them sway behind fast
    // turns of the view.
    internal sealed class WeaponPose
    {
        private sealed class Part
        {
            public Transform Transform;
            public bool IsArm;
            public Transform[] Bones;
            public bool Applied;
            public Vector3 BaseLocalPosition;
            public Quaternion BaseLocalRotation;
            public Vector3 AppliedLocalPosition;
            public Quaternion AppliedLocalRotation;
        }

        private static readonly string[] weaponWords = { "gun", "weapon", "bow", "rifle", "blade", "sword", "mech", "cannon", "tool", "shield", "staff", "spear", "axe", "hammer", "launcher", "pistol", "club", "knife" };
        private static readonly string[] armWords = { "arm", "shoulder", "clav" };
        private static readonly string[] gameplayWords = { "hitbox", "hurtbox", "collision", "trigger" };
        private static readonly string[] bodyWords ={ "thigh", "pelvis", "hip", "leg", "calf", "foot", "neck", "head", "cloth", "coat", "cape", "scarf", "hair", "jiggle", "belt", "pack", "tail", "wing", "jet", "light" };

        // sway: the most the arms trail behind a turn, in degrees at strength 1; how many seconds of the turn
        // rate they trail by before leveling off; how quickly they follow and settle
        private const float SwayMaxDegrees = 5f;
        private const float SwayResponse = 0.04f;
        private const float SwaySmoothTime = 0.09f;

        private readonly List<Part> parts = new List<Part>();
        private readonly List<Transform> hands = new List<Transform>();
        private readonly float holdDistance;
        private float swayLastYaw;
        private float swayLag;
        private float swayVelocity;
        private bool swayValid;
        private int swayFrame;

        public string Description { get; }

        // how far the arms trail behind the view's turning right now, in degrees
        public float SwayDegrees => swayValid ? swayLag : 0f;

        private WeaponPose(float holdDistance, string description)
        {
            this.holdDistance = holdDistance;
            Description = description;
        }

        public static WeaponPose Build(Transform model, Transform head, ChildLocator locator, float headRadius)
        {
            // the chain from the head down to the model root; arms and weapons branch off it
            HashSet<Transform> spine = new HashSet<Transform>();
            for (Transform t = head; t; t = t.parent)
            {
                spine.Add(t);
                if (t == model) break;
            }

            List<Transform> hands = new List<Transform>();
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                if (IsHand(t.name) && !hands.Contains(t)) hands.Add(t);
            if (locator)
            {
                foreach (string entry in new[] { "HandL", "HandR", "Hand" })
                {
                    Transform t = locator.FindChild(entry);
                    if (t && !hands.Contains(t)) hands.Add(t);
                }
            }

            List<Part> parts = new List<Part>();
            HashSet<Transform> armRoots = new HashSet<Transform>();
            foreach (Transform hand in hands)
            {
                Transform root = BranchRoot(hand, spine);
                if (root && armRoots.Add(root)) parts.Add(new Part { Transform = root, IsArm = true });
            }

            foreach (Transform bone in spine)
            {
                // the model's own children are its meshes, hitboxes and hurtboxes, not bones
                if (bone == model) continue;
                for (int i = 0; i < bone.childCount; i++)
                {
                    Transform child = bone.GetChild(i);
                    if (spine.Contains(child) || armRoots.Contains(child)) continue;
                    string name = child.name.ToLowerInvariant();
                    if (name.StartsWith("ik", System.StringComparison.Ordinal) || Contains(name, bodyWords) || Contains(name, gameplayWords)) continue;
                    Transform[] bones = child.GetComponentsInChildren<Transform>(true);
                    if (!Contains(name, weaponWords) && !(bones.Length >= 3 && HasMuzzle(bones))) continue;
                    // what hits and gets hit must stay where the game put it
                    if (child.GetComponent<Renderer>() || child.GetComponentInChildren<HurtBox>(true) || child.GetComponentInChildren<HitBox>(true)) continue;
                    if (bones.Length > 64) System.Array.Resize(ref bones, 64);
                    // an arm that ends in a weapon instead of a hand (MUL-T) turns with the arms
                    bool isArm = Contains(name, armWords);
                    if (isArm) armRoots.Add(child);
                    parts.Add(new Part { Transform = child, Bones = bones, IsArm = isArm });
                }
            }

            StringBuilder description = new StringBuilder();
            foreach (Part part in parts)
            {
                description.Append(description.Length == 0 ? "" : ", ").Append('\'').Append(part.Transform.name).Append('\'');
                if (!part.IsArm) description.Append(" (while held)");
            }
            WeaponPose pose = new WeaponPose(Mathf.Max(0.2f, headRadius * 2.5f), armRoots.Count == 0 ? "no arms found" : description.ToString());
            pose.parts.AddRange(parts);
            pose.hands.AddRange(hands);
            return pose;
        }

        // Called once per frame after the animation. The animator rewrites the bones each frame it runs; a
        // bone it left alone still carries last frame's turn, which is taken back before turning again.
        public void Apply(float degrees, float yaw, float sway)
        {
            foreach (Part part in parts) TakeBack(part);
            float lag = SwayLag(yaw, sway);
            if (degrees <= 0.01f && Mathf.Abs(lag) < 0.01f) return;
            Vector3 pivot = Vector3.zero;
            int arms = 0;
            foreach (Part part in parts)
            {
                if (!part.IsArm || !part.Transform) continue;
                pivot += part.Transform.position;
                arms++;
            }
            if (arms == 0) return;
            pivot /= arms;
            // lowering: around the view's horizontal right axis, down on the screen whatever the stance;
            // sway: around the vertical through the shoulders
            Quaternion turn = Quaternion.AngleAxis(lag, Vector3.up) * Quaternion.AngleAxis(degrees, Quaternion.Euler(0f, yaw, 0f) * Vector3.right);
            foreach (Part part in parts)
            {
                Transform t = part.Transform;
                if (!t || (!part.IsArm && !IsHeld(part))) continue;
                part.BaseLocalPosition = t.localPosition;
                part.BaseLocalRotation = t.localRotation;
                t.SetPositionAndRotation(pivot + turn * (t.position - pivot), turn * t.rotation);
                part.AppliedLocalPosition = t.localPosition;
                part.AppliedLocalRotation = t.localRotation;
                part.Applied = true;
            }
        }

        public void Release()
        {
            foreach (Part part in parts) TakeBack(part);
            swayValid = false;
        }

        // The body turns with the view at once, so on their own the arms would be locked to the screen while
        // the view turns (looking up and down, the game's aim smoothing already lets them trail). They trail
        // behind fast turns by up to a few degrees and settle smoothly once the turn stops. Returns how far,
        // in degrees around the vertical (negative trails a turn to the right).
        private float SwayLag(float yaw, float strength)
        {
            float deltaTime = Time.deltaTime;
            if (!swayValid)
            {
                swayLastYaw = yaw;
                swayLag = 0f;
                swayVelocity = 0f;
                swayValid = true;
                swayFrame = Time.frameCount;
                return 0f;
            }
            // moved on once per frame, however often the camera is set up
            if (deltaTime <= 0f || swayFrame == Time.frameCount) return swayLag;
            swayFrame = Time.frameCount;
            float rate = Mathf.DeltaAngle(swayLastYaw, yaw) / deltaTime;
            swayLastYaw = yaw;
            float limit = SwayMaxDegrees * Mathf.Max(0f, strength);
            // grows with the turn rate and levels off smoothly at the limit; noisy mouse input is evened out
            // by the damping
            float target = limit > 0.01f ? -limit * (float)Math.Tanh(rate * SwayResponse / limit) : 0f;
            swayLag = Mathf.SmoothDamp(swayLag, target, ref swayVelocity, SwaySmoothTime, Mathf.Infinity, deltaTime);
            return swayLag;
        }

        private static void TakeBack(Part part)
        {
            if (!part.Applied) return;
            part.Applied = false;
            Transform t = part.Transform;
            if (!t || t.localPosition != part.AppliedLocalPosition || t.localRotation != part.AppliedLocalRotation) return;
            t.localPosition = part.BaseLocalPosition;
            t.localRotation = part.BaseLocalRotation;
        }

        private bool IsHeld(Part part)
        {
            float limit = holdDistance * holdDistance;
            foreach (Transform hand in hands)
            {
                if (!hand) continue;
                Vector3 position = hand.position;
                foreach (Transform bone in part.Bones)
                    if (bone && (bone.position - position).sqrMagnitude < limit) return true;
            }
            return false;
        }

        // the ancestor of t that hangs directly off the spine
        private static Transform BranchRoot(Transform t, HashSet<Transform> spine)
        {
            if (spine.Contains(t)) return null;
            while (t.parent && !spine.Contains(t.parent)) t = t.parent;
            return t.parent ? t : null;
        }

        private static bool IsHand(string name)
        {
            string n = name.ToLowerInvariant().Replace("_", ".").Replace(" ", ".");
            return n == "hand" || n == "hand.l" || n == "hand.r" || n == "l.hand" || n == "r.hand" || n == "handl" || n == "handr"
                || n == "hand.left" || n == "hand.right" || n == "left.hand" || n == "right.hand" || n.EndsWith(":lefthand", System.StringComparison.Ordinal)
                || n.EndsWith(":righthand", System.StringComparison.Ordinal);
        }

        private static bool HasMuzzle(Transform[] bones)
        {
            foreach (Transform bone in bones)
                if (bone.name.IndexOf("muzzle", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool Contains(string name, string[] words)
        {
            foreach (string word in words)
                if (name.Contains(word)) return true;
            return false;
        }
    }
}
