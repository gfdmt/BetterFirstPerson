using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    // First person for one body on one camera rig. The game's own scene camera is used: each frame, after the
    // rig has set it up, it is moved to the eyes and given the first person field of view. Everything that
    // gets changed is put back in End.
    internal sealed class FirstPersonSession
    {
        // bodies already described in the log: first person comes and goes (drop pod, cutscenes), the log line once
        private static readonly HashSet<int> logged = new HashSet<int>();

        public readonly CharacterBody Body;
        public readonly GameObject BodyObject;
        public readonly CameraRigController Rig;
        public readonly Camera Camera;
        public readonly Transform Model;
        public readonly CharacterModel CharacterModel;
        public readonly ChildLocator Locator;
        public readonly HeadAnchor Head;
        public readonly string BodyName;
        public readonly SurvivorSettings Survivor;
        // a layer the camera does not draw, for faded out parts; -1 if there is none
        public readonly int HideLayer;

        private readonly EntityStateMachine[] stateMachines;
        private readonly WeaponPose weaponPose;
        private readonly Transform cameraTransform;
        private readonly bool cameraIsChild;
        private readonly Vector3 cameraLocalPosition;
        private readonly Quaternion cameraLocalRotation;
        private readonly float originalNearClip;
        private readonly bool hideLayerWasDrawn;
        private readonly bool originalFading;
        private readonly Transform aimOrigin;
        private readonly Vector3 aimOriginLocalPosition;
        private readonly AimAnimator aimAnimator;
        private bool aimDiagnosticBroken;
        private float lastGameFov;
        private bool fovLogged;

        private Vector3 steadyOffset;
        private bool steadyValid;
        private float steadyTime;

        public Vector3 ViewForward => cameraTransform.forward;

        private FirstPersonSession(CharacterBody body, CameraRigController rig, Camera camera, Transform model, HeadAnchor head, string bodyName, SurvivorSettings survivor)
        {
            Body = body;
            BodyObject = body.gameObject;
            Rig = rig;
            Camera = camera;
            Model = model;
            CharacterModel = model.GetComponent<CharacterModel>();
            Locator = model.GetComponent<ChildLocator>();
            Head = head;
            BodyName = bodyName;
            Survivor = survivor;
            stateMachines = body.GetComponents<EntityStateMachine>();

            cameraTransform = camera.transform;
            cameraIsChild = cameraTransform != rig.transform;
            cameraLocalPosition = cameraTransform.localPosition;
            cameraLocalRotation = cameraTransform.localRotation;
            originalNearClip = camera.nearClipPlane;
            originalFading = rig.enableFading;
            aimOrigin = body.aimOriginTransform;
            aimOriginLocalPosition = aimOrigin ? aimOrigin.localPosition : Vector3.zero;

            HideLayer = FindHideLayer();
            hideLayerWasDrawn = HideLayer >= 0 && (camera.cullingMask & (1 << HideLayer)) != 0;
            weaponPose = WeaponPose.Build(model, head.Transform, Locator, head.Radius);
            aimAnimator = model.GetComponent<AimAnimator>();
        }

        public static FirstPersonSession Start(CharacterBody body, CameraRigController rig)
        {
            Camera camera = rig.sceneCam;
            ModelLocator modelLocator = body.GetComponent<ModelLocator>();
            Transform model = modelLocator ? modelLocator.modelTransform : null;
            if (!camera || !model) return null;
            string bodyName = BodyCatalog.GetBodyName(body.bodyIndex);
            if (string.IsNullOrEmpty(bodyName)) bodyName = body.name.Replace("(Clone)", "");
            SurvivorSettings survivor = Settings.ForSurvivor(bodyName);
            HeadAnchor head = HeadFinder.Find(body, model, survivor != null ? survivor.HeadPath.Value : null);
            if (head == null)
            {
                Plugin.Log.LogWarning("No head found on " + bodyName + ": set its Head Path in the survivor settings");
                return null;
            }
            FirstPersonSession session = new FirstPersonSession(body, rig, camera, model, head, bodyName, survivor);
            session.Begin();
            return session;
        }

        private void Begin()
        {
            // the rig still works out a third person position; fading by its distance would dither our own body
            Rig.enableFading = false;
            if (hideLayerWasDrawn) Camera.cullingMask &= ~(1 << HideLayer);
            if (logged.Add(Body.GetInstanceID()))
                Plugin.Log.LogInfo("First person on " + BodyName + ": eyes at " + Head.Source + ", head size " + Head.Radius.ToString("0.000")
                    + " m, head bone " + (Head.Bone ? "'" + Head.Bone.name + "'" : "none") + ", " + Head.Eyes.Length + " eye bones; weapon lowering moves "
                    + weaponPose.Description + (HideLayer >= 0 ? "; faded parts go to layer " + HideLayer : ""));
            FirstPersonVisuals.Begin(this);
        }

        public void End()
        {
            try
            {
                FirstPersonVisuals.End();
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning("Restoring the weapon and items failed: " + exception.Message);
            }
            try
            {
                weaponPose.Release();
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning("Restoring the arms failed: " + exception.Message);
            }
            if (aimOrigin) aimOrigin.localPosition = aimOriginLocalPosition;
            if (Camera)
            {
                Camera.nearClipPlane = originalNearClip;
                if (hideLayerWasDrawn) Camera.cullingMask |= 1 << HideLayer;
                if (lastGameFov > 1f) Camera.fieldOfView = lastGameFov;
                if (cameraIsChild)
                {
                    cameraTransform.localPosition = cameraLocalPosition;
                    cameraTransform.localRotation = cameraLocalRotation;
                }
            }
            if (Rig) Rig.enableFading = originalFading;
        }

        public bool OwnsStateMachine(EntityStateMachine stateMachine)
        {
            foreach (EntityStateMachine own in stateMachines)
                if (ReferenceEquals(own, stateMachine)) return true;
            return false;
        }

        // right after the rig set its camera for this frame (end of its LateUpdate)
        public void AfterRigCameraState(float gameFov)
        {
            lastGameFov = gameFov;
            float fov = FieldOfView.Compute(Rig, gameFov, Camera.aspect, out float baseFov);
            Camera.fieldOfView = fov;
            Camera.nearClipPlane = Settings.NearClip.Value;
            if (!fovLogged)
            {
                fovLogged = true;
                Plugin.Log.LogInfo("First person FOV: game " + gameFov.ToString("0.0") + " (base " + baseFov.ToString("0.0") + "), aspect " + Camera.aspect.ToString("0.000")
                    + ", applied vertical " + fov.ToString("0.0") + " = horizontal " + FieldOfView.VerticalToHorizontal(fov, Camera.aspect).ToString("0.0"));
            }
            float yaw = cameraTransform.eulerAngles.y;
            bool turned = FirstPersonController.SnapBodyYaw(this, yaw);
            // the arms are aimed in the next update from the body's facing and the aim direction: both from
            // this frame's view, whichever runs first, so the aim never jumps between frames
            InputBankTest input = Body ? Body.inputBank : null;
            if (input) input.aimDirection = cameraTransform.forward;
            float sway = turned ? Settings.WeaponSway.Value : 0f;
            weaponPose.Apply(Survivor != null ? Survivor.WeaponLowering.Value : 0f, yaw, sway);
            RecordAim();
            PlaceCamera();
        }

        // How the animation aims the arms off the body's facing (near zero while the body follows the view)
        // and how far they sway, for the log. Reads a field the game keeps to itself: should that ever stop
        // working, only this goes quiet.
        private void RecordAim()
        {
            if (aimDiagnosticBroken || !aimAnimator || !aimAnimator.enabled) return;
            try
            {
                RecordAimFrom(aimAnimator);
            }
            catch (Exception)
            {
                aimDiagnosticBroken = true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RecordAimFrom(AimAnimator animator)
        {
            FirstPersonVisuals.RecordAim(animator.currentLocalAngles.yaw, weaponPose.SwayDegrees);
        }

        // just before the camera draws: everything has moved for this frame
        public void OnPreCull()
        {
            PlaceCamera();
            FirstPersonVisuals.OnPreCull(Camera);
        }

        // In front of the head along the view's horizontal direction, further forward while looking down,
        // steadied against animation, and never inside a wall.
        private void PlaceCamera()
        {
            if (!Head.Transform) return;
            Vector3 euler = cameraTransform.eulerAngles;
            float yaw = euler.y;
            float pitchDown = Mathf.DeltaAngle(0f, euler.x);
            Vector3 center = Head.Center;
            float radius = Head.Radius;
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            // looking down the view reaches back under the head: the neck, the collar, the inside of the chest
            float push = Mathf.Max(0f, Settings.LookDownPush.Value) * Mathf.Clamp01((pitchDown - 15f) / 55f);
            float offset = Mathf.Max(0f, Settings.ForwardOffset.Value);
            float front = radius * offset;
            // never behind the eyes
            if (offset > 0f && Head.Eyes.Length > 0) front = Mathf.Max(front, Head.EyesAhead(center, forward) + 0.03f);
            float distance = front + radius * push;
            float height = Survivor != null ? Survivor.CameraHeight.Value * 0.01f : 0f;
            Vector3 position = Steady(center, yaw, radius) + forward * distance + Vector3.up * height;
            Vector3 toPosition = position - center;
            float length = toPosition.magnitude;
            if (length > 0.0001f)
            {
                // checked from the real head: the steadied point may lag into a wall
                float margin = Camera.nearClipPlane + 0.02f;
                Vector3 direction = toPosition / length;
                if (Physics.Raycast(center, direction, out RaycastHit hit, length + margin, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                    position = center + direction * Mathf.Max(0f, hit.distance - margin);
            }
            cameraTransform.position = position;
            // shots start at the eyes, so they go exactly where the crosshair points
            if (aimOrigin && ReferenceEquals(Body.aimOriginTransform, aimOrigin)) aimOrigin.position = position;
        }

        // Animations move the head a lot. Its offset from the model is followed through a low-pass filter in
        // the view's yaw frame and kept close to the real head (barely behind the face or below it, a bit to
        // the sides, more in front and above). The model itself is followed exactly, so moving, jumping and
        // turning are never delayed, only the animation's jitter is taken out.
        private Vector3 Steady(Vector3 center, float yaw, float radius)
        {
            if (!Model)
            {
                steadyValid = false;
                return center;
            }
            Vector3 anchor = Model.position;
            Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 offset = Quaternion.Inverse(yawRotation) * (center - anchor);
            float strength = Mathf.Clamp01(Settings.Stabilization.Value);
            float now = Time.time;
            float deltaTime = steadyValid ? now - steadyTime : 0f;
            if (!steadyValid || strength <= 0f)
            {
                steadyOffset = offset;
            }
            else
            {
                // placed twice per frame; only the first placement moves the filter on
                if (deltaTime > 0f)
                    steadyOffset = Vector3.Lerp(steadyOffset, offset, 1f - Mathf.Exp(-deltaTime / (0.3f * strength)));
                Vector3 lag = steadyOffset - offset;
                float reach = radius * strength;
                lag.x = Mathf.Clamp(lag.x, -0.8f * reach, 0.8f * reach);
                lag.y = Mathf.Clamp(lag.y, -0.4f * reach, 0.8f * reach);
                lag.z = Mathf.Clamp(lag.z, -0.25f * reach, 1.25f * reach);
                steadyOffset = offset + lag;
            }
            steadyValid = true;
            steadyTime = now;
            if (deltaTime > 0f) FirstPersonVisuals.RecordSway(offset, steadyOffset, deltaTime);
            return anchor + yawRotation * steadyOffset;
        }

        // the game's "noDraw" layer; else a layer without a name
        private static int FindHideLayer()
        {
            int layer = LayerIndex.noDraw.intVal;
            if (layer >= 0 && layer < 32) return layer;
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return -1;
        }
    }
}
