using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;

namespace BetterFirstPerson
{
    // Tells whether the local player is playing a CustomEmotesAPI emote. Read through reflection, so the mod
    // has no dependency on it.
    internal static class EmotesSupport
    {
        private const string EmotesGuid = "com.weliveinasociety.CustomEmotesAPI";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool looked;
        private static MemberInfo localMapper;
        private static MemberInfo clipName;
        private static Type clipOwner;

        public static bool IsLocalEmotePlaying()
        {
            if (!looked) Look();
            if (localMapper == null) return false;
            try
            {
                object mapper = Read(localMapper, null);
                if (mapper == null || (mapper is UnityEngine.Object unityObject && !unityObject)) return false;
                Type type = mapper.GetType();
                if (clipName == null || clipOwner != type)
                {
                    clipOwner = type;
                    clipName = Member(type, "currentClipName", Instance);
                    if (clipName == null) return false;
                }
                string clip = Read(clipName, mapper) as string;
                return !string.IsNullOrEmpty(clip) && clip != "none";
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Look()
        {
            looked = true;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(EmotesGuid, out PluginInfo info) || !info.Instance) return;
                Type api = info.Instance.GetType().Assembly.GetType("EmotesAPI.CustomEmotesAPI", false);
                if (api != null) localMapper = Member(api, "localMapper", Static);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning("CustomEmotesAPI found but not readable: " + exception.Message);
            }
        }

        private static MemberInfo Member(Type type, string name, BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field != null) return field;
            return type.GetProperty(name, flags);
        }

        private static object Read(MemberInfo member, object target)
        {
            if (member is FieldInfo field) return field.GetValue(target);
            if (member is PropertyInfo property) return property.GetValue(target, null);
            return null;
        }
    }
}
