using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HumankindAssetFramework
{
    // THE UNIT TABLE (2026-11-19, user request): one row per registry entry for the F8 window's "Units" dialog - what it
    // is, what it costs the engine, its main settings and when it was baked. A cell that could not be read stays EMPTY:
    // a 0 would read as "costs nothing" (Unknown is not a number).
    internal sealed class UnitRow
    {
        public string Name = "", Pawn = "", Status = "", Type = "", Properties = "", Baked = "";
        public int Meshes = -1, Quads = -1, MaxQuads = -1, Verts = -1, Bones = -1, AtlasW = -1, AtlasH = -1, Clips = -1;   // -1 = not read
        public DateTime BakedUtc = DateTime.MinValue;   // MinValue = no date on record (baked before the registry kept one)
        public string MaxQuadMesh = "";
    }

    internal static class UnitTableRules
    {
        [ProcessLived("literal column titles")] internal static readonly string[] Columns = { "Unit", "Status", "Type", "Meshes", "Largest mesh", "Quads", "Verts", "Bones", "Atlas", "Clips", "Properties", "Baked" };

        // What the entry is. A retexture owns no skeleton; an entry with neither a skeleton guid nor a texture was never baked.
        internal static string TypeOf(ModelEntry e)
        {
            bool hasSkeleton = (e.sa | e.sb | e.sc | e.sd) != 0;
            if (!hasSkeleton) return string.IsNullOrEmpty(e.textureFile) ? "not baked" : "retexture";
            if (!e.Roles[0].Authored) return "static";
            return e.animStateDriven ? "state-driven" : "animated";
        }

        // Where it stands this session, the strongest statement first.
        internal static string StatusOf(ModelEntry e, bool skeletonLoaded)
            => e.disabled ? "disabled" : e.repointed ? "in use" : skeletonLoaded ? "loaded" : TypeOf(e) == "retexture" ? "waiting" : "not loaded";

        // The settings that change what the player sees or hears, in a fixed order; plain defaults say nothing.
        internal static string PropertiesOf(ModelEntry e)
        {
            var p = new List<string>();
            if (Math.Abs(e.scale - 1f) > 0.0005f) p.Add("scale " + e.scale.ToString("0.###", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(e.turretBone)) p.Add("turret");
            if (!string.IsNullOrEmpty(e.handPropName)) p.Add("hand prop");
            if (e.deployOnStop) p.Add("deploys");
            if (e.fireOnAttack) p.Add("fires");
            if (e.engineSound || !string.IsNullOrEmpty(e.soundFile)) p.Add("sound");
            if (!string.IsNullOrEmpty(e.textureFile) && TypeOf(e) != "retexture") p.Add("own skin");
            return string.Join(", ", p);
        }

        // The registry's stamp ("2026-11-19T00:02:11Z") to a UTC time; anything else is "no date".
        internal static DateTime ParseBaked(string stamp)
            => !string.IsNullOrWhiteSpace(stamp)
               && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                ? t : DateTime.MinValue;

        internal static string BakedText(DateTime utc, TimeZoneInfo zone)
            => utc == DateTime.MinValue ? "" : TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        // The entry's meshes as (name, quads) and their vertex counts. A count of 0 or less was not readable; a sum
        // with one such mesh in it stays "not read" instead of coming out too low.
        internal static void AddMeshes(UnitRow r, IList<KeyValuePair<string, int>> quads, IList<int> verts)
        {
            r.Meshes = quads.Count;
            if (quads.Count == 0) return;
            if (quads.All(m => m.Value > 0))
            {
                r.Quads = quads.Sum(m => m.Value);
                var top = quads.OrderByDescending(m => m.Value).First();
                r.MaxQuads = top.Value; r.MaxQuadMesh = top.Key;
            }
            if (verts != null && verts.Count == quads.Count && verts.All(v => v > 0)) r.Verts = verts.Sum();
        }

        internal static string Num(int n) => n < 0 ? "" : n.ToString("N0", CultureInfo.InvariantCulture);

        // "16,316 (99%)": the largest mesh against the engine's per-mesh draw ceiling - past it the mesh's tail is not
        // drawn. Rounded DOWN, so 100% is shown only at the ceiling itself.
        internal static string LargestText(UnitRow r, int ceiling)
            => r.MaxQuads < 0 ? "" : Num(r.MaxQuads) + " (" + (r.MaxQuads * 100L / ceiling) + "%)";

        internal static string AtlasText(UnitRow r) => r.AtlasW < 0 ? "" : r.AtlasW + " x " + r.AtlasH;

        // 0 fine, 1 close to the ceiling (90% and up), 2 over it.
        internal static int QuadAlarm(UnitRow r, int ceiling) => r.MaxQuads < 0 ? 0 : r.MaxQuads > ceiling ? 2 : r.MaxQuads * 10L >= ceiling * 9L ? 1 : 0;

        internal static string Cell(UnitRow r, int column, int ceiling)
        {
            switch (column)
            {
                case 0: return r.Name;
                case 1: return r.Status;
                case 2: return r.Type;
                case 3: return Num(r.Meshes);
                case 4: return LargestText(r, ceiling);
                case 5: return Num(r.Quads);
                case 6: return Num(r.Verts);
                case 7: return Num(r.Bones);
                case 8: return AtlasText(r);
                case 9: return Num(r.Clips);
                case 10: return r.Properties;
                default: return r.Baked;
            }
        }

        // Sorted on a column; numbers by value, "not read" last in either direction, ties by name.
        internal static List<UnitRow> Sorted(IEnumerable<UnitRow> rows, int column, bool descending)
        {
            Func<UnitRow, long> num = null; Func<UnitRow, string> text = null;
            switch (column)
            {
                case 1: text = r => r.Status; break;
                case 2: text = r => r.Type; break;
                case 3: num = r => r.Meshes; break;
                case 4: num = r => r.MaxQuads; break;
                case 5: num = r => r.Quads; break;
                case 6: num = r => r.Verts; break;
                case 7: num = r => r.Bones; break;
                case 8: num = r => r.AtlasW < 0 ? -1 : (long)r.AtlasW * r.AtlasH; break;
                case 9: num = r => r.Clips; break;
                case 10: text = r => r.Properties; break;
                case 11: num = r => r.BakedUtc == DateTime.MinValue ? -1 : r.BakedUtc.Ticks; break;
                default: text = r => r.Name; break;
            }
            var list = rows.ToList();
            list.Sort((a, b) =>
            {
                int c;
                if (num != null)
                {
                    long x = num(a), y = num(b);
                    if ((x < 0) != (y < 0)) return x < 0 ? 1 : -1;
                    c = x.CompareTo(y);
                }
                else c = string.Compare(text(a), text(b), StringComparison.OrdinalIgnoreCase);
                if (descending) c = -c;
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }
    }

    internal static partial class UniversalInject
    {
        // One row per registry entry, read from what is loaded right now. Reflection reads: call on open and on Refresh,
        // not per frame.
        internal static List<UnitRow> UnitRows()
        {
            var rows = new List<UnitRow>();
            var snapshot = entries;
            if (snapshot == null) return rows;
            foreach (var e in snapshot)
            {
                var r = new UnitRow { Name = e.resourceName ?? "", Pawn = e.pawnDescription ?? "" };
                try
                {
                    r.Type = UnitTableRules.TypeOf(e);
                    r.Status = UnitTableRules.StatusOf(e, e.skeleton != null);
                    r.Properties = UnitTableRules.PropertiesOf(e);
                    r.BakedUtc = UnitTableRules.ParseBaked(e.bakedUtc);
                    r.Baked = UnitTableRules.BakedText(r.BakedUtc, TimeZoneInfo.Local);
                    if (r.Type != "retexture" && r.Type != "not baked") r.Clips = e.Roles.Count(x => x.Authored);
                    if (e.tex != null) { r.AtlasW = e.tex.width; r.AtlasH = e.tex.height; }
                    if (e.skeleton != null)
                    {
                        if (GetMember(e.skeleton, "BoneInfos") is Array bones) r.Bones = bones.Length;
                        if (GetMember(e.skeleton, "skinnedMeshInfos") is System.Collections.IEnumerable infos)
                        {
                            var quads = new List<KeyValuePair<string, int>>(); var verts = new List<int>();
                            foreach (var si in infos)
                            {
                                var fmc = GetMember(si, "FxMeshContent");
                                quads.Add(new KeyValuePair<string, int>(GetMember(si, "MeshName") as string ?? "?", fmc == null ? 0 : MemberInt(fmc, "QuadCount", 0)));
                                verts.Add(fmc == null ? 0 : MemberInt(fmc, "VertexCount", 0));
                            }
                            UnitTableRules.AddMeshes(r, quads, verts);
                        }
                    }
                }
                catch (Exception ex) { r.Status = "unreadable: " + ex.GetType().Name; }
                rows.Add(r);
            }
            return rows;
        }
    }
}
