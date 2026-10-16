using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BelmontHaptics
{
    /// <summary>
    /// Direct P/Invoke into the game's own fmodstudio.dll (FMOD 2.03.09).
    /// Studio API = FMOD_Studio_*, core API = FMOD5_* (both exported from fmodstudio.dll in this build).
    /// </summary>
    internal static unsafe class Fmod
    {
        const string DLL = "fmodstudio";
        public const int OK = 0;

        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        static bool _resolverSet;
        /// <summary>Bind to the copy of fmodstudio.dll the game already loaded (it lives in the game's Plugins folder, not on the default search path).</summary>
        public static void InstallResolver()
        {
            if (_resolverSet) return;
            _resolverSet = true;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Fmod).Assembly, (name, asm, path) =>
                {
                    if (!name.StartsWith("fmodstudio", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                    foreach (var n in new[] { "fmodstudio.dll", "fmodstudioL.dll" })
                    {
                        var h = GetModuleHandleW(n);
                        if (h != IntPtr.Zero) return h;
                    }
                    return IntPtr.Zero;
                });
            }
            catch (Exception e) { Plugin.L.LogWarning("FMOD resolver: " + e.Message); }
        }

        // ---- Studio: system / banks ----
        [DllImport(DLL)] public static extern int FMOD_Studio_System_IsValid(IntPtr system);
        [DllImport(DLL)] public static extern int FMOD_Studio_System_GetBankCount(IntPtr system, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_System_GetBankList(IntPtr system, IntPtr[] array, int capacity, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetPath(IntPtr bank, byte[] path, int size, out int retrieved);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetLoadingState(IntPtr bank, out int state);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetEventCount(IntPtr bank, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetEventList(IntPtr bank, IntPtr[] array, int capacity, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetBusCount(IntPtr bank, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetBusList(IntPtr bank, IntPtr[] array, int capacity, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetVCACount(IntPtr bank, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bank_GetVCAList(IntPtr bank, IntPtr[] array, int capacity, out int count);

        // ---- Studio: events ----
        [DllImport(DLL)] public static extern int FMOD_Studio_EventDescription_GetPath(IntPtr desc, byte[] path, int size, out int retrieved);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventDescription_GetID(IntPtr desc, out Guid id);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventDescription_IsOneshot(IntPtr desc, out int oneshot);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventDescription_Is3D(IntPtr desc, out int is3d);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventDescription_GetLength(IntPtr desc, out int lengthMs);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventDescription_GetInstanceCount(IntPtr desc, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventDescription_GetInstanceList(IntPtr desc, IntPtr[] array, int capacity, out int count);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventInstance_GetChannelGroup(IntPtr inst, out IntPtr group);
        [DllImport(DLL)] public static extern int FMOD_Studio_EventInstance_IsValid(IntPtr inst);

        // ---- Studio: buses / VCAs ----
        [DllImport(DLL)] public static extern int FMOD_Studio_Bus_GetPath(IntPtr bus, byte[] path, int size, out int retrieved);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bus_LockChannelGroup(IntPtr bus);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bus_UnlockChannelGroup(IntPtr bus);
        [DllImport(DLL)] public static extern int FMOD_Studio_Bus_GetChannelGroup(IntPtr bus, out IntPtr group);
        [DllImport(DLL)] public static extern int FMOD_Studio_VCA_GetPath(IntPtr vca, byte[] path, int size, out int retrieved);

        // ---- Core ----
        [DllImport(DLL)] public static extern int FMOD5_ChannelGroup_GetParentGroup(IntPtr cg, out IntPtr parent);
        [DllImport(DLL)] public static extern int FMOD5_ChannelGroup_GetDSP(IntPtr cg, int index, out IntPtr dsp);
        [DllImport(DLL)] public static extern int FMOD5_DSP_SetMeteringEnabled(IntPtr dsp, int inputEnabled, int outputEnabled);
        [DllImport(DLL)] public static extern int FMOD5_DSP_GetMeteringInfo(IntPtr dsp, IntPtr inputInfo, out MeteringInfo outputInfo);
        public const int DSP_HEAD = -1;

        [StructLayout(LayoutKind.Sequential)]
        public struct MeteringInfo
        {
            public int numsamples;
            public fixed float peaklevel[32];
            public fixed float rmslevel[32];
            public short numchannels;
        }

        // ---- helpers ----
        public delegate int PathFn(IntPtr h, byte[] buf, int size, out int retrieved);
        static readonly byte[] _buf = new byte[512];
        public static string Path(PathFn fn, IntPtr h)
        {
            try
            {
                int r = fn(h, _buf, _buf.Length, out int got);
                if (r != OK || got <= 0) return null;
                int n = Math.Min(got, _buf.Length) - 1; // includes null terminator
                return Encoding.UTF8.GetString(_buf, 0, Math.Max(0, n));
            }
            catch { return null; }
        }

        public delegate int ListFn(IntPtr h, IntPtr[] arr, int cap, out int count);
        public delegate int CountFn(IntPtr h, out int count);
        public static IntPtr[] List(CountFn count, ListFn list, IntPtr h)
        {
            if (count(h, out int n) != OK || n <= 0) return Array.Empty<IntPtr>();
            var arr = new IntPtr[n];
            if (list(h, arr, n, out int got) != OK) return Array.Empty<IntPtr>();
            if (got < n) Array.Resize(ref arr, got);
            return arr;
        }

        /// <summary>Read FMOD.Studio.System.handle from FMODUnity.RuntimeManager.StudioSystem (call on the Unity main thread).</summary>
        public static IntPtr ReadStudioHandle(Type runtimeManager)
        {
            var prop = runtimeManager.GetProperty("StudioSystem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var sys = prop?.GetValue(null);
            if (sys == null) return IntPtr.Zero;
            var t = sys.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object v = t.GetField("handle", F)?.GetValue(sys) ?? t.GetProperty("handle", F)?.GetValue(sys);
            if (v is IntPtr p) return p;
            if (v is long l) return new IntPtr(l);
            return IntPtr.Zero;
        }
    }
}
