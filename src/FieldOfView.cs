using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    // The game takes the camera FOV from the body's camera params (60 vertical for most survivors) and changes
    // it on the way: sprinting widens it, scopes narrow it, and some skills and rides widen it for their third
    // person framing (Enforcer's dash and shield). First person starts from the configured horizontal FOV and
    // follows the game's change relative to the body's own base FOV: narrowing fully, so scopes work, and
    // widening only as much as the sprint setting allows, never more than sprinting does. The game's own
    // value is left alone, so mouse sensitivity (which the game scales with it) stays as in vanilla.
    internal static class FieldOfView
    {
        // vanilla sprinting widens the FOV by 1.3 times, which at 60 degrees is this much in tangent terms
        private const float MaxWidening = 1.41f;

        private static float TanHalf(float fov)
        {
            return Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        }

        public static float VerticalToHorizontal(float verticalFov, float aspect)
        {
            return 2f * Mathf.Atan(TanHalf(verticalFov) * aspect) * Mathf.Rad2Deg;
        }

        public static float Compute(CameraRigController rig, float gameFov, float aspect, out float baseFov)
        {
            baseFov = rig ? rig.baseFov : 60f;
            CameraTargetParams targetParams = rig ? rig.targetParams : null;
            if (targetParams && targetParams.cameraParams)
            {
                CharacterCameraParamsData neutral = CharacterCameraParamsData.basic;
                CharacterCameraParamsData.Blend(in targetParams.cameraParams.data, ref neutral, 1f);
                if (neutral.fov.value > 1f) baseFov = neutral.fov.value;
            }
            float horizontal = Mathf.Clamp(Settings.FieldOfView.Value, 30f, 170f);
            float tanHalfVertical = TanHalf(horizontal) / Mathf.Max(aspect, 0.01f);
            if (gameFov < 1f || baseFov < 1f) return 2f * Mathf.Atan(tanHalfVertical) * Mathf.Rad2Deg;
            float change = TanHalf(gameFov) / TanHalf(baseFov);
            if (change > 1f)
            {
                float effect = Mathf.Clamp01(Settings.SprintFovEffect.Value);
                change = 1f + (Mathf.Min(change, MaxWidening) - 1f) * effect;
            }
            return Mathf.Clamp(2f * Mathf.Atan(tanHalfVertical * change) * Mathf.Rad2Deg, 1f, 170f);
        }
    }
}
