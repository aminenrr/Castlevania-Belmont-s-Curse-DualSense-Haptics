using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BelmontHaptics
{
    /// <summary>
    /// Puts USB DualSense / DualSense Edge controllers back into native audio-haptics mode.
    /// Games and apps that use classic rumble set the "rumble emulation" bits (byte 1, bits 0 and 1)
    /// of the DualSense output report. The Edge keeps that state, and while it is set the controller
    /// ignores the haptic audio on channels 3/4. Sending one output report with those bits cleared
    /// (and no other valid flags, so lights, triggers and volumes are untouched) turns haptics back on.
    /// </summary>
    internal static class DualSenseHid
    {
        const ushort SonyVid = 0x054C;
        const ushort DualSensePid = 0x0CE6;
        const ushort EdgePid = 0x0DF2;

        static bool _loggedNone, _loggedBt;
        const string BtHidService = "00001124-0000-1000-8000-00805f9b34fb";

        /// <summary>Returns how many controllers got the report.</summary>
        public static int RestoreNativeHaptics(bool log)
        {
            int sent = 0;
            try
            {
                HidD_GetHidGuid(out Guid hidGuid);
                IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (set == INVALID_HANDLE) return 0;
                try
                {
                    var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifd); i++)
                    {
                        string path = GetPath(set, ref ifd);
                        if (path == null) continue;
                        string p = path.ToLowerInvariant();
                        if (log && !_loggedBt && p.Contains(BtHidService) && p.Contains("054c") && (p.Contains("0ce6") || p.Contains("0df2")))
                        {
                            _loggedBt = true;
                            Plugin.L.LogWarning("[hid] A DualSense is also connected over Bluetooth. Haptics only work over USB: turn off Bluetooth or unpair the controller, then replug the USB cable.");
                        }
                        if (!p.Contains("vid_054c")) continue;
                        bool edge = p.Contains("pid_0df2");
                        if (!edge && !p.Contains("pid_0ce6")) continue;
                        if (edge && !Plugin.EdgeFix.Value) continue;
                        if (!edge && !Plugin.DualSenseFix.Value) continue;
                        if (TrySend(path, edge, log)) sent++;
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            catch (Exception e) { if (log) Plugin.L.LogWarning("[hid] haptics mode reset failed: " + e.Message); }
            if (sent == 0 && log && !_loggedNone) { _loggedNone = true; Plugin.L.LogInfo("[hid] no USB DualSense HID found to reset (fine for Bluetooth/DSX setups)."); }
            return sent;
        }

        static bool TrySend(string path, bool edge, bool log)
        {
            using SafeFileHandle h = CreateFile(path, GENERIC_WRITE | GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return false;

            if (!HidD_GetPreparsedData(h, out IntPtr pre)) return false;
            HIDP_CAPS caps;
            try { if (HidP_GetCaps(pre, out caps) != HIDP_STATUS_SUCCESS) return false; }
            finally { HidD_FreePreparsedData(pre); }

            int len = caps.OutputReportByteLength;
            // USB output report 0x02 is 48..64 bytes. Bluetooth uses a different, CRC'd report; haptics don't work there anyway.
            if (len < 48 || len > 64) return false;

            var buf = new byte[len];
            buf[0] = 0x02; // USB output report id
            buf[1] = 0x00; // valid_flag0: rumble emulation + "rumble not haptics" bits cleared -> native audio haptics
            buf[2] = 0x00; // valid_flag1: touch nothing else (LEDs, mic, power save)
            bool ok = WriteFile(h, buf, (uint)len, out uint written, IntPtr.Zero) && written > 0;
            if (log) Plugin.L.LogInfo($"[hid] {(edge ? "DualSense Edge" : "DualSense")} set to native haptics mode: {(ok ? "ok" : "write failed (" + Marshal.GetLastWin32Error() + ")")}");
            return ok;
        }

        static string GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifd)
        {
            SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out uint need, IntPtr.Zero);
            if (need == 0) return null;
            IntPtr mem = Marshal.AllocHGlobal((int)need);
            try
            {
                Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                if (!SetupDiGetDeviceInterfaceDetail(set, ref ifd, mem, need, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(mem + 4);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // ---- Win32 ----
        const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        const int HIDP_STATUS_SUCCESS = 0x00110000;
        static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint len, out uint written, IntPtr overlapped);
    }
}
