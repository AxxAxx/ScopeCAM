using System.Reflection;
using System.Runtime.InteropServices;

namespace ScopeCAM;

/// <summary>
/// Minimal P/Invoke layer over amcam.dll (the ToupTek/AmScope camera SDK
/// that ships with the AmScope software). Only the functions this app
/// needs are declared.
/// </summary>
internal static class AmcamNative
{
    private const string Dll = "amcam.dll";

    // The DLL is not redistributed with this app; load it from the
    // AmScope installation. A copy next to the exe takes precedence.
    private static readonly string[] SearchPaths =
    {
        Path.Combine(AppContext.BaseDirectory, "amcam.dll"),
        @"C:\Program Files\AmScope\AmScope\x64\amcam.dll",
    };

    static AmcamNative()
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
        {
            if (!name.Equals(Dll, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            foreach (var path in SearchPaths)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                    return handle;
            }
            return IntPtr.Zero; // fall back to default search
        });
    }

    // Events delivered to the StartPullModeWithCallback callback.
    public const uint EVENT_EXPOSURE = 0x0001;     // auto exposure changed exposure/gain
    public const uint EVENT_TEMPTINT = 0x0002;     // white balance temp/tint changed
    public const uint EVENT_IMAGE = 0x0004;        // a live frame is ready to pull
    public const uint EVENT_ERROR = 0x0080;        // generic error, operation stopped
    public const uint EVENT_DISCONNECTED = 0x0081; // camera unplugged

    // Image adjustment ranges (from the SDK header).
    public const int TEMP_MIN = 2000, TEMP_MAX = 15000, TEMP_DEF = 6503;
    public const int TINT_MIN = 200, TINT_MAX = 2500, TINT_DEF = 1000;
    public const int GAMMA_MIN = 20, GAMMA_MAX = 180, GAMMA_DEF = 100;
    public const int CONTRAST_MIN = -255, CONTRAST_MAX = 255, CONTRAST_DEF = 0;
    public const int SATURATION_MIN = 0, SATURATION_MAX = 255, SATURATION_DEF = 128;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DeviceV2
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string displayname;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string id;
        public IntPtr model;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FrameInfoV2
    {
        public uint width;
        public uint height;
        public uint flag;
        public uint seq;
        public ulong timestamp;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void EventCallback(uint nEvent, IntPtr ctx);

    [DllImport(Dll)]
    public static extern uint Amcam_EnumV2([Out] DeviceV2[] devices);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern IntPtr Amcam_Open(string? id);

    [DllImport(Dll)]
    public static extern void Amcam_Close(IntPtr h);

    [DllImport(Dll)]
    public static extern int Amcam_StartPullModeWithCallback(IntPtr h, EventCallback cb, IntPtr ctx);

    [DllImport(Dll)]
    public static extern int Amcam_Stop(IntPtr h);

    [DllImport(Dll)]
    public static extern int Amcam_PullImageV2(IntPtr h, IntPtr pImageData, int bits, ref FrameInfoV2 info);

    [DllImport(Dll)]
    public static extern int Amcam_get_Size(IntPtr h, out int width, out int height);

    // Returns the number of supported resolutions (negative = error).
    [DllImport(Dll)]
    public static extern int Amcam_get_ResolutionNumber(IntPtr h);

    [DllImport(Dll)]
    public static extern int Amcam_get_Resolution(IntPtr h, uint index, out int width, out int height);

    // Select resolution by index; only valid while the stream is stopped.
    [DllImport(Dll)]
    public static extern int Amcam_put_eSize(IntPtr h, uint index);

    [DllImport(Dll)]
    public static extern int Amcam_get_eSize(IntPtr h, out uint index);

    [DllImport(Dll)]
    public static extern int Amcam_get_ExpTimeRange(IntPtr h, out uint min, out uint max, out uint def);

    [DllImport(Dll)]
    public static extern int Amcam_get_ExpoTime(IntPtr h, out uint timeUs);

    [DllImport(Dll)]
    public static extern int Amcam_put_ExpoTime(IntPtr h, uint timeUs);

    [DllImport(Dll)]
    public static extern int Amcam_get_ExpoAGainRange(IntPtr h, out ushort min, out ushort max, out ushort def);

    [DllImport(Dll)]
    public static extern int Amcam_get_ExpoAGain(IntPtr h, out ushort gainPercent);

    [DllImport(Dll)]
    public static extern int Amcam_put_ExpoAGain(IntPtr h, ushort gainPercent);

    [DllImport(Dll)]
    public static extern int Amcam_get_AutoExpoEnable(IntPtr h, out int enabled);

    [DllImport(Dll)]
    public static extern int Amcam_put_AutoExpoEnable(IntPtr h, int enabled);

    // AMCAM_OPTION_SHARPENING: (threshold << 24) | (radius << 16) | strength;
    // strength [0,500] (0 = off), radius [1,10], threshold [0,255].
    public const uint OPTION_SHARPENING = 0x1e;

    // Auto-exposure stability. THRESHOLD [2,15] def 5: minimum brightness
    // delta before AE reacts. PERCENT: 1-99 = meter on the brightest N% of
    // pixels (default 1 — twitchy on shiny PCBs), 0/100 = full-frame
    // average. DAMP (thousandths, [0,1000] def 0): higher = smoother,
    // slower exposure/gain transitions.
    // Auto-exposure policy: 0 = Exposure Only, 1 = Exposure Preferred
    // (default), 2 = Gain Only, 3 = Gain Preferred.
    public const uint OPTION_AUTOEXP_POLICY = 0x10;

    // Demosaic method: 0 = bilinear (default), 1 = VNG, 2 = PPG, 3 = AHD,
    // 4 = EA. Bilinear produces false color on fine detail.
    public const uint OPTION_DEMOSAIC = 0x12;

    // Latency: USB grab thread priority 0-3 (default 1) and frame queue
    // depths [2,1024] (default 3) — shorter queue = less lag.
    public const uint OPTION_THREAD_PRIORITY = 0x02;
    public const uint OPTION_FRAME_DEQUE_LENGTH = 0x37;
    public const uint OPTION_BACKEND_DEQUE_LENGTH = 0x41;
    public const uint OPTION_AUTOEXP_THRESHOLD = 0x29;
    public const uint OPTION_AUTOEXPOSURE_PERCENT = 0x4a;
    public const uint OPTION_AUTOEXP_EXPOTIME_DAMP = 0x60;
    public const uint OPTION_AUTOEXP_GAIN_DAMP = 0x61;

    [DllImport(Dll)]
    public static extern int Amcam_put_Option(IntPtr h, uint option, int value);

    [DllImport(Dll)]
    public static extern int Amcam_get_Option(IntPtr h, uint option, out int value);

    // Frame speed level (USB bandwidth); range [0, get_MaxSpeed()].
    [DllImport(Dll)]
    public static extern ushort Amcam_get_MaxSpeed(IntPtr h);

    [DllImport(Dll)]
    public static extern int Amcam_put_Speed(IntPtr h, ushort speed);

    // Auto-exposure limits: time in microseconds, gain in percent.
    [DllImport(Dll)]
    public static extern int Amcam_put_MaxAutoExpoTimeAGain(IntPtr h, uint maxTimeUs, ushort maxGain);

    // Auto-exposure target brightness [16, 220], default 120.
    [DllImport(Dll)]
    public static extern int Amcam_put_AutoExpoTarget(IntPtr h, ushort target);

    [DllImport(Dll)]
    public static extern int Amcam_get_AutoExpoTarget(IntPtr h, out ushort target);

    // Anti-flicker: 0 = 60 Hz AC, 1 = 50 Hz AC, 2 = DC.
    [DllImport(Dll)]
    public static extern int Amcam_put_HZ(IntPtr h, int hz);

    // One-push auto white balance (temp/tint mode); call after streaming
    // has started. Callbacks are optional (pass IntPtr.Zero).
    [DllImport(Dll)]
    public static extern int Amcam_AwbOnce(IntPtr h, IntPtr fnTTProc, IntPtr ctxTT);

    [DllImport(Dll)]
    public static extern int Amcam_get_TempTint(IntPtr h, out int temp, out int tint);

    [DllImport(Dll)]
    public static extern int Amcam_put_TempTint(IntPtr h, int temp, int tint);

    [DllImport(Dll)]
    public static extern int Amcam_put_Gamma(IntPtr h, int gamma);

    [DllImport(Dll)]
    public static extern int Amcam_get_Gamma(IntPtr h, out int gamma);

    [DllImport(Dll)]
    public static extern int Amcam_put_Contrast(IntPtr h, int contrast);

    [DllImport(Dll)]
    public static extern int Amcam_get_Contrast(IntPtr h, out int contrast);

    [DllImport(Dll)]
    public static extern int Amcam_put_Saturation(IntPtr h, int saturation);

    [DllImport(Dll)]
    public static extern int Amcam_get_Saturation(IntPtr h, out int saturation);

    [DllImport(Dll)]
    public static extern int Amcam_get_MaxAutoExpoTimeAGain(IntPtr h, out uint maxTimeUs, out ushort maxGain);

    [DllImport(Dll)]
    public static extern int Amcam_put_HFlip(IntPtr h, int hflip);

    [DllImport(Dll)]
    public static extern int Amcam_put_VFlip(IntPtr h, int vflip);

    [DllImport(Dll)]
    public static extern int Amcam_Pause(IntPtr h, int pause);

    // Realtime mode 1 = drop pending frames, always show the newest one.
    [DllImport(Dll)]
    public static extern int Amcam_put_RealTime(IntPtr h, int val);

    /// <summary>Enumerate connected cameras.</summary>
    public static DeviceV2[] Enumerate()
    {
        // The SDK contract is an array of AMCAM_MAX (128) entries.
        var devices = new DeviceV2[128];
        uint count = Amcam_EnumV2(devices);
        return devices.Take((int)count).ToArray();
    }

    public static bool Ok(int hresult) => hresult >= 0;
}
