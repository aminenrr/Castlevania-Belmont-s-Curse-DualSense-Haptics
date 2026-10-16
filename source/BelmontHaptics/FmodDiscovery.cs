using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace BelmontHaptics
{
    /// <summary>
    /// v0.3 discovery: catalogs every FMOD bank / bus / VCA / event, logs each event the
    /// first time it plays (with the bus it routes into), counts plays, and meters bus
    /// levels. Everything runs on the Unity main thread from a postfix on
    /// FMODUnity.RuntimeManager.Update, using direct calls into fmodstudio.dll.
    /// </summary>
    internal static class FmodDiscovery
    {
        static Type _rm;
        static IntPtr _sys;
        static bool _failed;
        static int _bankCount = -1;
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static long _lastBusLog, _lastSummary, _lastBankCheck;

        sealed class Ev { public IntPtr Desc; public string Path; public bool OneShot, Is3D; public int LengthMs; public int Plays; public string Bus; public int Cat = -1; public HashSet<IntPtr> Live = new(); }
        sealed class Bus { public IntPtr H; public string Path; public IntPtr Cg; public IntPtr Dsp; public bool Locked; }
        sealed class Pending { public Ev E; public IntPtr Inst; public int Tries; }

        static readonly Dictionary<IntPtr, Ev> _events = new();
        static readonly Dictionary<IntPtr, Bus> _buses = new();
        static readonly Dictionary<IntPtr, Bus> _busByCg = new();
        static readonly HashSet<IntPtr> _banksSeen = new();
        static readonly List<Pending> _pending = new();
        static readonly IntPtr[] _inst = new IntPtr[256];
        static string _catalogPath;

        public static void Install(Harmony h)
        {
            if (!Plugin.FmodDiscoveryOn.Value && !FmodSfx.On.Value) return;
            Fmod.InstallResolver();
            _rm = GameTypes.FindAny("FMODUnity.RuntimeManager", "FMODUnity", "Assembly-CSharp-firstpass", "Assembly-CSharp");
            if (_rm == null) { Plugin.L.LogWarning("[fmod] FMODUnity.RuntimeManager not found, discovery off."); return; }
            var upd = AccessTools.Method(_rm, "Update");
            if (upd == null) { Plugin.L.LogWarning("[fmod] RuntimeManager.Update not found, discovery off."); return; }
            h.Patch(upd, postfix: new HarmonyMethod(typeof(FmodDiscovery), nameof(UpdatePostfix)));
            _catalogPath = Path.Combine(Paths.BepInExRootPath, "BelmontHaptics_fmod.txt");
            Plugin.L.LogInfo($"[fmod] hooked ({_rm.Assembly.GetName().Name}). Catalog -> {_catalogPath}");
        }

        static void UpdatePostfix()
        {
            if (_failed) return;
            try { Tick(); }
            catch (Exception e) { _failed = true; Plugin.L.LogError("[fmod] FMOD hook crashed, turning it off: " + e); }
        }

        static bool Log => Plugin.FmodDiscoveryOn.Value;

        static void Tick()
        {
            long now = _clock.ElapsedMilliseconds;
            if (_sys == IntPtr.Zero || Fmod.FMOD_Studio_System_IsValid(_sys) == 0)
            {
                _sys = Fmod.ReadStudioHandle(_rm);
                if (_sys == IntPtr.Zero) return;
                Plugin.L.LogInfo($"[fmod] Studio system 0x{_sys.ToInt64():X}");
            }

            if (now - _lastBankCheck > 1000) { _lastBankCheck = now; ScanBanks(); }
            PollEvents();
            FmodSfx.Tick(_sys);
            if (Log) ResolvePending();
            if (Log && Plugin.FmodMeterBuses.Value && now - _lastBusLog > 2000) { _lastBusLog = now; MeterBuses(); }
            if (Log && now - _lastSummary > 30000) { _lastSummary = now; Summary(); }
        }

        // ---------- catalog ----------
        static void ScanBanks()
        {
            if (Fmod.FMOD_Studio_System_GetBankCount(_sys, out int n) != Fmod.OK || n == _bankCount) return;
            _bankCount = n;
            var banks = Fmod.List(c(Fmod.FMOD_Studio_System_GetBankCount), l(Fmod.FMOD_Studio_System_GetBankList), _sys);
            bool changed = false;
            foreach (var b in banks)
            {
                if (!_banksSeen.Add(b)) continue;
                changed = true;
                string bp = Fmod.Path(Fmod.FMOD_Studio_Bank_GetPath, b) ?? "?";
                var evs = Fmod.List(c(Fmod.FMOD_Studio_Bank_GetEventCount), l(Fmod.FMOD_Studio_Bank_GetEventList), b);
                var bus = Fmod.List(c(Fmod.FMOD_Studio_Bank_GetBusCount), l(Fmod.FMOD_Studio_Bank_GetBusList), b);
                var vca = Fmod.List(c(Fmod.FMOD_Studio_Bank_GetVCACount), l(Fmod.FMOD_Studio_Bank_GetVCAList), b);
                if (Log) Plugin.L.LogInfo($"[fmod-bank] {bp}  events={evs.Length} buses={bus.Length} vcas={vca.Length}");
                if (Log) foreach (var v in vca) Plugin.L.LogInfo("[fmod-vca] " + (Fmod.Path(Fmod.FMOD_Studio_VCA_GetPath, v) ?? "?"));
                foreach (var bh in bus)
                {
                    if (_buses.ContainsKey(bh)) continue;
                    var B = new Bus { H = bh, Path = Fmod.Path(Fmod.FMOD_Studio_Bus_GetPath, bh) ?? "?" };
                    _buses[bh] = B;
                    if (Log) Plugin.L.LogInfo("[fmod-bus] " + B.Path);
                }
                foreach (var d in evs)
                {
                    if (_events.ContainsKey(d)) continue;
                    var E = new Ev { Desc = d, Path = Fmod.Path(Fmod.FMOD_Studio_EventDescription_GetPath, d) };
                    if (E.Path == null && Fmod.FMOD_Studio_EventDescription_GetID(d, out Guid g) == Fmod.OK) E.Path = "{" + g + "}";
                    Fmod.FMOD_Studio_EventDescription_IsOneshot(d, out int os); E.OneShot = os != 0;
                    Fmod.FMOD_Studio_EventDescription_Is3D(d, out int d3); E.Is3D = d3 != 0;
                    Fmod.FMOD_Studio_EventDescription_GetLength(d, out E.LengthMs);
                    E.Cat = FmodSfx.Classify(E.Path);
                    _events[d] = E;
                }
            }
            if (changed) { LockBuses(); if (Log) WriteCatalog(); Plugin.L.LogInfo($"[fmod] catalog: {_events.Count} events, {_buses.Count} buses in {_banksSeen.Count} banks"); }
        }

        static Fmod.CountFn c(Fmod.CountFn f) => f;
        static Fmod.ListFn l(Fmod.ListFn f) => f;

        static void LockBuses()
        {
            if (!Log || !Plugin.FmodMeterBuses.Value) return;
            foreach (var b in _buses.Values)
                if (!b.Locked) b.Locked = Fmod.FMOD_Studio_Bus_LockChannelGroup(b.H) == Fmod.OK;
        }

        static void EnsureBusCg(Bus b)
        {
            if (b.Cg != IntPtr.Zero) return;
            if (Fmod.FMOD_Studio_Bus_GetChannelGroup(b.H, out var cg) != Fmod.OK || cg == IntPtr.Zero) return;
            b.Cg = cg; _busByCg[cg] = b;
            if (Fmod.FMOD5_ChannelGroup_GetDSP(cg, Fmod.DSP_HEAD, out var dsp) == Fmod.OK)
            {
                b.Dsp = dsp;
                Fmod.FMOD5_DSP_SetMeteringEnabled(dsp, 0, 1);
            }
        }

        static void WriteCatalog()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Belmont Haptics FMOD catalog (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ")");
                sb.AppendLine("## Buses");
                foreach (var b in _buses.Values.OrderBy(x => x.Path)) sb.AppendLine(b.Path);
                sb.AppendLine();
                sb.AppendLine("## Events (path | oneshot | 3d | length ms | plays | bus)");
                foreach (var e in _events.Values.OrderBy(x => x.Path))
                    sb.AppendLine($"{e.Path} | {(e.OneShot ? "oneshot" : "loop/sustain")} | {(e.Is3D ? "3d" : "2d")} | {e.LengthMs} | {e.Plays} | {e.Bus ?? "-"}");
                File.WriteAllText(_catalogPath, sb.ToString());
            }
            catch (Exception ex) { Plugin.L.LogWarning("[fmod] catalog write failed: " + ex.Message); }
        }

        // ---------- plays ----------
        static void PollEvents()
        {
            foreach (var e in _events.Values)
            {
                if (Fmod.FMOD_Studio_EventDescription_GetInstanceCount(e.Desc, out int cnt) != Fmod.OK) continue;
                if (cnt == 0) { if (e.Live.Count > 0) e.Live.Clear(); continue; }
                if (Fmod.FMOD_Studio_EventDescription_GetInstanceList(e.Desc, _inst, _inst.Length, out int got) != Fmod.OK) continue;
                bool anyNew = false;
                for (int i = 0; i < got; i++)
                {
                    var h = _inst[i];
                    if (e.Live.Contains(h)) continue;
                    anyNew = true;
                    e.Plays++;
                    if (e.Cat >= 0) FmodSfx.OnNewInstance(e.Cat, h, e.Path); else if (e.Plays == 1) FmodSfx.OnUnclassifiedPlay(e.Path);
                    if (e.Plays == 1 && Log) _pending.Add(new Pending { E = e, Inst = h });
                }
                if (anyNew || got != e.Live.Count)
                {
                    e.Live.Clear();
                    for (int i = 0; i < got; i++) e.Live.Add(_inst[i]);
                }
            }
        }

        /// <summary>For the first play of each event: find which bus its ChannelGroup feeds, then log it.</summary>
        static void ResolvePending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                string bus = null;
                if (Fmod.FMOD_Studio_EventInstance_GetChannelGroup(p.Inst, out var cg) == Fmod.OK && cg != IntPtr.Zero)
                {
                    foreach (var b in _buses.Values) EnsureBusCg(b);
                    var cur = cg;
                    for (int d = 0; d < 16 && cur != IntPtr.Zero; d++)
                    {
                        if (_busByCg.TryGetValue(cur, out var B)) { bus = B.Path; break; }
                        if (Fmod.FMOD5_ChannelGroup_GetParentGroup(cur, out var par) != Fmod.OK) break;
                        cur = par;
                    }
                }
                p.Tries++;
                if (bus != null || p.Tries > 20)
                {
                    p.E.Bus = bus;
                    Plugin.L.LogInfo($"[fmod-play] {p.E.Path}  -> {bus ?? "(bus unknown)"}  {(p.E.OneShot ? "oneshot" : "loop")} {(p.E.Is3D ? "3d" : "2d")} {p.E.LengthMs}ms");
                    _pending.RemoveAt(i);
                }
            }
        }

        // ---------- bus meters ----------
        static unsafe void MeterBuses()
        {
            var sb = new StringBuilder();
            foreach (var b in _buses.Values.OrderBy(x => x.Path))
            {
                EnsureBusCg(b);
                if (b.Dsp == IntPtr.Zero) continue;
                if (Fmod.FMOD5_DSP_GetMeteringInfo(b.Dsp, IntPtr.Zero, out var mi) != Fmod.OK) continue;
                float pk = 0, rms = 0;
                int ch = Math.Clamp((int)mi.numchannels, 0, 32);
                for (int c = 0; c < ch; c++) { pk = Math.Max(pk, mi.peaklevel[c]); rms = Math.Max(rms, mi.rmslevel[c]); }
                if (pk < 0.001f) continue;
                sb.Append($" {b.Path}={pk:0.00}/{rms:0.00}");
            }
            if (sb.Length > 0) Plugin.L.LogInfo("[fmod-meter] peak/rms" + sb);
        }

        static void Summary()
        {
            var top = _events.Values.Where(e => e.Plays > 0).OrderByDescending(e => e.Plays).Take(40).ToList();
            if (top.Count == 0) return;
            Plugin.L.LogInfo("[fmod-top] most played so far: " + string.Join(", ", top.Select(e => $"{e.Path} x{e.Plays}")));
            WriteCatalog();
        }
    }
}
