using System;
using System.IO;
using System.Reflection;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
using RiskOfOptions;
using UnityEngine;

// a few game members used here are not public; the game's runtime allows the access with this
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace BetterFirstPerson
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.rune580.riskofoptions")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.gfdmt.betterfirstperson";
        public const string Name = "Better First Person";
        public const string Version = "1.1.1";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            LoadIcon();
            FirstPersonController.Install();
            MuzzleFlashes.Install();
        }

        private static void LoadIcon()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BetterFirstPerson.icon.png"))
                {
                    if (stream == null) return;
                    byte[] data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int count = stream.Read(data, read, data.Length - read);
                        if (count <= 0) break;
                        read += count;
                    }
                    Texture2D texture = new Texture2D(2, 2);
                    if (!texture.LoadImage(data)) return;
                    Sprite icon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                    ModSettingsManager.SetModIcon(icon);
                }
            }
            catch (Exception exception)
            {
                Log.LogWarning("Mod icon not loaded: " + exception.Message);
            }
        }
    }
}
