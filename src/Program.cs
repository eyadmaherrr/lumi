// Lumi - a Dynamic Island for Windows: a black notch hugging the top edge of the screen that
// morphs to show what's going on (music, focus timer, downloads, pop-ups) and opens into a
// dashboard of small apps. It also keeps the PC awake so long-running agents keep working.
// Built with the C# 5 compiler that ships with .NET Framework 4 (see build.ps1).
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

[assembly: AssemblyTitle("Lumi")]
[assembly: AssemblyProduct("Lumi")]
[assembly: AssemblyDescription("A Dynamic Island for Windows")]
[assembly: AssemblyCopyright("Eyad Maher")]
[assembly: AssemblyVersion(Lumi.Program.Version)]
[assembly: AssemblyFileVersion(Lumi.Program.Version)]

namespace Lumi
{
    static class Program
    {
        public const string Name = "Lumi";
        public const string Version = "1.0.0";

        // Lumi.exe --screenshots <folder>: render README images with sample data, off-screen.
        public static bool Demo;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--screenshots")
            {
                Demo = true;
                var demoApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var demo = new IslandWindow();
                demoApp.Exit += delegate { demo.Cleanup(); };
                demo.Show();
                demo.RunScreenshots(args[1]);
                demoApp.Run();
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\Lumi.SingleInstance", out created))
            {
                if (!created) return;
                WinForms.Application.EnableVisualStyles();
                WinForms.ToolStripManager.Renderer = new DarkMenuRenderer();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var island = new IslandWindow();
                app.Exit += delegate { island.Cleanup(); };
                island.Show();
                app.Run();
            }
        }
    }

    static class Native
    {
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_HOTKEY = 0x0312, WM_CLIPBOARDUPDATE = 0x031D, WM_MOUSEHWHEEL = 0x020E;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;

        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        // Colour of the screen pixel under the mouse.
        public static System.Windows.Media.Color PixelUnderCursor()
        {
            POINT p;
            GetCursorPos(out p);
            IntPtr dc = GetDC(IntPtr.Zero);
            uint c = GetPixel(dc, p.X, p.Y);
            ReleaseDC(IntPtr.Zero, dc);
            return System.Windows.Media.Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }

        [StructLayout(LayoutKind.Sequential)]
        public class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);
    }

    // ---------- Core Audio: output level for the waveform, master volume for the HUD ----------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // The default speakers/headphones. Used only from the UI thread.
    class AudioDevice
    {
        IAudioMeterInformation meter;
        IAudioEndpointVolume volume;
        DateTime acquired;

        // Re-acquire now and then so switching speakers/headphones is picked up.
        void Ensure()
        {
            if ((meter != null || volume != null) && (DateTime.UtcNow - acquired).TotalSeconds < 5) return;
            acquired = DateTime.UtcNow;
            meter = null;
            volume = null;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out device) != 0 || device == null) return;
                object result;
                Guid iid = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out result) == 0) meter = result as IAudioMeterInformation;
                iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out result) == 0) volume = result as IAudioEndpointVolume;
            }
            catch { }
        }

        // Peak level of what's playing, 0..1, or -1 if it can't be read.
        public double Peak()
        {
            try
            {
                Ensure();
                float peak;
                return meter != null && meter.GetPeakValue(out peak) == 0 ? peak : -1;
            }
            catch { meter = null; return -1; }
        }

        public bool TryGetVolume(out double level, out bool muted)
        {
            level = 0;
            muted = false;
            try
            {
                Ensure();
                float l;
                bool m;
                if (volume == null || volume.GetMasterVolumeLevelScalar(out l) != 0 || volume.GetMute(out m) != 0) return false;
                level = l;
                muted = m;
                return true;
            }
            catch { volume = null; return false; }
        }

        public void SetVolume(double level)
        {
            try
            {
                Ensure();
                if (volume == null) return;
                Guid context = Guid.Empty;
                volume.SetMasterVolumeLevelScalar((float)Math.Max(0, Math.Min(1, level)), ref context);
                if (level > 0) volume.SetMute(false, ref context);
            }
            catch { }
        }
    }
}
