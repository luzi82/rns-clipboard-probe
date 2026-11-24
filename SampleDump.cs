// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace RNSClipboardProbe;

internal readonly struct SavedSample {
    public string TextPath { get; init; }
    public string ImagePath { get; init; }
    public string? ImageError { get; init; }
}

internal static class SampleDump {
    private static readonly string SampleRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RNSClipboardProbe", "samples");

    public static SavedSample Save(string text) {
        var now = DateTime.Now;
        var folder = Path.Combine(SampleRoot, now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = stamp;
        for (var n = 2; File.Exists(Path.Combine(folder, name + ".txt")) || File.Exists(Path.Combine(folder, name + ".png")); n++) {
            name = stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
        }
        var textPath = Path.Combine(folder, name + ".txt");
        var imagePath = Path.Combine(folder, name + ".png");
        File.WriteAllText(textPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string? imageError = null;
        try {
            WindowPng.Save(imagePath);
        } catch (Exception ex) {
            imageError = ex.Message;
        }
        return new SavedSample { TextPath = textPath, ImagePath = imagePath, ImageError = imageError };
    }
}

internal static class WindowPng {
    public static void Save(string path) {
        var hwnd = LargestWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("game window was not found");
        if (!TryRect(hwnd, out var rect)) throw new InvalidOperationException("game window has no size");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width is < 32 or > 8192 || height is < 32 or > 8192) {
            throw new InvalidOperationException("game window size is " + width + "x" + height);
        }
        var pixels = Capture(hwnd, rect.Left, rect.Top, width, height);
        var png = Encode(pixels, width, height);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(png);
    }

    private static byte[] Capture(IntPtr hwnd, int left, int top, int width, int height) {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try {
            var previous = SelectObject(memory, bitmap);
            var printed = PrintWindow(hwnd, memory, 2);
            SelectObject(memory, previous);
            var pixels = ReadPixels(screen, bitmap, width, height);
            if (printed && !MostlyBlack(pixels)) return pixels;
            // Screen-copy fallback is only valid when the game's own window is foreground.
            if (GetForegroundWindow() != hwnd) throw new InvalidOperationException("Game capture failed; bring the game to the foreground and retry.");
            previous = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020);
            SelectObject(memory, previous);
            pixels = ReadPixels(screen, bitmap, width, height);
            if (!copied || MostlyBlack(pixels)) throw new InvalidOperationException("Game capture returned an empty image; retry after the screen changes.");
            return pixels;
        } finally {
            DeleteObject(bitmap);
            DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] ReadPixels(IntPtr screen, IntPtr bitmap, int width, int height) {
        var info = new BitmapInfo {
            Size = 40,
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var lines = GetDIBits(screen, bitmap, 0, height, pixels, ref info, 0);
        if (lines == 0) throw new InvalidOperationException("GetDIBits failed");
        return pixels;
    }

    private static bool MostlyBlack(byte[] pixels) {
        if (pixels.Length < 4) return true;
        var dark = 0;
        var seen = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 64) {
            seen++;
            if (pixels[i] < 8 && pixels[i + 1] < 8 && pixels[i + 2] < 8) dark++;
        }
        return seen > 0 && dark * 100 / seen >= 95;
    }

    private static byte[] Encode(byte[] bgra, int width, int height) {
        var stride = width * 4;
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++) {
            var destination = y * (1 + width * 3);
            var source = y * stride;
            var offset = destination + 1;
            for (var x = 0; x < width; x++) {
                raw[offset++] = bgra[source + 2];
                raw[offset++] = bgra[source + 1];
                raw[offset++] = bgra[source];
                source += 4;
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) {
            zlib.Write(raw);
        }
        var payload = compressed.ToArray();
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        WriteBe(header, 0, width);
        WriteBe(header, 4, height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, payload);
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, byte[] data) {
        WriteBe(output, (uint) data.Length);
        output.Write(type);
        output.Write(data);
        var signed = new byte[type.Length + data.Length];
        type.CopyTo(signed);
        data.CopyTo(signed, type.Length);
        WriteBe(output, Crc(signed));
    }

    private static uint Crc(ReadOnlySpan<byte> data) {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data) {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteBe(byte[] buffer, int offset, int value) {
        buffer[offset] = (byte) (value >> 24);
        buffer[offset + 1] = (byte) (value >> 16);
        buffer[offset + 2] = (byte) (value >> 8);
        buffer[offset + 3] = (byte) value;
    }

    private static void WriteBe(Stream output, uint value) {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte) (value >> 24);
        bytes[1] = (byte) (value >> 16);
        bytes[2] = (byte) (value >> 8);
        bytes[3] = (byte) value;
        output.Write(bytes);
    }

    private static IntPtr LargestWindow() {
        var process = Environment.ProcessId;
        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumProc visit = (hwnd, _) => {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != process || !IsWindowVisible(hwnd)) return true;
            if (!TryRect(hwnd, out var rect)) return true;
            var area = (long) (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea) {
                bestArea = area;
                best = hwnd;
            }
            return true;
        };
        EnumWindows(visit, IntPtr.Zero);
        return best;
    }

    private static bool TryRect(IntPtr hwnd, out Rect rect) {
        if (DwmGetWindowAttribute(hwnd, 9, out rect, Marshal.SizeOf<Rect>()) == 0
            && rect.Right > rect.Left && rect.Bottom > rect.Top) {
            return true;
        }
        return GetWindowRect(hwnd, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, int start, int lines, byte[] bits, ref BitmapInfo info, int usage);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);
}
