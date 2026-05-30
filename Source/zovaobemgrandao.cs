using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using legend.forkedskylights.shapes;
using MapComp_Skylights = Dubs_Skylight.MapComp_Skylights;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace legend.forkedskylights.shapes
{
    internal record Coords(ushort x, ushort z) : IComparable<Coords>
    {
        internal Coords(IntVec3 v) : this((ushort)v.x, (ushort)v.z) { }

        public int CompareTo(Coords? other)
        {
            if (other is null) return 1;
            var cmp = x.CompareTo(other.x);
            return cmp != 0 ? cmp : z.CompareTo(other.z);
        }
    }

    internal record Rect(Coords coords, Size size)
    {
        internal CellRect CellRect => new CellRect(coords.x, coords.z, size.width, size.height);
    }

    internal record Size(byte width, byte height)
    {
        internal static readonly Size ONE = new Size(1, 1);
        internal Size(IntVec2 v) : this((byte)v.x, (byte)v.z) { }
        internal Size? Rotated => width != height ? new Size(height, width) : null;

        internal float ShapeValue
        {
            get
            {
                var w = (double)width;
                var h = (double)height;
                return (float)((w * h) * 100 + (Math.Min(w, h) / Math.Max(w, h)) * 10 + (width == height ? 1 : height > width ? 0.5 : 0));
            }
        }
    }
}

namespace legend.forkedskylights
{
    public class Main : Mod
    {
        public Main(ModContentPack content) : base(content)
        {
            new Harmony(content.PackageIdPlayerFacing).PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    public class CategoryDef_Hidden : DesignationCategoryDef { }

    [StaticConstructorOnStartup]
    public class Designator_AddSkylight : Designator_Cells
    {
        private static readonly ThingDef _skyLightA =
            DefDatabase<ThingDef>.GetNamed("SkyLightA") ?? throw new Exception("'SkyLightA' not found");

        public Designator_AddSkylight()
        {
            var skyLightA = _skyLightA;
            defaultLabel = DefDatabase<DesignationCategoryDef>.GetNamed("skylights", errorOnFail: false)?.label ?? skyLightA.label;
            defaultDesc = skyLightA.description;
            icon = skyLightA.uiIcon;
            useMouseIcon = true;
        }

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Plans;

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            var skyLightA = _skyLightA;
            var thingClass = skyLightA.thingClass;
            var idx = Map.cellIndices.CellToIndex(c);
            return c.InBounds(Map) &&
                   !c.Fogged(Map) &&
                   !(Map.thingGrid.ThingsListAtFast(idx) ?? []).Any(t =>
                       t?.def?.thingClass == thingClass) &&
                   !(Map.blueprintGrid[idx] ?? []).Any(b =>
                       b?.EntityToBuild() is ThingDef d && d.thingClass == thingClass) &&
                   GenConstruct.CanPlaceBlueprintAt(skyLightA.entityDefToBuild ?? skyLightA, c, Rot4.North, Map);
        }

        public override void DesignateSingleCell(IntVec3 cell) => DesignateMultiCell([cell]);

        public override void DesignateMultiCell(IEnumerable<IntVec3> allCells)
        {
            foreach (var (rect, skylight) in Covering(allCells))
            {
                var cells = rect.CellRect;
                var size = cells.Size;

                var correctRot = skylight.size == size;
                var rot = correctRot
                    ? skylight.defaultPlacingRot
                    : skylight.defaultPlacingRot.Rotated(RotationDirection.Clockwise);

                var cell = cells.CenterCell;
                cell.x -= cells.Width % 2 == 0 ? 1 : 0;
                cell.z -= correctRot && (cells.Height % 2 == 0) ? 1 : 0;

                var stuff = GenStuff.DefaultStuffFor(skylight);

                if (DebugSettings.godMode)
                {
                    var thing = ThingMaker.MakeThing(skylight, stuff);
                    thing.SetFactionDirect(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, cell, Map, rot);
                }
                else
                {
                    var bpDef = skylight.blueprintDef
                        ?? DefDatabase<ThingDef>.AllDefs
                               .FirstOrDefault(d => d.entityDefToBuild == skylight);

                    if (bpDef == null)
                    {
                        Log.Error($"[ForkedSkylights] {skylight.defName} has no blueprintDef and none could be found in DefDatabase - skipping");
                        continue;
                    }
                    var blueprint = (Blueprint_Build)Activator.CreateInstance(bpDef.thingClass);
                    blueprint.def = bpDef;
                    blueprint.PostMake();
                    blueprint.SetFactionDirect(Faction.OfPlayer);
                    blueprint.stuffToUse = stuff;
                    GenSpawn.Spawn(blueprint, cell, Map, rot);
                }
            }
        }

        private static List<(Rect, ThingDef)> Covering(IEnumerable<IntVec3> cells)
        {
            var fallback = (Size.ONE, _skyLightA);

            var skylightListBigToSmall = GetSkylightListBigToSmall();
            SortedSet<Coords> inputSet = new(from c in cells select new Coords(c));
            List<(Rect, ThingDef)> outputList = new();
            while (inputSet.FirstOrDefault() is { } cell)
            {
                var (size, skylight) =
                    (from s in skylightListBigToSmall where AllContained(inputSet, cell, s.Item1) select s)
                    .FirstOrFallback(fallback);
                if (skylight is null)
                {
                    break;
                }

                for (ushort x = cell.x, xLimit = (ushort)(x + size.width); x < xLimit; ++x)
                {
                    for (ushort z = cell.z, zLimit = (ushort)(z + size.height); z < zLimit; ++z)
                    {
                        inputSet.Remove(new Coords(x, z));
                    }
                }

                outputList.Add((new Rect(cell, size), skylight));
            }

            return outputList;
        }

        private static bool AllContained(SortedSet<Coords> inputSet, Coords cell, Size size)
        {
            for (ushort x = cell.x, xLimit = (ushort)(x + size.width); x < xLimit; ++x)
            {
                for (ushort z = cell.z, zLimit = (ushort)(z + size.height); z < zLimit; ++z)
                {
                    if (!inputSet.Contains(new Coords(x, z)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static List<(Size, ThingDef)> GetSkylightListBigToSmall()
        {
            var list = _skylightListBigToSmall;
            if (list.Count == 0)
            {
                list.AddRange(from s in GetSkylights() select (s.Key, s.Value));
                list.SortByDescending(s => s.Item1.ShapeValue);
            }

            return list;
        }

        private static readonly List<(Size, ThingDef)> _skylightListBigToSmall = new();

        private static Dictionary<Size, ThingDef> GetSkylights()
        {
            Dictionary<Size, ThingDef> skylights = new();
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def is null || def.thingClass != _skyLightA.thingClass)
                {
                    continue;
                }

                Size size = new(def.size);
                skylights.Add(size, def);

                if (size.Rotated is { } rotated)
                {
                    skylights.Add(rotated, def);
                }
            }

            return skylights;
        }
    }
}

namespace legend.forkedskylights.harmonypatches
{
    [HarmonyPatch(typeof(MainTabWindow_Architect), "CacheDesPanels")]
    internal class MainTabWindow_Architect__CacheDesPanels
    {
        internal static void Postfix(ref MainTabWindow_Architect __instance)
        {
            if (AccessTools.Field(__instance.GetType(), "desPanelsCached")?.GetValue(__instance) is
                List<ArchitectCategoryTab> desPanelsCached)
            {
                desPanelsCached.RemoveAll(t => t.def is CategoryDef_Hidden);
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_SanguophageUtility_InSunlight
    {
        static bool Prepare() =>
            AccessTools.TypeByName("RimWorld.SanguophageUtility") != null;

        static MethodBase TargetMethod() =>
            AccessTools.Method("RimWorld.SanguophageUtility:InSunlight");

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Callvirt
                    && ((MethodInfo)instruction.operand).DeclaringType == typeof(RoofGrid)
                    && ((MethodInfo)instruction.operand).Name == "Roofed")
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Callvirt,
                        AccessTools.Method(typeof(Patch_SanguophageUtility_InSunlight),
                            nameof(RoofedWithoutSkylight)));
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        public static bool RoofedWithoutSkylight(this RoofGrid rg, IntVec3 cell, Map map)
        {
            var skylightComp = MapComp_Skylights.LightComps[map.uniqueID];
            var isSkylight = skylightComp.SkylightGrid[map.cellIndices.CellToIndex(cell)];
            return rg.Roofed(cell) && !isSkylight;
        }
    }
}
