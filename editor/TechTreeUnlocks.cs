// TechTreeUnlocks.cs (HAF editor) — what a technology unlocks, for the Tech Tree viewer's cards and side strip.
//
// The game does not look this up anywhere else: a technology's unlocks ARE the effects stored in its own definition
// (TechnologyDefinition.SimulationEventEffects), and DepartmentOfScience.UpdateTechnologyUnlockInfo turns each effect
// that is not Hidden into one entry of the card's icon row. This file follows that function, effect type by effect
// type (decompiled, 2026-11-22):
//
//   UnlockConstructible              one entry for ALL the constructibles it names: the one with the highest Level
//                                    among those valid for the player's faction (the later one on a tie)
//   UnlockSettlementStartingPackage  the package
//   ApplyDescriptor                  the descriptor            AddEmpireBattleAbility   the ability
//   UnlockResource                   one entry per resource    ApplyCostModifier        the modifier
//   UnlockAction / ProhibitAction    the action type           ResetAllCivics / AddFame / UpgradeAllRoads
// A non-empty UIMapperOverride on the effect replaces the name the entry's UI mapper is looked up by.
//
// The editor has no player, so "valid for the player's faction" becomes "valid for a culture that is in nobody's
// list": a constructible whose faction prerequisite names no culture, or names the cultures it is NOT for (Operator
// None: the base Harbour is for everyone but the four cultures with a harbour of their own). Only when an effect
// names no such constructible does an emblematic one stand for it. The others are counted as alternatives.
//
// The icon is the entry's UI mapper image, as TechnologyUnlockItem takes it: for a constructible the "Small" image of
// the faction's visual variation, which a mapper stores as "Default_Small" (plus "Variation1_Small", ...); for the
// rest "Small" or "Picto". Read by name through reflection and SerializedObject: no reference to the game's types.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class TechTreeUnlocks
{
    public enum Kind { Unit, Extension, Infrastructure, Other }   // the game's TechnologyUnlockItem.UnlockType, without its striped variant

    public sealed class Unlock
    {
        public Kind Kind;
        public string Effect = "";        // the effect's type without "SimulationEventEffect_"
        public string ElementName = "";   // what is unlocked: the name its UI mapper is looked up by
        public string Title = "";         // its title as the mapper gives it; the name when there is no mapper
        public string ImagePath;          // the icon as a project asset path, or null
        public string ImageGuid;          // the icon as a Unity GUID text (the key into the game's atlases), or null when the mapper names none
        public bool MapperFound;
        public int Alternatives;          // further constructibles the same effect names (emblematic variants, lower levels)
    }

    /// <summary>The project's UI mappers and constructible definitions by name.</summary>
    public sealed class Index
    {
        public readonly Dictionary<string, UnityEngine.Object> Mappers = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        public readonly Dictionary<string, UnityEngine.Object> Constructibles = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
    }

    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string EffectPrefix = "SimulationEventEffect_";

    // the image keys in the order they are tried; then any other key that ends in the suffix
    static readonly string[] ConstructibleKeys = { "Default_Small", "Small" };
    static readonly string[] OtherKeys = { "Small", "Default_Small", "Picto" };
    static readonly string[] PictoKeys = { "Picto", "Small", "Default_Small" };

    /// <summary>
    /// One pass over the project's ScriptableObject assets. A copy under Databases wins over one under the reference
    /// folder, which wins over one elsewhere, as for the technologies themselves.
    /// </summary>
    internal static Index BuildIndex(string referenceRoot)
    {
        var index = new Index();
        var constructibleType = TechTreeData.FindType("Amplitude.Mercury.Data.Simulation.ConstructibleDefinition");
        var hasImages = new Dictionary<Type, bool>();
        var mapperRank = new Dictionary<string, int>(StringComparer.Ordinal);
        var constructibleRank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            int rank = IsUnder(path, TechTreeData.DatabasesRoot) ? 2 : IsUnder(path, referenceRoot) ? 1 : 0;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || string.IsNullOrEmpty(o.name)) continue;
                var type = o.GetType();
                if (constructibleType != null && constructibleType.IsInstanceOfType(o)) { Put(index.Constructibles, constructibleRank, o, rank); continue; }
                if (!hasImages.TryGetValue(type, out bool mapper)) hasImages[type] = mapper = FieldOf(type, "Images") != null;
                if (mapper) Put(index.Mappers, mapperRank, o, rank);
            }
        }
        return index;
    }

    static bool IsUnder(string path, string root) =>
        !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(root) && (path == root || path.StartsWith(root + "/", StringComparison.Ordinal));

    static void Put(Dictionary<string, UnityEngine.Object> map, Dictionary<string, int> ranks, UnityEngine.Object o, int rank)
    {
        if (ranks.TryGetValue(o.name, out int had) && had >= rank) return;
        map[o.name] = o; ranks[o.name] = rank;
    }

    /// <summary>
    /// The entries of a technology's icon row, in the order of its effects. `localize` turns a mapper's title (a
    /// "%Key" or plain text) into what is shown.
    /// </summary>
    internal static List<Unlock> Read(object technologyDefinition, Index index, Func<string, string> localize)
    {
        var list = new List<Unlock>();
        if (technologyDefinition == null || index == null) return list;
        if (!(ValueOf(technologyDefinition, "SimulationEventEffects") is IEnumerable effects)) return list;
        foreach (object effect in effects)
        {
            if (effect == null) continue;
            if (ValueOf(effect, "Hidden") is bool hidden && hidden) continue;
            string type = effect.GetType().Name;
            string what = type.StartsWith(EffectPrefix, StringComparison.Ordinal) ? type.Substring(EffectPrefix.Length) : type;
            string over = ValueOf(effect, "UIMapperOverride") as string ?? "";
            switch (what)
            {
                case "UnlockConstructible":
                {
                    var names = ReferenceNames(ValueOf(effect, "ConstructibleReferences"));
                    string chosen = Choose(names, index);
                    if (chosen == null && over == "") break;
                    var u = Make(what, over != "" ? over : chosen, KindOf(chosen, index), ConstructibleKeys, "_Small", index, localize);
                    u.Alternatives = Math.Max(0, names.Count - 1);
                    list.Add(u);
                    break;
                }
                case "UnlockSettlementStartingPackage":
                    Add(list, what, over, ReferenceName(ValueOf(effect, "StartingPackageReference")), Kind.Extension, OtherKeys, index, localize);
                    break;
                case "ApplyDescriptor":
                    Add(list, what, over, ReferenceName(ValueOf(effect, "Descriptor")), Kind.Other, OtherKeys, index, localize);
                    break;
                case "AddEmpireBattleAbility":
                    Add(list, what, over, ReferenceName(ValueOf(effect, "BattleAbilityDefinition")), Kind.Other, OtherKeys, index, localize);
                    break;
                case "UnlockResource":
                    foreach (string resource in ReferenceNames(ValueOf(effect, "ResourceReferences")))
                        Add(list, what, over, resource, Kind.Other, PictoKeys, index, localize);
                    break;
                case "ApplyCostModifier":
                    Add(list, what, over, ReferenceName(ValueOf(effect, "CostModifierReference")), Kind.Other, OtherKeys, index, localize);
                    break;
                case "UnlockAction":
                case "ProhibitAction":
                    Add(list, what, over, ValueOf(effect, "ActionType")?.ToString() ?? "", Kind.Other, PictoKeys, index, localize);
                    break;
                case "ResetAllCivics":
                case "AddFame":
                case "UpgradeAllRoads":
                    Add(list, what, over, what, Kind.Other, OtherKeys, index, localize);
                    break;
                // anything else is no entry of the icon row (the game logs "not handled" for it)
            }
        }
        return list;
    }

    static void Add(List<Unlock> list, string effect, string over, string element, Kind kind, string[] keys, Index index, Func<string, string> localize)
    {
        string name = over != "" ? over : element;
        if (string.IsNullOrEmpty(name)) return;
        list.Add(Make(effect, name, kind, keys, null, index, localize));
    }

    static Unlock Make(string effect, string element, Kind kind, string[] keys, string anySuffix, Index index, Func<string, string> localize)
    {
        var u = new Unlock { Effect = effect, ElementName = element ?? "", Kind = kind, Title = element ?? "" };
        if (element != null && index.Mappers.TryGetValue(element, out var mapper))
        {
            u.MapperFound = true;
            string title = ValueOf(mapper, "Title") as string;
            if (!string.IsNullOrEmpty(title)) u.Title = localize != null ? localize(title) : title;
            u.ImagePath = TechTreeData.ImageOf(mapper, keys, anySuffix, out _, out u.ImageGuid);
        }
        return u;
    }

    /// <summary>
    /// The constructible one UnlockConstructible effect stands for on a card. The game takes the highest Level among
    /// those valid for the player's faction, the later one on a tie; here "valid" is ForEveryone, and only when the
    /// effect names no such constructible, an emblematic one. A name the project has no definition for is passed
    /// over; when it has none of them, the first name stands.
    /// </summary>
    internal static string Choose(IList<string> names, Index index)
    {
        string best = null; int bestLevel = int.MinValue; bool bestGeneric = false;
        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name) || !index.Constructibles.TryGetValue(name, out var definition)) continue;
            bool generic = ForEveryone(definition);
            int level = ValueOf(definition, "Level") is int l ? l : 0;
            if (best == null || (generic && !bestGeneric) || (generic == bestGeneric && level >= bestLevel))
            { best = name; bestLevel = level; bestGeneric = generic; }
        }
        if (best != null) return best;
        foreach (string name in names) if (!string.IsNullOrEmpty(name)) return name;
        return null;
    }

    /// <summary>
    /// The constructible is valid for a culture that is named in no list (ConstructibleHelper.CheckPrerequisite on its
    /// FactionPrerequisite): it names no culture, or its Operator is None (2), which makes the names an exclusion.
    /// With Any (0) or AnyInFactionsHistory (1) and names, it belongs to those cultures: an emblematic.
    /// </summary>
    internal static bool ForEveryone(object constructible)
    {
        object prerequisite = ValueOf(constructible, "FactionPrerequisite");
        if (prerequisite == null) return true;
        var names = ValueOf(prerequisite, "serializableFactionNames") as string[];
        if (names == null || names.Length == 0) return true;
        object op = ValueOf(prerequisite, "Operator");
        try { return op != null && Convert.ToInt32(op) == 2; }
        catch (Exception) { return false; }
    }

    /// <summary>The frame an entry gets, by what the game's TechnologyUnlockItem.FindUnlockType tests.</summary>
    internal static Kind KindOf(string constructibleName, Index index)
    {
        if (constructibleName == null || !index.Constructibles.TryGetValue(constructibleName, out var definition)) return Kind.Infrastructure;
        for (var t = definition.GetType(); t != null; t = t.BaseType)
        {
            if (t.Name == "ExtensionDistrictDefinition" || t.Name == "SettlementEvolutionDefinition") return Kind.Extension;
            if (t.Name == "UnitDefinition") return Kind.Unit;
        }
        return Kind.Infrastructure;
    }

    // ── reflection by name, through the type's whole hierarchy ───────────────
    static FieldInfo FieldOf(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, ALL | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        return null;
    }

    static object ValueOf(object o, string field) => o == null ? null : FieldOf(o.GetType(), field)?.GetValue(o);

    // a DatatableElementReference keeps its target as serializableElementName
    static string ReferenceName(object reference) => ValueOf(reference, "serializableElementName") as string ?? "";

    static List<string> ReferenceNames(object references)
    {
        var names = new List<string>();
        if (references is IEnumerable list)
            foreach (object reference in list)
            {
                string name = ReferenceName(reference);
                if (name != "") names.Add(name);
            }
        return names;
    }
}
