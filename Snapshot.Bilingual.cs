// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;

namespace RNSClipboardProbe;

internal static unsafe partial class Snapshot {
    private static F8Catalog? f8Catalog;

    private static string BilingualBrief(IRNSReloaded rns) {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
        var catalog = f8Catalog ??= new F8Catalog(Path.Combine(dir, "Data"));
        var cache = new Dictionary<string, RValue?>();
        RValue? Value(string path) {
            if (cache.TryGetValue(path, out var found)) return found;
            try { found = Resolve(rns, QuerySyntax.Parse(path)); }
            catch (QueryFailure) { found = null; }
            cache[path] = found;
            return found;
        }
        double? Num(string path) {
            var v = Value(path);
            return v is RValue n && n.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64 ? n.ToDouble() : null;
        }
        string Text(string path) => Value(path) is RValue v && v.Type == RValueType.String ? QueryString(rns, v) : "";
        double[] NumericArray(string path) {
            if (Value(path) is not RValue array || array.Type != RValueType.Array) return [];
            var length = ArrayLength(rns, array);
            if (length is < 0 or > 64) return [];
            var numbers = new double[length];
            for (var i = 0; i < length; i++) {
                if (TryIndex(rns, array, i, out var cell) && cell.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) numbers[i] = cell.ToDouble();
            }
            return numbers;
        }
        var source = new F8Source(Num, Text, NumericArray, path => Value(path) is RValue v ? v.ToBool() : null);
        return new F8Renderer(catalog).Render(F8Capture.Capture(source, catalog));
    }
}
