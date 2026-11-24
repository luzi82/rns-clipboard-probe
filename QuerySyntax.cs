// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
namespace RNSClipboardProbe;
internal enum PathKind { Field, Index, MapKey }
internal sealed record PathStep(PathKind Kind, string? Name = null, double Number = 0, bool IsString = false);
internal sealed record QueryPath(bool Global, string Root, int InstanceIndex, List<PathStep> Steps);
// Data paths only, never GML evaluation. [?key] is an explicit map lookup.
internal static class QuerySyntax {
    public static QueryPath Parse(string text) {
        if (text.Length is 0 or > 4096) throw new FormatException("Path must contain 1 to 4096 characters.");
        var i = 0;
        bool global = text.StartsWith("global.", StringComparison.Ordinal);
        if (global) i = 7;
        string Name() {
            var start = i;
            if (i == text.Length || !(char.IsAsciiLetter(text[i]) || text[i] == '_')) throw new FormatException("Expected a field name.");
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
            return text[start..i];
        }
        var root = Name();
        var steps = new List<PathStep>();
        var instance = 0;
        var first = true;
        while (i < text.Length) {
            if (steps.Count >= 64) throw new FormatException("Path exceeds 64 steps.");
            if (text[i] == '.') {
                i++;
                steps.Add(new(PathKind.Field, Name()));
            } else if (text[i] == '[') {
                i++;
                var map = i < text.Length && text[i] == '?';
                if (map) i++;
                while (i < text.Length && text[i] == ' ') i++;
                PathStep step;
                if (i < text.Length && text[i] == '"') {
                    var start = i++;
                    while (i < text.Length) {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i++] == '"') break;
                    }
                    var key = JsonSerializer.Deserialize<string>(text[start..Math.Min(i, text.Length)]) ?? "";
                    step = new(map ? PathKind.MapKey : PathKind.Index, key, IsString: true);
                } else {
                    var start = i;
                    while (i < text.Length && text[i] != ']' && text[i] != ' ') i++;
                    if (!double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)) throw new FormatException("Expected a finite numeric key or JSON quoted string.");
                    step = new(map ? PathKind.MapKey : PathKind.Index, Number: n);
                }
                while (i < text.Length && text[i] == ' ') i++;
                if (i == text.Length || text[i++] != ']') throw new FormatException("Expected closing bracket.");
                if (first && !global) {
                    if (step.Kind != PathKind.Index || step.IsString || step.Number < 0 || step.Number > 100000 || step.Number != Math.Truncate(step.Number)) throw new FormatException("Object selector must be a nonnegative instance index.");
                    instance = (int)step.Number;
                } else steps.Add(step);
            } else throw new FormatException("Expected '.' or '['.");
            first = false;
        }
        return new(global, root, instance, steps);
    }
}
