// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;

namespace RNSClipboardProbe;

public class Mod : IMod {
    public const int HotkeyVirtualKey = 0x77; // F8
#if PROBE_DIAGNOSTICS
    public const int SaveHotkeyVirtualKey = 0x78; // F9, temporary sample dump

    // Switch the localhost query socket off by setting this to false.
    public const bool TcpListen = true;
    public const int TcpPort = 47731;
#endif

    private ILoggerV1? _logger;
    private IRNSReloaded? _rns;
    private int _started;
    private int _ready;
    private int _busy;
    private bool _f8Down;
#if PROBE_DIAGNOSTICS
    private bool _f9Down;
#endif
    private long _nextPoll;

    public void Start(IModLoaderV1 loader) => StartEx(loader, null!);

    public void StartEx(IModLoaderV1 loader, IModConfigV1 config) {
        if (Interlocked.Exchange(ref this._started, 1) != 0) return;
        this._logger = loader.GetLogger();
        var rnsRef = loader.GetController<IRNSReloaded>();
        if (rnsRef == null || !rnsRef.TryGetTarget(out var rns)) {
            this._logger.PrintMessage(
                "RNS clipboard probe: RNSReloaded is not loaded. Enable the RNSReloaded mod, then launch again.",
                this._logger.ColorRed
            );
            return;
        }
        this._rns = rns;
        rns.OnReady += this.Ready;
    }

    public void Suspend() { }
    public void Resume() { }
    public void Unload() { }
    public bool CanUnload() => false;
    public bool CanSuspend() => false;

    public Action Disposing => () => {
#if PROBE_DIAGNOSTICS
        ProbeListen.Stop();
#endif
        if (this._rns != null) {
            this._rns.OnReady -= this.Ready;
            this._rns.OnExecuteIt -= this.OnExecute;
        }
    };

    private void Ready() {
        if (Interlocked.Exchange(ref this._ready, 1) != 0) return;
        var rns = this._rns;
        var logger = this._logger;
        if (rns == null || logger == null) return;

        this._f8Down = (GetAsyncKeyState(HotkeyVirtualKey) & 0x8000) != 0;
#if PROBE_DIAGNOSTICS
        this._f9Down = (GetAsyncKeyState(SaveHotkeyVirtualKey) & 0x8000) != 0;
#endif
        rns.OnExecuteIt += this.OnExecute;
#if PROBE_DIAGNOSTICS
        if (TcpListen) {
            try {
                ProbeListen.Start(TcpPort, logger);
            } catch (Exception ex) {
                logger.PrintMessage("RNS clipboard probe listener failed: " + ex.Message, logger.ColorRed);
            }
        }
#endif
        logger.PrintMessage(
            "RNS clipboard probe ready. Press F8 to copy bilingual abilities, carried items, shop/loot choices and run stats.",
            logger.ColorGreen
        );
    }

    private void OnExecute(ExecuteItArguments args) {
        if (this._ready == 0 || Volatile.Read(ref this._busy) != 0) return;
        var now = Environment.TickCount64;
        if (now < this._nextPoll) return;
        this._nextPoll = now + 32;

#if PROBE_DIAGNOSTICS
        var pending = ProbeListen.Peek();
#endif
        var f8Down = (GetAsyncKeyState(HotkeyVirtualKey) & 0x8000) != 0;

        var f8 = f8Down && !this._f8Down;

        this._f8Down = f8Down;
#if PROBE_DIAGNOSTICS
        var f9Down = (GetAsyncKeyState(SaveHotkeyVirtualKey) & 0x8000) != 0;
        var f9 = f9Down && !this._f9Down;
        this._f9Down = f9Down;
        if (pending == null && !f8 && !f9) return;
#else
        if (!f8) return;
#endif
        if (this._rns == null || this._logger == null) {
#if PROBE_DIAGNOSTICS
            if (pending != null) ProbeListen.Complete(pending, "error not ready");
#endif
            return;
        }

        if (Interlocked.Exchange(ref this._busy, 1) != 0) return;
        try {
#if PROBE_DIAGNOSTICS
            if (pending != null && pending.TryBegin()) {
                string reply;
                try {
                    reply = Snapshot.Answer(this._rns, pending.Text);
                } catch (Exception ex) {
                    reply = "error " + ex.GetType().Name;
                }
                ProbeListen.Complete(pending, reply);
            }
#endif
            if (f8) {
                var note = Snapshot.Brief(this._rns);
                if (Clipboard.TrySetText(note)) {
                    this._logger.PrintMessage($"RNS clipboard probe copied {note.Length} characters.", this._logger.ColorGreen);
                } else {
                    this._logger.PrintMessage("RNS clipboard probe could not open the clipboard.", this._logger.ColorRed);
                }
            }
#if PROBE_DIAGNOSTICS
            if (f9) this.SaveSample(Snapshot.Build(this._rns));
#endif
        } catch (Exception ex) {
            var failure = "RNS clipboard probe failed:\n" + ex;
            if (f8) Clipboard.TrySetText(failure);
            this._logger.PrintMessage("RNS clipboard probe failed: " + ex.Message, this._logger.ColorRed);
        } finally {
            Volatile.Write(ref this._busy, 0);
        }
    }

#if PROBE_DIAGNOSTICS
    private void SaveSample(string text) {
        var logger = this._logger;
        if (logger == null) return;
        try {
            var saved = SampleDump.Save(text);
            if (saved.ImageError == null) {
                logger.PrintMessage("RNS clipboard probe saved " + saved.TextPath + " and " + saved.ImagePath + ".", logger.ColorGreen);
            } else {
                logger.PrintMessage("RNS clipboard probe saved " + saved.TextPath + " but the screenshot failed: " + saved.ImageError, logger.ColorRed);
            }
        } catch (Exception ex) {
            logger.PrintMessage("RNS clipboard probe save failed: " + ex.Message, logger.ColorRed);
        }
    }

#endif

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
