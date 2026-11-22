// TechTreeUnlocks.cs (HAF editor) — what a technology unlocks, for the Tech Tree viewer's cards and side strip.
//
// The game does not look this up anywhere else: a technology's unlocks ARE the effects stored in its own definition
// (TechnologyDefinition.SimulationEventEffects). Two places of the game turn them into the card's icon row, and this
// file follows both (decompiled; reviewed 2026-11-22 against an independent re-implementation run over the ENC data):
//
// 1. DepartmentOfScience.UpdateTechnologyUnlockInfo makes one entry per effect that is not Hidden:
//      UnlockConstructible              ONE entry for all the constructibles it names: the highest Level among those
//                                       valid for the player's faction, the later one on a tie; NO entry when none is
//      UnlockSettlementStartingPackage  the package (the effect's UIMapperOverride is not used)
//      ApplyDescriptor / AddEmpireBattleAbility   the descriptor / the ability       ("empire bonus")
//      UnlockResource                   one entry per resource
//      ApplyCostModifier                the modifier
//      UnlockAction / ProhibitAction    the action type
//      ResetAllCivics                   "Effect_ResetAllCivics";   AddFame / UpgradeAllRoads: the override only
//    Elsewhere a non-empty UIMapperOverride replaces the entry's name.
// 2. TechnologyItem sorts the entries (TechnologyUnlocksComparer) and TechnologyUnlockItem draws each:
//      order   cost / fame / package / other first, then bonus, resource, action, constructible; two constructibles
//              by their UI mapper's SortPriority; otherwise by name (ordinal). List.Sort, so ties are NOT stable.
//      icon    a constructible: its mapper's "Small" image of the faction's visual variation ("Default_Small", then
//              "Small"); action and resource: "Picto"; everything else: "Small". A missing key is a blank icon.
//      mapper  by the entry's name; an action by "ActionType" + its enum name; fame by its override, else "FameGains".
//              An action without its mapper, and a constructible name that is no constructible definition, get NO icon.
//
// The editor has no player. "Valid for the player's faction" is read for a culture that is in nobody's list
// (ForBaseCulture): the entry such a player sees is the one on the card, and the constructibles other cultures get
// instead are kept with their cultures (Others). An effect that names ONLY other cultures' constructibles gives such
// a player no icon; here it is kept and marked (OnlyFor), after the sorted row. An entry the game would not draw is
// kept too, with the reason (NotShown), for the side strip.
//
//              So do a resource and a cost modifier whose name is no such definition.
//
// Not followed: the game also needs the effect's DLC (kept as Dlc and said in the side strip); it finds a resource's
// mapper through the definition that owns its ResourceType (here by the entry's name: the same mapper unless two
// resource definitions share a type, which none in the ENC project do); a unit's class overlay and the emblematic
// decoration are not drawn.
// Read by name through reflection and SerializedObject: no reference to the game's types.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class TechTreeUnlocks
{
    public enum Kind { Unit, Extension, Infrastructure, Other }   // the game's TechnologyUnlockItem.UnlockType, without its striped variant

    /// <summary>Another constructible of the same UnlockConstructible effect: what some cultures get instead.</summary>
    public sealed class Variant
    {
        public string Name = "", Title = "";
        public string[] Cultures = new string[0];   // empty: for everyone too (a lower level, or the one a tie went against)
    }

    public sealed class Unlock
    {
        public Kind Kind;
        public string Effect = "";        // the effect's type without "SimulationEventEffect_"
        public string ElementName = "";   // the entry's name (the game's UnlockElementName)
        public string MapperName = "";    // the UI mapper it is looked up by: the name; "ActionType" + name for an action
        public string Title = "";         // its title as the mapper gives it; the name when there is no mapper
        public bool MapperFound;
        public string NotShown;           // non-null: the game draws NO icon for this entry, and this is why
        public string[] OnlyFor;          // non-null: only these cultures get it; a player of another culture has no icon here
        public List<Variant> Others = new List<Variant>();
        public string Dlc = "";           // the downloadable content the effect needs; "" for none

        // the icon, read from the mapper when first asked for (a SerializedObject per mapper is too dear for every build)
        internal UnityEngine.Object Mapper;
        internal string[] Keys;
        bool imageRead; string imagePath, imageGuid;
        public string ImagePath { get { ReadImage(); return imagePath; } set { imagePath = value; imageRead = true; } }
        /// <summary>The icon as a Unity GUID text (the key into the game's atlases); null when the mapper names none.</summary>
        public string ImageGuid { get { ReadImage(); return imageGuid; } set { imageGuid = value; imageRead = true; } }
        void ReadImage()
        {
            if (imageRead) return;
            imageRead = true;
            if (Mapper != null && Keys != null) imagePath = TechTreeData.ImageOf(Mapper, Keys, null, out _, out imageGuid);
        }

        // what the game's comparer looks at
        internal int TypeOrder = -1;          // index in { bonus, resource, action, constructible }; -1 for the rest
        internal bool HasSortPriority; internal int SortPriority;
        internal int ConstructibleOrder = 6;  // index in the comparer's constructible types; -1 a type not among them; 6 no definition
    }

    /// <summary>The project's UI mappers and constructible definitions by name, and which names are a resource or a cost modifier definition.</summary>
    public sealed class Index
    {
        public readonly Dictionary<string, UnityEngine.Object> Mappers = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        public readonly Dictionary<string, UnityEngine.Object> Constructibles = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        public readonly HashSet<string> Resources = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> CostModifiers = new HashSet<string>(StringComparer.Ordinal);
    }

    const BindingFlags ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string EffectPrefix = "SimulationEventEffect_";
    /// <summary>Every empire starts as this culture, so "is or ever was one of ..." (AnyInFactionsHistory) with it in the list is everyone.</summary>
    public const string StartingCulture = "Civilization_Era0_DefaultTribe";
    const int Bonus = 0, Resource = 1, Action = 2, Constructible = 3;   // TechnologyUnlocksComparer.UnlockTypeOrder

    // The ONE image a kind of entry asks its mapper for (a constructible: the default visual variation, then the plain key).
    static readonly string[] ConstructibleKeys = { "Default_Small", "Small" };
    static readonly string[] SmallKey = { "Small" };
    static readonly string[] PictoKey = { "Picto" };
    // TechnologyUnlocksComparer.ConstructibleTypesOrder, by the definition types' names (it decides only where a mapper is missing)
    static readonly string[] ConstructibleKinds = { "ExtensionDistrictDefinition", "ExploitationDistrictDefinition", "SettlementImprovementDefinition", "SettlementEvolutionDefinition", "UnitDefinition", "RaiseArmyDefinition" };

    /// <summary>
    /// One pass over the project's ScriptableObject assets. A copy under Databases wins over one under the reference
    /// folder, which wins over one elsewhere, as for the technologies themselves. A "mapper" is any asset with an
    /// Images list (in the ENC project: exactly its UIMapper objects).
    /// </summary>
    internal static Index BuildIndex(string referenceRoot)
    {
        var index = new Index();
        var constructibleType = TechTreeData.FindType("Amplitude.Mercury.Data.Simulation.ConstructibleDefinition");
        var what = new Dictionary<Type, int>();   // per type: 1 a mapper, 2 a resource definition, 3 a cost modifier definition, 0 none of them
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
                if (!what.TryGetValue(type, out int kind))
                    what[type] = kind = IsA(type, "ResourceDefinition") ? 2 : IsA(type, "CostModifierDefinition") ? 3 : FieldOf(type, "Images") != null ? 1 : 0;
                if (kind == 1) Put(index.Mappers, mapperRank, o, rank);
                else if (kind == 2) index.Resources.Add(o.name);
                else if (kind == 3) index.CostModifiers.Add(o.name);
            }
        }
        return index;
    }

    static bool IsA(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType) if (t.Name == name) return true;
        return false;
    }

    static bool IsUnder(string path, string root) =>
        !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(root) && (path == root || path.StartsWith(root + "/", StringComparison.Ordinal));

    static void Put(Dictionary<string, UnityEngine.Object> map, Dictionary<string, int> ranks, UnityEngine.Object o, int rank)
    {
        if (ranks.TryGetValue(o.name, out int had) && had >= rank) return;
        map[o.name] = o; ranks[o.name] = rank;
    }

    /// <summary>
    /// A technology's entries: first the row a player of a culture in nobody's list sees, in the game's order
    /// (entries the game would not draw are in it, with NotShown set, because they take part in the game's sort);
    /// then the entries only other cultures get (OnlyFor), sorted the same way. `localize` turns a mapper's title
    /// (a "%Key" or plain text) into what is shown.
    /// </summary>
    internal static List<Unlock> Read(object technologyDefinition, Index index, Func<string, string> localize)
    {
        var row = new List<Unlock>(); var cultural = new List<Unlock>();
        if (technologyDefinition == null || index == null) return row;
        if (!(ValueOf(technologyDefinition, "SimulationEventEffects") is IEnumerable effects)) return row;
        foreach (object effect in effects)
        {
            if (effect == null) continue;
            if (ValueOf(effect, "Hidden") is bool hidden && hidden) continue;
            string type = effect.GetType().Name;
            string what = type.StartsWith(EffectPrefix, StringComparison.Ordinal) ? type.Substring(EffectPrefix.Length) : type;
            string over = ValueOf(effect, "UIMapperOverride") as string ?? "";   // the game's default, and what the assets store, is null
            int rowBefore = row.Count, culturalBefore = cultural.Count;
            switch (what)
            {
                case "UnlockConstructible":
                    ReadConstructible(effect, over, index, localize, row, cultural);
                    break;
                case "UnlockSettlementStartingPackage":   // the override is not used for this one
                    row.Add(Make(what, ReferenceName(ValueOf(effect, "StartingPackageReference")), null, Kind.Extension, -1, SmallKey, index, localize));
                    break;
                case "ApplyDescriptor":
                    row.Add(Make(what, Or(over, ReferenceName(ValueOf(effect, "Descriptor"))), null, Kind.Other, Bonus, SmallKey, index, localize));
                    break;
                case "AddEmpireBattleAbility":
                    row.Add(Make(what, Or(over, ReferenceName(ValueOf(effect, "BattleAbilityDefinition"))), null, Kind.Other, Bonus, SmallKey, index, localize));
                    break;
                case "UnlockResource":
                    foreach (string resource in ReferenceNames(ValueOf(effect, "ResourceReferences")))
                    {
                        var u = Make(what, Or(over, resource), null, Kind.Other, Resource, PictoKey, index, localize);
                        if (!index.Resources.Contains(u.ElementName)) u.NotShown = "'" + u.ElementName + "' is no resource definition of this project";   // TryBindResource
                        row.Add(u);
                    }
                    break;
                case "ApplyCostModifier":
                {
                    var u = Make(what, Or(over, ReferenceName(ValueOf(effect, "CostModifierReference"))), null, Kind.Other, -1, SmallKey, index, localize);
                    if (!index.CostModifiers.Contains(u.ElementName)) u.NotShown = "'" + u.ElementName + "' is no cost modifier definition of this project";   // TryBindCostModifier
                    row.Add(u);
                    break;
                }
                case "UnlockAction":
                case "ProhibitAction":
                {
                    // TechnologyUnlockItem.TryBindAction: a name that Enum.TryParse takes as an ActionType (a member's
                    // name, or a number) is looked up as that member's own mapper, "ActionType" + the member's name;
                    // any other name as itself. Without a mapper the game draws nothing.
                    object action = ValueOf(effect, "ActionType");
                    string name = Or(over, action?.ToString() ?? "");
                    string member = null;
                    if (action != null && action.GetType().IsEnum && name.Trim() != "")
                        try { member = Enum.Parse(action.GetType(), name).ToString(); } catch (Exception) { }
                    var u = Make(what, name, member != null ? "ActionType" + member : name, Kind.Other, Action, PictoKey, index, localize);
                    if (!u.MapperFound) u.NotShown = "there is no UI mapper '" + u.MapperName + "'";
                    row.Add(u);
                    break;
                }
                case "AddFame":   // TryBindFameGain: the override's mapper, else the shared "FameGains"
                    row.Add(Make(what, over, over != "" && index.Mappers.ContainsKey(over) ? over : "FameGains", Kind.Other, -1, SmallKey, index, localize));
                    break;
                case "ResetAllCivics":
                    row.Add(Make(what, Or(over, "Effect_ResetAllCivics"), null, Kind.Other, -1, SmallKey, index, localize));
                    break;
                case "UpgradeAllRoads":
                    row.Add(Make(what, over, null, Kind.Other, -1, SmallKey, index, localize));
                    break;
                // anything else is no entry of the icon row (the game logs "not handled" for it)
            }
            string dlc = DlcOf(effect);
            if (dlc != "")
            {
                for (int i = rowBefore; i < row.Count; i++) row[i].Dlc = dlc;
                for (int i = culturalBefore; i < cultural.Count; i++) cultural[i].Dlc = dlc;
            }
        }
        // The game's sort, with the game's algorithm: List.Sort on the entries in the effects' order. Not stable, and
        // which of two equal entries comes first depends on that algorithm (three entries: a fixed network).
        row.Sort(Compare);
        cultural.Sort(Compare);
        row.AddRange(cultural);
        return row;
    }

    static string Or(string first, string second) => first != "" ? first : (second ?? "");

    static void ReadConstructible(object effect, string over, Index index, Func<string, string> localize, List<Unlock> row, List<Unlock> cultural)
    {
        var names = ReferenceNames(ValueOf(effect, "ConstructibleReferences"));
        if (names.Count == 0) return;   // the game: no entry
        string chosen = Choose(names, index, out bool forBase);
        if (chosen == null)
        {
            // not one of its names is a constructible of this project: the game makes no entry; here the dangling name is said
            var missing = Make("UnlockConstructible", names[0], null, Kind.Infrastructure, Constructible, ConstructibleKeys, index, localize);
            missing.NotShown = "the project has no constructible definition for " + (names.Count == 1 ? "it" : "any of the " + names.Count + " it names");
            row.Add(missing);
            return;
        }
        // UpdateTechnologyUnlockInfo: the override replaces the name; TryBindConstructible then needs that NAME to be a
        // constructible definition, and takes kind, mapper and image from it
        string element = Or(over, chosen);
        var u = Make("UnlockConstructible", element, null, KindOf(element, index), Constructible, ConstructibleKeys, index, localize);
        if (!index.Constructibles.TryGetValue(element, out var shownDefinition))
            u.NotShown = "'" + element + "' is no constructible definition of this project";
        else
            u.ConstructibleOrder = ConstructibleOrderOf(shownDefinition);
        if (u.Mapper != null && ValueOf(u.Mapper, "SortPriority") is int priority) { u.HasSortPriority = true; u.SortPriority = priority; }
        foreach (string name in names)
        {
            if (name == chosen) continue;
            var other = new Variant { Name = name, Title = TitleOf(name, index, localize) };
            if (index.Constructibles.TryGetValue(name, out var definition) && !ForBaseCulture(definition)) other.Cultures = CulturesOf(definition);
            u.Others.Add(other);
        }
        if (forBase) { row.Add(u); return; }
        u.OnlyFor = index.Constructibles.TryGetValue(chosen, out var chosenDefinition) ? CulturesOf(chosenDefinition) : new string[0];
        cultural.Add(u);
    }

    static Unlock Make(string effect, string element, string mapperName, Kind kind, int typeOrder, string[] keys, Index index, Func<string, string> localize)
    {
        element = element ?? "";
        var u = new Unlock { Effect = effect, ElementName = element, MapperName = mapperName ?? element, Kind = kind, Title = element, TypeOrder = typeOrder, Keys = keys };
        if (u.MapperName != "" && index.Mappers.TryGetValue(u.MapperName, out var mapper))
        {
            u.MapperFound = true; u.Mapper = mapper;
            string title = ValueOf(mapper, "Title") as string;
            if (!string.IsNullOrEmpty(title)) u.Title = localize != null ? localize(title) : title;
        }
        return u;
    }

    static string TitleOf(string name, Index index, Func<string, string> localize)
    {
        if (!index.Mappers.TryGetValue(name, out var mapper)) return name;
        string title = ValueOf(mapper, "Title") as string;
        return string.IsNullOrEmpty(title) ? name : (localize != null ? localize(title) : title);
    }

    /// <summary>TechnologyUnlocksComparer.Compare, on what Read kept of each entry.</summary>
    internal static int Compare(Unlock a, Unlock b)
    {
        if (a.TypeOrder == Constructible && b.TypeOrder == Constructible)
        {
            if (a.HasSortPriority && b.HasSortPriority) return a.SortPriority.CompareTo(b.SortPriority);
            int kinds = a.ConstructibleOrder.CompareTo(b.ConstructibleOrder);
            return kinds != 0 ? kinds : string.CompareOrdinal(a.ElementName, b.ElementName);
        }
        int types = a.TypeOrder.CompareTo(b.TypeOrder);
        return types != 0 ? types : string.CompareOrdinal(a.ElementName, b.ElementName);   // StaticString.CompareTo is ordinal (checked on Amplitude.dll)
    }

    /// <summary>
    /// The constructible one UnlockConstructible effect stands for. The game: the highest Level among those valid for
    /// the player's faction, the later one on a tie. `forBase` = one of them is valid for a culture in nobody's list
    /// (then the answer is the game's for such a player); otherwise the same rule over the other cultures' ones.
    /// Null when the project has a definition for none of the names.
    /// </summary>
    internal static string Choose(IList<string> names, Index index, out bool forBase)
    {
        string best = null; int bestLevel = int.MinValue; bool bestBase = false;
        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name) || !index.Constructibles.TryGetValue(name, out var definition)) continue;
            bool isBase = ForBaseCulture(definition);
            int level = ValueOf(definition, "Level") is int l ? l : 0;
            if (best == null || (isBase && !bestBase) || (isBase == bestBase && level >= bestLevel))
            { best = name; bestLevel = level; bestBase = isBase; }
        }
        forBase = bestBase;
        return best;
    }

    /// <summary>
    /// The constructible is valid for an empire whose culture is named in no list (ConstructibleHelper.CheckPrerequisite
    /// on its FactionPrerequisite). No culture named: yes. Operator None (2): the names are the cultures it is NOT
    /// for: yes. AnyInFactionsHistory (1): for an empire that is or ever was one of them, and every empire starts as
    /// the tribe: yes when the tribe is named. Any (0), or 1 without the tribe: it belongs to those cultures.
    /// </summary>
    internal static bool ForBaseCulture(object constructible)
    {
        object prerequisite = ValueOf(constructible, "FactionPrerequisite");
        if (prerequisite == null) return true;
        var names = ValueOf(prerequisite, "serializableFactionNames") as string[];
        if (names == null || names.Length == 0) return true;
        int op;
        try { op = Convert.ToInt32(ValueOf(prerequisite, "Operator")); }   // the game's field is an enum
        catch (Exception) { return false; }
        if (op == 2) return true;
        return op == 1 && Array.IndexOf(names, StartingCulture) >= 0;
    }

    static string[] CulturesOf(object constructible) =>
        ValueOf(ValueOf(constructible, "FactionPrerequisite"), "serializableFactionNames") as string[] ?? new string[0];

    /// <summary>The frame an entry gets, by what the game's TechnologyUnlockItem.FindUnlockType tests (the type or a base of it).</summary>
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

    static int ConstructibleOrderOf(object definition)
    {
        for (var t = definition.GetType(); t != null; t = t.BaseType)
        {
            int i = Array.IndexOf(ConstructibleKinds, t.Name);
            if (i >= 0) return i;
        }
        return -1;   // Array.IndexOf on the comparer's list, for a definition of another type
    }

    // DownloadableContentPrerequisite.DownloadableContent: 0 = none
    static string DlcOf(object effect)
    {
        object content = ValueOf(ValueOf(effect, "DlcPrerequisite"), "DownloadableContent");
        if (content == null) return "";
        try { return Convert.ToInt32(content) == 0 ? "" : content.ToString(); }
        catch (Exception) { return ""; }
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
