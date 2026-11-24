// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;

namespace RNSClipboardProbe;

internal static unsafe partial class Snapshot {
    private readonly struct FieldHit {
        public string Field { get; init; }
        public string Raw { get; init; }
        public string Name { get; init; }
        public string Key { get; init; }
    }

    private readonly struct SuffixChoice {
        public int Skill { get; init; }
        public int Slot { get; init; }
        public string Field { get; init; }
        public string Raw { get; init; }
        public string Name { get; init; }
        public string MoveKey { get; init; }
    }

    // Shop cards are counted from 1, matching the row on screen.
    // Card 6 is the primary upgrade (store slot 5, storeSlotUpgradeInd 1) and its
    // name ends with skill 1. Card 7 is the secondary upgrade and ends with skill 2.
    private static int SuffixSkill(int slot, int kind) {
        if (kind is 1 or 2) return kind;
        if (slot == 5) return 1;
        if (slot == 6) return 2;
        return 0;
    }

    private static void ApplyShopNames(IRNSReloaded rns, List<Offer> shop, List<Offer> abilities) {
        if (shop.Count == 0) return;
        var choices = SkillSuffixes(rns, abilities);
        var systems = FindNamed(rns, "obj_itemSystem", 1);
        var hasSys = systems.Count > 0;
        var sys = hasSys ? systems[0] : default;
        var ind = hasSys ? ReadInstanceVar(rns, sys, "storeSlotUpgradeInd") : null;
        var potionSlots = hasSys ? OpenSlots(rns, sys, upgrades: false) : [];
        for (var i = 0; i < shop.Count; i++) {
            var offer = shop[i];
            if (offer.Name.Length > 0) continue;
            var kind = 0;
            if (ind is RValue kinds && TryIndex(rns, kinds, offer.Slot, out var cell)) kind = cell.ToInt(0);
            var skill = SuffixSkill(offer.Slot, kind);
            if (skill is 1 or 2) {
                var choice = choices.FirstOrDefault(item => item.Skill == skill && !string.IsNullOrEmpty(item.Name));
                if (!string.IsNullOrEmpty(choice.Name)) {
                    var key = LooksLikeKey(choice.Raw) ? choice.Raw : offer.Key;
                    if (key.Length == 0) key = choice.MoveKey ?? "";
                    shop[i] = new Offer(offer.Slot, key, choice.Name, offer.Price, offer.Id);
                    continue;
                }
            }
            var labeled = WareName(rns, offer.Slot, kind, potionSlots);
            if (string.IsNullOrEmpty(labeled.Name)) continue;
            shop[i] = new Offer(offer.Slot, labeled.Key.Length > 0 ? labeled.Key : offer.Key, labeled.Name, offer.Price, offer.Id);
        }
    }

    private static void WriteSkillSuffix(StringBuilder sb, IRNSReloaded rns, List<Offer> abilities) {
        foreach (var field in WareFields) {
            var hits = FindFields(rns, field);
            if (hits.Count == 0) {
                sb.Append(field).AppendLine("=(missing)");
                continue;
            }
            foreach (var hit in hits) {
                sb.Append(hit.Field).Append(" raw=").Append(Shown(hit.Raw));
                sb.Append(" key=").Append(Shown(hit.Key));
                sb.Append(" name=").AppendLine(Shown(hit.Name));
            }
        }
        for (var skill = 1; skill <= 2; skill++) {
            var skillName = SkillLabel(rns, abilities, skill);
            sb.Append("skill ").Append(skill.ToString(CultureInfo.InvariantCulture));
            sb.Append(" name=").AppendLine(Shown(skillName));
            foreach (var text in StringsEnding(rns, skillName)) {
                sb.Append("  endsWith=").AppendLine(Shown(text));
            }
        }
        var choices = SkillSuffixes(rns, abilities);
        if (choices.Count == 0) {
            sb.AppendLine("chosen=(none)");
            return;
        }
        foreach (var choice in choices) {
            sb.Append("skill ").Append(choice.Skill.ToString(CultureInfo.InvariantCulture));
            sb.Append(" shopSlot ").Append((choice.Slot + 1).ToString(CultureInfo.InvariantCulture));
            sb.Append(" field=").Append(choice.Field);
            sb.Append(" raw=").Append(Shown(choice.Raw));
            sb.Append(" move=").Append(Shown(choice.MoveKey));
            sb.Append(" name=").AppendLine(Shown(choice.Name));
        }
    }

    private static readonly string[] WareFields = [
        "pot0", "pot1", "pot2",
        "itemKey2", "itemKey3", "itemKey4", "itemKey5", "itemKey6", "itemKey7", "itemKey8",
        "upgradeP", "upgradeS", "upgradeSp", "upgradeD",
        "upgradeElm0", "upgradeElm1", "upgradeElm2", "upgradeElm3",
    ];

    private static List<SuffixChoice> SkillSuffixes(IRNSReloaded rns, List<Offer> abilities) {
        var choices = new List<SuffixChoice>();
        var token = ClassToken(rns, abilities);
        for (var skill = 1; skill <= 2; skill++) {
            var skillName = SkillLabel(rns, abilities, skill);
            if (skillName.Length == 0) continue;
            var fields = skill == 1
                ? new[] { "upgradeP", "upgradeElm0", "itemKey6", "itemKey5" }
                : new[] { "upgradeS", "upgradeElm1", "itemKey7", "itemKey6" };
            FieldHit? ware = null;
            foreach (var field in fields) {
                var hit = FindFields(rns, field).FirstOrDefault(item => !string.IsNullOrEmpty(item.Raw));
                if (string.IsNullOrEmpty(hit.Raw)) continue;
                ware ??= hit;
                if (EndsWithSkill(hit.Name, skillName)) {
                    choices.Add(new SuffixChoice {
                        Skill = skill,
                        Slot = 4 + skill,
                        Field = hit.Field,
                        Raw = hit.Raw,
                        Name = hit.Name,
                        MoveKey = hit.Key,
                    });
                    ware = null;
                    break;
                }
            }
            if (ware is not FieldHit held || string.IsNullOrEmpty(held.Raw)) continue;
            var moveKey = GemMoveKey(token, skill, held.Key.Length > 0 ? held.Key : held.Raw);
            var moveName = moveKey.Length == 0 ? "" : ItemCatalog.NameForKey(moveKey, LanguageIndex(rns));
            if (!EndsWithSkill(moveName, skillName)) continue;
            choices.Add(new SuffixChoice {
                Skill = skill,
                Slot = 4 + skill,
                Field = held.Field,
                Raw = held.Raw,
                Name = moveName,
                MoveKey = moveKey,
            });
        }
        return choices;
    }

    private static FieldHit WareName(IRNSReloaded rns, int slot, int kind, List<int> potionSlots) {
        var fields = new List<string>();
        if (kind == 3) fields.AddRange(["itemKey8", "upgradeSp", "upgradeElm2"]);
        else if (kind == 4) fields.AddRange(["upgradeD", "upgradeElm3"]);
        else {
            var potion = potionSlots.IndexOf(slot);
            if (potion >= 0) fields.Add("pot" + potion.ToString(CultureInfo.InvariantCulture));
            if (slot is >= 2 and <= 8) fields.Add("itemKey" + slot.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var field in fields) {
            var hit = FindFields(rns, field).FirstOrDefault(item => !string.IsNullOrEmpty(item.Name) && !item.Name.StartsWith("mv_", StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(hit.Name)) return hit;
        }
        return default;
    }

    private static List<FieldHit> FindFields(IRNSReloaded rns, string name) {
        var found = new List<FieldHit>();
        void Add(string source, RValue value) {
            if (!Present(value) || found.Count >= 4) return;
            var hit = DescribeField(rns, source, value);
            if (string.IsNullOrEmpty(hit.Raw)) return;
            if (found.Any(item => item.Raw == hit.Raw && item.Name == hit.Name)) return;
            found.Add(hit);
        }
        var pointer = rns.FindGlobalValue(name);
        if (pointer != null) Add("global." + name, *pointer);
        try {
            var got = rns.ExecuteCodeFunction("variable_global_get", [name]);
            if (got is RValue global) Add("variable_global_get." + name, global);
        } catch {
            // The name is an instance field on one of the holders below.
        }
        foreach (var objectName in new[] { "obj_itemSystem", "obj_storeInfo", "obj_notchExStore", "obj_itemUpgrade", "obj_runMenu" }) {
            foreach (var inst in FindNamed(rns, objectName, 2)) {
                if (ReadInstanceVar(rns, inst, name) is RValue local) Add(objectName + "." + name, local);
            }
        }
        if (found.Count > 0) return found;
        var count = Math.Clamp(rns.ExecuteCodeFunction("instance_number", [AllObjects])?.ToInt(0) ?? 0, 0, 400);
        for (var i = 0; i < count && found.Count < 2; i++) {
            RValue handle;
            try {
                var id = rns.ExecuteCodeFunction("instance_find", [AllObjects, i]);
                if (id is not RValue live || !Present(live)) continue;
                handle = live;
            } catch {
                continue;
            }
            if (ReadInstanceVar(rns, handle, name) is not RValue value) continue;
            Add(ObjectName(rns, handle) + "." + name, value);
        }
        return found;
    }

    private static FieldHit DescribeField(IRNSReloaded rns, string source, RValue value) {
        var language = LanguageIndex(rns);
        if (value.Type == RValueType.String) {
            var text = ReadString(rns, value);
            if (text.Length == 0) return default;
            var key = LooksLikeKey(text) ? text : "";
            var name = key.Length > 0 ? ItemCatalog.NameForKey(key, language) : text;
            if (name.Length == 0 && key.Length > 0 && IdFromKey(rns, key) is int mapped) name = NameFromId(rns, mapped);
            if (name.Length == 0) name = text;
            return new FieldHit { Field = source, Raw = text, Name = name, Key = key };
        }
        if (value.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            var id = value.ToInt(-1);
            if (id <= 0) return default;
            var key = KeyFromId(rns, id);
            if (key.Length == 0) key = ItemCatalog.Key(id);
            var name = NameFromId(rns, id);
            if (name.Length == 0 && key.Length > 0) name = ItemCatalog.NameForKey(key, language);
            return new FieldHit {
                Field = source,
                Raw = id.ToString(CultureInfo.InvariantCulture),
                Name = name,
                Key = key,
            };
        }
        if (value.Type is RValueType.Object or RValueType.Ref) {
            foreach (var child in new[] { "itemKey", "key", "itKey", "name", "itemId", "dataId", "itId" }) {
                var inner = ReadOn(rns, value, child) ?? ReadInstanceVar(rns, value, child);
                if (inner is not RValue nested) continue;
                var hit = DescribeField(rns, source + "." + child, nested);
                if (!string.IsNullOrEmpty(hit.Raw)) return hit;
            }
        }
        return default;
    }

    private static string SkillLabel(IRNSReloaded rns, List<Offer> abilities, int number) {
        foreach (var skill in abilities) {
            var stats = SkillStats(rns, skill);
            if (SkillNumber(stats, skill.Slot) != number) continue;
            if (skill.Name.Length > 0) return skill.Name;
            if (skill.Key.Length > 0) return ItemCatalog.NameForKey(skill.Key, LanguageIndex(rns));
        }
        return "";
    }

    private static string ClassToken(IRNSReloaded rns, List<Offer> abilities) {
        foreach (var skill in abilities) {
            var token = TokenIn(skill.Key);
            if (token.Length > 0) return token;
        }
        var anim = PlayerText(rns, "animationKey", "animKey");
        return TokenIn(anim);
    }

    private static string TokenIn(string key) {
        if (key.StartsWith("mv_", StringComparison.Ordinal)) key = key[3..];
        else if (key.StartsWith("anim_", StringComparison.Ordinal)) key = key[5..];
        else return "";
        var cut = key.IndexOf('_');
        return cut > 0 ? key[..cut] : "";
    }

    private static string GemMoveKey(string classToken, int skill, string ware) {
        if (classToken.Length == 0 || skill is < 1 or > 4 || ware.Length == 0) return "";
        var gem = GemToken(ware);
        if (gem.Length == 0) return "";
        return "mv_" + classToken + "_" + (skill - 1).ToString(CultureInfo.InvariantCulture) + "_" + gem;
    }

    private static string GemToken(string ware) {
        var lower = ware.ToLowerInvariant();
        if (lower.Contains("sapphire") || lower.Contains("blue")) return "sapphire";
        if (lower.Contains("emerald") || lower.Contains("green")) return "emerald";
        if (lower.Contains("ruby") || lower.Contains("red")) return "ruby";
        if (lower.Contains("garnet") || lower.Contains("yellow")) return "garnet";
        if (lower.Contains("opal") || lower.Contains("purple")) return "opal";
        if (lower.Contains("diamond")) return "diamond";
        return "";
    }

    private static List<string> StringsEnding(IRNSReloaded rns, string skill) {
        var found = new List<string>();
        if (skill.Length == 0) return found;
        void Consider(string text) {
            if (found.Count >= 8 || !EndsWithSkill(text, skill) || found.Contains(text)) return;
            found.Add(text);
        }
        foreach (var name in ItemCatalog.NamesEnding(skill, LanguageIndex(rns))) Consider(name);
        var map = Live(rns.FindGlobalValue("langNameMap"));
        if (map.Type == RValueType.Array) {
            var len = Math.Min(ArrayLength(rns, map), 2500);
            for (var i = 0; i < len && found.Count < 8; i++) {
                if (!TryIndex(rns, map, i, out var entry)) continue;
                Consider(FirstTextValue(rns, entry, 0));
            }
        }
        return found;
    }

    private static bool EndsWithSkill(string name, string skill) {
        return skill.Length > 0 && name.Length > skill.Length && name.EndsWith(skill, StringComparison.Ordinal);
    }

    private static string PlayerText(IRNSReloaded rns, params string[] names) {
        var global = rns.GetGlobalInstance();
        if (global == null) return "";
        var bodies = Live(rns.FindValue(global, "player"));
        if (!TryIndex(rns, bodies, 0, out var team)) return "";
        if (!TryIndex(rns, team, 0, out var body)) body = team;
        foreach (var name in names) {
            var text = ReadTextOn(rns, body, name);
            if (text.Length > 0) return text;
            if (body.Type == RValueType.Object) {
                var direct = TextField(rns, body, name);
                if (direct.Length > 0) return direct;
            }
        }
        return "";
    }
}
