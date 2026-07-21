using System.Runtime.InteropServices;
using BubblesBot.Bot.Overlay.Native;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Small GDI capture helper for visual live-test oracles.</summary>
internal static class ClientRegionCapture
{
    private const uint SrcCopy = 0x00CC0020;

    public static bool TryCapture(
        WindowInfo window,
        int regionX,
        int regionY,
        int regionWidth,
        int regionHeight,
        out byte[] pixels,
        out string error)
    {
        pixels = [];
        error = string.Empty;
        if (regionWidth <= 0 || regionHeight <= 0
            || regionX < 0 || regionY < 0
            || regionX + regionWidth > window.Width
            || regionY + regionHeight > window.Height)
        {
            error = "capture region is outside the client";
            return false;
        }

        nint screenDc = 0, memoryDc = 0, bitmap = 0, previous = 0;
        try
        {
            screenDc = OverlayNative.GetDC(0);
            if (screenDc == 0) { error = "GetDC failed"; return false; }
            memoryDc = OverlayNative.CreateCompatibleDC(screenDc);
            if (memoryDc == 0) { error = "CreateCompatibleDC failed"; return false; }

            var info = new OverlayNative.BITMAPINFO
            {
                bmiHeader = new OverlayNative.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<OverlayNative.BITMAPINFOHEADER>(),
                    biWidth = regionWidth,
                    biHeight = -regionHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = OverlayNative.BI_RGB,
                },
            };
            bitmap = OverlayNative.CreateDIBSection(
                memoryDc, ref info, OverlayNative.DIB_RGB_COLORS, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) { error = "CreateDIBSection failed"; return false; }
            previous = OverlayNative.SelectObject(memoryDc, bitmap);

            if (!OverlayNative.BitBlt(
                    memoryDc, 0, 0, regionWidth, regionHeight,
                    screenDc, window.OriginX + regionX, window.OriginY + regionY, SrcCopy))
            {
                error = $"BitBlt failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            OverlayNative.GdiFlush();
            pixels = new byte[regionWidth * regionHeight * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return true;
        }
        finally
        {
            if (memoryDc != 0 && previous != 0) OverlayNative.SelectObject(memoryDc, previous);
            if (bitmap != 0) OverlayNative.DeleteObject(bitmap);
            if (memoryDc != 0) OverlayNative.DeleteDC(memoryDc);
            if (screenDc != 0) OverlayNative.ReleaseDC(0, screenDc);
        }
    }
}
