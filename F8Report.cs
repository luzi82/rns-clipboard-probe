// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace RNSClipboardProbe;

internal sealed class F8Entry {
    public int Id { get; init; }
    public int Slot { get; init; }
    public string Key { get; init; } = "";
    public string LiveName { get; init; } = "";
    public string LiveTemplate { get; init; } = "";
    public double[] Cd { get; init; } = [];
    public double[] Hb { get; init; } = [];
    public double[]? AdjustedCd { get; set; }
    public double[]? AdjustedHb { get; set; }
    public double? Damage { get; set; }
    public double? Stock { get; set; }
    public double? Price { get; set; }
    public double? Counter { get; set; }
    public F8Entry? OtherFace { get; set; }
    public F8Entry? Upgrade { get; set; }
}

internal sealed class F8Run {
    public int Language { get; init; }
    public double? Hp { get; init; }
    public double? MaxHp { get; init; }
    public double? Gold { get; init; }
    public double? Level { get; init; }
    public int? Difficulty { get; init; }
    public string ClassKey { get; init; } = "";
    public int? Stage { get; init; }
    public string StageKey { get; init; } = "";
    public List<F8Entry> Skills { get; } = [];
    public List<F8Entry> Carried { get; } = [];
    public List<F8Entry> Shop { get; } = [];
    public List<F8Entry> Loot { get; } = [];
    public bool ShopOpen { get; init; }
    public bool LootOpen { get; init; }
}

// Read the shipped translations by key; no language switching or translated guesses.
internal sealed class F8Catalog {
    private readonly Dictionary<string, string[]> names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> descriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> phrases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> titles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> statusAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> statusDescriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> statusNames = new(StringComparer.Ordinal);

    public F8Catalog(string dataDirectory) {
        foreach (var file in new[] { "Names_Move.csv", "Names_Item.csv", "Names_Potion.csv" })
            Load(file, true, names);
        foreach (var file in new[] { "Descriptions_Move.csv", "Descriptions_Item.csv", "Descriptions_Potion.csv" })
            Load(file, true, descriptions);
        foreach (var file in new[] { "Strings.csv", "Strings2.csv", "Strings_Menu.csv" })
            Load(file, false, phrases);
        Load("Titles_Character.csv", true, titles, last: true);
        Load("Names_HBS.csv", true, statusNames);
        Load("Descriptions_HBS.csv", true, statusDescriptions);
        foreach (var pair in statusNames) {
            statusAliases[Normal(pair.Key[4..])] = pair.Key;
            statusAliases[Normal(Pick(pair.Value, 0))] = pair.Key;
        }
        foreach (var (key, values) in Rows(Path.Combine(dataDirectory, "Strings_Item.csv"), false)) {
            var english = Pick(values, 0);
            if (english.Length > 0) words.TryAdd(Normal(english), values);
            if (key.StartsWith("itdesc_", StringComparison.Ordinal)) {
                words.TryAdd(Normal(key[7..]), values);
                var hbs = "hbs_" + key[7..];
                if (statusNames.ContainsKey(hbs)) statusAliases[Normal(english)] = hbs;
            }
        }
        Alias("HEXS", "SUPERHEX"); Alias("HEXP", "POISONHEX"); Alias("LUCKY", "RABBITLUCK");

        void Alias(string alias, string target) {
            if (words.TryGetValue(Normal(target), out var value)) words[Normal(alias)] = value;
            if (statusAliases.TryGetValue(Normal(target), out var key)) statusAliases[Normal(alias)] = key;
        }
        void Load(string file, bool level, Dictionary<string, string[]> target, bool last = false) {
            foreach (var (key, values) in Rows(Path.Combine(dataDirectory, file), level)) {
                if (last) target[key] = values;
                else target.TryAdd(key, values);
            }
        }
    }

    private static IEnumerable<(string Key, string[] Values)> Rows(string path, bool level) {
        if (!File.Exists(path)) yield break;
        using var parser = new TextFieldParser(path, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var first = true;
        while (!parser.EndOfData) {
            var fields = parser.ReadFields();
            if (first) { first = false; continue; }
            if (fields == null || fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0])) continue;
            yield return (fields[0], fields.Skip(level ? 2 : 1).ToArray());
        }
    }
    private static string Pick(string[] values, int language) => language >= 0 && language < values.Length ? values[language] : "";
    private static string Get(Dictionary<string, string[]> table, string key, int language) => table.TryGetValue(key, out var v) ? Pick(v, language) : "";
    public string Name(string key, int language) => Get(names, key, language);
    public string Description(string key, int language) => Get(descriptions, key, language);
    public string Phrase(string key, int language) => Get(phrases, key, language);
    public string Title(string key, int language) => Get(titles, key, language);
    public static string Normal(string text) => Regex.Replace(text.ToUpperInvariant(), "[- _]", "");
    private static string Core(string token) => Regex.Replace(token, @"-(?:[ADHS]-X|\d+|X)$", "");
    public string Word(string token, int language) {
        var key = Normal(token);
        if (!words.TryGetValue(key, out var row)) words.TryGetValue(Normal(Core(token)), out row);
        return row == null ? "" : Pick(row, language);
    }
    public string StatusKey(string token) {
        if (statusAliases.TryGetValue(Normal(token), out var key) || statusAliases.TryGetValue(Normal(Core(token)), out key)) return key;
        var charge = Core(token).TrimEnd('S');
        if (charge is "CHARGE" or "SUPERCHARGE" or "ULTRACHARGE" or "OMEGACHARGE") return "charge:" + charge.ToLowerInvariant();
        if (token is "DARKSPELL" or "SHACKLES") return "charge:" + token.ToLowerInvariant();
        return "";
    }
    public string StatusDescription(string key, int language) {
        if (!key.StartsWith("charge:", StringComparison.Ordinal)) return Get(statusDescriptions, key, language);
        var charge = key[7..];
        var value = Phrase("infodraw_" + charge, language);
        var multiplier = charge switch { "charge" => "1.5", "supercharge" => "2.0", "ultracharge" => "2.5", "omegacharge" => "3.0", _ => "" };
        return value.Replace("dmgAm", multiplier);
    }
    public string StatusName(string key, int language) => key.StartsWith("charge:", StringComparison.Ordinal)
        ? Word(key[7..], language) : Get(statusNames, key, language);
}

internal sealed class F8Renderer(F8Catalog catalog) {
    private readonly Dictionary<string, string> usedStatuses = new(StringComparer.Ordinal);
    private int language;
    private bool EnglishOnly => language == 0;
    private static readonly string[] Roles = ["主技能", "辅技能", "特殊技能", "防御技能"];
    private static readonly string[] EnglishRoles = ["Primary", "Secondary", "Special", "Defensive"];
    internal static string Number(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);
    internal static string Seconds(double ms, int lang) => (ms / 1000).ToString("0.0###", CultureInfo.InvariantCulture) + (lang == 0 ? "s" : "秒");
    private static string Clean(string text) => text.Replace("#", "\n").Replace("\r\n", "\n").Replace("{", "").Replace("}", "").Trim();
    private string Pair(string local, string english) => EnglishOnly || local.Length == 0 || local == english ? english : english.Length == 0 ? local : local + " / " + english;
    private string Name(F8Entry entry, int lang) {
        var name = catalog.Name(entry.Key, lang);
        if (name.Length == 0 && lang == language) name = entry.LiveName;
        return name.Length > 0 ? name : entry.Key;
    }
    private string Name(F8Entry entry) => Pair(Name(entry, language), Name(entry, 0));
    private string Heading(string local, string english) => EnglishOnly ? english : local;
    public string Render(F8Run run) {
        language = run.Language;
        usedStatuses.Clear();
        var sb = new StringBuilder();
        sb.Append("HP: ").Append(run.Hp is double hp ? Number(hp) : "?");
        if (run.MaxHp is double max) sb.Append(" / ").Append(Number(max));
        sb.AppendLine();
        if (run.Gold is double gold) sb.Append("Gold: ").AppendLine(Number(gold));
        if (run.Level is double level) sb.Append("Level: ").AppendLine(Number(level));
        var role = Pair(catalog.Title(run.ClassKey, language), catalog.Title(run.ClassKey, 0));
        if (role.Length > 0) sb.Append("Class: ").AppendLine(role);
        var stage = Pair(catalog.Phrase(run.StageKey, language), catalog.Phrase(run.StageKey, 0)).Replace("#", " ");
        if (run.Stage != null || stage.Length > 0) sb.Append("Stage: ").Append(run.Stage?.ToString(CultureInfo.InvariantCulture) ?? "?").Append(stage.Length > 0 ? " / " + stage : "").AppendLine();
        if (run.Difficulty is int d) sb.Append("Difficulty: ").AppendLine(Pair(catalog.Phrase("menuchar_difficulty_" + d, language), catalog.Phrase("menuchar_difficulty_" + d, 0)));
        sb.AppendLine().Append("## ").AppendLine(Heading("技能", "Abilities")).AppendLine();
        foreach (var skill in run.Skills) Skill(sb, skill, false);
        Section(sb, "已携带道具", "Carried items", run.Carried, true, false);
        if (run.ShopOpen) Section(sb, "商店", "Shop", run.Shop, false, true);
        if (run.LootOpen) Section(sb, "战利品选项", "Loot choices", run.Loot, false, false);
        if (usedStatuses.Count > 0) {
            sb.Append("## ").AppendLine(Heading("特殊状态说明", "Special statuses")).AppendLine();
            foreach (var (key, token) in usedStatuses.ToArray()) {
                var local = catalog.StatusName(key, language);
                var en = catalog.StatusName(key, 0);
                sb.Append("### ").AppendLine(Pair(local, "*" + (en.Length > 0 ? en.ToUpperInvariant() : token) + "*")).AppendLine();
                if (!EnglishOnly) sb.AppendLine(Clean(catalog.StatusDescription(key, language))).AppendLine();
                sb.AppendLine(Clean(catalog.StatusDescription(key, 0))).AppendLine();
            }
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
    private void Section(StringBuilder sb, string local, string english, List<F8Entry> entries, bool carried, bool prices) {
        sb.Append("## ").AppendLine(Heading(local, english)).AppendLine();
        if (entries.Count == 0) sb.AppendLine(Heading("（无）", "(none)")).AppendLine();
        foreach (var entry in entries) {
            sb.Append("### ").AppendLine(Name(entry)).AppendLine();
            if (prices && entry.Price is double price) sb.Append(Heading("售价: ", "Price: ")).Append(Number(price)).AppendLine(Heading(" 金粒", " gold")).AppendLine();
            if (entry.OtherFace != null) sb.Append(Heading(carried ? "目前形态: " : "当前槽位形态: ", carried ? "Current form: " : "Current slot form: ")).AppendLine(Name(entry)).AppendLine();
            // Captures establish the current value, but not yet its countdown direction.
            if (carried && entry.Key is "it_heavens_codex" or "it_hells_codex" && entry.Counter is double count)
                sb.Append(Heading("形态计数: ", "Form counter: ")).Append(Number(count)).AppendLine(Heading("（尚未验证是否为剩余技能次数）", " (remaining ability-use meaning unverified)")).AppendLine();
            Description(sb, entry);
            if (entry.OtherFace is F8Entry other) {
                sb.Append("#### ").Append(Heading("另一形态: ", "Other form: ")).AppendLine(Name(other)).AppendLine();
                Description(sb, other);
            }
            if (entry.Upgrade is F8Entry upgrade) {
                sb.Append("#### ").AppendLine(Heading("升级后技能", "Upgraded ability")).AppendLine();
                Skill(sb, upgrade, true);
            }
        }
    }
    private void Skill(StringBuilder sb, F8Entry entry, bool preview) {
        var slot = Math.Clamp(entry.Slot, 0, 3);
        sb.Append(preview ? "##### " : "### ").Append(EnglishOnly ? EnglishRoles[slot] : Roles[slot]).Append(": ").AppendLine(Name(entry)).AppendLine();
        foreach (var lang in EnglishOnly ? new[] { 0 } : new[] { language, 0 }) {
            if (!preview && entry.AdjustedCd != null) Stats(sb, entry, lang, true);
            Stats(sb, entry, lang, false);
            sb.AppendLine(Description(entry, lang)).AppendLine();
        }
    }
    private static double At(double[] a, int i) => i < a.Length ? a[i] : 0;
    private static void Stats(StringBuilder sb, F8Entry entry, int lang, bool adjusted) {
        var cd = adjusted ? entry.AdjustedCd ?? entry.Cd : entry.Cd;
        var damage = adjusted ? entry.Damage ?? At(entry.AdjustedHb ?? entry.Hb, 4) : At(entry.Hb, 4);
        sb.Append(lang == 0 ? adjusted ? "(Adjusted) Damage " : "(Base) Damage " : adjusted ? "(调整数值) 伤害 " : "(基础数值) 基础伤害 ")
            .Append(Number(damage)).Append(lang == 0 ? "  Cooldown " : "  冷却 ").Append(Seconds(At(cd, 1), lang))
            .Append(lang == 0 ? "  Global cooldown " : "  全局冷却 ").Append(Seconds(At(cd, 2), lang))
            .Append(lang == 0 ? "  Max uses " : "  次数上限 ").AppendLine(Number(At(cd, 6)));
        if (adjusted && entry.Stock is double stock) sb.Append(lang == 0 ? "Remaining uses: " : "当前可用次数: ").AppendLine(Number(stock));
    }
    private void Description(StringBuilder sb, F8Entry entry) {
        if (!EnglishOnly) sb.AppendLine(Description(entry, language)).AppendLine();
        sb.AppendLine(Description(entry, 0)).AppendLine();
    }
    private string Description(F8Entry entry, int lang) {
        var template = catalog.Description(entry.Key, lang);
        if (template.Length == 0 && lang == language) template = entry.LiveTemplate;
        if (template.Length == 0) return lang == 0 ? "(Description unavailable)" : "（说明不可用）";
        // Description numbers are explicitly the base data; adjusted values are separate above.
        return Fill(template, entry, lang);
    }
    internal string Fill(string template, F8Entry entry, int lang) => Clean(Regex.Replace(template, @"\[([^\]]+)\]", match => {
        var token = match.Groups[1].Value;
        var pieces = token.Split('_', 2);
        double? number = pieces[0] switch {
            "STR" => At(entry.Hb, 4), "CD" => At(entry.Cd, 1), "GCD" => At(entry.Cd, 2),
            "HBSL" => At(entry.Hb, 9), "HBSSTR" => At(entry.Hb, 8), "LUCK" => At(entry.Hb, 11),
            "VAR0" => At(entry.Hb, 17), "VAR1" => At(entry.Hb, 18), "VAR2" => At(entry.Hb, 19), "VAR3" => At(entry.Hb, 20), _ => null
        };
        if (number is double n) {
            var suffix = pieces.Length > 1 ? pieces[1] : "";
            if (suffix == "PERCENT" || token == "LUCK") return Number(n * 100) + "%";
            if (suffix is "SECONDS" or "SECOND" || token is "CD" or "GCD" or "HBSL") return Seconds(n, lang);
            if (suffix == "TIMES") return lang == 0 ? n == 1 ? "once" : n == 2 ? "twice" : Number(n) + " times" : Number(n) + "次";
            return Number(n);
        }
        var key = catalog.StatusKey(token);
        if (key.Length > 0) usedStatuses.TryAdd(key, token);
        var en = catalog.Word(token, 0);
        if (en.Length == 0) en = token;
        var local = catalog.Word(token, lang);
        return lang == 0 || local.Length == 0 || local == en ? "*" + en + "*" : local + "（*" + en + "*）";
    }));
}
