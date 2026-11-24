// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
namespace RNSClipboardProbe;

internal sealed record F8Source(Func<string, double?> Number, Func<string, string> Text, Func<string, double[]> Numbers, Func<string, bool?> Boolean);

internal static class F8Capture {
    public static F8Run Capture(F8Source source, F8Catalog catalog) {
        double? Num(string path) => source.Number(path);
        string Text(string path) => source.Text(path);
        bool Flag(string path, bool fallback) => source.Boolean(path) ?? fallback;
        int Count(string path) => (int)(Num(path + ".Length") ?? 0);
        double[] NumericArray(string path) => source.Numbers(path);
        int FindIndex(string path, string key) {
            for (var i = 0; i < Count(path); i++) if (Text(path + "[" + i + "]") == key) return i;
            return -1;
        }
        var languageIndex = FindIndex("obj_config_manager.configSaveKey", "language");
        var language = languageIndex >= 0 ? (int)(Num("global.config[" + languageIndex + "]") ?? -1) : -1;
        if (language is < 0 or > 5) {
            var label = Text("global.languageMap[?\"menuoptions_language\"]");
            language = Enumerable.Range(0, 6).FirstOrDefault(i => catalog.Phrase("menuoptions_language", i) == label, 0);
        }
        var difficultyIndex = FindIndex("obj_save_manager.saveInfoKey", "difficulty_v2");
        var difficulty = difficultyIndex < 0 ? null : Num("global.saveInfo[" + difficultyIndex + "]");
        var match = Regex.Match(Text("global.player[0][0].animationKey"), @"^anim_([^_]+)_");
        var classToken = match.Success ? match.Groups[1].Value : "";
        var stageKey = "";
        var stageId = Num("global.stageCurrent");
        // stageCurrent identifies the place; hallwayCurrent is the zero-based run ordinal.
        for (var i = 0; i < Count("global.hallwayData"); i++) {
            if (Num("global.hallwayData[" + i + "][1]") != stageId) continue;
            var key = Text("global.hallwayData[" + i + "][0]");
            if (!key.StartsWith("hw_", StringComparison.Ordinal)) continue;
            key = "stg_" + key[3..];
            if (catalog.Phrase(key, 0).Length == 0) continue;
            stageKey = catalog.Phrase(key + "2", 0).Length > 0 ? key + "2" : key;
            break;
        }
        var hallway = Num("global.hallwayCurrent");
        var run = new F8Run {
            Language = language,
            Hp = Num("global.playerHp[0][0]"), MaxHp = Num("global.playerStat[0][0][1]"),
            Gold = Num("global.playerGold[0]"), Level = Num("global.playerLv[0]"),
            Difficulty = difficulty == null ? null : (int)difficulty.Value,
            ClassKey = classToken.Length > 0 ? "al_" + classToken + "_rabbit" : "",
            Stage = hallway == null ? null : (int)hallway.Value + 1, StageKey = stageKey,
            ShopOpen = !Flag("obj_itemSystem.storeInvHidden", true),
            LootOpen = !Flag("obj_itemSystem.lootInvHidden", true),
        };
        F8Entry? Entry(int id, int slot) {
            if (id <= 0 || id >= Count("global.itemData")) return null;
            var root = "global.itemData[" + id + "][0]";
            var key = Text(root + "[0]");
            if (key.Length == 0 || key.StartsWith("mv_none", StringComparison.Ordinal)) return null;
            // Entries are per slot, so two copies of an item retain distinct counters.
            return new F8Entry {
                Id = id, Slot = slot, Key = key, LiveName = Text(root + "[2]"), LiveTemplate = Text(root + "[3]"),
                Cd = NumericArray("global.itemCd[" + id + "][0]"), Hb = NumericArray("global.itemHitbox[" + id + "][0]"),
            };
        }
        List<F8Entry> ReadRow(int row, bool prices = false) {
            var list = new List<F8Entry>();
            var root = "obj_itemSystem.slots[" + row + "]";
            for (var slot = 0; slot < Math.Min(Count(root), 128); slot++) {
                var sq = Num(root + "[" + slot + "][1]");
                if (sq == null || sq < 0) continue;
                var map = "global.squareMap[?" + F8Renderer.Number(sq.Value) + "]";
                var id = Num(map + "[1]");
                if (id == null || Entry((int)id.Value, slot) is not F8Entry entry) continue;
                entry.Counter = Num(map + "[9]");
                if (prices) entry.Price = Num("obj_itemSystem.storeSlotCost[" + slot + "]");
                if (row == 7) {
                    // Catalog arrays stay untouched; the square's numbers are the run-adjusted values.
                    entry.AdjustedCd = (double[])entry.Cd.Clone();
                    entry.Damage = Num(map + "[5]");
                    if (entry.AdjustedCd.Length > 2) {
                        entry.AdjustedCd[1] = Num(map + "[8]") ?? entry.Cd[1];
                        entry.AdjustedCd[2] = Num(map + "[7]") ?? entry.Cd[2];
                    }
                    for (var h = 0; h < 64; h++) {
                        var hotbar = "obj_hotbarSlot[" + h + "]";
                        if (Num(hotbar + ".itemRowId") != row || Num(hotbar + ".slotId") != slot || Num(hotbar + ".dataId") != entry.Id) continue;
                        entry.Stock = Num(hotbar + ".stock");
                        entry.AdjustedHb = NumericArray(hotbar + ".hbData");
                        var cd = NumericArray(hotbar + ".cdData");
                        if (cd.Length > 6 && entry.AdjustedCd.Length > 6) entry.AdjustedCd[6] = cd[6];
                        break;
                    }
                }
                var otherKey = Text("global.itemData[" + entry.Id + "][0][19]");
                var otherId = Num("global.itemData[" + entry.Id + "][0][20]");
                if (otherKey.Length > 0) {
                    if (otherId == null || otherId <= 0 || Text("global.itemData[" + (int)otherId.Value + "][0][0]") != otherKey) {
                        otherId = null;
                        for (var n = 1; n < Count("global.itemData"); n++) {
                            if (Text("global.itemData[" + n + "][0][0]") == otherKey) { otherId = n; break; }
                        }
                    }
                    if (otherId != null && (int)otherId.Value != entry.Id) entry.OtherFace = Entry((int)otherId.Value, slot);
                }
                list.Add(entry);
            }
            return list;
        }
        run.Skills.AddRange(ReadRow(7).Where(x => x.Slot < 4));
        run.Carried.AddRange(ReadRow(11));
        if (run.ShopOpen) run.Shop.AddRange(ReadRow(2, true));
        if (run.LootOpen) run.Loot.AddRange(ReadRow(1));
        foreach (var ware in run.Shop) {
            if (!ware.Key.StartsWith("up_", StringComparison.Ordinal)) continue;
            var slot = Num("global.itemData[" + ware.Id + "][0][5]");
            var offset = Num("global.itemData[" + ware.Id + "][0][7]");
            var skill = run.Skills.FirstOrDefault(x => x.Slot == slot);
            if (skill == null || offset == null) continue;
            var oldOffset = Num("global.itemData[" + skill.Id + "][0][6]") ?? 0;
            ware.Upgrade = Entry(skill.Id - (int)oldOffset + (int)offset.Value, skill.Slot);
        }
        return run;
    }
}
