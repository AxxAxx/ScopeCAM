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
    public const uint EVENT_IMAGE = 0x0004;        // a live frame is ready to pull
    public const uint EVENT_ERROR = 0x0080;        // generic error, operation stopped
    public const uint EVENT_DISCONNECTED = 0x0081; // camera unplugged

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
