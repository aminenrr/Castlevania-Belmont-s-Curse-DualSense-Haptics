using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BelmontHaptics
{
    [BepInPlugin(Guid, "Belmont Haptics", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "amine.nr.belmonthaptics";
        public const string Version = "0.3.5";
        internal static ManualLogSource L;
        internal static HapticEngine Engine;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> KeepNativeRumble;
        internal static ConfigEntry<float> Strength;
        internal static ConfigEntry<float> LowHz;
        internal static ConfigEntry<float> HighHz;
        internal static ConfigEntry<float> LowGain;
        internal static ConfigEntry<float> HighGain;
        internal static ConfigEntry<float> SideBleed;
        internal static ConfigEntry<float> TransientGain;
        internal static ConfigEntry<float> TransientThreshold;
        internal static ConfigEntry<string> DeviceMatch;
        internal static ConfigEntry<bool> LogEvents;
        internal static ConfigEntry<bool> StartupChime;
        internal static ConfigEntry<bool> AudioHaptics;
        internal static ConfigEntry<float> AudioGain;
        internal static ConfigEntry<float> AudioCutoffHz;
        internal static ConfigEntry<float> AudioGate;
        internal static ConfigEntry<bool> FmodDiscoveryOn;
        internal static ConfigEntry<bool> FmodMeterBuses;
        internal static ConfigEntry<float> RumbleGain;

        public override void Load()
        {
            L = Log;
            Enabled = Config.Bind("General", "Enabled", true, "Turn the DualSense voice-coil haptics on/off.");
            KeepNativeRumble = Config.Bind("General", "KeepNativeRumble", false, "Also send the game's original weak rumble (normally off so they don't fight).");
            Strength = Config.Bind("General", "Strength", 1.0f, new ConfigDescription("Master haptic strength.", new AcceptableValueRange<float>(0f, 2f)));
            LowHz = Config.Bind("Tuning", "LowMotorHz", 75f, "Frequency used for the game's low-frequency (heavy) motor. Deep thump.");
            HighHz = Config.Bind("Tuning", "HighMotorHz", 185f, "Frequency used for the game's high-frequency (light) motor. Sharp buzz.");
            LowGain = Config.Bind("Tuning", "LowGain", 1.0f, "Gain for the heavy motor layer.");
            HighGain = Config.Bind("Tuning", "HighGain", 0.8f, "Gain for the light motor layer.");
            SideBleed = Config.Bind("Tuning", "SideBleed", 0.35f, new ConfigDescription("Heavy motor plays on the left grip, light on the right, like real rumble motors. This is how much of each leaks into the other side (1 = same on both).", new AcceptableValueRange<float>(0f, 1f)));
            TransientGain = Config.Bind("Tuning", "TransientGain", 0.9f, "Extra 'click' on sudden hits (whip impacts, landings). 0 = off.");
            TransientThreshold = Config.Bind("Tuning", "TransientThreshold", 0.2f, "How big a jump in rumble counts as a hit.");
            DeviceMatch = Config.Bind("Device", "NameMatch", "Wireless Controller;DualSense", "Semicolon list of audio device name parts used to find the DualSense (USB).");
            LogEvents = Config.Bind("Debug", "LogRumbleEvents", false, "Log each new rumble feedback name once (helps per-event tuning later).");
            StartupChime = Config.Bind("Debug", "StartupChime", true, "Short pulse on the controller when haptics connect.");

            AudioHaptics = Config.Bind("Audio", "AudioHaptics", false, "OLD v0.2 loopback mode: feels ALL game audio incl. music/voices. Replaced by FMOD SFX haptics in v0.3.");
            AudioGain = Config.Bind("Audio", "AudioGain", 2.5f, "Strength of the loopback audio haptics.");
            AudioCutoffHz = Config.Bind("Audio", "AudioCutoffHz", 220f, "Only sound below this frequency drives the loopback haptics.");
            AudioGate = Config.Bind("Audio", "AudioGate", 0.02f, "Quiet sounds below this level are ignored.");

            FmodDiscoveryOn = Config.Bind("Discovery", "FmodDiscoveryLog", false, "(v0.3.0 discovery tool) Log FMOD banks, buses and events (and which bus each event plays on). Writes BepInEx/BelmontHaptics_fmod.txt.");
            FmodMeterBuses = Config.Bind("Discovery", "MeterBuses", false, "Log bus levels every 2 s during discovery.");

            RumbleGain = Config.Bind("General", "RumbleLayer", 0.6f, "Strength of the game's own rumble layer on top of SFX haptics (0 = SFX only).");
            FmodSfx.Bind(Config);

            Engine = new HapticEngine();
            Engine.Start();

            var h = new Harmony(Guid);
            RumblePatches.Apply(h);
            TryPatchFeedbackEvents(h);
            try { FmodDiscovery.Install(h); }
            catch (Exception e) { L.LogWarning("[fmod] discovery install failed: " + e); }
            L.LogInfo($"Belmont Haptics {Version} loaded (FMOD SFX haptics: {(FmodSfx.On.Value ? "on" : "off")})");
        }

        public override bool Unload()
        {
            Engine?.Stop();
            return true;
        }

        static void TryPatchFeedbackEvents(Harmony h)
        {
            try
            {
                var t = GameTypes.FindAny("RumbleFeedbackInstanceData", "Assembly-CSharp", "MotherBase.FeedbackManager");
                var m = t == null ? null : AccessTools.Method(t, "SendEvent");
                if (m == null) { L.LogWarning("RumbleFeedbackInstanceData.SendEvent not found, rumble names won't be logged."); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(SendEventPrefix)));
                L.LogInfo("Hooked " + t.FullName + ".SendEvent");
            }
            catch (Exception e) { L.LogWarning("Event hook failed: " + e.Message); }
        }

        static readonly HashSet<string> Seen = new();
        static void SendEventPrefix(object __instance)
        {
            if (!LogEvents.Value) return;
            try
            {
                var p = AccessTools.Property(__instance.GetType(), "rumbleFeedbackParams")?.GetValue(__instance)
                        ?? AccessTools.Property(__instance.GetType(), "feature")?.GetValue(__instance);
                var name = p == null ? null : AccessTools.Property(p.GetType(), "name")?.GetValue(p) as string;
                if (!string.IsNullOrEmpty(name) && Seen.Add(name)) L.LogInfo("[rumble] " + name);
            }
            catch { }
        }
    }

    /// <summary>Same hooks as v0.2, resolved by reflection so no interop dlls are needed at compile time.</summary>
    static class RumblePatches
    {
        public static void Apply(Harmony h)
        {
            var pad = GameTypes.Find("Unity.InputSystem", "UnityEngine.InputSystem.Gamepad");
            var ds = GameTypes.Find("Unity.InputSystem", "UnityEngine.InputSystem.DualShock.DualSenseGamepadHID");
            if (pad == null) { Plugin.L.LogError("Gamepad type not found, rumble hooks off."); return; }
            foreach (var t in new[] { ds, pad })
            {
                if (t == null) continue;
                Patch(h, t, "SetMotorSpeeds", nameof(SetMotors));
                Patch(h, t, "PauseHaptics", nameof(Pause));
                Patch(h, t, "ResumeHaptics", nameof(Resume));
                Patch(h, t, "ResetHaptics", nameof(Reset));
            }
        }

        static void Patch(Harmony h, Type t, string method, string prefix)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(t, method);
                if (m == null) { Plugin.L.LogWarning($"{t.Name}.{method} not found"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(RumblePatches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Plugin.L.LogWarning($"Patch {t.Name}.{method} failed: {e.Message}"); }
        }

        static bool SetMotors(float lowFrequency, float highFrequency)
        {
            if (!Plugin.Enabled.Value || Plugin.Engine == null || !Plugin.Engine.Connected) return true; // let native rumble through
            Plugin.Engine.SetMotors(lowFrequency, highFrequency);
            return Plugin.KeepNativeRumble.Value;
        }
        static void Pause() => Plugin.Engine?.Pause(true);
        static void Resume() => Plugin.Engine?.Pause(false);
        static void Reset() => Plugin.Engine?.SetMotors(0, 0);
    }
}
