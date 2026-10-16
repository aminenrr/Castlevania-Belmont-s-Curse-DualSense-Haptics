using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;

namespace BelmontHaptics
{
    /// <summary>
    /// Resolves game / Unity types at runtime from BepInEx/interop, so the plugin
    /// compiles without any interop dlls. Loads the interop assembly by path first
    /// (AccessTools.TypeByName only sees assemblies that are already loaded, which is
    /// why the v0.2 RumbleFeedbackInstanceData lookup failed).
    /// </summary>
    internal static class GameTypes
    {
        public static Assembly Asm(string name)
        {
            var a = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == name);
            if (a != null) return a;
            try { return Assembly.Load(new AssemblyName(name)); } catch { }
            try
            {
                var p = Path.Combine(Paths.BepInExRootPath, "interop", name + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            catch (Exception e) { Plugin.L.LogWarning($"Could not load {name}: {e.Message}"); }
            return null;
        }

        /// <summary>Find a type by full name, then by short name (Il2CppInterop may add an Il2Cpp namespace prefix).</summary>
        public static Type Find(string asmName, string fullName)
        {
            var a = Asm(asmName);
            if (a == null) return null;
            var t = a.GetType(fullName) ?? a.GetType("Il2Cpp" + fullName) ?? a.GetType("Il2Cpp." + fullName);
            if (t != null) return t;
            string shortName = fullName.Substring(fullName.LastIndexOf('.') + 1);
            Type[] all;
            try { all = a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { all = e.Types.Where(x => x != null).ToArray(); }
            return all.FirstOrDefault(x => x.Name == shortName && (x.Namespace ?? "").EndsWith(fullName.Contains('.') ? fullName.Substring(0, fullName.LastIndexOf('.')) : "", StringComparison.Ordinal))
                ?? all.FirstOrDefault(x => x.Name == shortName);
        }

        public static Type FindAny(string fullName, params string[] asmNames)
        {
            foreach (var n in asmNames) { var t = Find(n, fullName); if (t != null) return t; }
            return null;
        }
    }
}
