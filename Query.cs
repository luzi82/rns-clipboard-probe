// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;
namespace RNSClipboardProbe;

internal static unsafe partial class Snapshot {
    internal const string ProtocolVersion = "2.0";
    private const int MaxPage = 256;
    private sealed class QueryFailure(string code, string message) : Exception(message) {
        public string Code { get; } = code;
    }
    private sealed class ReadBudget { public int Remaining = 4096; }
    private sealed record ValueTypeInfo(string Type, string RawType, string? EngineType = null, string? Handle = null);

    public static string Answer(IRNSReloaded rns, string query) {
        query = query.Trim();
        try {
            if (query.StartsWith('{') || query.StartsWith('[')) {
                using var doc = JsonDocument.Parse(query, new JsonDocumentOptions { MaxDepth = 16 });
                return Encode(Dispatch(rns, doc.RootElement));
            }
            if (query is "help" or "help()") return Encode(Help());
            if (query.StartsWith("screenshot(", StringComparison.Ordinal) && query.EndsWith(')')) {
                var arg = query[11..^1].Trim();
                var path = arg.StartsWith('"') ? JsonSerializer.Deserialize<string>(arg) : arg;
                return Encode(CaptureQuery(path ?? ""));
            }
            var value = Resolve(rns, QuerySyntax.Parse(query));
            return Present(value) ? Encode(Export(rns, value, 4, new ReadBudget())) : "missing";
        } catch (Exception ex) {
            if (!(query.StartsWith('{') || query.StartsWith('[')) && ex is QueryFailure { Code: "missing" }) return "missing";
            return Encode(Failure(ex));
        }
    }

    private static string Encode(object? value) => JsonSerializer.Serialize(value);
    private static object Failure(Exception ex) => new {
        ok = false,
        error = new { code = ex is QueryFailure q ? q.Code : ex is FormatException or JsonException ? "invalid_request" : "read_failed", message = ex.Message },
    };

    private static object Dispatch(IRNSReloaded rns, JsonElement request) {
        try {
            if (request.ValueKind == JsonValueKind.Array) return Batch(rns, request);
            if (request.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON request object.");
            var op = StringOption(request, "op", "read");
            if (op == "help") return Help();
            if (op == "selftest") return SelfTest(rns);
            if (op == "batch") {
                if (!request.TryGetProperty("queries", out var queries)) throw new FormatException("Missing 'queries'.");
                return Batch(rns, queries);
            }
            if (op == "screenshot") return CaptureQuery(StringOption(request, "path"));
            var path = StringOption(request, "path");
            var current = Resolve(rns, QuerySyntax.Parse(path));
            var info = TypeInfo(rns, current);
            var depth = IntOption(request, "depth", 2, 0, 6);
            if (op == "inspect") return new { ok = true, path, type = info.Type, rawType = info.RawType, engineType = info.EngineType, handle = info.Handle, isStruct = info.Type == "struct", length = LengthOrNull(rns, current, info) };
            if (op == "read") return new { ok = true, path, type = info.Type, rawType = info.RawType, value = Export(rns, current, depth, new ReadBudget()) };
            if (op is "page" or "keys") return Page(rns, current, path, info, op == "keys", IntOption(request, "offset", 0, 0, int.MaxValue), IntOption(request, "limit", 64, 1, MaxPage), depth);
            throw new FormatException("Unknown operation: " + op);
        } catch (Exception ex) { return Failure(ex); }
    }

    private static object Batch(IRNSReloaded rns, JsonElement requests) {
        if (requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() is < 1 or > 64) throw new FormatException("Batch must contain 1 to 64 requests.");
        var results = new List<object>();
        foreach (var r in requests.EnumerateArray()) {
            try {
                if (r.ValueKind != JsonValueKind.Object || StringOption(r, "op", "read") == "batch") results.Add(Failure(new FormatException("Nested batches are not supported.")));
                else results.Add(Dispatch(rns, r));
            } catch (Exception ex) { results.Add(Failure(ex)); }
        }
        return new { ok = true, results };
    }
    private static string StringOption(JsonElement r, string key, string? fallback = null) {
        if (!r.TryGetProperty(key, out var v)) return fallback ?? throw new FormatException("Missing '" + key + "'.");
        return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new FormatException("'" + key + "' must be a string.");
    }
    private static int IntOption(JsonElement r, string key, int fallback, int min, int max) {
        if (!r.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max) throw new FormatException($"'{key}' must be between {min} and {max}.");
        return n;
    }

    private static RValue Resolve(IRNSReloaded rns, QueryPath path) {
        RValue current;
        if (path.Global) {
            var pointer = rns.FindGlobalValue(path.Root);
            if (pointer == null) throw new QueryFailure("missing", "Global variable not found: " + path.Root);
            current = *pointer;
        } else current = QueryInstance(rns, path.Root, path.InstanceIndex);
        foreach (var step in path.Steps) {
            if (step.Kind == PathKind.Field) {
                var info = TypeInfo(rns, current);
                // Reserved metadata. Bracket strings access literal fields with the same names.
                current = step.Name switch {
                    "Type" => new RValue(info.Type),
                    "RawType" => new RValue(info.RawType),
                    "Length" => new RValue(LengthOrNull(rns, current, info) ?? throw new QueryFailure("wrong_type", "Length requires an array, string, struct, instance, or typed ds_map.")),
                    "Keys" => Keys(rns, current, info),
                    _ => Field(rns, current, step.Name!, info),
                };
            } else if (step.Kind == PathKind.MapKey) {
                var info = TypeInfo(rns, current);
                if (info.Type != "ds_map") throw new QueryFailure("wrong_type", "[?key] requires a confirmed ds_map handle; got " + info.Type + ".");
                current = MapRead(rns, current, step.IsString ? QueryText(rns, step.Name!) : new RValue(step.Number));
            } else if (step.IsString) {
                var info = TypeInfo(rns, current);
                current = info.Type == "ds_map" ? MapRead(rns, current, QueryText(rns, step.Name!)) : Field(rns, current, step.Name!, info);
            } else {
                if (current.Type != RValueType.Array) throw new QueryFailure("wrong_type", "Numeric [index] requires an array; use [?number] for a map key.");
                if (step.Number < 0 || step.Number > int.MaxValue || step.Number != Math.Truncate(step.Number)) throw new QueryFailure("bad_index", "Array index must be a nonnegative integer.");
                if (!TryIndex(rns, current, (int)step.Number, out var cell)) throw new QueryFailure("missing", "Array index out of range.");
                current = cell;
            }
        }
        return current;
    }

    private static RValue Field(IRNSReloaded rns, RValue current, string name, ValueTypeInfo info) {
        var key = QueryText(rns, name);
        if (info.Type == "struct") {
            if (!CallBool(rns, "variable_struct_exists", [current, key])) throw new QueryFailure("missing", "Struct field not found: " + name);
            return Call(rns, "variable_struct_get", [current, key]);
        }
        if (info.Type == "instance") {
            if (!CallBool(rns, "variable_instance_exists", [current, key])) throw new QueryFailure("missing", "Instance field not found: " + name);
            return Call(rns, "variable_instance_get", [current, key]);
        }
        throw new QueryFailure("wrong_type", "Field lookup requires a struct or instance; got " + info.Type + ".");
    }
    private static RValue MapRead(IRNSReloaded rns, RValue map, RValue key) {
        if (!CallBool(rns, "ds_map_exists", [map, key])) throw new QueryFailure("missing", "Map key not found.");
        return Call(rns, "ds_map_find_value", [map, key]);
    }
    private static RValue Call(IRNSReloaded rns, string name, RValue[] args) => rns.ExecuteCodeFunction(name, args) ?? throw new QueryFailure("engine_unavailable", "Engine function unavailable: " + name);
    private static bool CallBool(IRNSReloaded rns, string name, RValue[] args) => Call(rns, name, args).ToBool();

    private static RValue QueryText(IRNSReloaded rns, string text) {
        // RNSReloaded.CreateString uses the Windows ANSI code page. Feed JSON-escaped
        // ASCII through the engine for Unicode keys instead of corrupting their UTF-8.
        return text.All(c => c is > '\0' and < '\u0080') ? new RValue(text)
            : Call(rns, "json_parse", [new RValue(JsonSerializer.Serialize(text))]);
    }

    private static ValueTypeInfo TypeInfo(IRNSReloaded rns, RValue value) {
        var raw = value.Type.ToString();
        // Arbitrary numbers are never probed as DS IDs: resource tables can reuse numeric IDs.
        if (value.Type is not (RValueType.Ref or RValueType.Object)) return new(raw.ToLowerInvariant(), raw);
        var engineType = QueryString(rns, Call(rns, "typeof", [value]));
        if (engineType == "method") return new("method", raw, engineType);
        if (CallBool(rns, "is_struct", [value])) return new("struct", raw, engineType);
        if (value.Type == RValueType.Ref) {
            var handle = QueryString(rns, Call(rns, "string", [value]));
            if (handle.StartsWith("ref ds_map ", StringComparison.Ordinal)) return new("ds_map", raw, engineType, handle);
            if (handle.StartsWith("ref instance ", StringComparison.Ordinal)) return new("instance", raw, engineType, handle);
            return new("ref", raw, engineType, handle);
        }
        return new("instance", raw, engineType);
    }
    private static string QueryString(IRNSReloaded rns, RValue value) {
        if (value.Type != RValueType.String) throw new QueryFailure("wrong_type", "Expected a string from the engine.");
        return rns.GetString(&value);
    }
    private static int? LengthOrNull(IRNSReloaded rns, RValue value, ValueTypeInfo info) => info.Type switch {
        "array" => ArrayLength(rns, value),
        "string" => Call(rns, "string_length", [value]).ToInt(),
        "ds_map" => Call(rns, "ds_map_size", [value]).ToInt(),
        "struct" or "instance" => ArrayLength(rns, Keys(rns, value, info)),
        _ => null,
    };
    private static RValue Keys(IRNSReloaded rns, RValue value, ValueTypeInfo info) {
        var function = info.Type switch {
            "struct" => "variable_struct_get_names",
            "instance" => "variable_instance_get_names",
            "ds_map" => "ds_map_keys_to_array",
            _ => throw new QueryFailure("wrong_type", "Keys requires a struct, instance, or typed ds_map."),
        };
        var keys = Call(rns, function, [value]);
        if (keys.Type != RValueType.Array) throw new QueryFailure("read_failed", "Engine did not return a key array.");
        return keys;
    }

    private static object Page(IRNSReloaded rns, RValue value, string path, ValueTypeInfo info, bool keysOnly, int offset, int limit, int depth) {
        var array = info.Type == "array";
        if (!array && info.Type is not ("struct" or "instance" or "ds_map")) throw new QueryFailure("wrong_type", "Pagination requires an array, struct, instance, or typed ds_map.");
        var keys = array ? default : Keys(rns, value, info);
        var total = ArrayLength(rns, array ? value : keys);
        if (total < 0) throw new QueryFailure("read_failed", "Could not read collection length.");
        var end = (int)Math.Min(total, (long)offset + limit);
        var items = new List<object?>();
        var budget = new ReadBudget();
        for (var i = offset; i < end; i++) {
            RValue key = new(i);
            if (!array && !TryIndex(rns, keys, i, out key)) throw new QueryFailure("read_failed", "Could not read key.");
            if (keysOnly) { items.Add(Export(rns, key, depth, budget)); continue; }
            RValue cell;
            if (array) {
                if (!TryIndex(rns, value, i, out cell)) throw new QueryFailure("read_failed", "Could not read array entry.");
            } else cell = info.Type == "ds_map" ? MapRead(rns, value, key) : Field(rns, value, QueryString(rns, key), info);
            items.Add(new { key = Export(rns, key, depth, budget), value = Export(rns, cell, depth, budget) });
        }
        return new { ok = true, path, type = info.Type, total, offset, count = items.Count, nextOffset = end < total ? (int?)end : null, items };
    }

    private static object? Export(IRNSReloaded rns, RValue value, int depth, ReadBudget budget) {
        if (--budget.Remaining < 0) return new { truncated = true, reason = "node_budget" };
        switch (value.Type) {
            case RValueType.Undefined: case RValueType.Unset: return new { type = "undefined" };
            case RValueType.Null: return null;
            case RValueType.String:
                var text = QueryString(rns, value);
                return text.Length <= 65536 ? text : new { type = "string", length = text.Length, truncated = true, value = text[..65536] };
            case RValueType.Bool: return value.ToBool();
            case RValueType.Int32: return value.Int32;
            case RValueType.Int64: return value.Int64;
            case RValueType.Real: return double.IsFinite(value.Real) ? (object)value.Real : new { type = "real", value = value.Real.ToString(CultureInfo.InvariantCulture) };
            case RValueType.Array:
                var length = ArrayLength(rns, value);
                if (length < 0) throw new QueryFailure("read_failed", "Could not read array length.");
                if (depth == 0 || length > 64) return new { array = length, truncated = length > 0 };
                var cells = new List<object?>();
                for (var i = 0; i < length; i++) {
                    if (!TryIndex(rns, value, i, out var cell)) throw new QueryFailure("read_failed", "Could not read array entry.");
                    cells.Add(Export(rns, cell, depth - 1, budget));
                }
                return cells;
            case RValueType.Ref: case RValueType.Object:
                var info = TypeInfo(rns, value);
                if (info.Type is "struct" or "instance" && depth > 0) {
                    var keys = Keys(rns, value, info);
                    var count = ArrayLength(rns, keys);
                    if (count < 0) throw new QueryFailure("read_failed", "Could not read field count.");
                    if (count <= 64) {
                        var fields = new Dictionary<string, object?>();
                        for (var i = 0; i < count; i++) {
                            if (!TryIndex(rns, keys, i, out var key)) continue;
                            var name = QueryString(rns, key);
                            fields[name] = Export(rns, Field(rns, value, name, info), depth - 1, budget);
                        }
                        return fields;
                    }
                }
                return new { type = info.Type, rawType = info.RawType, handle = info.Handle, length = LengthOrNull(rns, value, info), reference = "0x" + ((ulong)value.Pointer).ToString("X", CultureInfo.InvariantCulture) };
            default: return new { type = value.Type.ToString().ToLowerInvariant() };
        }
    }
    private static RValue QueryInstance(IRNSReloaded rns, string name, int index) {
        var count = Math.Clamp(Call(rns, "instance_number", [AllObjects]).ToInt(), 0, 10000);
        var seen = 0;
        for (var i = 0; i < count; i++) {
            var found = Call(rns, "instance_find", [AllObjects, i]);
            if (!Present(found) || ObjectName(rns, found) != name) continue;
            if (seen++ == index) return found;
        }
        throw new QueryFailure("missing", $"No instance {name}[{index}].");
    }
    private static object CaptureQuery(string path) {
#if PROBE_DIAGNOSTICS
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Screenshot requires an absolute .png path.");
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new QueryFailure("file_exists", "Screenshot will not overwrite an existing file.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WindowPng.Save(path);
        return new { ok = true, path, capturedAt = DateTimeOffset.Now.ToString("O"), bytes = new FileInfo(path).Length };
#else
        throw new QueryFailure("disabled", "Screenshot diagnostics are disabled in this build.");
#endif
    }
    // Exercise native structs and typed map keys without touching game-owned data.
    private static object SelfTest(IRNSReloaded rns) {
        var checks = new List<string>();
        void Check(bool passed, string label) {
            if (!passed) throw new QueryFailure("selftest_failed", label);
            checks.Add(label);
        }
        var fixture = Call(rns, "json_parse", ["{\"Type\":\"literal\",\"Length\":42,\"Keys\":\"literal keys\",\"label\":\"\\u96ea\",\"values\":[1,true,null]}"]);
        var info = TypeInfo(rns, fixture);
        Check(info.Type == "struct", "Native struct detected");
        Check(QueryString(rns, Field(rns, fixture, "Type", info)) == "literal", "Literal Type field preserved");
        Check(Field(rns, fixture, "Length", info).ToInt() == 42, "Literal Length field preserved");
        Check(QueryString(rns, Field(rns, fixture, "Keys", info)) == "literal keys", "Literal Keys field preserved");
        Check(QueryString(rns, Field(rns, fixture, "label", info)) == "雪", "Unicode struct field read");
        Check(QueryString(rns, QueryText(rns, "雪")) == "雪", "Unicode input conversion");
        Check(LengthOrNull(rns, fixture, info) == 5, "Struct field count");
        var array = Field(rns, fixture, "values", info);
        Check(array.Type == RValueType.Array && ArrayLength(rns, array) == 3, "Nested struct array");
        var map = Call(rns, "ds_map_create", []);
        try {
            Check(TypeInfo(rns, map).Type == "ds_map", "Typed ds_map detected");
            Call(rns, "ds_map_add", [map, 15, "numeric"]);
            Call(rns, "ds_map_add", [map, "15", "string"]);
            Call(rns, "ds_map_add", [map, -1.5, "fraction"]);
            Call(rns, "ds_map_add", [map, QueryText(rns, "雪"), "unicode"]);
            Check(QueryString(rns, MapRead(rns, map, 15)) == "numeric", "Numeric map key read");
            Check(QueryString(rns, MapRead(rns, map, "15")) == "string", "String key distinct from number");
            Check(QueryString(rns, MapRead(rns, map, -1.5)) == "fraction", "Negative fractional map key read");
            Check(QueryString(rns, MapRead(rns, map, QueryText(rns, "雪"))) == "unicode", "Unicode map key read");
            Check(LengthOrNull(rns, map, TypeInfo(rns, map)) == 4, "Map size");
            Check(ArrayLength(rns, Keys(rns, map, TypeInfo(rns, map))) == 4, "Map keys enumerated");
        } finally { Call(rns, "ds_map_destroy", [map]); }
        return new { ok = true, checks, fixtureType = info };
    }
    private static object Help() => new {
        ok = true, protocol = ProtocolVersion, readOnly = true,
        operations = new[] { "read", "inspect", "keys", "page", "batch", "screenshot", "help", "selftest" },
        pathExamples = new[] { "global.itemCd[6][0].Length", "global.squareMap.Type", "global.squareMap.RawType", "global.squareMap.Keys", "global.squareMap[?15]", "global.languageMap[?\"itemset_daynight\"]", "obj_itemSystem[0].slots", "global.someStruct[\"Type\"]" },
        limits = new { maxRequestBytes = 65536, maxBatch = 64, maxPage = MaxPage, maxDepth = 6, maxResponseBytes = 4194304 },
        screenshot = "screenshot(\"D:/absolute/path.png\")",
    };
}
