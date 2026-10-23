using System;
using BepInEx;
using BepInEx.Configuration;
using EntityStates;
using RoR2;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

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
            On.RoR2.CharacterModel.UpdateOverlays += UpdateOverlays;
            On.RoR2.GenericSkill.OnExecute += SkillExecuted;
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
            if (ToggleKeyPressed() && !PauseManager.isPaused && !IsTyping(localUser))
            {
                // While first person could be on screen, the key switches what is on screen. While it has to
                // wait (drop pod, cutscene, emote), the key switches whether it comes back afterwards.
                wanted = body && CanRun(body, rig) ? Session == null : !wanted;
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
            if (!ReferenceEquals(rig.target, body.gameObject) || rig.hasOverride) return false;
            // vehicles that hide the body: the drop pod, the Volcanic Egg fireball, being swallowed. Rides where the
            // body stays in view keep first person: Enforcer's dash, ziplines.
            VehicleSeat vehicle = body.currentVehicle;
            if (vehicle && (vehicle.hidePassenger || vehicle.isSurvivorPod)) return false;
            if (Settings.ThirdPersonWhileEmoting.Value && EmotesSupport.IsLocalEmotePlaying()) return false;
            return true;
        }

        // The shortcut's own check refuses while any other key is held, so the key didn't react while running
        // or sprinting: here only the key itself and its modifiers count.
        private static bool ToggleKeyPressed()
        {
            KeyboardShortcut shortcut = Settings.ToggleKey.Value;
            KeyCode key = shortcut.MainKey;
            if (key == KeyCode.None || !UnityInput.Current.GetKeyDown(key)) return false;
            foreach (KeyCode modifier in shortcut.Modifiers)
                if (!UnityInput.Current.GetKey(modifier)) return false;
            return true;
        }

        // typing in the chat, the console or a text field of a menu
        private static bool IsTyping(LocalUser localUser)
        {
            EventSystem eventSystem = localUser.eventSystem;
            GameObject selected = eventSystem ? eventSystem.currentSelectedGameObject : null;
            if (!selected) return false;
            TMP_InputField textField = selected.GetComponent<TMP_InputField>();
            if (textField && textField.isFocused) return true;
            InputField legacyField = selected.GetComponent<InputField>();
            return legacyField && legacyField.isFocused;
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

        // every model runs this every frame: one reference check for the others
        private static void UpdateOverlays(On.RoR2.CharacterModel.orig_UpdateOverlays orig, CharacterModel self)
        {
            orig(self);
            FirstPersonSession session = Session;
            if (session != null && ReferenceEquals(self, session.CharacterModel) && Settings.HideOwnBursts.Value)
                FirstPersonVisuals.StripOverlays(self);
        }

        private static void SkillExecuted(On.RoR2.GenericSkill.orig_OnExecute orig, GenericSkill self)
        {
            orig(self);
            FirstPersonSession session = Session;
            if (session != null && ReferenceEquals(self.characterBody, session.Body)) FirstPersonVisuals.OnSkillExecuted(self);
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
            // the turn itself happens every frame in SnapBodyYaw, with that frame's view: turning here, on the
            // physics step, would pull the body back to the view of a frame or two ago and make it stutter
            lastSteerTime = Time.fixedTime;
        }

        // Per frame, with this frame's view: keep the body exactly under the camera. Only while the main
        // movement state steers it, so skills that turn the body keep control. True when the body was turned.
        internal static bool SnapBodyYaw(FirstPersonSession session, float yaw)
        {
            if (Settings.BodyRotation.Value != BodyTurning.FollowCamera) return false;
            if (Time.fixedTime - lastSteerTime > Time.fixedDeltaTime * 2f) return false;
            CharacterDirection direction = session.Body ? session.Body.characterDirection : null;
            if (!direction) return false;
            direction.yaw = yaw;
            return true;
        }
    }
}
