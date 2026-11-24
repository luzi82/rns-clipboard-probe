// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using RNSClipboardProbe;

var checks = 0;
void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks++; }
// Synthetic translations and stats keep the public tests independent of game files.
var temp = Path.Combine(Path.GetTempPath(), "rns-clipboard-probe-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try {
    void Csv(string file, string rows, bool level = true) => File.WriteAllText(Path.Combine(temp, file),
        (level ? "key,level,English,Japanese,Chinese,Korean,Ukrainian,French\n" : "key,English,Japanese,Chinese,Korean,Ukrainian,French\n") + rows);
    Csv("Names_Move.csv", "mv_demo_0,0,Demo Strike,,测试攻击\nmv_demo_0_sapphire,0,Sapphire Demo Strike,,蓝宝石测试攻击\n");
    Csv("Descriptions_Move.csv", "mv_demo_0,0,Deals [STR] damage. [CHARGES] an ability.,,造成[STR]伤害。[CHARGES]技能。\nmv_demo_0_sapphire,0,Grants [ASTRA] for [VAR0_SECONDS].,,给予[VAR0_SECONDS]的[ASTRA]。\n");
    Csv("Names_Item.csv", "it_demo,0,Demo Relic,,测试道具\nit_other,0,Other Relic,,另一道具\n");
    Csv("Descriptions_Item.csv", "it_demo,0,Changes form.,,转换形态。\nit_other,0,Another form.,,另一形态。\n");
    Csv("Names_Potion.csv", "up_demo,0,Demo Upgrade,,测试升级\n");
    Csv("Descriptions_Potion.csv", "up_demo,0,Upgrades an ability.,,升级技能。\n");
    Csv("Titles_Character.csv", "al_demo_rabbit,0,the Demo Rabbit,,测试兔子\nal_demo_rabbit,1,Demo Rabbit,,测试兔子\n");
    Csv("Strings_Menu.csv", "menuchar_difficulty_0,CUTE,,萌萌哒\nmenuchar_difficulty_2,HARD,,困难\nmenuoptions_language,Language,,语言\n", false);
    Csv("Strings.csv", "stg_demo,Demo#Area,,测试区域\nstg_demo2,Demo Area,,测试区域\ninfodraw_charge,Charge deals dmgAmx damage.,,充能造成dmgAm倍伤害。\n", false);
    Csv("Strings_Item.csv", "itdesc_charge,CHARGE,,充能\nitdesc_charges,CHARGES,,充能\nitdesc_astra,ASTRA,,星光\n", false);
    Csv("Names_HBS.csv", "hbs_astra,0,ASTRA,,星光\n");
    Csv("Descriptions_HBS.csv", "hbs_astra,0,A synthetic test status.,,测试状态。\n");
    var catalog = new F8Catalog(temp);
    var numbers = new Dictionary<string, double> {
        ["obj_config_manager.configSaveKey.Length"] = 1, ["global.config[0]"] = 2,
        ["obj_save_manager.saveInfoKey.Length"] = 1, ["global.saveInfo[0]"] = 2,
        ["global.playerHp[0][0]"] = 5, ["global.playerStat[0][0][1]"] = 7,
        ["global.playerGold[0]"] = 46, ["global.playerLv[0]"] = 6,
        ["global.hallwayData.Length"] = 1, ["global.hallwayData[0][1]"] = 9,
        ["global.hallwayCurrent"] = 0, ["global.stageCurrent"] = 9, ["global.itemData.Length"] = 20,
        ["obj_itemSystem.slots[7].Length"] = 1, ["obj_itemSystem.slots[7][0][1]"] = 100,
        ["global.squareMap[?100][1]"] = 1, ["global.squareMap[?100][5]"] = 231,
        ["global.squareMap[?100][7]"] = 400, ["global.squareMap[?100][8]"] = 250,
        ["obj_hotbarSlot[0].itemRowId"] = 7, ["obj_hotbarSlot[0].slotId"] = 0,
        ["obj_hotbarSlot[0].dataId"] = 1, ["obj_hotbarSlot[0].stock"] = 1,
        ["obj_itemSystem.slots[11].Length"] = 1, ["obj_itemSystem.slots[11][0][1]"] = 101,
        ["global.squareMap[?101][1]"] = 5, ["global.itemData[5][0][20]"] = -1,
        ["obj_itemSystem.slots[2].Length"] = 1, ["obj_itemSystem.slots[2][0][1]"] = 102,
        ["global.squareMap[?102][1]"] = 10, ["obj_itemSystem.storeSlotCost[0]"] = 24,
        ["global.itemData[10][0][5]"] = 0, ["global.itemData[10][0][7]"] = 1,
        ["global.itemData[1][0][6]"] = 0
    };
    var texts = new Dictionary<string, string> {
        ["obj_config_manager.configSaveKey[0]"] = "language", ["obj_save_manager.saveInfoKey[0]"] = "difficulty_v2",
        ["global.player[0][0].animationKey"] = "anim_demo_rabbit0", ["global.hallwayData[0][0]"] = "hw_demo",
        ["global.itemData[1][0][0]"] = "mv_demo_0", ["global.itemData[2][0][0]"] = "mv_demo_0_sapphire",
        ["global.itemData[5][0][0]"] = "it_demo", ["global.itemData[6][0][0]"] = "it_other",
        ["global.itemData[5][0][19]"] = "it_other", ["global.itemData[10][0][0]"] = "up_demo"
    };
    var cd = new double[] { 0, 8000, 1400, 0, 1, 1, 2, 0 };
    var hb = new double[22]; hb[4] = 220; hb[17] = 400;
    var source = new F8Source(p => numbers.TryGetValue(p, out var n) ? n : null,
        p => texts.GetValueOrDefault(p, ""), p => p.Contains("Cd") || p.EndsWith(".cdData") ? (double[])cd.Clone() : (double[])hb.Clone(),
        p => p == "obj_itemSystem.storeInvHidden" ? false : p == "obj_itemSystem.lootInvHidden" ? true : null);
    var run = F8Capture.Capture(source, catalog);
    Check(run.Hp == 5 && run.MaxHp == 7 && run.Difficulty == 2, "HP, maximum HP, keyed difficulty");
    Check(run.Stage == 1 && run.StageKey == "stg_demo2", "Extra region ordinal and place are independent");
    Check(run.Skills.Count == 1 && run.Carried.Count == 1 && run.Shop.Count == 1, "Slot classification");
    Check(run.Shop[0].Price == 24 && run.Shop[0].Upgrade?.Id == 2, "Upgrade preview and price");
    Check(run.Carried[0].OtherFace?.Id == 6, "Linked face lookup before resolved ID exists");
    var render = new F8Renderer(catalog);
    var output = render.Render(run);
    Check(output.Contains("HP: 5 / 7") && output.Contains("困难 / HARD"), "Bilingual summary");
    Check(output.Contains("Demo Strike") && output.Contains("测试攻击"), "Bilingual ability name");
    Check(output.Contains("伤害 231  冷却 0.25秒") && output.Contains("基础伤害 220  冷却 8.0秒"), "Separate adjusted/base values and millisecond conversion");
    Check(output.Contains("次数上限 2") && output.Contains("当前可用次数: 1"), "Stock differs from capacity");
    Check(output.Contains("充能（*CHARGES*）") && output.Contains("1.5x") && output.Contains("1.5倍"), "Bilingual charge emphasis and explanation");
    Check(output.Contains("星光（*ASTRA*）") && output.Contains("A synthetic test status."), "Status glossary includes preview statuses");
    Check(output.Contains("0.4秒") && !output.Contains("[STR]") && !output.Contains("[VAR"), "Resolved time placeholders");
    numbers["global.config[0]"] = 0;
    var english = render.Render(F8Capture.Capture(source, catalog));
    Check(!english.Contains("测试") && Regex.Matches(english, "### Primary: Demo Strike").Count == 1, "English is not duplicated");
    Check(F8Renderer.Seconds(200, 0) == "0.2s" && F8Renderer.Seconds(0, 2) == "0.0秒", "Time units");
    var parsed = QuerySyntax.Parse("global.squareMap[?15].Length");
    Check(parsed.Global && parsed.Steps[0].Kind == PathKind.MapKey && parsed.Steps[1].Name == "Length", "Typed map path");
    foreach (var invalid in new[] { "global.a=1", "global.a();", "global.a[-]", "obj[-1]", "global.a[NaN]" }) {
        var rejected = false;
        try { QuerySyntax.Parse(invalid); } catch (FormatException) { rejected = true; }
        Check(rejected, "Reject invalid query: " + invalid);
    }
    // All six locales must use CSV labels where available and local mod labels elsewhere.
    var csvKeys = new[] { "weapontype_primary", "itemtype_ability", "menuinp_items", "talkact_shop", "weapontype_loot", "menumod_sheet_hbs", "infodraw_short_header", "infodraw_strength_sm", "infodraw_strength", "infodraw_gcd", "infodraw_maxstock" };
    foreach (var key in csvKeys) File.AppendAllText(Path.Combine(temp, "Strings.csv"), key + "," + string.Join(",", Enumerable.Range(0, 6).Select(i => "CSV_" + i + "_" + key)) + "\n");
    File.AppendAllText(Path.Combine(temp, "Strings.csv"), "weapontype_secondary,English fallback,,,,,\n");
    File.AppendAllText(Path.Combine(temp, "Strings.csv"), "infodraw_short_cooldown," + string.Join(",", Enumerable.Range(0, 6).Select(i => "CSV_" + i + "_cooldown: amNums")) + "\n");
    File.AppendAllText(Path.Combine(temp, "Strings.csv"), "infodraw_short_numsec,numSecs,numSec秒,numSec秒,numSec초,numSec с,numSecs\n");
    var localizedCatalog = new F8Catalog(temp);
    for (var lang = 0; lang < 6; lang++) {
        numbers["global.config[0]"] = lang;
        var localizedRun = F8Capture.Capture(source, localizedCatalog);
        localizedRun.Carried.Add(new F8Entry { Key = "it_heavens_codex", Counter = 3, OtherFace = new F8Entry { Key = "it_hells_codex" } });
        var localized = new F8Renderer(localizedCatalog).Render(localizedRun);
        Check(localized.Contains("CSV_" + lang + "_weapontype_primary") && localized.Contains("CSV_" + lang + "_infodraw_short_header"), "CSV ability labels and adjusted header: " + lang);
        Check(localized.Contains("CSV_" + lang + "_cooldown 0.25"), "CSV template label is reused without leaking placeholders: " + lang);
        Check(localized.Contains(F8Labels.Fallback("base", lang)) && localized.Contains(F8Labels.Fallback("counterUnverified", lang)), "Mod-specific labels use the active language: " + lang);
        Check(F8Labels.Get(localizedCatalog, "secondary", lang) == "English fallback", "Missing CSV translation falls back to English: " + lang);
        if (lang != 2) Check(!new[] { "基础数值", "调整数值", "当前可用次数", "售价", "目前形态", "另一形态", "升级后技能", "说明不可用", "尚未验证" }.Any(localized.Contains), "No Chinese UI leakage: " + lang);
    }
    Check(F8Labels.Seconds(400, 3, localizedCatalog) == "0.4초" && F8Labels.Seconds(400, 4, localizedCatalog) == "0.4 с", "Korean and Ukrainian CSV time units");
    Check(F8Labels.Times(2, 1) == "2回" && F8Labels.Times(2, 3) == "2회" && F8Labels.Times(2, 5) == "2 fois", "Localized counts");
    numbers["global.config[0]"] = 99;
    Check(F8Capture.Capture(source, localizedCatalog).Language == 0, "Unknown language with no UI label falls back to English");
    Check(F8Labels.Fallback("base", -1) == "Base" && F8Labels.Fallback("base", 6) == "Base", "Invalid renderer locales use English");
    if (args.Length > 0) {
        using var pe = new PEReader(File.OpenRead(args[0]));
        var reader = pe.GetMetadataReader();
        var types = reader.TypeDefinitions.Select(h => reader.GetString(reader.GetTypeDefinition(h).Name)).ToHashSet();
        var fields = reader.FieldDefinitions.Select(h => reader.GetString(reader.GetFieldDefinition(h).Name)).ToHashSet();
        var methods = reader.MethodDefinitions.Select(h => reader.GetString(reader.GetMethodDefinition(h).Name)).ToHashSet();
        var references = reader.TypeReferences.Select(h => reader.GetString(reader.GetTypeReference(h).Namespace)).ToHashSet();
        Check(types.Contains("Mod") && types.Contains("F8Renderer"), "Release contains mod and F8");
        Check(!types.Contains("ProbeListen") && !references.Contains("System.Net.Sockets"), "TCP listener is absent from release binary");
        Check(!types.Contains("WindowPng") && !types.Contains("SampleDump"), "Screenshot and dump implementations are absent");
        Check(!fields.Contains("SaveHotkeyVirtualKey") && !fields.Contains("_f9Down") && !methods.Contains("SaveSample"), "F9 handler is absent");
        Check(!fields.Contains("TcpPort") && !fields.Contains("TcpListen"), "TCP diagnostics constants are absent");
    }
    Console.WriteLine($"Passed {checks} release checks.");
} finally {
    // Only delete the unique temporary directory created by this test.
    var resolved = Path.GetFullPath(temp);
    if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolved).StartsWith("rns-clipboard-probe-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
}
