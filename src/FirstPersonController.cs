using System;
using EntityStates;
using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    // Decides when first person runs and connects the session to the game: the player's input update, the
    // camera rig and the movement state that turns the body.
    internal static class FirstPersonController
    {
        public static FirstPersonSession Session { get; private set; }

        // the player wants first person; it runs whenever the situation allows (alive, not in a drop pod,
        // no cutscene camera, not emoting)
        private static bool wanted;
        private static CharacterBody lastBody;
        private static CharacterBody failedBody;
        private static float lastSteerTime = -1f;

        public static void Install()
        {
            On.RoR2.PlayerCharacterMasterController.Update += PlayerUpdate;
            On.RoR2.CameraRigController.SetCameraState += SetCameraState;
            On.EntityStates.GenericCharacterMain.HandleMovements += HandleMovements;
            On.RoR2.CharacterModel.UpdateMaterials += UpdateMaterials;
            Camera.onPreCull += PreCull;
            Run.onRunDestroyGlobal += run => EndSession();
            Application.quitting += FirstPersonVisuals.OnQuit;
        }

        private static void PlayerUpdate(On.RoR2.PlayerCharacterMasterController.orig_Update orig, PlayerCharacterMasterController self)
        {
            orig(self);
            NetworkUser user = self.networkUser;
            LocalUser localUser = user ? user.localUser : null;
            // the host runs this for every player; only the first local one is looked after
            if (localUser == null || localUser != LocalUserManager.GetFirstLocalUser()) return;
            CameraRigController rig = user.cameraRigController;
            if (!rig) return;
            CharacterMaster master = self.master;
            CharacterBody body = master ? master.GetBody() : null;

            if (body && !ReferenceEquals(body, lastBody))
            {
                // respawn, next stage, revival
                if (!ReferenceEquals(lastBody, null) && !Settings.RememberFirstPerson.Value) wanted = false;
                lastBody = body;
                failedBody = null;
            }
            if (Settings.ToggleKey.Value.IsDown() && !PauseManager.isPaused && !localUser.isUIFocused)
            {
                wanted = !wanted;
                failedBody = null;
            }

            bool run = wanted && body && !ReferenceEquals(body, failedBody) && CanRun(body, rig);
            FirstPersonSession session = Session;
            if (session != null && (!run || !ReferenceEquals(session.Body, body) || !ReferenceEquals(session.Rig, rig)))
            {
                EndSession();
                session = null;
            }
            if (session == null && run)
            {
                session = StartSession(body, rig);
                if (session == null) failedBody = body;
            }
            if (session == null) return;

            // aim along the view; the game aims at the point its third person camera looks at
            InputBankTest input = body.inputBank;
            if (input) input.aimDirection = session.ViewForward;
            if (Settings.BodyRotation.Value == BodyTurning.Smart) body.SetAimTimer(0.2f);
        }

        private static bool CanRun(CharacterBody body, CameraRigController rig)
        {
            HealthComponent health = body.healthComponent;
            if (!health || !health.alive) return false;
            if (!ReferenceEquals(rig.target, body.gameObject) || rig.hasOverride || body.currentVehicle) return false;
            if (Settings.ThirdPersonWhileEmoting.Value && EmotesSupport.IsLocalEmotePlaying()) return false;
            return true;
        }

        private static FirstPersonSession StartSession(CharacterBody body, CameraRigController rig)
        {
            try
            {
                Session = FirstPersonSession.Start(body, rig);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("First person could not start: " + exception);
                Session = null;
            }
            return Session;
        }

        public static void EndSession()
        {
            FirstPersonSession session = Session;
            Session = null;
            if (session == null) return;
            try
            {
                session.End();
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("Leaving first person failed: " + exception);
            }
        }

        // something threw while running: back to third person for this body instead of breaking the camera
        private static void Fail(Exception exception)
        {
            Plugin.Log.LogError("First person stopped after an error, press the key to try again: " + exception);
            failedBody = Session != null ? Session.Body : null;
            EndSession();
        }

        private static void SetCameraState(On.RoR2.CameraRigController.orig_SetCameraState orig, CameraRigController self, CameraState cameraState)
        {
            orig(self, cameraState);
            FirstPersonSession session = Session;
            if (session == null || !ReferenceEquals(session.Rig, self)) return;
            try
            {
                session.AfterRigCameraState(cameraState.fov);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private static void PreCull(Camera camera)
        {
            FirstPersonSession session = Session;
            if (session == null || !ReferenceEquals(session.Camera, camera)) return;
            try
            {
                session.OnPreCull();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private static void UpdateMaterials(On.RoR2.CharacterModel.orig_UpdateMaterials orig, CharacterModel self)
        {
            orig(self);
            if (Session != null) FirstPersonVisuals.AfterModelMaterialsUpdated(self);
        }

        // Vanilla turns the body to the aim only while shooting. In first person it always faces the view, so
        // walking backwards plays the backpedal animation instead of turning the body (and its back) around.
        private static void HandleMovements(On.EntityStates.GenericCharacterMain.orig_HandleMovements orig, GenericCharacterMain self)
        {
            orig(self);
            // every character runs this on every physics step: cheap checks first
            FirstPersonSession session = Session;
            if (session == null || Settings.BodyRotation.Value != BodyTurning.FollowCamera || !session.OwnsStateMachine(self.outer)) return;
            CharacterBody body = session.Body;
            CharacterDirection direction = body ? body.characterDirection : null;
            InputBankTest input = body ? body.inputBank : null;
            if (!direction || !input) return;
            Vector3 aim = input.aimDirection;
            aim.y = 0f;
            if (aim.sqrMagnitude < 0.0001f) return;
            direction.moveVector = aim.normalized;
            // no turning lag either: the back never swings into view
            direction.forward = aim;
            lastSteerTime = Time.fixedTime;
        }

        // Per frame, with this frame's view: keep the body exactly under the camera. Only while the main
        // movement state steers it, so skills that turn the body keep control.
        internal static void SnapBodyYaw(FirstPersonSession session, float yaw)
        {
            if (Settings.BodyRotation.Value != BodyTurning.FollowCamera) return;
            if (Time.fixedTime - lastSteerTime > Time.fixedDeltaTime * 2f) return;
            CharacterDirection direction = session.Body ? session.Body.characterDirection : null;
            if (direction) direction.yaw = yaw;
        }
    }
}
