// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;

namespace RNSClipboardProbe;

internal static class Clipboard {
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    public static bool TrySetText(string text) {
        for (var attempt = 0; attempt < 5; attempt++) {
            if (!OpenClipboard(IntPtr.Zero)) continue;
            IntPtr memory = IntPtr.Zero;
            var handedOff = false;
            try {
                if (!EmptyClipboard()) return false;
                var chars = text.ToCharArray();
                var bytes = (nuint) (chars.Length + 1) * sizeof(char);
                memory = GlobalAlloc(GmemMoveable | GmemZeroInit, bytes);
                if (memory == IntPtr.Zero) return false;
                var locked = GlobalLock(memory);
                if (locked == IntPtr.Zero) return false;
                try {
                    Marshal.Copy(chars, 0, locked, chars.Length);
                } finally {
                    GlobalUnlock(memory);
                }
                if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) return false;
                handedOff = true;
                return true;
            } finally {
                if (!handedOff && memory != IntPtr.Zero) GlobalFree(memory);
                CloseClipboard();
            }
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
