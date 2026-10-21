using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    // The game takes the camera FOV from the body's camera params (60 vertical for most survivors) and
    // multiplies in a smoothed sprint widening and any skill zoom. First person keeps those two effects as
    // ratios on top of the configured horizontal FOV. The game's own value is left alone, so mouse
    // sensitivity (which the game scales with it) stays as in vanilla.
    internal static class FieldOfView
    {
        private static float TanHalf(float fov)
        {
            return Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        }

        public static float VerticalToHorizontal(float verticalFov, float aspect)
        {
            return 2f * Mathf.Atan(TanHalf(verticalFov) * aspect) * Mathf.Rad2Deg;
        }

        public static float Compute(CameraRigController rig, float gameFov, float aspect, out float neutralFov, out float paramsFov)
        {
            neutralFov = rig ? rig.baseFov : 60f;
            paramsFov = gameFov;
            CameraTargetParams targetParams = rig ? rig.targetParams : null;
            if (targetParams)
            {
                CharacterCameraParamsData current = CharacterCameraParamsData.basic;
                CharacterCameraParamsData.Blend(in targetParams.currentCameraParamsData, ref current, 1f);
                paramsFov = current.fov.value;
                neutralFov = paramsFov;
                if (targetParams.cameraParams)
                {
                    CharacterCameraParamsData neutral = CharacterCameraParamsData.basic;
                    CharacterCameraParamsData.Blend(in targetParams.cameraParams.data, ref neutral, 1f);
                    neutralFov = neutral.fov.value;
                }
            }
            if (gameFov < 1f || paramsFov < 1f || neutralFov < 1f) return gameFov;
            float zoom = TanHalf(paramsFov) / TanHalf(neutralFov);
            float sprint = TanHalf(gameFov) / TanHalf(paramsFov);
            sprint = 1f + (sprint - 1f) * Mathf.Clamp01(Settings.SprintFovEffect.Value);
            float horizontal = Mathf.Clamp(Settings.FieldOfView.Value, 30f, 170f);
            float tanHalfVertical = TanHalf(horizontal) / Mathf.Max(aspect, 0.01f);
            return Mathf.Clamp(2f * Mathf.Atan(tanHalfVertical * zoom * sprint) * Mathf.Rad2Deg, 1f, 170f);
        }
    }
}
