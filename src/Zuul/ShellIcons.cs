using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Zuul;

// The icon Explorer would show for an app or a file, at the size asked for.
internal static class ShellIcons
{
    // Null entries remember failures, so a missing icon isn't looked up on every keystroke.
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Get(string parsingName, int size)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (Cache.TryGetValue(parsingName, out var cached)) return cached;
        Bitmap? icon = null;
        try { icon = Load(parsingName, size); }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { }
        return Cache[parsingName] = icon;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Bitmap? Load(string parsingName, int size)
    {
        var id = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref id, out var factory) != 0 || factory == null) return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, 0, out var handle) != 0 || handle == IntPtr.Zero) return null;
            try { return ToBitmap(handle); }
            finally { DeleteObject(handle); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    private static Bitmap? ToBitmap(IntPtr handle)
    {
        var info = new BitmapStruct();
        if (GetObject(handle, Marshal.SizeOf<BitmapStruct>(), ref info) == 0 || info.Width <= 0 || info.Height <= 0) return null;
        var width = info.Width;
        var height = info.Height;
        // A negative height asks for rows top to bottom, the way Avalonia wants them.
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32
        };
        var pixels = new byte[width * height * 4];
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, handle, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }

        // Old icons come without alpha; treat them as solid rather than invisible.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
        if (!hasAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pinned.AddrOfPinnedObject(),
                new PixelSize(width, height), new Vector(96, 96), width * 4);
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapStruct
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid id,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref BitmapStruct bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
