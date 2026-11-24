// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;

namespace RNSClipboardProbe;

internal static unsafe partial class Snapshot {
    public static string Brief(IRNSReloaded rns) => BilingualBrief(rns);

    private static void WriteRun(StringBuilder sb, IRNSReloaded rns, int language) {
        var hasHp = GlobalNumber(rns, out var hp, "playerHp", "displayHp") || BodyNumber(rns, out hp, "hp", "playerHp");
        var hasMax = GlobalNumber(rns, out var max, "hpMax", "displayMaxHp", "maxHp") || BodyNumber(rns, out max, "hpMax", "maxHp", "displayMaxHp");
        if (hasHp && hasMax) sb.Append("hp ").Append(Whole(hp)).Append('/').AppendLine(Whole(max));
        else if (hasHp) sb.Append("hp ").AppendLine(Whole(hp));
        if (GlobalNumber(rns, out var gold, "playerGold") || BodyNumber(rns, out gold, "gold", "playerGold")) {
            sb.Append("gold ").AppendLine(Whole(gold));
        }
        if (GlobalNumber(rns, out var level, "playerLv", "level") || BodyNumber(rns, out level, "playerLv", "level")) {
            sb.Append("level ").AppendLine(Whole(level));
        }
        var className = ClassName(rns, language);
        if (className.Length > 0) sb.Append("class ").AppendLine(className);
        if (GlobalNumber(rns, out var cleared, "notchCurrent", "fightNum", "stageIndex") || BodyNumber(rns, out cleared, "notchCurrent", "fightNum", "stageIndex")) {
            sb.Append("cleared ").AppendLine(Whole(cleared));
        }
        var stageNo = GlobalNumber(rns, out var stageIndex, "stageIndex") || BodyNumber(rns, out stageIndex, "stageIndex") ? Whole(stageIndex) : "";
        var stageName = StageName(rns, language);
        if (stageNo.Length > 0 || stageName.Length > 0) {
            sb.Append("stage");
            if (stageNo.Length > 0) sb.Append(' ').Append(stageNo);
            if (stageName.Length > 0) sb.Append(' ').Append(stageName);
            sb.AppendLine();
        }
        var difficulty = DifficultyName(rns, language);
        if (difficulty.Length > 0) sb.Append("difficulty ").AppendLine(difficulty);
        if (sb.Length > 0) sb.AppendLine();
    }

    private static void WriteSkill(StringBuilder sb, IRNSReloaded rns, Offer skill, int language, HashSet<string> noted, List<string> notes) {
        var stats = SkillStats(rns, skill);
        var name = skill.Name.Length > 0 ? skill.Name : skill.Key;
        if (name.Length == 0) name = "(unknown)";
        var number = SkillNumber(stats, skill.Slot);
        if (number <= 0) number = skill.Slot >= 0 ? skill.Slot + 1 : 1;
        sb.Append(number.ToString(CultureInfo.InvariantCulture)).Append("技: ").AppendLine(name);
        if ((stats.Cd?.Length ?? 0) >= 3 || (stats.Hb?.Length ?? 0) > 4) {
            var damage = stats.HasStrength ? stats.Strength : BaseDamage(stats);
            sb.Append("(調整數值) 伤害 ").Append(Whole(damage));
            sb.Append("  冷却 ").Append(Seconds(Cd(stats, 1)));
            sb.Append("  全局冷却 ").AppendLine(Seconds(Cd(stats, 2)));
            sb.Append("(基础數值) 基础伤害 ").Append(Whole(BaseDamage(stats)));
            sb.Append("  全局冷却 ").Append(Seconds(Cd(stats, 2)));
            sb.Append("  基础冷却 ").Append(Seconds(Cd(stats, 1)));
            sb.Append("  次数上限 ").AppendLine(Whole(UseLimit(stats)));
        }
        var template = Template(rns, skill);
        NoteTemplate(template, language, noted, notes);
        var desc = Fill(template, stats, language);
        if (desc.Length > 0) sb.AppendLine(desc);
        sb.AppendLine();
    }

    private static void WriteCard(StringBuilder sb, IRNSReloaded rns, Offer offer, bool price, int language, HashSet<string> noted, List<string> notes) {
        WriteFace(sb, rns, offer, price, language, noted, notes);
        if (OtherFace(rns, offer) is Offer other) WriteFace(sb, rns, other, price: false, language, noted, notes);
    }

    private static void WriteFace(StringBuilder sb, IRNSReloaded rns, Offer offer, bool price, int language, HashSet<string> noted, List<string> notes) {
        var name = offer.Name.Length > 0 ? offer.Name : offer.Key;
        if (name.Length == 0) name = offer.Id > 0 ? "id " + offer.Id.ToString(CultureInfo.InvariantCulture) : "(unknown)";
        sb.Append(name);
        if (price && offer.Price.Length > 0) sb.Append("  售價 ").Append(offer.Price).Append(" 金粒");
        sb.AppendLine();
        var stats = ItemStats(rns, offer.Id);
        var template = Template(rns, offer);
        NoteTemplate(template, language, noted, notes);
        var replaced = ReplacedDesc(rns, template, offer.Id);
        var desc = replaced.Length > 0 ? replaced : Fill(template, stats, language);
        if (desc.Length > 0) sb.AppendLine(desc);
        sb.AppendLine();
    }

    // Items that change with the situation store the other item's key on transformKey
    // (狂风之矛 -> it_rain_spear / 雷雨之矛). There is no separate transformId field.
    private static Offer? OtherFace(IRNSReloaded rns, Offer offer) {
        var id = offer.Id > 0 ? offer.Id : offer.Key.Length > 0 ? IdFromKey(rns, offer.Key) ?? ItemCatalog.IdForKey(offer.Key) : 0;
        if (id <= 0 && offer.Key.Length == 0) return null;
        var link = id > 0 ? LinkedFace(rns, id) : default;
        var key = link.Key ?? "";
        var otherId = link.Id;
        if (key.Length == 0 && otherId <= 0) return null;
        if (key.Length == 0 && otherId > 0) key = KeyFromId(rns, otherId);
        if (key.Length == 0 && otherId > 0) key = ItemCatalog.Key(otherId);
        if (otherId <= 0 && key.Length > 0) otherId = IdFromKey(rns, key) ?? ItemCatalog.IdForKey(key);
        if (otherId == id || (key.Length > 0 && key == offer.Key)) return null;
        if (key.Length == 0 || !LooksLikeKey(key)) return null;
        var name = ItemCatalog.NameForKey(key, LanguageIndex(rns));
        if (name.Length == 0 && otherId > 0) name = NameFromId(rns, otherId);
        if (name.Length == 0) return null;
        return new Offer(offer.Slot, key, name, "", otherId);
    }

    private readonly struct FaceLink {
        public string Key { get; init; }
        public int Id { get; init; }
        public bool Present => Id > 0 || (Key != null && Key.Length > 0);
    }

    private static FaceLink LinkedFace(IRNSReloaded rns, int id) {
        var table = Live(rns.FindGlobalValue("itemData"));
        if (TryIndex(rns, table, id, out var entry)) {
            var found = FindTransform(rns, entry, 0);
            if (found.Present) return found;
        }
        foreach (var info in FindNamed(rns, "obj_lootInfo", 16)) {
            if (!TryNumber(rns, info, "itemId", out var itemId) || itemId != id) continue;
            var found = FindTransform(rns, info, 0);
            if (found.Present) return found;
        }
        return default;
    }

    private static FaceLink FindTransform(IRNSReloaded rns, RValue value, int depth) {
        if (depth > 3 || !Present(value)) return default;
        if (value.Type is RValueType.Object or RValueType.Ref) {
            var direct = ReadOn(rns, value, "transformKey") ?? ReadInstanceVar(rns, value, "transformKey");
            var link = AsTransform(rns, direct);
            if (link.Present) return link;
            foreach (var name in OwnKeys(rns, value, 40)) {
                if (name == "transformKey") continue;
                var child = ReadOn(rns, value, name) ?? ReadInstanceVar(rns, value, name);
                if (child is not RValue live || live.Type is not (RValueType.Object or RValueType.Ref or RValueType.Array)) continue;
                var found = FindTransform(rns, live, depth + 1);
                if (found.Present) return found;
            }
            return default;
        }
        if (value.Type != RValueType.Array) return default;
        var len = Math.Min(ArrayLength(rns, value), 8);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, value, i, out var cell)) continue;
            var found = FindTransform(rns, cell, depth + 1);
            if (found.Present) return found;
        }
        return default;
    }

    private static FaceLink AsTransform(IRNSReloaded rns, RValue? value) {
        if (value is not RValue live || !Present(live)) return default;
        if (live.Type == RValueType.String) {
            var text = ReadString(rns, live);
            return LooksLikeKey(text) ? new FaceLink { Key = text } : default;
        }
        if (live.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            var number = live.ToInt(-1);
            return number is > 0 and <= 8000 ? new FaceLink { Id = number } : default;
        }
        return default;
    }

    private static void NoteStatuses(IRNSReloaded rns, Offer offer, int language, HashSet<string> noted, List<string> notes) {
        NoteTemplate(Template(rns, offer), language, noted, notes);
    }

    // Skill and item text live in langDescMap, filled from the description sheets
    // (Descriptions_Move, Descriptions_Item, Descriptions_Potion) by scr_readsheet_get_desc.
    // [FLASH-INT] and the other bracket tokens are the English column of Strings_Item.
    // obj_inventoryInfo.itemDesc is that same text after the tooltip wraps it.
    private static string Template(IRNSReloaded rns, Offer offer) {
        var text = offer.Id > 0 ? LiveDesc(rns, offer.Id) : "";
        if (text.Length == 0) text = ScriptDesc(rns, offer.Key);
        if (text.Length == 0) text = SheetDesc(offer.Key, LanguageIndex(rns));
        return text;
    }

    private static string LiveDesc(IRNSReloaded rns, int id) {
        var mapped = DescAt(rns, Live(rns.FindGlobalValue("langDescMap")), id);
        if (mapped.Length > 0) return mapped;
        var table = Live(rns.FindGlobalValue("itemData"));
        if (!TryIndex(rns, table, id, out var entry)) return "";
        var name = NameFromId(rns, id);
        string best = "";
        CollectDesc(rns, entry, name, 0, ref best);
        return best;
    }

    private static void CollectDesc(IRNSReloaded rns, RValue value, string name, int depth, ref string best) {
        if (depth > 3 || !Present(value) || best.Contains('#')) return;
        var text = FullString(rns, value);
        if (text.Length > best.Length && text != name && !LooksLikeKey(text)) best = text;
        if (value.Type != RValueType.Array) return;
        var len = Math.Min(ArrayLength(rns, value), 8);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, value, i, out var cell)) continue;
            CollectDesc(rns, cell, name, depth + 1, ref best);
        }
    }

    private static string DescAt(IRNSReloaded rns, RValue map, int id) {
        if (!TryIndex(rns, map, id, out var entry)) return "";
        var direct = FullString(rns, entry);
        if (direct.Length > 0 && !LooksLikeKey(direct)) return direct;
        var lang = LanguageIndex(rns);
        if (TryIndex(rns, entry, lang, out var cell)) {
            var text = FullString(rns, cell);
            if (text.Length > 0 && !LooksLikeKey(text)) return text;
        }
        if (entry.Type != RValueType.Array) return "";
        var len = Math.Min(ArrayLength(rns, entry), 8);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, entry, i, out var nested)) continue;
            var text = FullString(rns, nested);
            if (text.Length > 0 && !LooksLikeKey(text)) return text;
        }
        return "";
    }

    private static string ScriptDesc(IRNSReloaded rns, string key) {
        if (key.Length == 0 || key.StartsWith("id=", StringComparison.Ordinal)) return "";
        try {
            var global = rns.GetGlobalInstance();
            RValue arg = default;
            rns.CreateString(&arg, key);
            var result = rns.ExecuteScript("scr_readsheet_get_desc", global, null, [arg]);
            if (result is not RValue value || value.Type != RValueType.String) return "";
            var copy = value;
            var text = rns.GetString(&copy);
            if (text.Length is < 2 or > 700 || text == key) return "";
            return text;
        } catch {
            return "";
        }
    }

    private static string FullString(IRNSReloaded rns, RValue value) {
        if (value.Type != RValueType.String) return "";
        try {
            var text = rns.GetString(&value);
            return text.Length > 700 ? text[..700] : text;
        } catch {
            return "";
        }
    }

    private static string Fill(string text, Stats stats, int language) {
        if (text.Length == 0) return "";
        text = text.Replace("#", "\n").Replace("\r\n", "\n").Replace('\r', '\n').Replace("{", "").Replace("}", "");
        if (text.Contains('[')) {
            text = Regex.Replace(text, @"\[([A-Za-z0-9_-]+)\]", match => {
                var token = match.Groups[1].Value;
                return NumberToken(token, stats) ?? (DescSheets.Tokens(language).TryGetValue(token, out var name) && name.Length > 0 ? name : match.Value);
            });
        }
        return text.Trim();
    }

    private static string? NumberToken(string token, Stats stats) {
        var core = token;
        var suffix = "";
        var split = token.IndexOf('_');
        if (split > 0) {
            core = token[..split];
            suffix = token[(split + 1)..];
        }
        double value;
        var found = false;
        var hb = stats.Hb ?? [];
        if (core == "STR") {
            value = BaseDamage(stats);
            found = hb.Length > 4 || stats.HasStrength;
        } else if (core == "CD") {
            value = Cd(stats, 1);
            found = (stats.Cd?.Length ?? 0) > 1;
        } else if (core == "GCD") {
            value = Cd(stats, 2);
            found = (stats.Cd?.Length ?? 0) > 2;
        } else if (core == "HBSL") {
            value = stats.HbsLength;
            found = value > 0;
        } else if (core == "HBSSTR") {
            value = stats.HbsStrength;
            found = value != 0;
        } else if (core is "VAR0" or "VAR1" or "VAR2" or "VAR3") {
            var index = 17 + (core[3] - '0');
            found = hb.Length > index;
            value = found ? hb[index] : 0;
        } else {
            return null;
        }
        if (!found) return null;
        if (suffix == "PERCENT") return Percent(value);
        if (suffix is "SECONDS" or "SECOND") return Seconds(value);
        if (suffix == "TIMES") return Whole(value);
        if (core is "CD" or "GCD" or "HBSL") return Seconds(value);
        return Whole(value);
    }

    private static string SheetDesc(string key, int language) {
        return key.Length == 0 ? "" : DescSheets.Description(key, language);
    }

    private readonly struct Stats {
        public double[] Cd { get; init; }
        public double[] Hb { get; init; }
        public double Strength { get; init; }
        public bool HasStrength { get; init; }
        public double Stock { get; init; }
        public double HbsLength { get; init; }
        public double HbsStrength { get; init; }
    }

    private static Stats SkillStats(IRNSReloaded rns, Offer skill) {
        foreach (var slot in FindNamed(rns, "obj_hotbarSlot", 48)) {
            if (!TryNumber(rns, slot, "itemRowId", out var row) || row != 7) continue;
            var slotNo = TryNumber(rns, slot, "slotId", out var shown) ? shown : -1;
            if (slotNo < 0 && TryNumber(rns, slot, "itemSlotId", out var itemSlot)) slotNo = itemSlot;
            if (slotNo != skill.Slot) continue;
            if (skill.Id > 0 && (!TryNumber(rns, slot, "dataId", out var dataId) || dataId != skill.Id)) continue;
            return ReadStats(rns, slot);
        }
        return ItemStats(rns, skill.Id);
    }

    private static Stats ItemStats(IRNSReloaded rns, int id) {
        if (id <= 0) return default;
        foreach (var info in FindNamed(rns, "obj_lootInfo", 16)) {
            if (!TryNumber(rns, info, "itemId", out var itemId) || itemId != id) continue;
            var stats = ReadStats(rns, info);
            if (HasCardStats(stats)) return stats;
        }
        foreach (var slot in FindNamed(rns, "obj_hotbarSlot", 48)) {
            if (!TryNumber(rns, slot, "dataId", out var dataId) || dataId != id) continue;
            var stats = ReadStats(rns, slot);
            if (HasCardStats(stats)) return stats;
        }
        var catalog = CatalogStats(rns, id);
        if (HasCardStats(catalog)) return catalog;
        return HarvestStats(rns, id);
    }

    private static bool HasCardStats(Stats stats) {
        return (stats.Cd?.Length ?? 0) > 1 || (stats.Hb?.Length ?? 0) > 4 || stats.HbsLength > 0;
    }

    private static string ReplacedDesc(IRNSReloaded rns, string template, int id) {
        if (id <= 0 || template.Length == 0 || !template.Contains('[')) return "";
        try {
            var global = rns.GetGlobalInstance();
            RValue text = default;
            rns.CreateString(&text, template);
            var result = rns.ExecuteScript("scr_readsheet_desc_strreplace", global, null, [text, new RValue(id)]);
            if (result is not RValue value || value.Type != RValueType.String) return "";
            var copy = value;
            var filled = rns.GetString(&copy);
            if (filled.Length is < 2 or > 700 || filled.Contains('[') || filled == template) return "";
            return filled.Replace("#", "\n").Replace("\r\n", "\n").Replace('\r', '\n').Replace("{", "").Replace("}", "").Trim();
        } catch {
            return "";
        }
    }

    private static Stats HarvestStats(IRNSReloaded rns, int id) {
        var table = Live(rns.FindGlobalValue("itemData"));
        if (!TryIndex(rns, table, id, out var entry)) return default;
        double strength = 0;
        double cooldown = 0;
        double gcd = 0;
        double length = 0;
        var vars = new double[4];
        var set = new bool[4];
        var hasStrength = false;
        var hasCooldown = false;
        var hasGcd = false;
        void Take(string key, double value) {
            var lower = key.ToLowerInvariant();
            if (lower is "u_sqvar0" or "hbvar0" or "sqvar0" or "greysqvar0" or "u_gold") {
                if (!set[0] || lower != "u_gold") {
                    vars[0] = value;
                    set[0] = true;
                }
            } else if (lower is "u_sqvar1" or "hbvar1" or "sqvar1") {
                vars[1] = value;
                set[1] = true;
            } else if (lower is "u_sqvar2" or "hbvar2" or "sqvar2") {
                vars[2] = value;
                set[2] = true;
            } else if (lower is "u_sqvar3" or "hbvar3" or "sqvar3") {
                vars[3] = value;
                set[3] = true;
            } else if (lower is "u_str" or "strength" or "basestrength" or "s_basestrength" or "ths0_basestrength") {
                strength = value;
                hasStrength = true;
            } else if (lower is "hiddencooldown" or "cooldown" or "cdlength" or "cd") {
                cooldown = value;
                hasCooldown = true;
            } else if (lower is "gcdlength" or "gcd") {
                gcd = value;
                hasGcd = true;
            } else if (lower is "r_hbslength" or "hbslength") {
                length = value;
            }
        }
        void Walk(RValue value, int depth) {
            if (depth > 4 || !Present(value)) return;
            if (value.Type is RValueType.Object or RValueType.Ref) {
                foreach (var key in OwnKeys(rns, value, 48)) {
                    var field = ReadInstanceVar(rns, value, key) ?? ReadOn(rns, value, key);
                    if (field is not RValue live) continue;
                    if (live.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) Take(key, live.ToDouble());
                    else Walk(live, depth + 1);
                }
                return;
            }
            if (value.Type != RValueType.Array) return;
            var len = Math.Min(ArrayLength(rns, value), 24);
            for (var i = 0; i < len; i++) {
                if (!TryIndex(rns, value, i, out var cell)) continue;
                Walk(cell, depth + 1);
            }
        }
        Walk(entry, 0);
        if (!hasStrength && !hasCooldown && !hasGcd && length <= 0 && !set.Any(flag => flag)) return default;
        var hb = new double[21];
        if (hasStrength) hb[4] = strength;
        for (var i = 0; i < 4; i++) {
            if (set[i]) hb[17 + i] = vars[i];
        }
        var cd = new double[7];
        if (hasCooldown) cd[1] = cooldown;
        if (hasGcd) cd[2] = gcd;
        return new Stats {
            Cd = hasCooldown || hasGcd ? cd : [],
            Hb = hasStrength || set.Any(flag => flag) ? hb : [],
            Strength = strength,
            HasStrength = hasStrength,
            HbsLength = length,
        };
    }

    private static Stats ReadStats(IRNSReloaded rns, RValue handle) {
        var cd = Numbers(rns, ReadInstanceVar(rns, handle, "cdData"));
        var hb = Numbers(rns, ReadInstanceVar(rns, handle, "hbData"));
        _ = TryNumber(rns, handle, "strength", out var strength);
        var hasStrength = ReadInstanceVar(rns, handle, "hasStrength") is RValue flag && flag.ToBool();
        _ = TryNumber(rns, handle, "stock", out var stock);
        _ = TryNumber(rns, handle, "hbsLength", out var hbsLength);
        _ = TryNumber(rns, handle, "hbsStrength", out var hbsStrength);
        if (hbsLength <= 0 && hb.Length > 9 && hb[9] >= 500) hbsLength = (int) Math.Round(hb[9]);
        return new Stats {
            Cd = cd,
            Hb = hb,
            Strength = strength,
            HasStrength = hasStrength || strength != 0,
            Stock = stock,
            HbsLength = hbsLength,
            HbsStrength = hbsStrength,
        };
    }

    private static Stats CatalogStats(IRNSReloaded rns, int id) {
        var table = Live(rns.FindGlobalValue("itemData"));
        if (!TryIndex(rns, table, id, out var entry)) return new Stats { Cd = [], Hb = [] };
        double[] cd = [];
        double[] hb = [];
        CollectArrays(rns, entry, 0, ref cd, ref hb);
        if (cd.Length < 2 && hb.Length < 5) return new Stats { Cd = [], Hb = [] };
        var hbs = hb.Length > 9 && hb[9] >= 500 ? hb[9] : 0;
        return new Stats { Cd = cd, Hb = hb, HbsLength = hbs, Strength = hb.Length > 4 ? hb[4] : 0 };
    }

    private static void CollectArrays(IRNSReloaded rns, RValue value, int depth, ref double[] cd, ref double[] hb) {
        if (depth > 3 || value.Type != RValueType.Array) return;
        var numbers = Numbers(rns, value);
        if (numbers.Length == 7 && cd.Length == 0) cd = numbers;
        if (numbers.Length == 21 && hb.Length == 0) hb = numbers;
        if (cd.Length > 0 && hb.Length > 0) return;
        var len = Math.Min(ArrayLength(rns, value), 8);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, value, i, out var cell)) continue;
            CollectArrays(rns, cell, depth + 1, ref cd, ref hb);
        }
    }

    private static double[] Numbers(IRNSReloaded rns, RValue? value) {
        if (value is not RValue array || array.Type != RValueType.Array) return [];
        var len = ArrayLength(rns, array);
        if (len is <= 0 or > 32) return [];
        var numbers = new double[len];
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, array, i, out var cell) || cell.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return [];
            numbers[i] = cell.ToDouble();
        }
        return numbers;
    }

    private static int SkillNumber(Stats stats, int slot) {
        var hb = stats.Hb ?? [];
        if (hb.Length > 3) {
            var number = (int) Math.Round(hb[3]);
            if (number is >= 1 and <= 4) return number;
        }
        return slot is >= 0 and <= 3 ? slot + 1 : 0;
    }

    private static double BaseDamage(Stats stats) {
        var hb = stats.Hb ?? [];
        return hb.Length > 4 ? hb[4] : stats.Strength;
    }

    private static double Cd(Stats stats, int index) {
        var cd = stats.Cd ?? [];
        return cd.Length > index ? cd[index] : 0;
    }

    private static double UseLimit(Stats stats) {
        if (stats.Stock > 0) return stats.Stock;
        var cd = stats.Cd ?? [];
        return cd.Length > 6 ? cd[6] : 0;
    }

    private static string Whole(double value) {
        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 0.001) return ((long) rounded).ToString(CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Seconds(double value) {
        var seconds = Math.Abs(value) >= 500 ? value / 1000.0 : value;
        return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "秒";
    }

    private static string Percent(double value) {
        var shown = Math.Abs(value) <= 3 ? value * 100.0 : value;
        return Whole(shown) + "%";
    }

    private static void NoteTemplate(string template, int language, HashSet<string> noted, List<string> notes) {
        if (template.Length == 0 || !template.Contains('[')) return;
        foreach (Match match in Regex.Matches(template, @"\[([A-Za-z0-9_-]+)\]")) {
            var key = DescSheets.StatusKey(match.Groups[1].Value);
            var text = key.Length == 0 ? "" : DescSheets.Status(match.Groups[1].Value, language);
            if (text.Length == 0 || !noted.Add(key)) continue;
            notes.Add(text);
        }
    }

    private static bool GlobalNumber(IRNSReloaded rns, out double number, params string[] names) {
        number = 0;
        foreach (var name in names) {
            var pointer = rns.FindGlobalValue(name);
            if (pointer == null || !IsLive(*pointer)) continue;
            if (!FirstNumber(rns, *pointer, out var value)) continue;
            if (value is < -1000 or > 10000000) continue;
            number = value;
            return true;
        }
        return false;
    }

    private static bool FirstNumber(IRNSReloaded rns, RValue value, out double number) {
        number = 0;
        if (value.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            number = value.ToDouble();
            return true;
        }
        if (value.Type == RValueType.Array && TryIndex(rns, value, 0, out var first)) return FirstNumber(rns, first, out number);
        return false;
    }

    private static string StageName(IRNSReloaded rns, int language) {
        foreach (var name in new[] { "stageName", "stageNameKey", "stageKey", "areaKey", "areaName", "stgKey" }) {
            var text = GlobalText(rns, name);
            if (text.Length == 0) text = PlayerText(rns, name);
            if (text.Length == 0) continue;
            var looked = DescSheets.Phrase(text, language);
            var chosen = looked.Length > 0 ? looked : text;
            if (LooksLikeKey(chosen)) continue;
            return chosen.Replace("#", "");
        }
        return "";
    }

    private static string GlobalText(IRNSReloaded rns, string name) {
        var pointer = rns.FindGlobalValue(name);
        if (pointer == null || !IsLive(*pointer)) return "";
        return FirstTextValue(rns, *pointer, 0);
    }

    private static string FirstTextValue(IRNSReloaded rns, RValue value, int depth) {
        if (depth > 3 || !Present(value)) return "";
        var text = FullString(rns, value);
        if (text.Length > 0) return text;
        if (value.Type != RValueType.Array) return "";
        var len = Math.Min(ArrayLength(rns, value), 8);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, value, i, out var cell)) continue;
            var nested = FirstTextValue(rns, cell, depth + 1);
            if (nested.Length > 0) return nested;
        }
        return "";
    }

    private static bool BodyNumber(IRNSReloaded rns, out double number, params string[] names) {
        number = 0;
        var global = rns.GetGlobalInstance();
        if (global == null) return false;
        var bodies = Live(rns.FindValue(global, "player"));
        if (!TryIndex(rns, bodies, 0, out var team)) return false;
        if (!TryIndex(rns, team, 0, out var body)) body = team;
        foreach (var name in names) {
            if (!TryNumber(rns, body, name, out var value)) continue;
            if (value is < -1000 or > 10000000) continue;
            number = value;
            return true;
        }
        return false;
    }

    private static string ClassName(IRNSReloaded rns, int language) {
        var token = ClassToken(rns, []);
        if (token.Length == 0) {
            var anim = PlayerText(rns, "animationKey", "animKey");
            token = TokenIn(anim);
        }
        if (token.Length == 0) return "";
        return DescSheets.Title("al_" + token + "_rabbit", language);
    }

    private static string DifficultyName(IRNSReloaded rns, int language) {
        if (!GlobalNumber(rns, out var value, "difficulty", "difficulty_v2")) return "";
        var index = (int) Math.Round(value);
        if (index is < 0 or > 3) return Whole(value);
        var name = DescSheets.Phrase("menuchar_difficulty_" + index.ToString(CultureInfo.InvariantCulture), language);
        return name.Length > 0 ? name : Whole(value);
    }

    private static int[]? ShopNames(IRNSReloaded rns, Kit kit) {
        var systems = FindNamed(rns, "obj_itemSystem", 1);
        if (systems.Count == 0) return null;
        var sys = systems[0];
        var abilityIds = new HashSet<int>();
        foreach (var skill in kit.Abilities) {
            if (skill.Id > 0) abilityIds.Add(skill.Id);
        }
        var focus = FocusSlot(rns, sys);
        var upgrade = focus >= 0 && FlagAt(rns, ReadInstanceVar(rns, sys, "storeSlotUpgrade"), focus);
        var focusId = focus >= 0 ? FocusWareId(rns, sys, upgrade, abilityIds) : -1;
        return NamedShopIds(rns, sys, abilityIds, focus, focusId);
    }

    private static Offer NamedOffer(IRNSReloaded rns, Offer offer, int[]? named) {
        if (offer.Key.Length > 0 || offer.Name.Length > 0) return offer;
        if (named == null || offer.Slot < 0 || offer.Slot >= named.Length || named[offer.Slot] <= 0) return offer;
        var resolved = OfferFromId(rns, offer.Slot, named[offer.Slot]);
        if (resolved.Key.Length == 0 && resolved.Name.Length == 0) return offer;
        return new Offer(resolved.Slot, resolved.Key, resolved.Name, offer.Price, named[offer.Slot]);
    }

    // pot0, pot1, pot2 are the three potion cards. upgradeP / upgradeS / upgradeSp / upgradeD
    // are the four gem cards, in the same order as storeSlotUpgradeInd 1..4.
    private static int[]? NamedShopIds(IRNSReloaded rns, RValue sys, HashSet<int> abilityIds, int focus, int focusId) {
        try {
            var ids = new int[12];
            Array.Fill(ids, -1);
            var pots = new List<int>();
            foreach (var name in new[] { "pot0", "pot1", "pot2" }) {
                var id = ShopScalar(rns, sys, name, abilityIds);
                if (id > 0) pots.Add(id);
            }
            if (pots.Count < 2) {
                var packed = ShopVector(rns, sys, "potItId", 3, abilityIds);
                if (packed.Count > pots.Count) pots = packed;
            }
            var potSlots = OpenSlots(rns, sys, upgrades: false);
            for (var i = 0; i < pots.Count && i < potSlots.Count; i++) ids[potSlots[i]] = pots[i];
            var upgradeSlots = OpenSlots(rns, sys, upgrades: true);
            if (ReadInstanceVar(rns, sys, "storeSlotUpgradeInd") is RValue ind) {
            foreach (var slot in upgradeSlots) {
                if (!TryIndex(rns, ind, slot, out var cell)) continue;
                var kind = cell.ToInt(-1);
                var field = kind switch { 1 => "upgradeP", 2 => "upgradeS", 3 => "upgradeSp", 4 => "upgradeD", _ => "" };
                if (field.Length == 0) continue;
                var id = ShopScalar(rns, sys, field, abilityIds);
                if (id > 0) ids[slot] = id;
            }
            }
            if (focus >= 0 && focusId >= 100 && ids[focus] == focusId) return ids;
            return PotionLike(ids) >= 4 ? ids : null;
        } catch {
            return null;
        }
    }

    private static List<int> OpenSlots(IRNSReloaded rns, RValue sys, bool upgrades) {
        var slots = new List<int>();
        var cost = ReadInstanceVar(rns, sys, "storeSlotCost");
        var heal = ReadInstanceVar(rns, sys, "storeSlotHeal");
        var upgrade = ReadInstanceVar(rns, sys, "storeSlotUpgrade");
        if (cost is not RValue costs) return slots;
        var len = Math.Min(ArrayLength(rns, costs), 12);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, costs, i, out var cell)) continue;
            var price = cell.ToInt(-1);
            if (price is <= 0 or >= 1000) continue;
            var isUpgrade = FlagAt(rns, upgrade, i);
            if (upgrades) {
                if (isUpgrade) slots.Add(i);
            } else if (!isUpgrade && !FlagAt(rns, heal, i)) {
                slots.Add(i);
            }
        }
        return slots;
    }

    private static int ShopScalar(IRNSReloaded rns, RValue sys, string name, HashSet<int> abilityIds) {
        foreach (var id in new[] { ScalarOn(rns, sys, name) }) {
            if (ShopWare(id, abilityIds)) return id;
        }
        var pointer = rns.FindGlobalValue(name);
        if (pointer != null && IsLive(*pointer)) {
            var id = UnpackId(rns, *pointer);
            if (ShopWare(id, abilityIds)) return id;
        }
        foreach (var objectName in new[] { "obj_storeInfo", "obj_notchExStore" }) {
            foreach (var inst in FindNamed(rns, objectName, 1)) {
                var id = ScalarOn(rns, inst, name);
                if (ShopWare(id, abilityIds)) return id;
            }
        }
        return -1;
    }

    private static List<int> ShopVector(IRNSReloaded rns, RValue sys, string name, int take, HashSet<int> abilityIds) {
        var found = new List<int>();
        RValue? value = ReadInstanceVar(rns, sys, name);
        if (value is not RValue local || !IsLive(local)) {
            var pointer = rns.FindGlobalValue(name);
            if (pointer != null && IsLive(*pointer)) value = *pointer;
        }
        if (value is not RValue array || array.Type != RValueType.Array) return found;
        var len = Math.Min(ArrayLength(rns, array), take);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, array, i, out var cell)) continue;
            var id = UnpackId(rns, cell);
            if (ShopWare(id, abilityIds)) found.Add(id);
        }
        return found;
    }

    private static int ScalarOn(IRNSReloaded rns, RValue handle, string name) {
        return ReadInstanceVar(rns, handle, name) is RValue value ? UnpackId(rns, value) : -1;
    }

    private static bool ShopWare(int id, HashSet<int> abilityIds) {
        if (!WareId(id, abilityIds)) return false;
        var key = ItemCatalog.Key(id);
        return key.StartsWith("pk_", StringComparison.Ordinal)
            || key.StartsWith("pt_", StringComparison.Ordinal)
            || key.StartsWith("up_", StringComparison.Ordinal);
    }

    // Every priced shop card is already on the shop row of obj_itemSystem.slots.
    // Cell [1] is the square id. The item id for that square is the entry in
    // squareMap / itId (global, or on obj_storeInfo / obj_itemSystem) that
    // matches invinfo_dataId for the hovered slot.
    private static int[] ShopIds(IRNSReloaded rns, RValue sys, HashSet<int> abilityIds, int focus, int focusId) {
        var none = new int[12];
        Array.Fill(none, -1);
        try {
            var squares = ShopSquares(rns, sys);
            int[]? fallback = null;
            foreach (var holder in ShopHolders(rns, sys)) {
                var bySlot = IdsBySlot(rns, holder, squares, abilityIds);
                if (bySlot == null) continue;
                if (focus >= 0 && focusId >= 100 && bySlot[focus] == focusId) return bySlot;
                if (fallback == null && PotionLike(bySlot) >= 4) fallback = bySlot;
            }
            return fallback ?? none;
        } catch {
            return none;
        }
    }

    private static int[] ShopSquares(IRNSReloaded rns, RValue sys) {
        var empty = new int[12];
        Array.Fill(empty, -1);
        if (ReadInstanceVar(rns, sys, "slots") is not RValue grid || grid.Type != RValueType.Array) return empty;
        var rows = ArrayLength(rns, grid);
        for (var r = 0; r < rows; r++) {
            if (!TryIndex(rns, grid, r, out var row) || row.Type != RValueType.Array) continue;
            var len = ArrayLength(rns, row);
            if (len < 9) continue;
            var next = new int[12];
            Array.Fill(next, -1);
            var hits = 0;
            for (var c = 0; c < 12 && c < len; c++) {
                if (!TryIndex(rns, row, c, out var cell) || cell.Type != RValueType.Array) continue;
                if (!TryIndex(rns, cell, 0, out var kind) || kind.ToInt(-1) != 6) continue;
                if (!TryIndex(rns, cell, 1, out var square)) continue;
                var number = square.ToInt(-1);
                if (number < 0) continue;
                next[c] = number;
                hits++;
            }
            if (hits >= 5) return next;
        }
        return empty;
    }

    private static List<RValue> ShopHolders(IRNSReloaded rns, RValue sys) {
        var holders = new List<RValue>();
        foreach (var name in new[] { "squareMap", "itemList", "itId", "sq", "potItId", "pot0" }) {
            var pointer = rns.FindGlobalValue(name);
            if (pointer != null && IsLive(*pointer)) holders.Add(*pointer);
            if (ReadInstanceVar(rns, sys, name) is RValue local && IsLive(local)) holders.Add(local);
        }
        foreach (var objectName in new[] { "obj_itemSystem", "obj_storeInfo", "obj_notchExStore" }) {
            foreach (var inst in FindNamed(rns, objectName, 2)) {
                foreach (var key in OwnKeys(rns, inst, 120)) {
                    if (SkipShopKey(key)) continue;
                    if (ReadInstanceVar(rns, inst, key) is RValue value && IsLive(value)) holders.Add(value);
                }
            }
        }
        return holders;
    }

    private static bool SkipShopKey(string key) {
        return key is "storeSlotCost" or "storeSlotHeal" or "storeSlotOPC" or "storeSlotOPCBin"
            or "storeSlotUpgrade" or "storeSlotUpgradeBin" or "storeSlotUpgradeInd"
            or "storeStringWidth" or "lootStringWidth" or "slots"
            or "highlightRow" or "highlightSlot" or "mouse_focus_i" or "mouse_held_i";
    }

    private static int[]? IdsBySlot(IRNSReloaded rns, RValue holder, int[] squares, HashSet<int> abilityIds) {
        var direct = Project(rns, holder, slot => slot, abilityIds);
        var viaSquare = Project(rns, holder, slot => slot < squares.Length ? squares[slot] : -1, abilityIds);
        if (direct == null) return viaSquare;
        if (viaSquare == null) return direct;
        return PotionLike(viaSquare) > PotionLike(direct) ? viaSquare : direct;
    }

    private static int[]? Project(IRNSReloaded rns, RValue holder, Func<int, int> indexOf, HashSet<int> abilityIds) {
        var ids = new int[12];
        var hits = 0;
        for (var slot = 0; slot < 12; slot++) {
            ids[slot] = -1;
            var index = indexOf(slot);
            if (index < 0) continue;
            var id = UnpackAt(rns, holder, index);
            if (!WareId(id, abilityIds)) continue;
            ids[slot] = id;
            hits++;
        }
        return hits >= 4 ? ids : null;
    }

    private static int UnpackAt(IRNSReloaded rns, RValue holder, int index) {
        if (holder.Type == RValueType.Array) {
            if (!TryIndex(rns, holder, index, out var cell)) return -1;
            return UnpackId(rns, cell);
        }
        if (holder.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return -1;
        var ds = holder.ToInt(-1);
        if (ds is < 0 or > 256) return -1;
        foreach (var function in new[] { "ds_map_find_value", "ds_list_find_value" }) {
            try {
                var found = rns.ExecuteCodeFunction(function, [holder, new RValue(index)]);
                if (found is not RValue cell) continue;
                var id = UnpackId(rns, cell);
                if (id > 0) return id;
            } catch {
                // This id is not that kind of list. The other kind may still hold the card.
            }
        }
        return -1;
    }

    private static int UnpackId(IRNSReloaded rns, RValue cell) {
        if (cell.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            var number = cell.ToInt(-1);
            return number > 0 ? number : -1;
        }
        if (cell.Type is RValueType.Object or RValueType.Ref) {
            foreach (var name in new[] { "itId", "itemId", "dataId", "potItId", "upgradeItemId" }) {
                var raw = ReadOn(rns, cell, name) ?? ReadInstanceVar(rns, cell, name);
                if (raw is not RValue value || value.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) continue;
                var number = value.ToInt(-1);
                if (number > 0) return number;
            }
        }
        if (cell.Type == RValueType.Array && TryIndex(rns, cell, 0, out var first)) return UnpackId(rns, first);
        return -1;
    }

    private static bool WareId(int id, HashSet<int> abilityIds) {
        return id is >= 100 and <= 8000 && !abilityIds.Contains(id);
    }

    private static int PotionLike(int[] ids) {
        var hits = 0;
        foreach (var id in ids) {
            if (id < 100) continue;
            var key = ItemCatalog.Key(id);
            if (key.StartsWith("pk_", StringComparison.Ordinal)
                || key.StartsWith("pt_", StringComparison.Ordinal)
                || key.StartsWith("up_", StringComparison.Ordinal)
                || id is >= 660 and <= 760) {
                hits++;
            }
        }
        return hits;
    }

    private static class DescSheets {
        private static readonly object Gate = new();
        private static bool loaded;
        private static Dictionary<string, string[]> byKey = new(StringComparer.Ordinal);
        private static Dictionary<string, string[]> tokens = new(StringComparer.Ordinal);
        private static Dictionary<string, string[]> phrases = new(StringComparer.Ordinal);
        private static Dictionary<string, string> statusKeys = new(StringComparer.Ordinal);
        private static Dictionary<string, string[]> statuses = new(StringComparer.Ordinal);
        private static Dictionary<string, string[]> titles = new(StringComparer.Ordinal);

        public static string Description(string key, int language) {
            Ensure();
            return byKey.TryGetValue(key, out var names) ? Pick(names, language) : "";
        }

        public static string Phrase(string key, int language) {
            Ensure();
            return phrases.TryGetValue(key, out var names) ? Pick(names, language) : "";
        }

        public static string StatusKey(string token) {
            Ensure();
            var core = token;
            var dash = token.LastIndexOf('-');
            if (dash > 0 && int.TryParse(token[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _)) core = token[..dash];
            return statusKeys.TryGetValue(core, out var key) ? key : "";
        }

        public static string Title(string key, int language) {
            Ensure();
            return titles.TryGetValue(key, out var names) ? Pick(names, language) : "";
        }

        public static string Status(string token, int language) {
            var key = StatusKey(token);
            if (key.Length == 0 || !statuses.TryGetValue(key, out var names)) return "";
            return Pick(names, language).Replace("#", "\n").Replace("\r\n", "\n").Trim();
        }

        public static Dictionary<string, string> Tokens(int language) {
            Ensure();
            var picked = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in tokens) {
                var name = Pick(pair.Value, language);
                if (name.Length > 0) picked[pair.Key] = name;
            }
            return picked;
        }

        private static string Pick(string[] names, int language) {
            if (names.Length == 0) return "";
            var index = language >= 0 && language < names.Length ? language : 0;
            return names[index].Length > 0 ? names[index] : names[0];
        }

        private static void Ensure() {
            if (loaded) return;
            lock (Gate) {
                if (loaded) return;
                loaded = true;
                var dir = GameDir();
                if (dir == null) return;
                var data = Path.Combine(dir, "Data");
                var text = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var file in new[] { "Descriptions_Move.csv", "Descriptions_Item.csv", "Descriptions_Potion.csv" }) {
                    foreach (var row in ReadRows(Path.Combine(data, file), levelColumn: true)) {
                        text.TryAdd(row.Key, row.Names);
                    }
                }
                var words = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var row in ReadRows(Path.Combine(data, "Strings_Item.csv"), levelColumn: false)) {
                    if (row.Token.Length > 0) words.TryAdd(row.Token, row.Names);
                }
                var said = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var file in new[] { "Strings.csv", "Strings2.csv", "Strings_Menu.csv" }) {
                    foreach (var row in ReadRows(Path.Combine(data, file), levelColumn: false)) said.TryAdd(row.Key, row.Names);
                }
                var alias = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in ReadRows(Path.Combine(data, "Names_HBS.csv"), levelColumn: true)) {
                    AddStatusAlias(alias, row.Key, row.Key);
                    if (row.Names.Length > 0) AddStatusAlias(alias, row.Names[0], row.Key);
                }
                var effects = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var row in ReadRows(Path.Combine(data, "Descriptions_HBS.csv"), levelColumn: true)) {
                    effects.TryAdd(row.Key, row.Names);
                }
                var roles = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var row in ReadRows(Path.Combine(data, "Titles_Character.csv"), levelColumn: true)) {
                    roles[row.Key] = row.Names;
                }
                byKey = text;
                tokens = words;
                phrases = said;
                statusKeys = alias;
                statuses = effects;
                titles = roles;
            }
        }

        private static void AddStatusAlias(Dictionary<string, string> alias, string text, string key) {
            if (text.Length == 0 || key.Length == 0) return;
            var cleaned = text.ToUpperInvariant().Replace(' ', '-');
            if (cleaned.StartsWith("HBS_", StringComparison.Ordinal)) cleaned = cleaned[4..];
            if (cleaned.Length > 0) alias.TryAdd(cleaned, key);
        }

        private static string? GameDir() {
            try {
                var path = Environment.ProcessPath;
                var dir = path == null ? null : Path.GetDirectoryName(path);
                if (dir != null && File.Exists(Path.Combine(dir, "Data", "Descriptions_Item.csv"))) return dir;
            } catch {
                return null;
            }
            return null;
        }

        private readonly struct Row {
            public string Key { get; init; }
            public string Token { get; init; }
            public string[] Names { get; init; }
        }

        private static List<Row> ReadRows(string path, bool levelColumn) {
            var rows = new List<Row>();
            if (!File.Exists(path)) return rows;
            foreach (var line in File.ReadAllLines(path)) {
                if (line.Length == 0 || line.StartsWith(',')) continue;
                var fields = SplitCsv(line);
                if (fields.Count == 0 || fields[0].Length == 0 || fields[0] == "key") continue;
                var start = levelColumn ? 2 : 1;
                var names = new string[6];
                for (var i = 0; i < names.Length; i++) names[i] = fields.Count > start + i ? fields[start + i] : "";
                var token = levelColumn ? "" : fields.Count > 1 ? fields[1] : "";
                rows.Add(new Row { Key = fields[0], Token = token, Names = names });
            }
            return rows;
        }

        private static List<string> SplitCsv(string line) {
            var fields = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++) {
                var c = line[i];
                if (quoted) {
                    if (c == '"') {
                        if (i + 1 < line.Length && line[i + 1] == '"') {
                            sb.Append('"');
                            i++;
                        } else {
                            quoted = false;
                        }
                    } else {
                        sb.Append(c);
                    }
                } else if (c == '"') {
                    quoted = true;
                } else if (c == ',') {
                    fields.Add(sb.ToString());
                    sb.Clear();
                } else {
                    sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields;
        }
    }
}
