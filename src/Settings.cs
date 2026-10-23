using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using RiskOfOptions;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;
using RoR2;
using UnityEngine;

namespace BetterFirstPerson
{
    internal enum BodyTurning
    {
        FollowCamera,
        Smart,
        Vanilla
    }

    internal sealed class SurvivorSettings
    {
        public string BodyName;
        public string Label;
        public ConfigEntry<float> WeaponLowering;
        public ConfigEntry<float> CameraHeight;
        public ConfigEntry<string> HeadPath;
    }

    // Everything the player can set, shown in the game's mod options. Survivor entries are added once the
    // survivor catalog is complete, so modded survivors get them too.
    internal static class Settings
    {
        private const string GeneralSection = "General";
        private const string CameraSection = "Camera";
        private const string WeaponSection = "Weapon";
        private const string SurvivorSection = "Survivors";

        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<bool> RememberFirstPerson;
        public static ConfigEntry<float> FieldOfView;
        public static ConfigEntry<float> SprintFovEffect;
        public static ConfigEntry<BodyTurning> BodyRotation;
        public static ConfigEntry<bool> ThirdPersonWhileEmoting;
        public static ConfigEntry<float> ForwardOffset;
        public static ConfigEntry<float> LookDownPush;
        public static ConfigEntry<float> Stabilization;
        public static ConfigEntry<float> NearClip;
        public static ConfigEntry<bool> FadeWeapon;
        public static ConfigEntry<float> FadeStart;
        public static ConfigEntry<float> FadeFull;
        public static ConfigEntry<float> MuzzleFlashSize;
        public static ConfigEntry<bool> HideOwnBursts;
        public static ConfigEntry<bool> HideBodyDuringSkills;
        public static ConfigEntry<float> WeaponSway;

        private static ConfigFile config;
        // values read from the file that no entry has claimed yet: settings of earlier versions
        private static Dictionary<ConfigDefinition, string> unclaimed;
        private static readonly Dictionary<string, SurvivorSettings> survivors = new Dictionary<string, SurvivorSettings>();

        // survivors that start with their weapon lowered, in degrees
        private static readonly Dictionary<string, float> defaultLowering = new Dictionary<string, float>
        {
            // the rifle is held high across the chest and fills a big part of the view
            { "RailgunnerBody", 20f }
        };

        public static void Bind(ConfigFile file)
        {
            config = file;
            unclaimed = ReadUnclaimed(file);

            ToggleKey = Bind(GeneralSection, "First Person Key", new KeyboardShortcut(KeyCode.O),
                "Switches first person on and off", null, "First Person Toggle");
            RememberFirstPerson = Bind(GeneralSection, "Remember First Person", true,
                "Go back to first person by itself after respawning and on the next stage");
            FieldOfView = Bind(GeneralSection, "Field Of View", 100f,
                "Horizontal field of view in first person, in degrees. Vanilla third person is about 91 at 16:9. Sprinting and scopes still widen and narrow it",
                new AcceptableValueRange<float>(60f, 150f));
            SprintFovEffect = Bind(GeneralSection, "Sprint FOV Effect", 0.5f,
                "How much of the game's sprint widening to keep. 1 = like vanilla, 0 = none",
                new AcceptableValueRange<float>(0f, 1f));
            BodyRotation = Bind(GeneralSection, "Body Rotation", BodyTurning.FollowCamera,
                "FollowCamera: the body always faces where you look, walking backwards plays the backpedal animation. Smart: vanilla turning, as if you were always shooting. Vanilla: unchanged");
            ThirdPersonWhileEmoting = Bind(GeneralSection, "Third Person While Emoting", true,
                "With CustomEmotesAPI installed: look from behind while an emote plays, so you can see it");

            ForwardOffset = Bind(CameraSection, "Camera Forward Offset", 1f,
                "How far in front of the head's center the camera sits, in head sizes. 1 = the face",
                new AcceptableValueRange<float>(0f, 3f));
            LookDownPush = Bind(CameraSection, "Look Down Push", 1.5f,
                "How much further forward the camera moves while you look down, in head sizes. Keeps the collar and the inside of the body out of view",
                new AcceptableValueRange<float>(0f, 4f));
            Stabilization = Bind(CameraSection, "Camera Stabilization", 0.6f,
                "Steadies the camera against head movement from animations: running bob, melee swings, Acrid's gallop. Moving, jumping and turning are never delayed. 0 = off, 1 = strongest",
                new AcceptableValueRange<float>(0f, 1f), "Camera Stabilization");
            NearClip = Bind(CameraSection, "Near Clip", 0.05f,
                "Closest distance the camera draws, in meters",
                new AcceptableValueRange<float>(0.01f, 0.5f), "Eyes Near Clip");

            FadeWeapon = Bind(WeaponSection, "Fade Weapon Covering Screen", true,
                "Fade your weapon, its gadgets and item displays out while they cover a big part of the screen (sprint jumps, falling, items in front of the face)");
            FadeStart = Bind(WeaponSection, "Weapon Fade Start", 0.3f,
                "Part of the screen your weapon may cover before it starts to fade. Item displays use half of the fade values",
                new AcceptableValueRange<float>(0.05f, 1f));
            FadeFull = Bind(WeaponSection, "Weapon Fade Full", 0.5f,
                "Part of the screen at which your weapon is faded out completely",
                new AcceptableValueRange<float>(0.05f, 1f));
            MuzzleFlashSize = Bind(WeaponSection, "Own Muzzle Flash Size", 0.35f,
                "Size of the muzzle flashes of your own weapons, which appear right in front of the camera. 1 = vanilla, 0 = hidden",
                new AcceptableValueRange<float>(0f, 1f));
            HideOwnBursts = Bind(WeaponSection, "Hide Bursts At Your Body", true,
                "Hide effects that go off at the middle of your own body, like the flash of Huntress' blink, and flashes the game draws over your own model: in first person they would light up right in front of the eyes");
            HideBodyDuringSkills = Bind(WeaponSection, "Hide Body During Skills", true,
                "Hide your body and weapon while a skill plays an animation that moves the whole body (Huntress' Arrow Rain and glaive throw, Mercenary's dashes), so it doesn't pass through the camera. Primary attacks keep them visible");
            WeaponSway = Bind(WeaponSection, "Weapon Sway", 1f,
                "How much the arms and the weapon trail behind fast turns before settling back. 0 = fixed to the view, 1 = a few degrees",
                new AcceptableValueRange<float>(0f, 2f));

            ModSettingsManager.SetModDescription("First person from your survivor's eyes. Press the First Person Key (O) in a run. Survivor settings are at the bottom: weapon lowering and camera height can be changed while playing.");
            ModSettingsManager.AddOption(new KeyBindOption(ToggleKey));
            ModSettingsManager.AddOption(new CheckBoxOption(RememberFirstPerson));
            ModSettingsManager.AddOption(new SliderOption(FieldOfView, new SliderConfig { min = 60f, max = 150f, FormatString = "{0:0}" }));
            ModSettingsManager.AddOption(new SliderOption(SprintFovEffect, new SliderConfig { min = 0f, max = 1f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new ChoiceOption(BodyRotation));
            ModSettingsManager.AddOption(new CheckBoxOption(ThirdPersonWhileEmoting));
            ModSettingsManager.AddOption(new SliderOption(ForwardOffset, new SliderConfig { min = 0f, max = 3f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new SliderOption(LookDownPush, new SliderConfig { min = 0f, max = 4f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new SliderOption(Stabilization, new SliderConfig { min = 0f, max = 1f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new SliderOption(NearClip, new SliderConfig { min = 0.01f, max = 0.5f, FormatString = "{0:0.000}" }));
            ModSettingsManager.AddOption(new CheckBoxOption(FadeWeapon));
            ModSettingsManager.AddOption(new SliderOption(FadeStart, new SliderConfig { min = 0.05f, max = 1f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new SliderOption(FadeFull, new SliderConfig { min = 0.05f, max = 1f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new SliderOption(MuzzleFlashSize, new SliderConfig { min = 0f, max = 1f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new CheckBoxOption(HideOwnBursts));
            ModSettingsManager.AddOption(new CheckBoxOption(HideBodyDuringSkills));
            ModSettingsManager.AddOption(new SliderOption(WeaponSway, new SliderConfig { min = 0f, max = 2f, FormatString = "{0:0.00}" }));

            // settings earlier versions had in these sections and that are gone now
            Forget(definition => definition.Section == GeneralSection || definition.Section == CameraSection || definition.Section == WeaponSection);

            if (RoR2Application.loadFinished) BindSurvivors();
            else RoR2Application.onLoad += BindSurvivors;
        }

        public static SurvivorSettings ForSurvivor(string bodyName)
        {
            return bodyName != null && survivors.TryGetValue(bodyName, out SurvivorSettings settings) ? settings : null;
        }

        private static void BindSurvivors()
        {
            try
            {
                HashSet<string> labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SurvivorSettings existing in survivors.Values) labels.Add(existing.Label);
                foreach (SurvivorDef survivor in SurvivorCatalog.allSurvivorDefs)
                {
                    GameObject prefab = survivor ? survivor.bodyPrefab : null;
                    if (!prefab || survivors.ContainsKey(prefab.name)) continue;
                    string label = Label(survivor, prefab.name);
                    if (!labels.Add(label))
                    {
                        label = label + " " + prefab.name;
                        labels.Add(label);
                    }
                    defaultLowering.TryGetValue(prefab.name, out float lowering);
                    SurvivorSettings settings = new SurvivorSettings { BodyName = prefab.name, Label = label };
                    settings.WeaponLowering = config.Bind(SurvivorSection, label + " Weapon Lowering", lowering, new ConfigDescription(
                        "Turns " + label + "'s arms and the weapon in them down around the shoulders, in degrees, so the weapon takes less of the screen. Can be changed while playing",
                        new AcceptableValueRange<float>(0f, 45f)));
                    settings.CameraHeight = config.Bind(SurvivorSection, label + " Camera Height", 0f, new ConfigDescription(
                        "Raises the camera above " + label + "'s eyes, in centimeters, so the weapon sits lower on the screen. Can be changed while playing",
                        new AcceptableValueRange<float>(-20f, 40f)));
                    settings.HeadPath = config.Bind(SurvivorSection, label + " Head Path", "",
                        "Advanced, for survivors whose head is not found: path inside the model to the object to look out of. Empty = found automatically");
                    ModSettingsManager.AddOption(new SliderOption(settings.WeaponLowering, new SliderConfig { min = 0f, max = 45f, FormatString = "{0:0}" }));
                    ModSettingsManager.AddOption(new SliderOption(settings.CameraHeight, new SliderConfig { min = -20f, max = 40f, FormatString = "{0:0}" }));
                    ModSettingsManager.AddOption(new StringInputFieldOption(settings.HeadPath));
                    survivors[prefab.name] = settings;
                }
                Forget(definition => definition.Section == SurvivorSection || definition.Section == "Survivors Config");
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("Survivor settings could not be set up: " + exception);
            }
        }

        // the English name where the game has it, else the body name without "Body"
        private static string Label(SurvivorDef survivor, string bodyName)
        {
            string name = null;
            try
            {
                string token = survivor.displayNameToken;
                if (!string.IsNullOrEmpty(token))
                {
                    string text = Language.GetString(token, "en");
                    if (!string.IsNullOrEmpty(text) && text != token) name = Regex.Replace(text, "<[^>]*>", "");
                }
            }
            catch (Exception)
            {
            }
            if (string.IsNullOrEmpty(name))
                name = bodyName.EndsWith("Body", StringComparison.Ordinal) ? bodyName.Substring(0, bodyName.Length - 4) : bodyName;
            StringBuilder clean = new StringBuilder(name.Length);
            foreach (char c in name)
                if ("=\n\t\\\"'[]".IndexOf(c) < 0) clean.Append(c);
            string label = clean.ToString().Trim();
            return label.Length > 0 ? label : bodyName;
        }

        private static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description,
            AcceptableValueBase range = null, string oldGeneralKey = null)
        {
            ConfigDefinition definition = new ConfigDefinition(section, key);
            bool inFile = unclaimed != null && unclaimed.ContainsKey(definition);
            ConfigEntry<T> entry = config.Bind(definition, defaultValue, new ConfigDescription(description, range));
            if (!inFile && unclaimed != null)
            {
                // earlier versions kept every setting under General
                ConfigDefinition old = new ConfigDefinition(GeneralSection, oldGeneralKey ?? key);
                if (!old.Equals(definition) && unclaimed.TryGetValue(old, out string value))
                {
                    try
                    {
                        entry.SetSerializedValue(value);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return entry;
        }

        private static Dictionary<ConfigDefinition, string> ReadUnclaimed(ConfigFile file)
        {
            try
            {
                PropertyInfo property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property != null ? property.GetValue(file, null) as Dictionary<ConfigDefinition, string> : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // drop old values from the file, so it only lists what this version uses
        private static void Forget(Predicate<ConfigDefinition> match)
        {
            if (unclaimed == null) return;
            List<ConfigDefinition> stale = new List<ConfigDefinition>();
            foreach (ConfigDefinition definition in unclaimed.Keys)
                if (match(definition)) stale.Add(definition);
            if (stale.Count == 0) return;
            foreach (ConfigDefinition definition in stale) unclaimed.Remove(definition);
            config.Save();
        }
    }
}
