using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AirPrinter;

internal static class WindowGlass
{
    public static void Apply(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget != null)
        {
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        uint tint = Theme.IsDark ? 0xB8201E1Eu : 0x99FAF7F5u;
        if (!TryAccent(handle, 4, tint))
        {
            _ = TryAccent(handle, 3, tint);
        }
    }

    private static bool TryAccent(nint handle, int state, uint tint)
    {
        var accent = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = 2,
            GradientColor = tint,
        };
        int size = Marshal.SizeOf<AccentPolicy>();
        nint data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var composition = new WindowCompositionAttributeData
            {
                Attribute = 19,
                Data = data,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(handle, ref composition) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
