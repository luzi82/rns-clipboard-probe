// Copyright (C) 2026 luzi82
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;

namespace RNSClipboardProbe;

internal readonly struct Offer {
    public int Slot { get; init; }
    public string Key { get; init; }
    public string Name { get; init; }
    public string Price { get; init; }
    public int Id { get; init; }

    public Offer(int slot, string key, string name, string price, int id = 0) {
        this.Slot = slot;
        this.Key = key;
        this.Name = name;
        this.Price = price;
        this.Id = id;
    }
}

internal static unsafe partial class Snapshot {
    private const int MaxPlayers = 8;
    private const int MaxRows = 12;

    private static readonly string[] Controllers = [
        "obj_inventorySystem", "obj_itemSystem", "obj_itemLootRoller",
        "obj_notchExStore", "obj_notchExLoot", "obj_storeInfo", "obj_lootInfo",
    ];

    private static readonly string[] ShopGroups = [
        "heal", "storeSlotHeal",
        "gem", "storeSlotOPCBin",
        "gem", "storeSlotOPC",
        "upgrade", "storeSlotUpgradeBin",
        "upgrade", "storeSlotUpgrade",
    ];

    public static string Build(IRNSReloaded rns) {
        var sb = new StringBuilder();
        sb.AppendLine("RNS clipboard probe (read-only)");
        sb.AppendLine("Hotkey: F9");
        sb.AppendLine("Captured: " + DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture));
        var global = rns.GetGlobalInstance();
        if (global == null) {
            sb.AppendLine("global instance: null");
            return sb.ToString();
        }
        var controllers = FindControllers(rns);
        AddInstance(rns, global, controllers, "currentNotchEx");
        var kit = ReadKit(rns);
        Section(sb, "characters", () => Characters(sb, rns, global));
        Section(sb, "abilities", () => WriteNamed(sb, kit.Abilities));
        Section(sb, "carried items", () => WriteNamed(sb, kit.Carried));
        Section(sb, "stage", () => { Stage(sb, rns, global, controllers); });
        Section(sb, "current notch", () => { Notch(sb, rns, global, controllers); });
        var shopOpen = kit.StoreHidden == false;
        var lootOpen = kit.LootHidden == false;
        if (shopOpen) {
            Section(sb, "shop stock", () => {
                if (kit.Shop.Count > 0) WriteOffers(sb, [("shop", kit.Shop)]);
                else Shop(sb, rns, global, controllers);
            });
        }
        if (lootOpen) {
            Section(sb, "loot options", () => {
                if (kit.Loot.Count > 0) WriteOffers(sb, [("loot", kit.Loot)]);
                else Loot(sb, rns, global, controllers);
            });
        }
        if (shopOpen || lootOpen) {
            Section(sb, "choice ui", () => ChoiceUi(sb, rns));
        }
        Section(sb, "trace", () => WriteTrace(sb, rns, kit));
        Section(sb, "skill suffix", () => WriteSkillSuffix(sb, rns, kit.Abilities));
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, Action body) {
        sb.AppendLine();
        sb.Append("## ").AppendLine(title);
        try {
            body();
        } catch (Exception ex) {
            sb.Append("failed: ").Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);
        }
    }

    private static void Characters(StringBuilder sb, IRNSReloaded rns, CInstance* global) {
        var count = PlayerCount(rns, global);
        if (count == 0) {
            sb.AppendLine("(none)");
            return;
        }
        var hp = Live(rns.FindValue(global, "playerHp"));
        var bodies = Live(rns.FindValue(global, "player"));
        _ = TryIndex(rns, hp, 0, out var team);
        _ = TryIndex(rns, bodies, 0, out var teamBodies);
        for (var i = 0; i < count; i++) {
            _ = TryIndex(rns, teamBodies, i, out var body);
            var anim = TextField(rns, body, "animationKey");
            if (anim.Length == 0) anim = TextField(rns, body, "animKey");
            var ally = TextField(rns, body, "allyId");
            var hpText = TryIndex(rns, team, i, out var hpCell) ? Plain(rns, hpCell) : "";
            sb.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ");
            sb.Append("animationKey=").Append(Shown(anim));
            sb.Append(" allyId=").Append(Shown(ally));
            sb.Append(" hp=").AppendLine(Shown(hpText));
        }
    }

    private static bool Stage(StringBuilder sb, IRNSReloaded rns, CInstance* global, List<RValue> controllers) {
        var name = FirstText(rns, global, controllers, "stageName");
        var key = FirstText(rns, global, controllers, "stageKey");
        if (key.Length == 0) key = FirstText(rns, global, controllers, "stageNameKey");
        sb.Append("name=").AppendLine(Shown(name));
        sb.Append("key=").AppendLine(Shown(key));
        return name.Length > 0 || key.Length > 0;
    }

    private static bool Notch(StringBuilder sb, IRNSReloaded rns, CInstance* global, List<RValue> controllers) {
        var notches = First(rns, global, controllers, "notches");
        if (!notches.Has || notches.Value.Type != RValueType.Array) {
            sb.AppendLine("(missing)");
            return false;
        }
        var (index, source) = NotchIndex(rns, global, controllers, notches.Value);
        if (index < 0) {
            sb.AppendLine("index=(missing)");
            return false;
        }
        _ = TryIndex(rns, notches.Value, index, out var notch);
        sb.Append("index=").Append(index.ToString(CultureInfo.InvariantCulture));
        sb.Append(" (").Append(source).Append(") ");
        sb.AppendLine(FormatNotch(rns, notch));
        return true;
    }

    private static bool Shop(StringBuilder sb, IRNSReloaded rns, CInstance* global, List<RValue> controllers) {
        var groups = new List<(string Label, List<Offer> Rows)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ShopGroups.Length; i += 2) {
            var label = ShopGroups[i];
            var name = ShopGroups[i + 1];
            if (!seen.Add(label + ":" + name)) continue;
            var rows = RowsFrom(rns, global, controllers, name);
            if (rows.Count == 0) continue;
            if (groups.Any(group => group.Label == label)) continue;
            groups.Add((label, rows));
        }
        if (groups.Count == 0) {
            var screen = ReadScreen(rns);
            if (screen.StoreHidden == false && screen.Rows.Count > 0) groups.Add(("shop", screen.Rows));
        }
        if (groups.Count == 0) {
            var squares = MarkedSquares(rns, global, controllers, loot: false);
            if (squares.Count > 0) groups.Add(("shop", squares));
        }
        ApplyPrices(rns, global, controllers, groups);
        return WriteOffers(sb, groups);
    }

    private static bool Loot(StringBuilder sb, IRNSReloaded rns, CInstance* global, List<RValue> controllers) {
        var screen = ReadScreen(rns);
        var rows = screen.Rows.Count > 0 && screen.StoreHidden != false ? screen.Rows : new List<Offer>();
        if (rows.Count == 0) {
            var limit = Count(rns, global, controllers, "lootAmount", "lootSlotCount", "lootCount");
            for (var i = 0; i < 6; i++) {
                if (limit is int n && i >= n) break;
                var found = First(rns, global, controllers, "loot" + i.ToString(CultureInfo.InvariantCulture));
                if (!found.Has) continue;
                var before = rows.Count;
                Collect(rns, found.Value, i, rows, 0);
                if (rows.Count == before) continue;
            }
        }
        if (rows.Count == 0) rows = MarkedSquares(rns, global, controllers, loot: true);
        if (rows.Count == 0) {
            foreach (var name in new[] { "lootSq", "loot", "spawnLootKey" }) {
                rows = RowsFrom(rns, global, controllers, name);
                if (rows.Count > 0) break;
            }
        }
        if (rows.Count == 0) rows = HotbarLoot(rns);
        var groups = rows.Count == 0 ? [] : new List<(string Label, List<Offer> Rows)> { ("loot", rows) };
        return WriteOffers(sb, groups);
    }

    private readonly struct Held {
        public int Row { get; init; }
        public int Slot { get; init; }
        public int DataId { get; init; }
        public int ItemId { get; init; }
        public int PlayerId { get; init; }
        public Offer Item { get; init; }
    }

    private readonly struct Traced {
        public int Row { get; init; }
        public int Slot { get; init; }
        public int DataId { get; init; }
        public int ItemId { get; init; }
        public int PlayerId { get; init; }
        public string Section { get; init; }
        public Offer Item { get; init; }
    }

    private readonly struct Kit {
        public List<Offer> Abilities { get; init; }
        public List<Offer> Carried { get; init; }
        public List<Offer> Shop { get; init; }
        public List<Offer> Loot { get; init; }
        public List<Traced> Lines { get; init; }
        public bool? LootHidden { get; init; }
        public bool? StoreHidden { get; init; }
    }

    private static Kit ReadKit(IRNSReloaded rns) {
        var empty = new Kit { Abilities = [], Carried = [], Shop = [], Loot = [], Lines = [] };
        try {
            var systems = FindNamed(rns, "obj_itemSystem", 1);
            RValue? sys = null;
            if (systems.Count > 0) sys = systems[0];
            var storeHidden = sys is RValue system ? FlagOf(rns, system, "storeInvHidden") : null;
            var lootHidden = sys is RValue lootSystem ? FlagOf(rns, lootSystem, "lootInvHidden") : null;
            var held = new List<Held>();
            var seen = new HashSet<(int Row, int Slot)>();
            foreach (var slot in FindNamed(rns, "obj_hotbarSlot", 48)) {
                CollectCell(rns, slot, -1, 0, 0, held, seen);
            }
            var handRow = HandRow(rns);
            if (sys is RValue itemSys && ReadInstanceVar(rns, itemSys, "slots") is RValue grid && grid.Type == RValueType.Array) {
                var len = Math.Min(ArrayLength(rns, grid), 24);
                for (var i = 0; i < len; i++) {
                    if (!TryIndex(rns, grid, i, out var cell)) continue;
                    CollectCell(rns, cell, i, i, 0, held, seen);
                    if (handRow is int hand && i == hand) CollectHandCells(rns, cell, hand, held, seen);
                }
                if (handRow is int owned) {
                    CollectHeldMarks(rns, grid, owned, held, seen);
                    if (ReadInstanceVar(rns, itemSys, "inventorySystem") is RValue inventory
                        && ReadOn(rns, inventory, "parts") is RValue parts) {
                        CollectPartIds(rns, parts, owned, held, seen, 0);
                    }
                }
            }
            var abilityRow = AbilityRow(held);
            var abilities = abilityRow is int skills
                ? held.Where(item => item.Row == skills && item.Slot is >= 0 and <= 3).OrderBy(item => item.Slot).Take(4).ToList()
                : new List<Held>();
            var abilityIds = new HashSet<int>();
            foreach (var skill in abilities) {
                var skillId = skill.DataId > 0 ? skill.DataId : skill.ItemId;
                if (skillId > 0) abilityIds.Add(skillId);
            }
            var abilityKeys = new HashSet<(int Row, int Slot)>(abilities.Select(item => (item.Row, item.Slot)));
            var others = held.Where(item => !abilityKeys.Contains((item.Row, item.Slot))
                && !abilityIds.Contains(item.DataId > 0 ? item.DataId : item.ItemId)).ToList();
            int? choiceRow = null;
            if (storeHidden == false || lootHidden == false) {
                var choice = others.Where(item => (abilityRow == null || item.Row != abilityRow)
                        && (handRow == null || item.Row != handRow))
                    .GroupBy(item => item.Row)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .FirstOrDefault();
                if (choice != null) choiceRow = choice.Key;
            }
            var lines = new List<Traced>();
            var shop = new List<Offer>();
            var loot = new List<Offer>();
            var carried = new List<Offer>();
            foreach (var item in held.OrderBy(item => item.Row).ThenBy(item => item.Slot)) {
                var section = "carried";
                var offer = item.Item;
                var id = item.DataId > 0 ? item.DataId : item.ItemId;
                if (abilityKeys.Contains((item.Row, item.Slot)) || abilityIds.Contains(id)) {
                    section = "ability";
                } else if (handRow is int hand && item.Row == hand) {
                    section = "carried";
                    carried.Add(offer);
                } else if (choiceRow is int open && item.Row == open && storeHidden == false) {
                    section = "shop";
                    var price = sys is RValue shopSys ? CostAt(rns, shopSys, item.Slot) : "";
                    if (price.Length > 0) offer = new Offer(offer.Slot, offer.Key, offer.Name, price, offer.Id);
                    shop.Add(offer);
                } else if (choiceRow is int shown && item.Row == shown && lootHidden == false) {
                    section = "loot";
                    loot.Add(offer);
                } else if (!offer.Key.StartsWith("mv_none", StringComparison.Ordinal) && offer.Name != "???") {
                    carried.Add(offer);
                }
                lines.Add(new Traced {
                    Row = item.Row,
                    Slot = item.Slot,
                    DataId = item.DataId,
                    ItemId = item.ItemId,
                    PlayerId = item.PlayerId,
                    Section = section,
                    Item = offer,
                });
            }
            if (lootHidden == false) {
                var cards = LootCards(rns, abilityIds);
                if (cards.Count > 0) loot = cards;
            }
            if (storeHidden == false && sys is RValue openShop) {
                var wares = StoreOffers(rns, openShop, abilityIds);
                if (wares.Count > 0) shop = wares;
            }
            return new Kit {
                Abilities = abilities.Select(item => item.Item).ToList(),
                Carried = carried,
                Shop = shop,
                Loot = loot,
                Lines = lines,
                LootHidden = lootHidden,
                StoreHidden = storeHidden,
            };
        } catch {
            return empty;
        }
    }

    private static int? AbilityRow(List<Held> held) {
        var match = held.GroupBy(item => item.Row)
            .Select(group => new { Row = group.Key, Slots = group.Select(item => item.Slot).ToHashSet(), Count = group.Count() })
            .Where(group => group.Slots.Contains(0) && group.Slots.Contains(1) && group.Slots.Contains(2) && group.Slots.Contains(3))
            .OrderBy(group => group.Count == 4 ? 0 : 1)
            .ThenBy(group => group.Row)
            .FirstOrDefault();
        return match?.Row;
    }

    private static int? HandRow(IRNSReloaded rns) {
        foreach (var slot in FindNamed(rns, "obj_hotbarSlot", 48)) {
            if (ReadIntOn(rns, slot, "rowId") != 2) continue;
            if (ReadIntOn(rns, slot, "itemRowId") is int row) return row;
        }
        return null;
    }

    private static void CollectHandCells(
        IRNSReloaded rns,
        RValue row,
        int hand,
        List<Held> into,
        HashSet<(int Row, int Slot)> seen
    ) {
        if (row.Type != RValueType.Array) return;
        var len = Math.Min(ArrayLength(rns, row), 16);
        for (var slot = 0; slot < len; slot++) {
            if (!TryIndex(rns, row, slot, out var cell) || !IsLive(cell)) continue;
            int? id = null;
            if (cell.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
                var number = cell.ToInt(-1);
                if (number is > 0 and <= 8000) id = number;
            } else if (cell.Type is RValueType.Object or RValueType.Ref) {
                var known = ReadIntOn(rns, cell, "dataId") ?? ReadIntOn(rns, cell, "itemId") ?? 0;
                if (known is > 0 and <= 8000) id = known;
            }
            if (id is not int item) continue;
            AddHeld(rns, hand, slot, item, into, seen);
        }
    }

    private static void CollectHeldMarks(
        IRNSReloaded rns,
        RValue grid,
        int hand,
        List<Held> into,
        HashSet<(int Row, int Slot)> seen
    ) {
        var rows = Math.Min(ArrayLength(rns, grid), 24);
        for (var r = 0; r < rows; r++) {
            if (!TryIndex(rns, grid, r, out var row) || row.Type != RValueType.Array) continue;
            var len = Math.Min(ArrayLength(rns, row), 16);
            for (var slot = 0; slot < len; slot++) {
                if (!TryIndex(rns, row, slot, out var cell)) continue;
                if (cell.Type is not (RValueType.Object or RValueType.Ref)) continue;
                if (FlagOf(rns, cell, "isHeld") != true) continue;
                var id = ReadIntOn(rns, cell, "dataId") ?? ReadIntOn(rns, cell, "itemId");
                if (id is not int known) continue;
                AddHeld(rns, hand, slot, known, into, seen);
            }
        }
    }

    private static void CollectPartIds(
        IRNSReloaded rns,
        RValue value,
        int hand,
        List<Held> into,
        HashSet<(int Row, int Slot)> seen,
        int depth
    ) {
        if (!IsLive(value) || depth > 2) return;
        if (value.Type == RValueType.Array) {
            var len = ArrayLength(rns, value);
            if (len is < 1 or > 16) return;
            for (var i = 0; i < len; i++) {
                if (!TryIndex(rns, value, i, out var cell)) continue;
                CollectPartIds(rns, cell, hand, into, seen, depth + 1);
            }
            return;
        }
        if (value.Type is not (RValueType.Object or RValueType.Ref)) return;
        if (FlagOf(rns, value, "isHeld") == true) {
            var id = ReadIntOn(rns, value, "dataId") ?? ReadIntOn(rns, value, "itemId");
            if (id is int known) AddHeld(rns, hand, into.Count, known, into, seen);
        }
        if (depth == 2) return;
        foreach (var key in OwnKeys(rns, value, 8)) {
            if (ReadOn(rns, value, key) is not RValue child) continue;
            if (child.Type is RValueType.Array or RValueType.Object or RValueType.Ref) {
                CollectPartIds(rns, child, hand, into, seen, depth + 1);
            }
        }
    }

    private static void AddHeld(
        IRNSReloaded rns,
        int row,
        int slot,
        int id,
        List<Held> into,
        HashSet<(int Row, int Slot)> seen
    ) {
        if (!seen.Add((row, slot))) return;
        into.Add(new Held {
            Row = row,
            Slot = slot,
            DataId = id,
            ItemId = 0,
            PlayerId = 0,
            Item = OfferFromId(rns, slot, id),
        });
    }

    private static void CollectCell(
        IRNSReloaded rns,
        RValue cell,
        int rowHint,
        int slotHint,
        int depth,
        List<Held> into,
        HashSet<(int Row, int Slot)> seen
    ) {
        if (!IsLive(cell)) return;
        if (cell.Type == RValueType.Array) {
            if (depth > 0) return;
            var len = Math.Min(ArrayLength(rns, cell), 16);
            for (var i = 0; i < len; i++) {
                if (!TryIndex(rns, cell, i, out var inner)) continue;
                CollectCell(rns, inner, rowHint, i, depth + 1, into, seen);
            }
            return;
        }
        var number = cell.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64 ? cell.ToInt(-1) : -1;
        if (cell.Type is not (RValueType.Object or RValueType.Ref) && number < 100000) return;
        var player = ReadIntOn(rns, cell, "playerId");
        if (player is int who && who != 0) return;
        var dataId = ReadIntOn(rns, cell, "dataId") ?? 0;
        var itemId = ReadIntOn(rns, cell, "itemId") ?? 0;
        var id = dataId > 0 ? dataId : itemId;
        if (id <= 0 || id > 8000) return;
        var row = rowHint;
        var slot = slotHint;
        if (ReadIntOn(rns, cell, "itemRowId") is int liveRow) row = liveRow;
        if (ReadIntOn(rns, cell, "itemSlotId") is int liveSlot) slot = liveSlot;
        else if (ReadIntOn(rns, cell, "uniqueId") is int unique) slot = unique;
        if (row >= 0 && !seen.Add((row, slot))) return;
        var offer = OfferFromId(rns, slot, id);
        if (offer.Key.Length == 0 && offer.Name.Length == 0) return;
        into.Add(new Held {
            Row = row,
            Slot = slot,
            DataId = dataId,
            ItemId = itemId,
            PlayerId = player ?? -1,
            Item = offer,
        });
    }

    private static void WriteTrace(StringBuilder sb, IRNSReloaded rns, Kit kit) {
        if (kit.Lines.Count == 0) {
            sb.AppendLine("(none)");
        }
        foreach (var line in kit.Lines) {
            sb.Append("row=").Append(line.Row.ToString(CultureInfo.InvariantCulture));
            sb.Append(" slot=").Append(line.Slot.ToString(CultureInfo.InvariantCulture));
            sb.Append(" dataId=").Append(line.DataId > 0 ? line.DataId.ToString(CultureInfo.InvariantCulture) : "-");
            sb.Append(" itemId=").Append(line.ItemId > 0 ? line.ItemId.ToString(CultureInfo.InvariantCulture) : "-");
            sb.Append(" name=").Append(Shown(line.Item.Name));
            sb.Append(" section=").Append(line.Section);
            sb.Append(" playerId=").Append(line.PlayerId >= 0 ? line.PlayerId.ToString(CultureInfo.InvariantCulture) : "-");
            sb.Append(" lootInvHidden=").Append(FlagText(kit.LootHidden));
            sb.Append(" storeInvHidden=").AppendLine(FlagText(kit.StoreHidden));
        }
        WriteSlotGrid(sb, rns);
        DumpWalked(sb, rns);
    }

    private static void WriteSlotGrid(StringBuilder sb, IRNSReloaded rns) {
        var systems = FindNamed(rns, "obj_itemSystem", 1);
        if (systems.Count == 0) return;
        if (ReadInstanceVar(rns, systems[0], "slots") is not RValue grid || grid.Type != RValueType.Array) {
            sb.AppendLine("slots=(missing)");
            return;
        }
        var rows = ArrayLength(rns, grid);
        if (rows < 0) rows = 0;
        for (var r = 0; r < rows; r++) {
            if (!TryIndex(rns, grid, r, out var row) || !IsLive(row)) {
                sb.Append("slots[").Append(r.ToString(CultureInfo.InvariantCulture)).AppendLine("]=null");
                continue;
            }
            if (row.Type != RValueType.Array) {
                sb.Append("slots[").Append(r.ToString(CultureInfo.InvariantCulture)).Append("]=");
                sb.AppendLine(JsonOf(rns, row, 0));
                continue;
            }
            var len = ArrayLength(rns, row);
            if (len < 0) len = 0;
            for (var c = 0; c < len; c++) {
                sb.Append("slots[").Append(r.ToString(CultureInfo.InvariantCulture)).Append("][");
                sb.Append(c.ToString(CultureInfo.InvariantCulture)).Append("]=");
                if (!TryIndex(rns, row, c, out var cell) || !IsLive(cell)) {
                    sb.AppendLine("null");
                    continue;
                }
                sb.AppendLine(JsonOf(rns, cell, 0));
            }
        }
        if (ReadInstanceVar(rns, systems[0], "inventorySystem") is not RValue inventory) return;
        sb.Append("inventorySystem.parts=");
        if (ReadOn(rns, inventory, "parts") is not RValue partList) {
            sb.AppendLine("-");
            return;
        }
        sb.AppendLine(TraceValue(rns, partList, true));
    }

    private static void DumpWalked(StringBuilder sb, IRNSReloaded rns) {
        foreach (var name in new[] { "obj_itemSystem", "obj_itemLootRoller", "obj_hotbarSlot" }) {
            var found = FindNamed(rns, name, 160);
            if (found.Count == 0) {
                sb.Append(name).AppendLine("=(missing)");
                continue;
            }
            foreach (var inst in found) {
                sb.Append(name);
                if (ReadIntOn(rns, inst, "itemRowId") is int row) {
                    sb.Append(" row=").Append(row.ToString(CultureInfo.InvariantCulture));
                }
                if (ReadIntOn(rns, inst, "itemSlotId") is int slot) {
                    sb.Append(" slot=").Append(slot.ToString(CultureInfo.InvariantCulture));
                }
                sb.AppendLine();
                DumpKeys(sb, rns, inst, "  ");
            }
        }
    }

    private static void DumpKeys(StringBuilder sb, IRNSReloaded rns, RValue target, string indent) {
        var keys = OwnKeys(rns, target, int.MaxValue);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var wrote = 0;
        foreach (var key in keys) {
            if (!seen.Add(key)) continue;
            var value = ReadInstanceVar(rns, target, key) ?? ReadOn(rns, target, key);
            sb.Append(indent).Append(key).Append(" = ");
            sb.AppendLine(value is RValue live ? TraceValue(rns, live, true) : "empty");
            wrote++;
        }
        if (wrote == 0) sb.Append(indent).AppendLine("(no keys)");
    }

    private const int JsonDepthLimit = 4;

    private static string TraceValue(IRNSReloaded rns, RValue value, bool nest) {
        return JsonOf(rns, value, nest ? 0 : JsonDepthLimit);
    }

    private static string JsonOf(IRNSReloaded rns, RValue value, int depth) {
        if (!Present(value)) return "null";
        if (depth > JsonDepthLimit) return "\"…\"";
        switch (value.Type) {
            case RValueType.String:
                return Quote(ReadString(rns, value));
            case RValueType.Real:
            case RValueType.Int32:
            case RValueType.Int64:
            case RValueType.Bool:
                var plain = Plain(rns, value);
                return plain.Length == 0 ? "null" : plain;
            case RValueType.Array:
                return JsonArray(rns, value, depth);
            case RValueType.Ref:
            case RValueType.Pointer:
            case RValueType.Accessor:
                return JsonRef(value);
            case RValueType.Object:
                return JsonStruct(rns, value, depth);
            default:
                return Quote(value.Type.ToString());
        }
    }

    private static string JsonArray(IRNSReloaded rns, RValue value, int depth) {
        var len = ArrayLength(rns, value);
        if (len <= 0) return len == 0 ? "[]" : "null";
        if (depth >= JsonDepthLimit) return "{\"array\":" + len.ToString(CultureInfo.InvariantCulture) + "}";
        if (len > 64) return "{\"array\":" + len.ToString(CultureInfo.InvariantCulture) + "}";
        var parts = new List<string>(len);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, value, i, out var cell) || !Present(cell)) {
                parts.Add("null");
                continue;
            }
            parts.Add(JsonOf(rns, cell, depth + 1));
        }
        return "[" + string.Join(",", parts) + "]";
    }

    private static string JsonStruct(IRNSReloaded rns, RValue value, int depth, int keyCap = 48) {
        if (depth >= JsonDepthLimit) return JsonRef(value);
        var keys = OwnKeys(rns, value, keyCap);
        if (keys.Count == 0) return JsonRef(value);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var key in keys) {
            if (!seen.Add(key)) continue;
            var field = ReadInstanceVar(rns, value, key) ?? ReadOn(rns, value, key);
            var shown = field is RValue live ? JsonOf(rns, live, depth + 1) : "null";
            parts.Add(Quote(key) + ":" + shown);
        }
        return "{" + string.Join(",", parts) + "}";
    }

    private static string JsonRef(RValue value) {
        var id = 0;
        if (value.Type == RValueType.Object && value.Object != null && value.Object->ID > 0) id = value.Object->ID;
        if (id <= 0) {
            var number = value.ToInt(-1);
            if (number > 0) id = number;
        }
        if (id > 0) return "{\"ref\":" + id.ToString(CultureInfo.InvariantCulture) + "}";
        return "{\"ref\":\"0x" + ((ulong) value.Pointer).ToString("X", CultureInfo.InvariantCulture) + "\"}";
    }

    private static string FlagText(bool? value) {
        return value switch { true => "true", false => "false", _ => "-" };
    }

    private static void WriteNamed(StringBuilder sb, List<Offer> rows) {
        if (rows.Count == 0) {
            sb.AppendLine("(none shown)");
            return;
        }
        foreach (var row in rows) {
            sb.Append('[').Append(row.Slot.ToString(CultureInfo.InvariantCulture)).Append("] ");
            sb.Append("key=").Append(Shown(row.Key));
            sb.Append(" name=").AppendLine(Shown(row.Name));
        }
    }

    private readonly struct Board {
        public List<Offer> Shop { get; init; }
        public List<Offer> Loot { get; init; }
    }

    private static Board ReadBoard(IRNSReloaded rns) {
        var none = new Board { Shop = [], Loot = [] };
        try {
            var systems = FindNamed(rns, "obj_itemSystem", 1);
            if (systems.Count == 0) return none;
            var sys = systems[0];
            var storeHidden = FlagOf(rns, sys, "storeInvHidden");
            var lootHidden = FlagOf(rns, sys, "lootInvHidden");
            var rows = RowsAt(rns, sys, HotbarRow(rns), storeHidden == false);
            if (rows.Count == 0) return none;
            if (storeHidden == false) return new Board { Shop = rows, Loot = [] };
            if (lootHidden == false) return new Board { Shop = [], Loot = rows };
            return none;
        } catch {
            return none;
        }
    }

    private static int HotbarRow(IRNSReloaded rns) {
        var best = 7;
        var bestCount = 0;
        var counts = new Dictionary<int, int>();
        foreach (var slot in FindNamed(rns, "obj_hotbarSlot", 48)) {
            var row = ReadInstanceVar(rns, slot, "itemRowId");
            if (row is not RValue value) continue;
            var number = value.ToInt(-1);
            if (number < 0) continue;
            counts[number] = counts.GetValueOrDefault(number) + 1;
        }
        foreach (var pair in counts) {
            if (pair.Value <= bestCount) continue;
            bestCount = pair.Value;
            best = pair.Key;
        }
        return best;
    }

    private static List<Offer> RowsAt(IRNSReloaded rns, RValue sys, int row, bool prices) {
        var rows = new List<Offer>();
        var nested = false;
        var line = default(RValue);
        var width = 0;
        if (ReadInstanceVar(rns, sys, "slots") is RValue grid && grid.Type == RValueType.Array
            && TryIndex(rns, grid, row, out var atRow) && atRow.Type == RValueType.Array) {
            nested = true;
            line = atRow;
            width = ArrayLength(rns, line);
        }
        var opc = ReadInstanceVar(rns, sys, "storeSlotOPCBin");
        var upgrade = ReadInstanceVar(rns, sys, "storeSlotUpgradeBin");
        var binWidth = 0;
        if (opc is RValue opcArr && opcArr.Type == RValueType.Array) binWidth = ArrayLength(rns, opcArr);
        if (upgrade is RValue upArr && upArr.Type == RValueType.Array) {
            binWidth = Math.Max(binWidth, ArrayLength(rns, upArr));
        }
        var count = nested ? width : 0;
        if (prices) count = Math.Max(count, binWidth);
        count = Math.Clamp(count, 0, 16);
        for (var i = 0; i < count; i++) {
            Offer? offer = null;
            if (nested && TryIndex(rns, line, i, out var cell)) offer = OfferFromAny(rns, cell, i);
            if (offer == null && prices) {
                var id = PositiveAt(rns, opc, i) ?? PositiveAt(rns, upgrade, i);
                if (id is int known) offer = OfferFromId(rns, i, known);
            }
            if (offer is not Offer rowOffer) continue;
            if (rowOffer.Key.Length == 0 && rowOffer.Name.Length == 0) continue;
            if (prices && rowOffer.Price.Length == 0) {
                var cost = CostAt(rns, sys, i);
                if (cost.Length > 0) rowOffer = new Offer(rowOffer.Slot, rowOffer.Key, rowOffer.Name, cost, rowOffer.Id);
            }
            rows.Add(rowOffer);
        }
        if (rows.Count > 0 || nested) return rows;
        if (ReadInstanceVar(rns, sys, "slots") is not RValue flat || flat.Type != RValueType.Array) return rows;
        var len = Math.Min(ArrayLength(rns, flat), 24);
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, flat, i, out var cell) || !SameRow(rns, cell, row)) continue;
            var slot = ReadIntOn(rns, cell, "itemSlotId") ?? i;
            if (OfferFromAny(rns, cell, slot) is not Offer offer) continue;
            if (offer.Key.Length == 0 && offer.Name.Length == 0) continue;
            if (prices && offer.Price.Length == 0) {
                var cost = CostAt(rns, sys, slot);
                if (cost.Length > 0) offer = new Offer(offer.Slot, offer.Key, offer.Name, cost, offer.Id);
            }
            rows.Add(offer);
        }
        return rows;
    }

    private static bool SameRow(IRNSReloaded rns, RValue cell, int row) {
        if (cell.Type is not (RValueType.Object or RValueType.Ref)) return false;
        var got = ReadOn(rns, cell, "itemRowId");
        return got is RValue value && IsLive(value) && value.ToInt(-1) == row;
    }

    private static Offer? OfferFromAny(IRNSReloaded rns, RValue cell, int slot) {
        if (!IsLive(cell)) return null;
        switch (cell.Type) {
            case RValueType.Real:
            case RValueType.Int32:
            case RValueType.Int64:
                var id = cell.ToInt(-1);
                return id is > 0 and <= 8000 ? OfferFromId(rns, slot, id) : null;
            case RValueType.Object:
            case RValueType.Ref:
                return OfferFromFields(rns, cell, slot);
            case RValueType.String:
                var text = ReadString(rns, cell);
                if (!LooksLikeItem(text)) return null;
                var key = LooksLikeKey(text) ? text : "";
                var name = key.Length == 0 ? text : "";
                var named = Resolve(rns, slot, key, name, "", null);
                return named.Key.Length == 0 && named.Name.Length == 0 ? null : named;
            case RValueType.Array:
                var len = ArrayLength(rns, cell);
                if (len is < 1 or > 4 || !TryIndex(rns, cell, 0, out var first) || first.Type == RValueType.Array) {
                    return null;
                }
                return OfferFromAny(rns, first, slot);
            default:
                return null;
        }
    }

    private static Offer? OfferFromFields(IRNSReloaded rns, RValue cell, int slot) {
        var key = FirstTextOn(rns, cell, "itemKey", "itKey", "key");
        var name = FirstTextOn(rns, cell, "name", "itemName");
        var id = ReadIntOn(rns, cell, "itemId") ?? ReadIntOn(rns, cell, "dataId");
        if (id is 0) id = null;
        var price = FirstTextOn(rns, cell, "cost", "price", "gold");
        if (price is "0" or "false") price = "";
        if (key.Length == 0 && name.Length == 0 && id == null) return null;
        var offer = Resolve(rns, slot, key, name, price, id);
        if (offer.Key.Length == 0 && offer.Name.Length == 0 && id is int known) {
            offer = new Offer(slot, "id=" + known.ToString(CultureInfo.InvariantCulture), "", price, known);
        }
        return offer.Key.Length == 0 && offer.Name.Length == 0 ? null : offer;
    }

    private static Offer OfferFromId(IRNSReloaded rns, int slot, int id) {
        var offer = Resolve(rns, slot, "", "", "", id);
        if (offer.Key.Length == 0 && offer.Name.Length == 0) {
            offer = new Offer(slot, "id=" + id.ToString(CultureInfo.InvariantCulture), "", "", id);
        }
        return offer.Id > 0 ? offer : new Offer(offer.Slot, offer.Key, offer.Name, offer.Price, id);
    }

    private static string FirstTextOn(IRNSReloaded rns, RValue target, params string[] names) {
        foreach (var name in names) {
            var text = ReadTextOn(rns, target, name);
            if (text.Length > 0 && text is not ("0" or "false")) return text;
        }
        return "";
    }

    private static int? PositiveAt(IRNSReloaded rns, RValue? array, int slot) {
        if (array is not RValue value || !TryIndex(rns, value, slot, out var cell)) return null;
        if (cell.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return null;
        var number = cell.ToInt(-1);
        return number is > 0 and <= 8000 ? number : null;
    }

    private static string CostAt(IRNSReloaded rns, RValue sys, int slot) {
        var array = ReadInstanceVar(rns, sys, "storeSlotCost");
        if (array is not RValue cost || !TryIndex(rns, cost, slot, out var cell)) return "";
        if (cell.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return "";
        return cell.ToInt(-1) > 0 ? Plain(rns, cell) : "";
    }

    private static bool? FlagOf(IRNSReloaded rns, RValue handle, string name) {
        var got = ReadInstanceVar(rns, handle, name);
        if (got is not RValue value || !IsLive(value)) return null;
        return value.Type is RValueType.Bool or RValueType.Real or RValueType.Int32 or RValueType.Int64
            ? value.ToBool()
            : null;
    }

    private static List<RValue> FindNamed(IRNSReloaded rns, string objectName, int cap) {
        var list = new List<RValue>();
        var countValue = rns.ExecuteCodeFunction("instance_number", [AllObjects]);
        var count = Math.Clamp(countValue?.ToInt(0) ?? 0, 0, 160);
        for (var i = 0; i < count && list.Count < cap; i++) {
            try {
                var found = rns.ExecuteCodeFunction("instance_find", [AllObjects, i]);
                if (found is not RValue id || !Present(id)) continue;
                if (ObjectName(rns, id) == objectName) list.Add(id);
            } catch {
                // One instance failing leaves the rest of the room readable.
            }
        }
        return list;
    }

    private static List<Offer> MarkedSquares(
        IRNSReloaded rns,
        CInstance* global,
        List<RValue> controllers,
        bool loot
    ) {
        var cells = new List<RValue>();
        foreach (var name in new[] { "squareMap", "itemList" }) {
            foreach (var value in Each(rns, global, controllers, name)) {
                var visits = 0;
                WalkSquares(rns, value, 0, false, false, true, loot, cells, ref visits);
                if (cells.Count > 0) break;
            }
            if (cells.Count > 0) break;
        }
        var rows = new List<Offer>();
        foreach (var value in cells) {
            if (rows.Count >= MaxRows) break;
            if (OfferFromSquare(rns, value, rows.Count) is Offer offer) rows.Add(offer);
        }
        return rows;
    }

    private static void WalkSquares(
        IRNSReloaded rns,
        RValue value,
        int depth,
        bool parentLoot,
        bool parentStore,
        bool allowMap,
        bool wantLoot,
        List<RValue> cells,
        ref int visits
    ) {
        if (cells.Count >= MaxRows || visits > 700 || depth > 4 || !Present(value)) return;
        visits++;
        if (value.Type is RValueType.Ref or RValueType.Pointer or RValueType.Accessor) return;
        if (allowMap && depth == 0 && value.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            try {
                var values = rns.ExecuteCodeFunction("ds_map_values_to_array", [value]);
                if (values is RValue array && array.Type == RValueType.Array) {
                    WalkSquares(rns, array, 0, parentLoot, parentStore, false, wantLoot, cells, ref visits);
                }
            } catch {
                // A real that is not a map is not a square grid.
            }
            return;
        }
        if (value.Type == RValueType.Array) {
            var len = ArrayLength(rns, value);
            if (len <= 0) return;
            var take = Math.Min(len, depth <= 1 ? 12 : 8);
            for (var i = 0; i < take; i++) {
                if (!TryIndex(rns, value, i, out var cell)) continue;
                WalkSquares(rns, cell, depth + 1, parentLoot, parentStore, false, wantLoot, cells, ref visits);
            }
            return;
        }
        if (value.Type != RValueType.Object || value.Object == null) return;
        if (Flag(rns, value, "sqHidden") || Flag(rns, value, "isHidden")) return;
        var isLoot = parentLoot || Flag(rns, value, "itemIsLoot");
        var isStore = parentStore || Flag(rns, value, "itemIsStore") || Flag(rns, value, "isStore");
        if (LooksLikeSquare(rns, value)) {
            if (wantLoot ? isLoot : isStore) cells.Add(value);
            return;
        }
        if (depth >= 3) return;
        List<string> names;
        try {
            var box = value;
            names = rns.GetStructKeys(&box);
        } catch {
            return;
        }
        if (names.Count == 0 || names.Count > 24) return;
        foreach (var name in names) {
            var child = rns.FindValue(value.Object, name);
            if (child == null || !IsLive(*child)) continue;
            var nested = *child;
            if (nested.Type is RValueType.Array or RValueType.Object) {
                WalkSquares(rns, nested, depth + 1, isLoot, isStore, false, wantLoot, cells, ref visits);
            }
        }
    }

    private static bool LooksLikeSquare(IRNSReloaded rns, RValue value) {
        if (TextField(rns, value, "itemKey", "itKey", "key").Length > 0) return true;
        var id = IntField(rns, value, "itemId", "dataId");
        return id is > 0;
    }

    private static Offer? OfferFromSquare(IRNSReloaded rns, RValue value, int fallbackSlot) {
        var key = TextField(rns, value, "itemKey", "itKey", "key");
        var name = TextField(rns, value, "name", "itemName");
        var id = IntField(rns, value, "itemId", "dataId", "lootItId", "lootId");
        if (id is 0) id = null;
        var price = TextField(rns, value, "cost", "price", "gold");
        var slot = IntField(rns, value, "itemSlotId", "slotId", "slot", "slotIndex") ?? fallbackSlot;
        if (key.Length == 0 && name.Length == 0 && id == null) return null;
        var offer = Resolve(rns, slot, key, name, price, id);
        if (offer.Key.Length == 0 && offer.Name.Length == 0 && id is int known) {
            offer = new Offer(slot, "id=" + known.ToString(CultureInfo.InvariantCulture), "", price, known);
        }
        return offer.Key.Length == 0 && offer.Name.Length == 0 ? null : offer;
    }

    private readonly struct ItemPoint {
        public RValue ItemSys { get; init; }
        public int Row { get; init; }
        public int Slot { get; init; }
    }

    private readonly struct ScreenOffers {
        public bool? StoreHidden { get; init; }
        public List<Offer> Rows { get; init; }
    }

    private static ScreenOffers ReadScreen(IRNSReloaded rns) {
        var empty = new ScreenOffers { StoreHidden = null, Rows = [] };
        try {
            var (maxSlots, open) = ReadRoller(rns);
            var points = new List<ItemPoint>();
            foreach (var slot in InstancesOf(rns, "obj_hotbarSlot", 48)) {
                if (Member(rns, slot, "itemSys") is not RValue itemSys || !IsLive(itemSys)) continue;
                if (MemberInt(rns, slot, "itemRowId") is not int row) continue;
                if (MemberInt(rns, slot, "itemSlotId") is not int index) continue;
                points.Add(new ItemPoint { ItemSys = itemSys, Row = row, Slot = index });
            }
            if (points.Count == 0) return empty;
            bool OpenSlot(int slot) {
                if (slot < 0) return false;
                if (maxSlots > 0 && slot >= maxSlots) return false;
                if (open.Count == 0) return slot < 8;
                return slot < open.Count && open[slot];
            }
            var best = points.GroupBy(point => point.Row)
                .Select(group => group.Where(point => OpenSlot(point.Slot)).ToList())
                .Where(group => group.Count > 0)
                .OrderByDescending(group => group.Count)
                .ThenByDescending(group => group[0].Row)
                .FirstOrDefault();
            if (best == null || best.Count == 0) return empty;
            bool? hidden = null;
            var rows = new List<Offer>();
            foreach (var point in best.GroupBy(point => point.Slot).Select(group => group.First()).OrderBy(point => point.Slot)) {
                hidden ??= StoreHidden(rns, point.ItemSys);
                if (!TryItemCell(rns, point.ItemSys, point.Row, point.Slot, out var cell)) continue;
                if (OfferFromCell(rns, cell, point.Slot) is Offer offer) rows.Add(offer);
            }
            return new ScreenOffers { StoreHidden = hidden, Rows = rows };
        } catch {
            return empty;
        }
    }

    private static (int MaxSlots, List<bool> Open) ReadRoller(IRNSReloaded rns) {
        var open = new List<bool>();
        var rollers = InstancesOf(rns, "obj_itemLootRoller", 2);
        if (rollers.Count == 0) return (0, open);
        var roller = rollers[0];
        var maxSlots = MemberInt(rns, roller, "maxLootSlots") ?? 0;
        var remaining = Member(rns, roller, "lootRemaining");
        var finished = Member(rns, roller, "lootFinished");
        var len = 0;
        if (remaining is RValue rem && rem.Type == RValueType.Array) len = Math.Max(len, ArrayLength(rns, rem));
        if (finished is RValue fin && fin.Type == RValueType.Array) len = Math.Max(len, ArrayLength(rns, fin));
        len = Math.Clamp(len, 0, 8);
        for (var i = 0; i < len; i++) {
            var still = true;
            if (remaining is RValue remArr && TryIndex(rns, remArr, i, out var remCell)) {
                still = remCell.ToInt(0) > 0 || remCell.ToBool();
            }
            if (still && finished is RValue finArr && TryIndex(rns, finArr, i, out var finCell) && finCell.ToBool()) {
                still = false;
            }
            open.Add(still);
        }
        return (maxSlots, open);
    }

    private static bool? StoreHidden(IRNSReloaded rns, RValue itemSys) {
        var flag = Member(rns, itemSys, "storeInvHidden");
        if (flag is RValue onSys && IsLive(onSys)) return onSys.ToBool();
        foreach (var inst in InstancesOf(rns, "obj_itemSystem", 4)) {
            var value = Member(rns, inst, "storeInvHidden");
            if (value is RValue live && IsLive(live)) return live.ToBool();
        }
        return null;
    }

    private static bool TryItemCell(IRNSReloaded rns, RValue itemSys, int row, int slot, out RValue cell) {
        cell = default;
        if (Member(rns, itemSys, "squareMap") is RValue grid && TryGridCell(rns, grid, row, slot, out cell)) return true;
        foreach (var inst in InstancesOf(rns, "obj_itemSystem", 2)) {
            if (Member(rns, inst, "squareMap") is RValue onInst && TryGridCell(rns, onInst, row, slot, out cell)) return true;
        }
        return false;
    }

    private static bool TryGridCell(IRNSReloaded rns, RValue grid, int row, int slot, out RValue cell) {
        cell = default;
        if (grid.Type is RValueType.Object or RValueType.Ref) {
            foreach (var name in OwnKeys(rns, grid, 12)) {
                if (Member(rns, grid, name) is not RValue child || child.Type != RValueType.Array) continue;
                if (TryGridCell(rns, child, row, slot, out cell)) return true;
            }
            return false;
        }
        if (grid.Type != RValueType.Array || !TryIndex(rns, grid, row, out var atRow)) return false;
        if (atRow.Type == RValueType.Array) return TryIndex(rns, atRow, slot, out cell);
        if (atRow.Type is RValueType.Object or RValueType.Ref) {
            foreach (var name in OwnKeys(rns, atRow, 12)) {
                if (Member(rns, atRow, name) is not RValue child || child.Type != RValueType.Array) continue;
                if (TryIndex(rns, child, slot, out cell)) return true;
            }
        }
        return false;
    }

    private static Offer? OfferFromCell(IRNSReloaded rns, RValue value, int slot) {
        var key = MemberText(rns, value, "itemKey", "itKey", "key");
        var name = MemberText(rns, value, "name", "itemName");
        var id = MemberInt(rns, value, "itemId", "dataId", "lootItId", "lootId");
        if (id is 0) id = null;
        var price = MemberText(rns, value, "cost", "price", "gold");
        if (key.Length == 0 && name.Length == 0 && id == null) return null;
        var offer = Resolve(rns, slot, key, name, price, id);
        if (offer.Key.Length == 0 && offer.Name.Length == 0 && id is int known) {
            offer = new Offer(slot, "id=" + known.ToString(CultureInfo.InvariantCulture), "", price, known);
        }
        return offer.Key.Length == 0 && offer.Name.Length == 0 ? null : offer;
    }

    private static RValue? Member(IRNSReloaded rns, RValue target, string key) {
        if (!IsLive(target)) return null;
        try {
            var got = rns.ExecuteCodeFunction("variable_instance_get", [target, key]);
            if (got is RValue value && IsLive(value)) return value;
        } catch {
            // A struct ref is read with variable_struct_get below.
        }
        try {
            var got = rns.ExecuteCodeFunction("variable_struct_get", [target, key]);
            if (got is RValue value && IsLive(value)) return value;
        } catch {
            // The pointer lookup is the library's own instance read.
        }
        if (target.Pointer != 0 && target.Type is RValueType.Object or RValueType.Ref) {
            var field = rns.FindValue((CInstance*) target.Pointer, key);
            if (field != null && IsLive(*field)) return *field;
        }
        return null;
    }

    private static string MemberText(IRNSReloaded rns, RValue target, params string[] names) {
        foreach (var name in names) {
            if (Member(rns, target, name) is not RValue value) continue;
            var text = Plain(rns, value);
            if (text.Length > 0) return text;
        }
        return "";
    }

    private static int? MemberInt(IRNSReloaded rns, RValue target, params string[] names) {
        foreach (var name in names) {
            if (Member(rns, target, name) is not RValue value || !IsLive(value)) continue;
            if (value.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) continue;
            var number = value.ToInt(-1);
            if (number >= 0) return number;
        }
        return null;
    }

    private static List<RValue> InstancesOf(IRNSReloaded rns, string objectName, int cap) {
        var list = new List<RValue>();
        var asset = rns.ExecuteCodeFunction("asset_get_index", [objectName]);
        if (asset is not RValue index || index.ToInt(-1) < 0) return list;
        var count = Math.Clamp(rns.ExecuteCodeFunction("instance_number", [index])?.ToInt(0) ?? 0, 0, cap);
        for (var i = 0; i < count; i++) {
            var inst = rns.ExecuteCodeFunction("instance_find", [index, i]);
            if (inst is RValue id && Present(id)) list.Add(id);
        }
        return list;
    }

    private static List<Offer> HotbarLoot(IRNSReloaded rns) {
        var rows = new List<Offer>();
        try {
            var asset = rns.ExecuteCodeFunction("asset_get_index", ["obj_hotbarSlot"]);
            if (asset is not RValue index || index.ToInt(-1) < 0) return rows;
            var count = Math.Clamp(rns.ExecuteCodeFunction("instance_number", [index])?.ToInt(0) ?? 0, 0, 16);
            for (var i = 0; i < count && rows.Count < MaxRows; i++) {
                var inst = rns.ExecuteCodeFunction("instance_find", [index, i]);
                if (inst is not RValue id || !Present(id)) continue;
                if (!InstanceFlag(rns, id, "itemIsLoot") || InstanceFlag(rns, id, "isHeld")) continue;
                var key = InstanceText(rns, id, "itemKey");
                if (key.Length == 0) key = InstanceText(rns, id, "itKey");
                var name = InstanceText(rns, id, "name");
                var itemId = InstanceInt(rns, id, "itemId") ?? InstanceInt(rns, id, "dataId");
                if (itemId is 0) itemId = null;
                var price = InstanceText(rns, id, "cost");
                if (price.Length == 0) price = InstanceText(rns, id, "price");
                var slot = InstanceInt(rns, id, "itemSlotId") ?? InstanceInt(rns, id, "slotIndex") ?? rows.Count;
                if (key.Length == 0 && name.Length == 0 && itemId == null) continue;
                var offer = Resolve(rns, slot, key, name, price, itemId);
                if (offer.Key.Length == 0 && offer.Name.Length == 0 && itemId is int known) {
                    offer = new Offer(slot, "id=" + known.ToString(CultureInfo.InvariantCulture), "", price, known);
                }
                if (offer.Key.Length > 0 || offer.Name.Length > 0) rows.Add(offer);
            }
        } catch {
            // Hotbar slots are optional. The square map remains the source.
        }
        return rows;
    }

    private static void AddInstance(IRNSReloaded rns, CInstance* global, List<RValue> controllers, string name) {
        var ptr = rns.FindValue(global, name);
        if (ptr == null || !IsLive(*ptr)) return;
        var id = *ptr;
        if (id.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64) || id.ToInt(0) < 100000) return;
        try {
            var exists = rns.ExecuteCodeFunction("instance_exists", [id]);
            if (exists is RValue ok && ok.ToBool()) controllers.Insert(0, id);
        } catch {
            // A stale notch id leaves the other controllers in place.
        }
    }

    private static List<RValue> Each(IRNSReloaded rns, CInstance* global, List<RValue> controllers, string name) {
        var values = new List<RValue>();
        var direct = rns.FindValue(global, name);
        if (direct != null && IsLive(*direct)) values.Add(*direct);
        foreach (var id in controllers) {
            try {
                var got = rns.ExecuteCodeFunction("variable_instance_get", [id, name]);
                if (got is RValue value && Present(value)) values.Add(value);
            } catch {
                // This instance does not have the grid. The next one may.
            }
        }
        return values;
    }

    private static bool Flag(IRNSReloaded rns, RValue value, string name) {
        if (value.Type != RValueType.Object || value.Object == null) return false;
        var field = rns.FindValue(value.Object, name);
        return field != null && IsLive(*field) && (*field).ToBool();
    }

    private static bool InstanceFlag(IRNSReloaded rns, RValue id, string name) {
        var got = rns.ExecuteCodeFunction("variable_instance_get", [id, name]);
        return got is RValue value && Present(value) && value.ToBool();
    }

    private static string InstanceText(IRNSReloaded rns, RValue id, string name) {
        var got = rns.ExecuteCodeFunction("variable_instance_get", [id, name]);
        return got is RValue value ? Plain(rns, value) : "";
    }

    private static int? InstanceInt(IRNSReloaded rns, RValue id, string name) {
        var got = rns.ExecuteCodeFunction("variable_instance_get", [id, name]);
        if (got is not RValue value || !Present(value)) return null;
        if (value.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return null;
        var number = value.ToInt(-1);
        return number >= 0 ? number : null;
    }

    private static bool WriteOffers(StringBuilder sb, List<(string Label, List<Offer> Rows)> groups) {
        var any = false;
        foreach (var (label, rows) in groups) {
            foreach (var row in rows) {
                any = true;
                sb.Append(label).Append(" slot ").Append(row.Slot.ToString(CultureInfo.InvariantCulture));
                sb.Append(" key=").Append(Shown(row.Key));
                sb.Append(" name=").Append(Shown(row.Name));
                sb.Append(" price=").AppendLine(row.Price.Length == 0 ? "-" : row.Price);
            }
        }
        if (!any) sb.AppendLine("(none shown)");
        return any;
    }

    private static List<Offer> RowsFrom(IRNSReloaded rns, CInstance* global, List<RValue> controllers, string name) {
        var found = First(rns, global, controllers, name);
        var rows = new List<Offer>();
        if (!found.Has) return rows;
        Collect(rns, found.Value, 0, rows, 0);
        return rows;
    }

    private static void Collect(IRNSReloaded rns, RValue value, int slot, List<Offer> into, int depth) {
        if (into.Count >= MaxRows || depth > 3 || !Present(value)) return;
        if (value.Type is RValueType.Ref or RValueType.Pointer or RValueType.Accessor) return;
        if (value.Type == RValueType.Array) {
            var len = ArrayLength(rns, value);
            if (len <= 0 || len > MaxRows) return;
            for (var i = 0; i < len; i++) {
                if (!TryIndex(rns, value, i, out var cell)) continue;
                Collect(rns, cell, i, into, depth + 1);
            }
            return;
        }
        if (value.Type == RValueType.Object) {
            var key = TextField(rns, value, "itemKey", "key", "spawnLootKey");
            var name = TextField(rns, value, "name", "itemName");
            var id = IntField(rns, value, "itemId", "dataId", "id", "lootItId", "lootId");
            if (id is 0) id = null;
            var price = TextField(rns, value, "cost", "price", "gold");
            var ownSlot = IntField(rns, value, "itemSlotId", "slot", "slotId", "itemRowId");
            if (key.Length == 0 && name.Length == 0 && id == null) return;
            into.Add(Resolve(rns, ownSlot ?? slot, key, name, price, id));
            return;
        }
        if (value.Type == RValueType.String) {
            var text = ReadString(rns, value);
            if (!LooksLikeItem(text)) return;
            var key = LooksLikeKey(text) ? text : "";
            var name = key.Length == 0 ? text : "";
            into.Add(Resolve(rns, slot, key, name, "", null));
            return;
        }
        if (value.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            var id = value.ToInt(-1);
            if (id <= 0 || id > 4000) return;
            var offer = Resolve(rns, slot, "", "", "", id);
            if (offer.Key.Length == 0 && offer.Name.Length == 0) {
                offer = new Offer(slot, "id=" + id.ToString(CultureInfo.InvariantCulture), "", "", id);
            }
            into.Add(offer);
        }
    }

    private static Offer Resolve(IRNSReloaded rns, int slot, string key, string name, string price, int? id) {
        if (id is int known) {
            if (key.Length == 0) key = KeyFromId(rns, known);
            if (name.Length == 0) name = NameFromId(rns, known);
        }
        if (key.Length > 0 && name.Length == 0 && IdFromKey(rns, key) is int mapped) {
            name = NameFromId(rns, mapped);
        }
        if (key.Length > 0 && name.Length == 0) name = ItemCatalog.NameForKey(key, LanguageIndex(rns));
        if (name.Length > 0 && key.Length == 0 && LooksLikeKey(name)) {
            key = name;
            name = IdFromKey(rns, key) is int fromKey ? NameFromId(rns, fromKey) : ItemCatalog.NameForKey(key, LanguageIndex(rns));
        }
        return new Offer(slot, key, name, price, id ?? 0);
    }

    private static void ApplyPrices(
        IRNSReloaded rns,
        CInstance* global,
        List<RValue> controllers,
        List<(string Label, List<Offer> Rows)> groups
    ) {
        var cost = First(rns, global, controllers, "storeSlotCost");
        if (!cost.Has) return;
        var prices = new List<string>();
        if (cost.Value.Type == RValueType.Array) {
            var len = ArrayLength(rns, cost.Value);
            if (len <= 0 || len > MaxRows) return;
            for (var i = 0; i < len; i++) {
                if (!TryIndex(rns, cost.Value, i, out var cell)) continue;
                var text = Plain(rns, cell);
                if (text.Length > 0) prices.Add(text);
            }
        } else if (cost.Value.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            prices.Add(Plain(rns, cost.Value));
        }
        if (prices.Count == 0) return;
        var filled = groups.Where(group => group.Rows.Count > 0).ToList();
        var same = filled.Where(group => group.Rows.Count == prices.Count).ToList();
        if (same.Count == 1) {
            Stamp(same[0].Rows, prices);
            return;
        }
        var total = filled.Sum(group => group.Rows.Count);
        if (total == prices.Count) {
            var cursor = 0;
            foreach (var group in filled) {
                for (var i = 0; i < group.Rows.Count && cursor < prices.Count; i++, cursor++) {
                    if (group.Rows[i].Price.Length > 0) continue;
                    group.Rows[i] = new Offer(group.Rows[i].Slot, group.Rows[i].Key, group.Rows[i].Name, prices[cursor], group.Rows[i].Id);
                }
            }
        }
    }

    private static void Stamp(List<Offer> rows, List<string> prices) {
        for (var i = 0; i < rows.Count && i < prices.Count; i++) {
            if (rows[i].Price.Length > 0) continue;
            rows[i] = new Offer(rows[i].Slot, rows[i].Key, rows[i].Name, prices[i], rows[i].Id);
        }
    }

    private static string NameFromId(IRNSReloaded rns, int id) {
        var live = LiveDisplayName(rns, id);
        if (live.Length > 0) return live;
        return ItemCatalog.Name(id, LanguageIndex(rns));
    }

    private static string KeyFromId(IRNSReloaded rns, int id) {
        var map = Live(rns.FindGlobalValue("itemDataKeyMap"));
        if (map.Type == RValueType.Array) {
            var text = TextAt(rns, map, id);
            if (LooksLikeKey(text)) return text;
        }
        var fromTable = FirstCatalogKey(rns, id);
        if (fromTable.Length > 0) return fromTable;
        var sheet = ItemCatalog.Key(id);
        if (sheet.Length > 0) return sheet;
        if (map.Type != RValueType.Object || map.Object == null) return "";
        List<string> names;
        try {
            var box = map;
            names = rns.GetStructKeys(&box);
        } catch {
            return "";
        }
        if (names.Count > 1200) return "";
        foreach (var name in names) {
            var value = Live(rns.FindValue(map.Object, name));
            if (Present(value) && value.ToInt(-1) == id && LooksLikeKey(name)) return name;
        }
        return "";
    }

    private static int? IdFromKey(IRNSReloaded rns, string key) {
        var map = Live(rns.FindGlobalValue("itemDataKeyMap"));
        if (map.Type == RValueType.Object && map.Object != null) {
            var value = Live(rns.FindValue(map.Object, key));
            if (Present(value)) {
                var id = value.ToInt(-1);
                if (id > 0) return id;
            }
        }
        if (map.Type != RValueType.Array) return null;
        var len = Math.Min(ArrayLength(rns, map), 800);
        for (var i = 0; i < len; i++) {
            if (TextAt(rns, map, i) == key) return i;
        }
        return null;
    }

    private static (int Index, string Source) NotchIndex(
        IRNSReloaded rns,
        CInstance* global,
        List<RValue> controllers,
        RValue notches
    ) {
        foreach (var name in new[] { "currentPos", "notchCurrent", "hallwayPos", "hallwayCurrent" }) {
            var found = First(rns, global, controllers, name);
            if (!found.Has) continue;
            var index = found.Value.ToInt(-1);
            var len = ArrayLength(rns, notches);
            if (index >= 0 && index < len) return (index, name);
        }
        return (-1, "");
    }

    private static string FormatNotch(IRNSReloaded rns, RValue notch) {
        if (!Present(notch) || notch.Type != RValueType.Array) return Shown(Plain(rns, notch));
        var typeNum = TryIndex(rns, notch, 0, out var type) ? type.ToInt() : 0;
        var typeName = Enum.IsDefined(typeof(NotchType), typeNum) ? ((NotchType) typeNum).ToString() : "Unknown";
        var encounter = TryIndex(rns, notch, 1, out var enc) && enc.Type == RValueType.String
            ? Quote(ReadString(rns, enc))
            : "-";
        var flags = TryIndex(rns, notch, 3, out var flag) ? flag.ToInt() : 0;
        return "type=" + typeName + " (" + typeNum.ToString(CultureInfo.InvariantCulture) + ") encounter="
               + encounter + " flags=" + FormatFlags(flags);
    }

    private static string FormatFlags(int flags) {
        var names = new List<string>();
        void Add(int bit, string name) {
            if ((flags & bit) != 0) names.Add(name);
        }
        Add(NotchFlag.BOSS, "boss");
        Add(NotchFlag.FINAL_BOSS, "final-boss");
        Add(NotchFlag.TOXBOX_BOSS, "toybox-boss");
        Add(NotchFlag.SHOPKEEP_CAT, "shop-asha");
        Add(NotchFlag.SHOPKEEP_WOLF, "shop-tassha");
        Add(NotchFlag.SHOPKEEP_BIRD, "shop-saya");
        Add(NotchFlag.FLOWER_FIGHT, "flower");
        Add(NotchFlag.FINALSONG, "final-song");
        Add(NotchFlag.MUSICFADE, "music-fade");
        var known = NotchFlag.BOSS | NotchFlag.FINAL_BOSS | NotchFlag.TOXBOX_BOSS | NotchFlag.SHOPKEEP_CAT
                    | NotchFlag.SHOPKEEP_WOLF | NotchFlag.SHOPKEEP_BIRD | NotchFlag.FLOWER_FIGHT
                    | NotchFlag.FINALSONG | NotchFlag.MUSICFADE;
        var rest = flags & ~known;
        if (rest != 0) names.Add("other=0x" + rest.ToString("X", CultureInfo.InvariantCulture));
        if (names.Count == 0) return "0";
        return string.Join(", ", names) + " (raw " + flags.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static int PlayerCount(IRNSReloaded rns, CInstance* global) {
        var table = Live(rns.FindValue(global, "playerNum"));
        if (TryIndex(rns, table, 0, out var cell) && IsLive(cell)) {
            var n = cell.ToInt(-1);
            if (n >= 0 && n <= MaxPlayers) return n;
        }
        try {
            return Math.Clamp(rns.utils.GetNumPlayers(), 0, MaxPlayers);
        } catch {
            return 0;
        }
    }

    private static int? Count(IRNSReloaded rns, CInstance* global, List<RValue> controllers, params string[] names) {
        foreach (var name in names) {
            var found = First(rns, global, controllers, name);
            if (!found.Has) continue;
            var n = found.Value.ToInt(-1);
            if (n >= 0 && n <= MaxRows) return n;
        }
        return null;
    }

    private readonly struct Found {
        public bool Ok { get; init; }
        public RValue Value { get; init; }
        public bool Has => this.Ok;
    }

    private static Found First(IRNSReloaded rns, CInstance* global, List<RValue> controllers, string name) {
        var direct = rns.FindValue(global, name);
        if (direct != null && IsLive(*direct)) return new Found { Ok = true, Value = *direct };
        foreach (var id in controllers) {
            try {
                var got = rns.ExecuteCodeFunction("variable_instance_get", [id, name]);
                if (got is RValue value && Present(value)) return new Found { Ok = true, Value = value };
            } catch {
                // The next controller may still hold the row.
            }
        }
        return default;
    }

    private static string FirstText(IRNSReloaded rns, CInstance* global, List<RValue> controllers, string name) {
        var found = First(rns, global, controllers, name);
        return found.Has ? Plain(rns, found.Value) : "";
    }

    private static List<RValue> FindControllers(IRNSReloaded rns) {
        var ids = new List<RValue>();
        foreach (var name in Controllers) {
            try {
                var asset = rns.ExecuteCodeFunction("asset_get_index", [name]);
                if (asset is not RValue index || index.ToInt(-1) < 0) continue;
                var countValue = rns.ExecuteCodeFunction("instance_number", [index]);
                var count = countValue?.ToInt(0) ?? 0;
                count = Math.Clamp(count, 0, 4);
                for (var i = 0; i < count; i++) {
                    var inst = rns.ExecuteCodeFunction("instance_find", [index, i]);
                    if (inst is RValue id && Present(id)) ids.Add(id);
                }
            } catch {
                // A missing object leaves the global variables as the source.
            }
        }
        return ids;
    }

    private static string TextField(IRNSReloaded rns, RValue value, params string[] names) {
        if (value.Type != RValueType.Object || value.Object == null) return "";
        foreach (var name in names) {
            var field = rns.FindValue(value.Object, name);
            if (field == null || !IsLive(*field)) continue;
            var text = Plain(rns, *field);
            if (text.Length > 0) return text;
        }
        return "";
    }

    private static int? IntField(IRNSReloaded rns, RValue value, params string[] names) {
        if (value.Type != RValueType.Object || value.Object == null) return null;
        foreach (var name in names) {
            var field = rns.FindValue(value.Object, name);
            if (field == null || !IsLive(*field)) continue;
            var cell = *field;
            if (cell.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) continue;
            var number = cell.ToInt(-1);
            if (number >= 0) return number;
        }
        return null;
    }

    private static string TextAt(IRNSReloaded rns, RValue array, int index) {
        return TryIndex(rns, array, index, out var cell) ? ReadString(rns, cell) : "";
    }

    private static bool TryIndex(IRNSReloaded rns, RValue array, int index, out RValue cell) {
        cell = default;
        if (array.Type != RValueType.Array || index < 0) return false;
        var len = ArrayLength(rns, array);
        if (index >= len) return false;
        var entry = rns.ArrayGetEntry(&array, index);
        if (entry == null) return false;
        cell = *entry;
        return true;
    }

    private static int ArrayLength(IRNSReloaded rns, RValue array) {
        if (array.Type != RValueType.Array) return -1;
        try {
            var n = rns.ArrayGetLength(&array);
            return n == null ? -1 : n.Value.ToInt(-1);
        } catch {
            return -1;
        }
    }

    private static RValue Live(RValue* value) {
        return value == null || !IsLive(*value) ? default : *value;
    }

    private static bool IsLive(RValue value) {
        return value.Type is not (RValueType.Undefined or RValueType.Unset or RValueType.Null);
    }

    private static bool Present(RValue value) => IsLive(value);

    private static string Plain(IRNSReloaded rns, RValue value) {
        if (!Present(value)) return "";
        return value.Type switch {
            RValueType.String => ReadString(rns, value),
            RValueType.Real => Num(value.Real),
            RValueType.Int32 => value.Int32.ToString(CultureInfo.InvariantCulture),
            RValueType.Int64 => value.Int64.ToString(CultureInfo.InvariantCulture),
            RValueType.Bool => value.ToBool() ? "true" : "false",
            _ => "",
        };
    }

    private static string ReadString(IRNSReloaded rns, RValue value) {
        if (value.Type != RValueType.String) return "";
        try {
            var text = rns.GetString(&value);
            if (text.Length > 80) text = text[..80];
            return text;
        } catch {
            return "";
        }
    }

    private const int AllObjects = -3;

    private readonly struct ChoiceHit {
        public string Name { get; init; }
        public RValue Handle { get; init; }
        public int Id { get; init; }
        public List<string> Keys { get; init; }
    }

    private readonly struct HeldSys {
        public int Slot { get; init; }
        public RValue Value { get; init; }
    }

    private static void ChoiceUi(StringBuilder sb, IRNSReloaded rns) {
        var countValue = rns.ExecuteCodeFunction("instance_number", [AllObjects]);
        var count = Math.Max(0, countValue?.ToInt(0) ?? 0);
        var indexes = new List<int>();
        if (count <= 160) {
            for (var i = 0; i < count; i++) indexes.Add(i);
        } else {
            for (var i = 0; i < 16; i++) indexes.Add(i);
            for (var i = count - 120; i < count; i++) {
                if (i >= 16) indexes.Add(i);
            }
        }
        var hits = new List<ChoiceHit>();
        var held = new List<HeldSys>();
        var systems = new List<RValue>();
        var heldSlots = new HashSet<int>();
        foreach (var index in indexes) {
            try {
                var found = rns.ExecuteCodeFunction("instance_find", [AllObjects, index]);
                if (found is not RValue id || !Present(id)) continue;
                var name = ObjectName(rns, id);
                if (name == "obj_hotbarSlot") {
                    var row = ReadInstanceVar(rns, id, "itemRowId");
                    var slot = ReadInstanceVar(rns, id, "itemSlotId");
                    var sys = ReadInstanceVar(rns, id, "itemSys");
                    if (row is RValue rowValue && slot is RValue slotValue && sys is RValue sysValue
                        && rowValue.ToInt(-1) == 7 && slotValue.ToInt(-1) is 1 or 2 or 3
                        && IsLive(sysValue) && heldSlots.Add(slotValue.ToInt(-1))) {
                        held.Add(new HeldSys { Slot = slotValue.ToInt(-1), Value = sysValue });
                    }
                }
                if (name == "obj_itemSystem" && systems.Count < 2) systems.Add(id);
                var keys = MatchingKeys(rns, id);
                if (!ChoiceWord(name) && keys.Count == 0) continue;
                hits.Add(new ChoiceHit { Name = name, Handle = id, Id = InstanceId(rns, id), Keys = keys });
            } catch {
                // One instance failing leaves the rest of the room readable.
            }
        }
        foreach (var slot in new[] { 1, 2, 3 }) {
            var match = held.FirstOrDefault(item => item.Slot == slot);
            sb.Append("hotbar row 7 slot ").Append(slot.ToString(CultureInfo.InvariantCulture)).Append(' ');
            if (match.Slot != slot) {
                sb.AppendLine("itemSys=(missing)");
                continue;
            }
            sb.AppendLine("itemSys");
            DescribeExact(sb, rns, match.Value);
        }
        if (systems.Count == 0) sb.AppendLine("obj_itemSystem=(missing)");
        foreach (var sys in systems) {
            sb.Append("obj_itemSystem id=").AppendLine(InstanceId(rns, sys).ToString(CultureInfo.InvariantCulture));
            DescribeVariables(sb, rns, sys);
        }
        sb.Append("scanned ").Append(indexes.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append('/').Append(count.ToString(CultureInfo.InvariantCulture)).AppendLine(" instances");
        if (hits.Count == 0) {
            sb.AppendLine("(no item, loot, store, square, or choice instance)");
            return;
        }
        hits.Sort((a, b) => {
            var byRank = ChoiceRank(a.Name).CompareTo(ChoiceRank(b.Name));
            if (byRank != 0) return byRank;
            var byKeys = b.Keys.Count.CompareTo(a.Keys.Count);
            return byKeys != 0 ? byKeys : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        foreach (var hit in hits) {
            sb.Append(hit.Name).Append(" id=").AppendLine(hit.Id.ToString(CultureInfo.InvariantCulture));
            foreach (var key in hit.Keys) {
                var value = ReadInstanceVar(rns, hit.Handle, key);
                sb.Append("  ").Append(key).Append(" = ");
                sb.AppendLine(value is RValue live && Present(live) ? TraceValue(rns, live, true) : "empty");
            }
        }
    }

    private static void DescribeExact(StringBuilder sb, IRNSReloaded rns, RValue value) {
        sb.Append("  ").AppendLine(RefHead(value));
        var keys = OwnKeys(rns, value, int.MaxValue);
        if (keys.Count == 0) {
            sb.AppendLine("  keys=(none)");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys) {
            if (!seen.Add(key)) continue;
            var field = ReadOn(rns, value, key);
            sb.Append("  ").Append(key).Append(" = ");
            if (field is not RValue live || !IsLive(live)) {
                sb.AppendLine("empty");
                continue;
            }
            sb.AppendLine(TraceValue(rns, live, true));
            if (key == "squareMap" || HoldsRows(rns, live)) DescribeRow7(sb, rns, live);
        }
    }

    private static void DescribeVariables(StringBuilder sb, IRNSReloaded rns, RValue handle) {
        var keys = OwnKeys(rns, handle, int.MaxValue);
        if (keys.Count == 0) {
            sb.AppendLine("  keys=(none)");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys) {
            if (!seen.Add(key)) continue;
            var field = ReadInstanceVar(rns, handle, key);
            sb.Append("  ").Append(key).Append(" = ");
            sb.AppendLine(field is RValue live && IsLive(live) ? TraceValue(rns, live, true) : "empty");
        }
    }

    private static bool HoldsRows(IRNSReloaded rns, RValue value) {
        if (value.Type == RValueType.Array) return ArrayLength(rns, value) > 7;
        if (value.Type is not (RValueType.Object or RValueType.Ref)) return false;
        foreach (var name in OwnKeys(rns, value, 20)) {
            if (name == "squareMap") return true;
            var child = ReadOn(rns, value, name);
            if (child is RValue inner && inner.Type == RValueType.Array && ArrayLength(rns, inner) > 7) return true;
        }
        return false;
    }

    private static void DescribeRow7(StringBuilder sb, IRNSReloaded rns, RValue grid) {
        if (grid.Type is RValueType.Object or RValueType.Ref) {
            foreach (var name in OwnKeys(rns, grid, 20)) {
                var child = ReadOn(rns, grid, name);
                if (child is not RValue inner || inner.Type != RValueType.Array || ArrayLength(rns, inner) <= 7) continue;
                sb.Append("  .").AppendLine(name);
                DescribeRow7(sb, rns, inner);
                return;
            }
        }
        if (grid.Type != RValueType.Array || !TryIndex(rns, grid, 7, out var row)) {
            sb.AppendLine("  [7] missing");
            return;
        }
        sb.Append("  [7] type=").Append(row.Type.ToString());
        if (row.Type == RValueType.Array) {
            sb.Append(" length=").Append(ArrayLength(rns, row).ToString(CultureInfo.InvariantCulture));
        }
        sb.AppendLine();
        foreach (var slot in new[] { 1, 2, 3 }) {
            sb.Append("  [7][").Append(slot.ToString(CultureInfo.InvariantCulture)).Append("] ");
            if (!EntryAt(rns, row, slot, out var cell)) {
                sb.AppendLine("missing");
                continue;
            }
            sb.Append("type=").Append(cell.Type.ToString());
            sb.Append(" itemId=").Append(ShownInt(ReadIntOn(rns, cell, "itemId")));
            sb.Append(" itemKey=").Append(Shown(ReadTextOn(rns, cell, "itemKey")));
            sb.Append(" dataId=").Append(ShownInt(ReadIntOn(rns, cell, "dataId")));
            sb.Append(" name=").Append(Shown(ReadTextOn(rns, cell, "name")));
            var entryKeys = OwnKeys(rns, cell, 8);
            if (entryKeys.Count > 0) sb.Append(" keys=").Append(string.Join(",", entryKeys));
            sb.AppendLine();
        }
    }

    private static string RefHead(RValue value) {
        var text = "type=" + value.Type;
        if (value.Pointer != 0) text += " ptr=" + ((ulong) value.Pointer).ToString("X", CultureInfo.InvariantCulture);
        if (value.Type == RValueType.Object && value.Object != null && value.Object->ID > 0) {
            text += " id=" + value.Object->ID.ToString(CultureInfo.InvariantCulture);
        } else {
            var number = value.ToInt(-1);
            if (number > 0) text += " id=" + number.ToString(CultureInfo.InvariantCulture);
        }
        return text;
    }

    private static RValue? ReadOn(IRNSReloaded rns, RValue target, string key) {
        try {
            var got = rns.ExecuteCodeFunction("variable_instance_get", [target, key]);
            if (got is RValue value && IsLive(value)) return value;
        } catch {
            // A struct ref is read by the next call.
        }
        try {
            var got = rns.ExecuteCodeFunction("variable_struct_get", [target, key]);
            if (got is RValue value && IsLive(value)) return value;
        } catch {
            // The ref has no field by this name.
        }
        return null;
    }

    private static int? ReadIntOn(IRNSReloaded rns, RValue target, string key) {
        if (ReadOn(rns, target, key) is not RValue value) return null;
        if (value.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return null;
        var number = value.ToInt(-1);
        return number >= 0 ? number : null;
    }

    private static string ReadTextOn(IRNSReloaded rns, RValue target, string key) {
        return ReadOn(rns, target, key) is RValue value ? Plain(rns, value) : "";
    }

    private static bool EntryAt(IRNSReloaded rns, RValue row, int slot, out RValue cell) {
        cell = default;
        if (row.Type == RValueType.Array) return TryIndex(rns, row, slot, out cell);
        if (row.Type is not (RValueType.Object or RValueType.Ref)) return false;
        foreach (var name in OwnKeys(rns, row, 12)) {
            if (Member(rns, row, name) is not RValue child || child.Type != RValueType.Array) continue;
            if (TryIndex(rns, child, slot, out cell)) return true;
        }
        return false;
    }

    private static string ShownInt(int? value) {
        return value is int number ? number.ToString(CultureInfo.InvariantCulture) : "-";
    }

    private static List<string> OwnKeys(IRNSReloaded rns, RValue target, int cap) {
        var keys = new List<string>();
        void Take(RValue? names) {
            if (keys.Count >= cap || names is not RValue arr || arr.Type != RValueType.Array) return;
            var len = ArrayLength(rns, arr);
            if (len <= 0) return;
            for (var i = 0; i < len && keys.Count < cap; i++) {
                if (!TryIndex(rns, arr, i, out var cell)) continue;
                var name = ReadString(rns, cell);
                if (name.Length > 0) keys.Add(name);
            }
        }
        try {
            Take(rns.ExecuteCodeFunction("variable_instance_get_names", [target]));
        } catch {
            // The struct name list is the other source.
        }
        try {
            Take(rns.ExecuteCodeFunction("variable_struct_get_names", [target]));
        } catch {
            // A ref with no name list still prints its type.
        }
        if (keys.Count == 0 && target.Type == RValueType.Object) {
            try {
                var box = target;
                foreach (var name in rns.GetStructKeys(&box)) {
                    if (name.Length == 0) continue;
                    keys.Add(name);
                    if (keys.Count >= cap) break;
                }
            } catch {
                // The type line still stands without keys.
            }
        }
        return keys;
    }

    private static int InstanceId(IRNSReloaded rns, RValue handle) {
        var builtin = ReadInstanceVar(rns, handle, "id");
        if (builtin is RValue idValue) {
            var fromVar = idValue.ToInt(-1);
            if (fromVar > 0) return fromVar;
        }
        if (handle.Type == RValueType.Object && handle.Object != null && handle.Object->ID > 0) {
            return handle.Object->ID;
        }
        var direct = handle.ToInt(-1);
        return direct > 0 ? direct : 0;
    }

    private static RValue? ReadInstanceVar(IRNSReloaded rns, RValue handle, string key) {
        try {
            var got = rns.ExecuteCodeFunction("variable_instance_get", [handle, key]);
            if (got is RValue value && IsLive(value)) return value;
        } catch {
            // The instance pointer below is the same lookup the library uses.
        }
        if (handle.Type == RValueType.Object && handle.Object != null) {
            var field = rns.FindValue(handle.Object, key);
            if (field != null && IsLive(*field)) return *field;
        }
        return null;
    }

    private static int ChoiceRank(string name) {
        if (name.Contains("itemLootRoller", StringComparison.Ordinal)) return 0;
        if (name.Contains("itemSystem", StringComparison.Ordinal)) return 1;
        if (name.Contains("hotbarSlot", StringComparison.Ordinal)) return 2;
        return 3;
    }

    private static string ObjectName(IRNSReloaded rns, RValue id) {
        var index = rns.ExecuteCodeFunction("variable_instance_get", [id, "object_index"]);
        if (index is not RValue obj || !Present(obj)) return "?";
        var name = rns.ExecuteCodeFunction("object_get_name", [obj]);
        if (name is not RValue text) return "?";
        var value = ReadString(rns, text);
        return value.Length == 0 ? "?" : value;
    }

    private static List<string> MatchingKeys(IRNSReloaded rns, RValue id) {
        var keys = new List<string>();
        var names = rns.ExecuteCodeFunction("variable_instance_get_names", [id]);
        if (names is not RValue arr || arr.Type != RValueType.Array) return keys;
        var len = ArrayLength(rns, arr);
        if (len <= 0) return keys;
        var take = Math.Min(len, 240);
        for (var i = 0; i < take; i++) {
            if (!TryIndex(rns, arr, i, out var cell)) continue;
            var name = ReadString(rns, cell);
            if (ChoiceWord(name)) keys.Add(name);
            if (keys.Count >= 12) break;
        }
        return keys;
    }

    private static bool ChoiceWord(string text) {
        if (text.Length == 0 || text.Length > 64) return false;
        var lower = text.ToLowerInvariant();
        return lower.Contains("item") || lower.Contains("loot") || lower.Contains("store")
               || lower.Contains("square") || lower.Contains("choice");
    }

    private static bool LooksLikeKey(string text) {
        if (text.Length is < 2 or > 64) return false;
        foreach (var c in text) {
            if (c > 127) return false;
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.')) return false;
        }
        return text.Contains('_');
    }

    private static bool LooksLikeItem(string text) {
        if (text.Length is < 1 or > 80) return false;
        return LooksLikeKey(text) || text.Any(c => c > 127);
    }

    private static string Shown(string text) {
        return text.Length == 0 ? "-" : Quote(text);
    }

    private static string Num(double value) {
        if (double.IsNaN(value) || double.IsInfinity(value)) return value.ToString(CultureInfo.InvariantCulture);
        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 0.000_000_1) return ((long) rounded).ToString(CultureInfo.InvariantCulture);
        return value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static string Quote(string text) {
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static List<Offer> LootCards(IRNSReloaded rns, HashSet<int> abilityIds) {
        var found = new List<(RValue Id, int ItemId, int TransformId)>();
        var count = Math.Clamp(rns.ExecuteCodeFunction("instance_number", [AllObjects])?.ToInt(0) ?? 0, 0, 400);
        for (var i = 0; i < count && found.Count < 8; i++) {
            RValue id;
            try {
                var foundHandle = rns.ExecuteCodeFunction("instance_find", [AllObjects, i]);
                if (foundHandle is not RValue handle || !Present(handle)) continue;
                id = handle;
            } catch {
                continue;
            }
            if (ObjectName(rns, id) != "obj_lootInfo") continue;
            var raw = ReadInstanceVar(rns, id, "itemId");
            if (raw is not RValue value || value.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) continue;
            var itemId = value.ToInt(-1);
            if (itemId is < 1 or > 8000 || abilityIds.Contains(itemId)) continue;
            if (!LootCardShown(rns, id)) continue;
            var transform = TryNumber(rns, id, "transformId", out var linked) && linked is > 0 and <= 8000 ? linked : 0;
            found.Add((id, itemId, transform));
        }
        var targets = new HashSet<int>();
        foreach (var row in found) {
            if (row.TransformId > 0) targets.Add(row.TransformId);
        }
        var limit = 5;
        foreach (var sys in FindNamed(rns, "obj_itemSystem", 1)) {
            if (TryNumber(rns, sys, "lootCount", out var shown) && shown is >= 1 and <= 8) limit = shown;
        }
        var rows = new List<Offer>();
        foreach (var row in found) {
            if (targets.Contains(row.ItemId) && found.Exists(other => other.ItemId != row.ItemId && other.TransformId == row.ItemId && row.TransformId != other.ItemId)) continue;
            rows.Add(OfferFromId(rns, rows.Count, row.ItemId));
            if (rows.Count == limit) break;
        }
        return rows;
    }

    private static bool LootCardShown(IRNSReloaded rns, RValue id) {
        var visible = ReadInstanceVar(rns, id, "visible");
        if (visible is RValue flag && (flag.Type is RValueType.Bool or RValueType.Real or RValueType.Int32 or RValueType.Int64) && !flag.ToBool()) return false;
        if (TryNumber(rns, id, "x", out var x) && x is < -5000 or > 4000) return false;
        if (TryNumber(rns, id, "y", out var y) && y is < -5000 or > 4000) return false;
        return true;
    }

    private static List<Offer> StoreOffers(IRNSReloaded rns, RValue sys, HashSet<int> abilityIds) {
        var cost = ReadInstanceVar(rns, sys, "storeSlotCost");
        if (cost is not RValue costs || costs.Type != RValueType.Array) return [];
        var len = Math.Min(ArrayLength(rns, costs), 12);
        var heal = ReadInstanceVar(rns, sys, "storeSlotHeal");
        var opc = ReadInstanceVar(rns, sys, "storeSlotOPC");
        var upgrade = ReadInstanceVar(rns, sys, "storeSlotUpgrade");
        var wares = ScanWares(rns);
        var used = new HashSet<int>();
        var focus = FocusSlot(rns, sys);
        var focusUpgrade = focus >= 0 && FlagAt(rns, upgrade, focus);
        var focusId = focus >= 0 ? FocusWareId(rns, sys, focusUpgrade, abilityIds) : -1;
        var listed = ShopIds(rns, sys, abilityIds, focus, focusId);
        var rows = new List<Offer>();
        for (var i = 0; i < len; i++) {
            if (!TryIndex(rns, costs, i, out var cell)) continue;
            if (cell.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) continue;
            var priceNum = cell.ToInt(-1);
            if (priceNum is <= 0 or >= 1000) continue;
            var price = Plain(rns, cell);
            var isHeal = FlagAt(rns, heal, i);
            var isUpgrade = FlagAt(rns, upgrade, i);
            var isLevel = !isHeal && !isUpgrade && !FlagAt(rns, opc, i);
            var ware = !isHeal && !isLevel ? TakeWare(wares, used, i, isUpgrade, SlotCenterX(rns, sys, i)) : null;
            ware ??= !isHeal && !isLevel ? GlobalWare(rns, sys, i, isUpgrade) : null;
            Offer offer;
            if (i < listed.Length && listed[i] > 0) {
                offer = ShopOffer(rns, i, listed[i], "", price);
            } else if (i == focus && focusId > 0) {
                offer = ShopOffer(rns, i, focusId, "", price);
            } else if (ware is Ware known && (known.Id <= 0 || !abilityIds.Contains(known.Id)) && (known.Id > 0 || known.Key.Length > 0)) {
                offer = ShopOffer(rns, i, known.Id, known.Key, price);
            } else if (isHeal) {
                offer = ShopOffer(rns, i, 0, "pk_health_full", price);
            } else if (isLevel) {
                offer = ShopOffer(rns, i, 0, "pk_level_up", price);
            } else {
                offer = new Offer(i, "", "", price);
            }
            if (offer.Price.Length == 0) offer = new Offer(offer.Slot, offer.Key, offer.Name, price, offer.Id);
            rows.Add(offer);
        }
        return rows;
    }

    private readonly struct Ware {
        public int Id { get; init; }
        public string Key { get; init; }
        public int X { get; init; }
        public bool HasX { get; init; }
        public int Hint { get; init; }
        public bool HasHint { get; init; }
        public bool Upgrade { get; init; }
    }

    private static List<Ware> ScanWares(IRNSReloaded rns) {
        var list = new List<Ware>();
        var count = Math.Clamp(rns.ExecuteCodeFunction("instance_number", [AllObjects])?.ToInt(0) ?? 0, 0, 400);
        for (var i = 0; i < count; i++) {
            RValue handle;
            try {
                var found = rns.ExecuteCodeFunction("instance_find", [AllObjects, i]);
                if (found is not RValue id || !Present(id)) continue;
                handle = id;
            } catch {
                continue;
            }
            var pot = TryNumber(rns, handle, "potItId", out var potId) && potId is > 0 and <= 8000 ? potId : 0;
            var upgradeId = TryNumber(rns, handle, "upgradeItemId", out var upId) && upId is > 0 and <= 8000 ? upId : 0;
            var key = SpriteKey(rns, handle);
            if (pot == 0 && upgradeId == 0 && key.Length == 0) continue;
            var hasX = TryNumber(rns, handle, "potX", out var potX) || TryNumber(rns, handle, "x", out potX);
            var hasHint = TryNumber(rns, handle, "upgradeSqSlot", out var hint) || TryNumber(rns, handle, "potNum", out hint);
            list.Add(new Ware {
                Id = upgradeId > 0 ? upgradeId : pot,
                Key = key,
                X = hasX ? potX : 0,
                HasX = hasX,
                Hint = hasHint ? hint : 0,
                HasHint = hasHint,
                Upgrade = upgradeId > 0 || key.StartsWith("up_", StringComparison.Ordinal),
            });
        }
        return list;
    }

    private static int FocusSlot(IRNSReloaded rns, RValue sys) {
        if (ArrayHead(rns, sys, "highlightRow") != 2) return -1;
        var kind = ArrayHead(rns, sys, "invinfo_type");
        if (kind is not (3 or 4)) return -1;
        var slot = ArrayHead(rns, sys, "invinfo_slotId");
        return slot is >= 0 and <= 11 ? slot : -1;
    }

    private static int ArrayHead(IRNSReloaded rns, RValue sys, string name) {
        var value = ReadInstanceVar(rns, sys, name);
        if (value is not RValue array || array.Type != RValueType.Array || !TryIndex(rns, array, 0, out var cell)) return -1;
        if (cell.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return -1;
        return cell.ToInt(-1);
    }

    private static int FocusWareId(IRNSReloaded rns, RValue sys, bool upgrade, HashSet<int> abilityIds) {
        var dataId = ArrayHead(rns, sys, "invinfo_dataId");
        if (dataId is >= 1 and <= 8000 && !abilityIds.Contains(dataId)) return dataId;
        var objectName = upgrade ? "obj_itemUpgrade" : "obj_inventoryInfo";
        var field = upgrade ? "upgradeItemId" : "itemId";
        var count = Math.Clamp(rns.ExecuteCodeFunction("instance_number", [AllObjects])?.ToInt(0) ?? 0, 0, 400);
        for (var i = 0; i < count; i++) {
            RValue id;
            try {
                var found = rns.ExecuteCodeFunction("instance_find", [AllObjects, i]);
                if (found is not RValue handle || !Present(handle)) continue;
                id = handle;
            } catch {
                continue;
            }
            if (ObjectName(rns, id) != objectName) continue;
            var raw = ReadInstanceVar(rns, id, field);
            if (raw is not RValue value || value.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) continue;
            var itemId = value.ToInt(-1);
            if (itemId is < 1 or > 8000 || abilityIds.Contains(itemId)) continue;
            return itemId;
        }
        return -1;
    }

    private static Ware? GlobalWare(IRNSReloaded rns, RValue sys, int slot, bool upgrade) {
        foreach (var name in upgrade ? new[] { "upgradeItemId" } : new[] { "potItId", "potId" }) {
            var local = ReadInstanceVar(rns, sys, name);
            if (WareAt(rns, local, slot, upgrade) is Ware onSys) return onSys;
            var pointer = rns.FindGlobalValue(name);
            if (pointer != null && WareAt(rns, *pointer, slot, upgrade) is Ware onGlobal) return onGlobal;
        }
        return null;
    }

    private static Ware? WareAt(IRNSReloaded rns, RValue? value, int slot, bool upgrade) {
        if (value is not RValue array || array.Type != RValueType.Array) return null;
        if (!TryIndex(rns, array, slot, out var cell)) return null;
        if (cell.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
            var number = cell.ToInt(-1);
            if (number is > 0 and <= 8000) return new Ware { Id = number, Key = "", Upgrade = upgrade };
        } else if (cell.Type == RValueType.String) {
            var text = ReadString(rns, cell);
            if (IsPotionKey(text)) return new Ware { Id = 0, Key = text, Upgrade = upgrade || text.StartsWith("up_", StringComparison.Ordinal) };
        }
        return null;
    }

    private static Ware? TakeWare(List<Ware> wares, HashSet<int> used, int slot, bool upgrade, int? centerX) {
        var hinted = -1;
        var nearest = -1;
        var nearestDist = int.MaxValue;
        for (var i = 0; i < wares.Count; i++) {
            if (used.Contains(i) || wares[i].Upgrade != upgrade) continue;
            if (wares[i].HasHint && (wares[i].Hint == slot || wares[i].Hint == slot + 15)) hinted = i;
            if (wares[i].HasX && centerX is int x) {
                var dist = Math.Abs(wares[i].X - x);
                if (dist < nearestDist) {
                    nearestDist = dist;
                    nearest = i;
                }
            }
        }
        var pick = hinted >= 0 ? hinted : nearest >= 0 && nearestDist <= 400 ? nearest : -1;
        if (pick < 0) return null;
        used.Add(pick);
        return wares[pick];
    }

    private static int? SlotCenterX(IRNSReloaded rns, RValue sys, int slot) {
        var slots = ReadInstanceVar(rns, sys, "slots");
        if (slots is not RValue grid || !TryIndex(rns, grid, 2, out var row)) return null;
        if (!TryIndex(rns, row, slot, out var cell)) return null;
        if (!TryIndex(rns, cell, 2, out var x)) return null;
        if (x.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return null;
        return x.ToInt(int.MinValue) == int.MinValue ? null : x.ToInt(0);
    }

    private static bool TryNumber(IRNSReloaded rns, RValue handle, string key, out int number) {
        number = 0;
        var value = ReadInstanceVar(rns, handle, key);
        if (value is not RValue live || live.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return false;
        number = live.ToInt(int.MinValue);
        return number != int.MinValue;
    }

    private static string SpriteKey(IRNSReloaded rns, RValue handle) {
        var sprite = ReadInstanceVar(rns, handle, "sprite_index");
        if (sprite is not RValue index || index.Type is not (RValueType.Real or RValueType.Int32 or RValueType.Int64)) return "";
        if (index.ToInt(-1) < 0) return "";
        try {
            var named = rns.ExecuteCodeFunction("sprite_get_name", [index]);
            if (named is not RValue text) return "";
            return PotionKeyIn(ReadString(rns, text));
        } catch {
            return "";
        }
    }

    private static string PotionKeyIn(string name) {
        foreach (var prefix in new[] { "pk_", "pt_", "up_" }) {
            var at = name.IndexOf(prefix, StringComparison.Ordinal);
            if (at < 0) continue;
            var end = at;
            while (end < name.Length && (char.IsLetterOrDigit(name[end]) || name[end] == '_')) end++;
            var key = name[at..end];
            if (IsPotionKey(key)) return key;
        }
        return "";
    }

    private static bool IsPotionKey(string key) {
        return key.StartsWith("pk_", StringComparison.Ordinal)
            || key.StartsWith("pt_", StringComparison.Ordinal)
            || key.StartsWith("up_", StringComparison.Ordinal);
    }

    private static Offer ShopOffer(IRNSReloaded rns, int slot, int id, string key, string price) {
        var language = LanguageIndex(rns);
        if (IsPotionKey(key)) {
            return new Offer(slot, key, ItemCatalog.NameForKey(key, language), price, id);
        }
        if (id <= 0) return new Offer(slot, "", "", price);
        var resolved = OfferFromId(rns, slot, id);
        if (IsPotionKey(resolved.Key)) return new Offer(slot, resolved.Key, resolved.Name, price, id);
        var potion = ItemCatalog.Potion(id, language);
        if (potion.Key.Length > 0 && (resolved.Key.StartsWith("mv_", StringComparison.Ordinal) || resolved.Key.StartsWith("it_", StringComparison.Ordinal) || resolved.Name.Length == 0)) {
            return new Offer(slot, potion.Key, potion.Name, price, id);
        }
        return new Offer(slot, resolved.Key, resolved.Name, price, resolved.Id > 0 ? resolved.Id : id);
    }

    private static bool FlagAt(IRNSReloaded rns, RValue? array, int index) {
        if (array is not RValue value || !TryIndex(rns, value, index, out var cell) || !IsLive(cell)) return false;
        return cell.Type is RValueType.Bool or RValueType.Real or RValueType.Int32 or RValueType.Int64 && cell.ToBool();
    }

    private static int LanguageIndex(IRNSReloaded rns) {
        var inferred = InferredLanguage(rns);
        var pointer = rns.FindGlobalValue("language");
        if (pointer != null && IsLive(*pointer)) {
            var value = *pointer;
            if (value.Type is RValueType.Real or RValueType.Int32 or RValueType.Int64) {
                var number = value.ToInt(-1);
                if (number is >= 1 and <= 5) return number;
                if (number == 0 && inferred is not int) return 0;
            }
            if (value.Type == RValueType.String) {
                var text = ReadString(rns, value).Trim().ToLowerInvariant();
                string[] suffix = ["en", "jp", "ch", "kr", "uk", "fr"];
                string[] names = ["english", "japanese", "chinese", "korean", "ukrainian", "french"];
                for (var i = 0; i < suffix.Length; i++) {
                    if (text == suffix[i] || text == names[i]) return i;
                }
            }
        }
        return inferred ?? 2;
    }

    private static int? InferredLanguage(IRNSReloaded rns) {
        for (var id = 1; id <= 700; id++) {
            var live = LiveDisplayName(rns, id);
            foreach (var c in live) {
                if (c is >= '\u4e00' and <= '\u9fff') return 2;
            }
        }
        return null;
    }

    private static string LiveDisplayName(IRNSReloaded rns, int id) {
        var table = Live(rns.FindGlobalValue("itemData"));
        if (TryIndex(rns, table, id, out var entry)) {
            var direct = DisplayText(rns, entry);
            if (direct.Length > 0) return direct;
            for (var i = 0; i < 8; i++) {
                if (!TryIndex(rns, entry, i, out var cell)) continue;
                var text = DisplayText(rns, cell);
                if (text.Length > 0) return text;
                for (var j = 0; j < 8; j++) {
                    var nested = TextAt(rns, cell, j);
                    if (nested.Length > 0 && !LooksLikeKey(nested)) return nested;
                }
            }
        }
        var localized = Live(rns.FindGlobalValue("langNameMap"));
        if (!TryIndex(rns, localized, id, out var mapped)) return "";
        var name = DisplayText(rns, mapped);
        return name.Length > 0 ? name : "";
    }

    private static string DisplayText(IRNSReloaded rns, RValue value) {
        var text = ReadString(rns, value);
        return text.Length > 0 && !LooksLikeKey(text) ? text : "";
    }

    private static string FirstCatalogKey(IRNSReloaded rns, int id) {
        var table = Live(rns.FindGlobalValue("itemData"));
        if (!TryIndex(rns, table, id, out var entry)) return "";
        var top = TextAt(rns, entry, 0);
        if (LooksLikeKey(top)) return top;
        for (var i = 0; i < 8; i++) {
            if (!TryIndex(rns, entry, i, out var cell)) continue;
            var text = TextAt(rns, cell, 0);
            if (LooksLikeKey(text)) return text;
            var own = ReadString(rns, cell);
            if (LooksLikeKey(own)) return own;
        }
        return "";
    }

    private static class ItemCatalog {
        private readonly struct Row {
            public string Key { get; init; }
            public string[] Names { get; init; }
        }

        private static readonly object Gate = new();
        private static bool loaded;
        private static Dictionary<int, Row> byId = [];
        private static Dictionary<string, Row> byKey = new(StringComparer.Ordinal);
        private static List<Row> potions = [];

        public static string Key(int id) {
            Ensure();
            return byId.TryGetValue(id, out var row) ? row.Key : "";
        }

        public static int IdForKey(string key) {
            Ensure();
            if (key.Length == 0) return 0;
            foreach (var pair in byId) {
                if (pair.Value.Key == key) return pair.Key;
            }
            return 0;
        }

        public static string Name(int id, int language) {
            Ensure();
            return byId.TryGetValue(id, out var row) ? Pick(row, language) : "";
        }

        public static string NameForKey(string key, int language) {
            Ensure();
            return byKey.TryGetValue(key, out var row) ? Pick(row, language) : "";
        }

        public static List<string> NamesEnding(string suffix, int language) {
            Ensure();
            var found = new List<string>();
            if (suffix.Length == 0) return found;
            foreach (var row in byKey.Values) {
                var name = Pick(row, language);
                if (name.Length <= suffix.Length || !name.EndsWith(suffix, StringComparison.Ordinal) || found.Contains(name)) continue;
                found.Add(name);
                if (found.Count >= 8) break;
            }
            return found;
        }

        public static string[]? NamesOf(int id) {
            Ensure();
            return byId.TryGetValue(id, out var row) ? row.Names : null;
        }

        public static (string Key, string Name) Potion(int index, int language) {
            Ensure();
            if (index < 0 || index >= potions.Count) return ("", "");
            var row = potions[index];
            return (row.Key, Pick(row, language));
        }

        private static string Pick(Row row, int language) {
            if (row.Names.Length == 0) return "";
            var index = language is >= 0 and < 6 ? language : 0;
            return index < row.Names.Length && row.Names[index].Length > 0 ? row.Names[index] : row.Names[0];
        }

        private static void Ensure() {
            if (loaded) return;
            lock (Gate) {
                if (loaded) return;
                loaded = true;
                var dir = GameDir();
                if (dir == null) return;
                var data = Path.Combine(dir, "Data");
                var ids = new Dictionary<int, Row>();
                var keys = new Dictionary<string, Row>(StringComparer.Ordinal);
                var next = 0;
                foreach (var file in new[] { "Names_Move.csv", "Names_Item.csv" }) {
                    foreach (var row in ReadRows(Path.Combine(data, file))) {
                        ids[next++] = row;
                        keys.TryAdd(row.Key, row);
                    }
                }
                var potionRows = ReadRows(Path.Combine(data, "Names_Potion.csv"));
                foreach (var row in potionRows) {
                    ids[next++] = row;
                    keys.TryAdd(row.Key, row);
                }
                byId = ids;
                byKey = keys;
                potions = potionRows;
            }
        }

        private static string? GameDir() {
            try {
                var path = Environment.ProcessPath;
                var dir = path == null ? null : Path.GetDirectoryName(path);
                if (dir != null && File.Exists(Path.Combine(dir, "Data", "Names_Item.csv"))) return dir;
            } catch {
                return null;
            }
            return null;
        }

        private static List<Row> ReadRows(string path) {
            var rows = new List<Row>();
            if (!File.Exists(path)) return rows;
            foreach (var line in File.ReadAllLines(path)) {
                if (line.Length == 0 || line.StartsWith(',')) continue;
                var fields = SplitCsv(line);
                if (fields.Count == 0 || fields[0].Length == 0 || fields[0] == "key") continue;
                var names = new string[6];
                for (var i = 0; i < names.Length; i++) names[i] = fields.Count > i + 2 ? fields[i + 2] : "";
                rows.Add(new Row { Key = fields[0], Names = names });
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
