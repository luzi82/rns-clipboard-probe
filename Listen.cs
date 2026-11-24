// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;
using Reloaded.Mod.Interfaces.Internal;

namespace RNSClipboardProbe;

internal static class ProbeListen {
    private static readonly object Gate = new();
    private static TcpListener? _listener;
    private static Thread? _thread;
    private static volatile bool _stop;
    private static Pending? _pending;

    internal sealed class Pending {
        private int _state; // 0 queued, 1 executing, 2 cancelled, 3 completed
        public required string Text { get; init; }
        public string Reply { get; set; } = "";
        public ManualResetEventSlim Done { get; } = new(false);
        public bool TryBegin() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public void Cancel() => Interlocked.CompareExchange(ref _state, 2, 0);
        public void Finish() => Interlocked.Exchange(ref _state, 3);
    }

    public static void Start(int port, ILoggerV1 logger) {
        lock (Gate) {
            if (_thread != null) return;
            _stop = false;
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _listener = listener;
            _thread = new Thread(() => AcceptLoop(listener, logger)) {
                IsBackground = true,
                Name = "RNSClipboardProbe.Listen",
            };
            _thread.Start();
        }
        logger.PrintMessage("RNS clipboard probe listening on 127.0.0.1:" + port.ToString() + ".", logger.ColorGreen);
    }

    public static void Stop() {
        Thread? thread;
        lock (Gate) {
            _stop = true;
            try {
                _listener?.Stop();
            } catch {
                // Stopping an unstarted or already closed socket is the shutdown path.
            }
            thread = _thread;
            _listener = null;
            _thread = null;
        }
        thread?.Join(200);
    }

    public static Pending? Peek() {
        var item = Volatile.Read(ref _pending);
        return item == null || item.Done.IsSet ? null : item;
    }

    public static void Complete(Pending item, string reply) {
        var text = reply.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0) text = "error read";
        item.Reply = text + "\n";
        item.Finish();
        item.Done.Set();
        Interlocked.CompareExchange(ref _pending, null, item);
    }

    private static void AcceptLoop(TcpListener listener, ILoggerV1 logger) {
        while (!_stop) {
            TcpClient client;
            try {
                client = listener.AcceptTcpClient();
            } catch {
                break;
            }
            try {
                Serve(client);
            } catch (Exception ex) {
                logger.PrintMessage("RNS clipboard probe query failed: " + ex.Message, logger.ColorRed);
            } finally {
                client.Close();
            }
        }
    }

    private static void Serve(TcpClient client) {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address)) return;
        client.ReceiveTimeout = 2000;
        client.SendTimeout = 10000;
        client.NoDelay = true;
        var stream = client.GetStream();
        var buffer = new byte[65536];
        var used = 0;
        while (used < buffer.Length) {
            var n = stream.Read(buffer, used, 1);
            if (n <= 0) return;
            used += n;
            if (buffer[used - 1] == '\n') break;
        }
        if (used == 0 || buffer[used - 1] != '\n') {
            Write(stream, "{\"ok\":false,\"error\":{\"code\":\"request_too_large\",\"message\":\"Send one UTF-8 line of at most 65536 bytes including newline.\"}}\n");
            return;
        }
        string line;
        try { line = new UTF8Encoding(false, true).GetString(buffer, 0, used).Trim(); }
        catch (DecoderFallbackException) { Write(stream, "{\"ok\":false,\"error\":{\"code\":\"invalid_utf8\"}}\n"); return; }
        var pending = new Pending { Text = line };
        if (Interlocked.CompareExchange(ref _pending, pending, null) != null) {
            Write(stream, "{\"ok\":false,\"error\":{\"code\":\"busy\"}}\n");
            return;
        }
        if (!pending.Done.Wait(TimeSpan.FromSeconds(15))) {
            pending.Cancel();
            Interlocked.CompareExchange(ref _pending, null, pending);
            Write(stream, "{\"ok\":false,\"error\":{\"code\":\"timeout\",\"message\":\"Queued requests are cancelled; an executing screenshot may still finish.\"}}\n");
            return;
        }
        Write(stream, pending.Reply);
    }

    private static void Write(NetworkStream stream, string text) {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 4194304) bytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":{\"code\":\"response_too_large\",\"message\":\"Use pagination or smaller batches.\"}}\n");
        stream.Write(bytes, 0, bytes.Length);
    }
}
