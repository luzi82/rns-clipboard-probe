// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
namespace RNSClipboardProbe;

// Column order matches the game's language setting: en, ja, zh, ko, uk, fr.
// Use shipped UI translations where the meaning matches. Only mod-specific text
// needs a full table here; missing CSV translations fall back to English.
internal static class F8Labels {
    private static readonly Dictionary<string, (string Key, string Placeholder)> GameKeys = new() {
        ["primary"] = ("weapontype_primary", ""), ["secondary"] = ("weapontype_secondary", ""),
        ["special"] = ("weapontype_special", ""), ["defensive"] = ("weapontype_defensive", ""),
        ["abilities"] = ("itemtype_ability", ""), ["carried"] = ("menuinp_items", ""),
        ["shop"] = ("talkact_shop", ""), ["loot"] = ("weapontype_loot", ""),
        ["statuses"] = ("menumod_sheet_hbs", ""), ["none"] = ("itemtype_none", ""),
        ["adjusted"] = ("infodraw_short_header", ""), ["damage"] = ("infodraw_strength_sm", ""),
        ["baseDamage"] = ("infodraw_strength", ""), ["cooldown"] = ("infodraw_short_cooldown", "amNum"),
        ["baseCooldown"] = ("infodraw_cooldown", ""), ["gcd"] = ("infodraw_gcd", ""),
        ["maxUses"] = ("infodraw_maxstock", ""),
    };
    private static readonly Dictionary<string, string[]> Text = new() {
        ["primary"] = ["Primary"], ["secondary"] = ["Secondary"], ["special"] = ["Special"], ["defensive"] = ["Defensive"],
        ["abilities"] = ["Abilities"], ["carried"] = ["Carried items"], ["shop"] = ["Shop"], ["loot"] = ["Loot choices"],
        ["statuses"] = ["Special statuses"], ["none"] = ["(none)"],
        ["adjusted"] = ["Adjusted", "調整値", "调整数值", "조정값", "Скориговані значення", "Valeurs ajustées"],
        ["base"] = ["Base", "基本値", "基础数值", "기본값", "Базові значення", "Valeurs de base"],
        ["damage"] = ["Damage", "威力", "伤害", "위력", "Шкода", "Dégâts"],
        ["baseDamage"] = ["Damage", "基本威力", "基础伤害", "기본 위력", "Базова шкода", "Dégâts de base"],
        ["cooldown"] = ["Cooldown", "クールダウン", "冷却", "재사용 대기시간", "Відновлення", "Temps de recharge"],
        ["baseCooldown"] = ["Cooldown", "基本クールダウン", "冷却", "기본 재사용 대기시간", "Базове відновлення", "Recharge de base"],
        ["gcd"] = ["Global cooldown", "グローバルクールダウン", "全局冷却", "전역 재사용 대기시간", "Глобальне відновлення", "Recharge globale"],
        ["maxUses"] = ["Max uses", "最大使用回数", "次数上限", "최대 사용 횟수", "Максимум використань", "Utilisations max"],
        ["remainingUses"] = ["Remaining uses", "現在の使用可能回数", "当前可用次数", "현재 사용 가능 횟수", "Залишилося використань", "Utilisations restantes"],
        ["price"] = ["Price", "価格", "售价", "가격", "Ціна", "Prix"],
        ["goldUnit"] = ["gold", "ゴールド", "金粒", "골드", "золота", "or"],
        ["currentForm"] = ["Current form", "現在の形態", "目前形态", "현재 형태", "Поточна форма", "Forme actuelle"],
        ["slotForm"] = ["Current slot form", "現在のスロットの形態", "当前槽位形态", "현재 슬롯 형태", "Поточна форма в комірці", "Forme actuelle de l’emplacement"],
        ["formCounter"] = ["Form counter", "形態カウンター", "形态计数", "형태 카운터", "Лічильник форми", "Compteur de forme"],
        ["counterUnverified"] = ["(remaining ability-use meaning unverified)", "（スキルの残り使用回数を示すかは未確認）", "（尚未验证是否为剩余技能次数）", "(남은 스킬 사용 횟수인지는 미확인)", "(не перевірено, чи це кількість використань умінь, що залишилася)", "(nombre d’utilisations de compétence restantes non confirmé)"],
        ["otherForm"] = ["Other form", "別の形態", "另一形态", "다른 형태", "Інша форма", "Autre forme"],
        ["upgradedAbility"] = ["Upgraded ability", "強化後のスキル", "升级后技能", "강화 후 스킬", "Покращене вміння", "Compétence améliorée"],
        ["missingDescription"] = ["(Description unavailable)", "（説明を取得できません）", "（说明不可用）", "(설명 없음)", "(Опис недоступний)", "(Description indisponible)"],
        ["timeUnit"] = ["s", "秒", "秒", "초", " с", "s"],
    };
    internal static int Language(int language) => language is >= 0 and < 6 ? language : 0;
    internal static string Fallback(string id, int language) {
        var row = Text[id];
        var index = Language(language);
        return index < row.Length && !string.IsNullOrWhiteSpace(row[index]) ? row[index] : row[0];
    }
    public static string Get(F8Catalog catalog, string id, int language) {
        language = Language(language);
        if (GameKeys.TryGetValue(id, out var source)) {
            var value = catalog.Phrase(source.Key, language);
            if (string.IsNullOrWhiteSpace(value)) value = catalog.Phrase(source.Key, 0);
            if (!string.IsNullOrWhiteSpace(value)) {
                // The short cooldown label shares a row with its numeric template.
                var cut = source.Placeholder.Length > 0 ? value.IndexOf(source.Placeholder, StringComparison.Ordinal) : -1;
                if (cut >= 0) value = value[..cut];
                return value.Trim().TrimEnd(':', '：').TrimEnd();
            }
        }
        return Fallback(id, language);
    }
    public static string Seconds(double milliseconds, int language, F8Catalog? catalog = null) {
        language = Language(language);
        var number = (milliseconds / 1000).ToString("0.0###", CultureInfo.InvariantCulture);
        var template = catalog?.Phrase("infodraw_short_numsec", language) ?? "";
        return template.Contains("numSec", StringComparison.Ordinal)
            ? template.Replace("numSec", number) : number + Fallback("timeUnit", language);
    }
    public static string Times(double count, int language) {
        var n = count.ToString("0.########", CultureInfo.InvariantCulture);
        return Language(language) switch {
            1 => n + "回", 2 => n + "次", 3 => n + "회", 5 => n + " fois",
            4 => n + (count % 10 == 1 && count % 100 != 11 ? " раз" : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? " рази" : " разів"),
            _ => count == 1 ? "once" : count == 2 ? "twice" : n + " times",
        };
    }
}
