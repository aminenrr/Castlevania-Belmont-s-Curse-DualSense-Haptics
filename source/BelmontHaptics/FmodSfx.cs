using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;

namespace BelmontHaptics
{
    /// <summary>
    /// v0.3 SFX haptics. Whitelisted FMOD events (footsteps, whip, weapon, landing, damage...)
    /// get a tiny pass-through DSP on their own ChannelGroup. The DSP measures that one sound's
    /// level on the FMOD mixer thread; the haptic synth turns it into a per-category waveform.
    /// Music, dialog, barks, UI and ambience never get a tap, so they can't leak in.
    /// </summary>
    internal static unsafe class FmodSfx
    {
        public enum Side { Both, Left, Right, Alternate }

        public sealed class Cat
        {
            public string Name;
            public ConfigEntry<bool> On;
            public ConfigEntry<string> Events;
            public ConfigEntry<float> Gain, Hz, ReleaseMs, Kick;
            public ConfigEntry<Side> Side;
            public string[] Prefixes;
        }

        public static readonly List<Cat> Cats = new();
        public static ConfigEntry<bool> On;
        public static ConfigEntry<float> Strength, AudioSens;
        public static ConfigEntry<bool> AudioDrive, LogTaps;
        public static ConfigEntry<string> Exclude;
        static string[] _exclude;

        // shared with the synth (written by FMOD mixer thread / Unity thread, read by WASAPI thread)
        public static float[] Peak = new float[16];
        public static float[] KickNow = new float[16];
        public static int[] AltRight = new int[16];

        public static void Bind(ConfigFile cfg)
        {
            On = cfg.Bind("SFX", "SfxHaptics", true, "v0.3: haptics from the game's own sound effects (FMOD), no music/voices.");
            Strength = cfg.Bind("SFX", "SfxStrength", 1.0f, "Master strength of all SFX haptics.");
            AudioDrive = cfg.Bind("SFX", "AudioDrive", true, "Follow each sound's real loudness/length. Off = fixed pulse per sound (Kick only).");
            AudioSens = cfg.Bind("SFX", "AudioSensitivity", 2.0f, "How strongly sound loudness maps to vibration.");
            Exclude = cfg.Bind("SFX", "ExcludeEvents", "event:/Music;event:/Dialog;event:/UI;event:/Environment;event:/_Control;event:/Barks",
                "Semicolon list of event path prefixes that are NEVER felt (checked first).");
            LogTaps = cfg.Bind("Debug", "LogSfxEvents", true, "Log each SFX event that triggers haptics ([sfx]) and player sounds that do NOT ([sfx-miss]), first time only.");
            _exclude = Split(Exclude.Value);

            //       name        events                                                                                                                                                                  gain  hz   relMs kick side
            Add(cfg, "Footsteps", "event:/MC/BaseMoveSet/MC_Footsteps;event:/MC/BaseMoveSet/MC_Stop_Walk",                                                                                                0.45f, 65f, 45f, 0.35f, Side.Alternate);
            Add(cfg, "Landing",   "event:/MC/BaseMoveSet/MC_Land;event:/MC/BaseMoveSet/MC_Stomp_Impact;event:/MC/BaseMoveSet/MC_Jump_Dive_Land",                                                                                                                                       1.0f, 55f, 90f, 0.7f, Side.Both);
            Add(cfg, "Movement",  "event:/MC/BaseMoveSet/MC_Jump;event:/MC/BaseMoveSet/MC_Wall_;event:/MC/BaseMoveSet/MC_Ledge_;event:/MC/BaseMoveSet/MC_Dodge;event:/MC/BaseMoveSet/MC_BackDodge;event:/MC/BaseMoveSet/MC_Slide;event:/MC/BaseMoveSet/MC_Roll;event:/MC/BaseMoveSet/MC_Kick;event:/MC/BaseMoveSet/MC_Dive;event:/MC/BaseMoveSet/MC_Stomp_Fall;event:/MC/BaseMoveSet/MC_Volte;event:/MC/BaseMoveSet/MC_OneWay", 0.5f, 110f, 50f, 0.35f, Side.Both);
            Add(cfg, "Whip",      "event:/MC/Whip/;event:/Weapons/WhipAttack",                                                                                                                           0.9f, 170f, 60f, 0.6f, Side.Right);
            Add(cfg, "Weapon",    "event:/Weapons/",                                                                                                                                                      0.8f, 140f, 60f, 0.5f, Side.Right);
            Add(cfg, "Spells",    "event:/Spells/;event:/MC/Arcana/;event:/MC/MC_Weapon_",                                                                                                                0.9f, 100f, 90f, 0.6f, Side.Both);
            Add(cfg, "Damage",    "event:/MC/Reactions/MC_Hit;event:/MC/Reactions/MC_Drown;event:/MC/Reactions/MC_Death;event:/MC/Reactions/MC_Petrify",                                                                                                              1.2f, 60f, 160f, 1.0f, Side.Both);
            Add(cfg, "Healing",   "event:/MC/Reactions/MC_Heal",                                                                                                                         0.6f, 45f, 250f, 0.5f, Side.Both);
            Add(cfg, "SaveMirror","event:/Environment/Interactibles/SpecialRooms/SaveRoom/Save_",                                                                                               0.7f, 50f, 300f, 0.5f, Side.Both);
            Add(cfg, "MenuNavigate","event:/UI/Menu/Navigate_Menu;event:/UI/Menu/InGame/Menu_InGame_ItemSelect_;event:/UI/Menu/InGame/Menu_InGame_TabSelect;event:/UI/Menu/InGame/Progress/Menu_Progress_Tab_Select;event:/UI/Menu/InGame/Map/Menu_Map_Highlight_Zone;event:/UI/Menu/InGame/Map/Menu_Map_Select;event:/UI/Menu/InGame/Map/Menu_Map_Marker_Select;event:/UI/Menu/InGame/Map/Menu_Map_Zoom;event:/UI/Menu/InGame/Map/Menu_Map_MapChange;event:/UI/Menu/InGame/Tarot/Menu_Tarot_Card_Select;event:/UI/Menu/InGame/Talisman/Menu_Talisman_SelectionArea_;event:/UI/Menu/Main/Menu_Main_SaveMenu_Select", 0.35f, 200f, 30f, 0.5f, Side.Both);
            Add(cfg, "Menu",      "event:/UI/Menu/", 0.5f, 120f, 60f, 0.5f, Side.Both);
            Add(cfg, "Abilities", "event:/VaniaMoves/", 1.0f, 90f, 80f, 0.8f, Side.Both);
            Add(cfg, "EnemyFeedback","event:/Mobs/_CommonMobs/CommonMobs_StaggerFeedback;event:/Mobs/_CommonMobs/CommonMobs_StaggerResist;event:/Mobs/_CommonMobs/CommonMobs_GlancingReaction;event:/Mobs/_CommonMobs/CommonMobs_BreakReaction", 0.7f, 75f, 60f, 0.6f, Side.Right);
            Add(cfg, "Pickups",   "event:/MC/Reactions/MC_Pick;event:/MC/Reactions/MC_LevelUp;event:/MC/Reactions/MC_ManaFull", 0.35f, 220f, 40f, 0.4f, Side.Both);
            Add(cfg, "EnemyHits", "event:/Mobs/_CommonMobs/CommonMobs_HitReaction;event:/Mobs/_CommonMobs/CommonMobs_Death;event:/Mobs/_CommonMobs/CommonMobs_BloodSplat",                             0.6f, 85f, 70f, 0.45f, Side.Both);
            Add(cfg, "Breakables","event:/GPE/Breakable/",                                                                                                                                                0.6f, 80f, 80f, 0.45f, Side.Both);
        }

        static void Add(ConfigFile cfg, string name, string ev, float gain, float hz, float rel, float kick, Side side)
        {
            string s = "SFX." + name;
            var c = new Cat
            {
                Name = name,
                On = cfg.Bind(s, "Enabled", true, $"Feel {name}."),
                Events = cfg.Bind(s, "Events", ev, "Semicolon list of FMOD event path prefixes in this category."),
                Gain = cfg.Bind(s, "Gain", gain, "Strength."),
                Hz = cfg.Bind(s, "Hz", hz, "Vibration frequency (low = thump, high = sharp)."),
                ReleaseMs = cfg.Bind(s, "ReleaseMs", rel, "How long it rings out."),
                Kick = cfg.Bind(s, "Kick", kick, "Instant pulse when the sound starts (0 = off)."),
                Side = cfg.Bind(s, "Side", side, "Which grip: Both, Left, Right, Alternate (left/right each time)."),
            };
            c.Prefixes = Split(c.Events.Value);
            Cats.Add(c);
        }

        static string[] Split(string v) => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public static int Classify(string path)
        {
            if (On == null || !On.Value || string.IsNullOrEmpty(path)) return -1;
            // longest matching prefix wins, so a specific category entry beats a broad exclude
            int best = -1, bestLen = 0;
            foreach (var x in _exclude) if (x.Length > bestLen && path.StartsWith(x, StringComparison.OrdinalIgnoreCase)) { best = -1; bestLen = x.Length; }
            for (int i = 0; i < Cats.Count; i++)
                foreach (var p in Cats[i].Prefixes)
                    if (p.Length > bestLen && path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { best = i; bestLen = p.Length; }
            return best;
        }

        static readonly string[] _missWatch = { "event:/MC/", "event:/Spells/", "event:/Weapons/" };
        public static void OnUnclassifiedPlay(string path)
        {
            if (LogTaps == null || !LogTaps.Value || path == null || !_logged.Add(path)) return;
            foreach (var w in _missWatch)
                if (path.StartsWith(w, StringComparison.OrdinalIgnoreCase)) { Plugin.L.LogInfo("[sfx-miss] " + path); return; }
        }

        // ---------------- native DSP ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct DspDesc
        {
            public uint pluginsdkversion;
            public fixed byte name[32];
            public uint version;
            public int numinputbuffers, numoutputbuffers;
            public IntPtr create, release, reset, read, process, setposition;
            public int numparameters;
            public IntPtr paramdesc;
            public IntPtr spf, spi, spb, spd, gpf, gpi, gpb, gpd;
            public IntPtr shouldiprocess, userdata, sys_register, sys_deregister, sys_mix;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ReadCb(IntPtr state, float* inb, float* outb, uint length, int inch, int* outch);

        [DllImport("fmodstudio")] static extern int FMOD_Studio_System_GetCoreSystem(IntPtr sys, out IntPtr core);
        [DllImport("fmodstudio")] static extern int FMOD5_System_CreateDSP(IntPtr core, DspDesc* desc, out IntPtr dsp);
        [DllImport("fmodstudio")] static extern int FMOD5_ChannelGroup_AddDSP(IntPtr cg, int index, IntPtr dsp);
        [DllImport("fmodstudio")] static extern int FMOD5_ChannelGroup_RemoveDSP(IntPtr cg, IntPtr dsp);
        [DllImport("fmodstudio")] static extern int FMOD5_DSP_Release(IntPtr dsp);
        [DllImport("fmodstudio")] static extern int FMOD_Studio_EventInstance_GetPlaybackState(IntPtr inst, out int state);

        static ReadCb[] _cbs;
        static DspDesc*[] _descs;
        static IntPtr _core, _coreFor;
        static bool _dspBroken;

        sealed class Tap { public IntPtr Inst, Cg, Dsp; public int Cat, Tries, Dead; }
        static readonly List<Tap> _taps = new();
        static readonly HashSet<string> _logged = new();
        const int MaxTaps = 64;

        static void InitDescs()
        {
            if (_descs != null) return;
            _cbs = new ReadCb[Cats.Count];
            _descs = new DspDesc*[Cats.Count];
            for (int i = 0; i < Cats.Count; i++)
            {
                int cat = i;
                _cbs[i] = (IntPtr st, float* inb, float* outb, uint len, int inch, int* outch) => Read(cat, inb, outb, len, inch, outch);
                var d = (DspDesc*)Marshal.AllocHGlobal(sizeof(DspDesc));
                *d = default;
                d->pluginsdkversion = 110; // FMOD_PLUGIN_SDK_VERSION
                var nm = System.Text.Encoding.ASCII.GetBytes("BH_" + Cats[i].Name);
                for (int k = 0; k < nm.Length && k < 31; k++) d->name[k] = nm[k];
                d->version = 1;
                d->numinputbuffers = 1;
                d->numoutputbuffers = 1;
                d->read = Marshal.GetFunctionPointerForDelegate(_cbs[i]);
                _descs[i] = d;
            }
        }

        static int Read(int cat, float* inb, float* outb, uint len, int inch, int* outch)
        {
            int oc = *outch, n = (int)len;
            float pk = 0f;
            if (inch <= 0) return 0;
            for (int s = 0; s < n; s++)
            {
                float* src = inb + s * inch;
                float m = 0f;
                for (int c = 0; c < inch; c++) m += src[c];
                m = MathF.Abs(m / inch);
                if (m > pk) pk = m;
                float* dst = outb + s * oc;
                for (int c = 0; c < oc; c++) dst[c] = src[c % inch];
            }
            if (pk > Peak[cat]) Peak[cat] = pk;
            return 0;
        }

        /// <summary>Called on the Unity main thread when a new instance of a whitelisted event shows up.</summary>
        public static void OnNewInstance(int cat, IntPtr inst, string path)
        {
            var c = Cats[cat];
            if (!c.On.Value) return;
            if (c.Side.Value == Side.Alternate) AltRight[cat] ^= 1;
            float k = c.Kick.Value;
            if (k > 0 && k > KickNow[cat]) KickNow[cat] = k;
            if (LogTaps.Value && _logged.Add(path)) Plugin.L.LogInfo($"[sfx] {c.Name} <- {path}");
            if (AudioDrive.Value && !_dspBroken && _taps.Count < MaxTaps) _taps.Add(new Tap { Inst = inst, Cat = cat });
        }

        public static void Tick(IntPtr studio)
        {
            if (_taps.Count == 0) return;
            if (_dspBroken) { _taps.Clear(); return; }
            if (_coreFor != studio)
            {
                _coreFor = studio;
                if (FMOD_Studio_System_GetCoreSystem(studio, out _core) != Fmod.OK) _core = IntPtr.Zero;
                InitDescs();
            }
            if (_core == IntPtr.Zero) { _taps.Clear(); return; }

            for (int i = _taps.Count - 1; i >= 0; i--)
            {
                var t = _taps[i];
                if (t.Dsp == IntPtr.Zero)
                {
                    if (Fmod.FMOD_Studio_EventInstance_GetChannelGroup(t.Inst, out var cg) == Fmod.OK && cg != IntPtr.Zero)
                    {
                        int r = FMOD5_System_CreateDSP(_core, _descs[t.Cat], out var dsp);
                        if (r != Fmod.OK) { Plugin.L.LogWarning($"[sfx] CreateDSP failed ({r}), falling back to Kick-only haptics."); _dspBroken = true; return; }
                        if (FMOD5_ChannelGroup_AddDSP(cg, Fmod.DSP_HEAD, dsp) != Fmod.OK) { FMOD5_DSP_Release(dsp); _taps.RemoveAt(i); continue; }
                        t.Cg = cg; t.Dsp = dsp;
                    }
                    else if (++t.Tries > 30 || Fmod.FMOD_Studio_EventInstance_IsValid(t.Inst) == 0) _taps.RemoveAt(i);
                    continue;
                }
                bool alive = Fmod.FMOD_Studio_EventInstance_IsValid(t.Inst) != 0
                             && FMOD_Studio_EventInstance_GetPlaybackState(t.Inst, out int ps) == Fmod.OK && ps != 2; // 2 = STOPPED
                if (alive) continue;
                FMOD5_ChannelGroup_RemoveDSP(t.Cg, t.Dsp);
                if (FMOD5_DSP_Release(t.Dsp) == Fmod.OK || ++t.Dead > 120) _taps.RemoveAt(i);
            }
        }
    }
}
