using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The detail-vegetation grids of one map item (a road's two sides, a terrain item's, a bezier patch, a prefab's
    /// corners), on the item's own root object: registered with EtsVegetationLod while the item is live, so its grass
    /// goes when the stream unloads the item (grass owner, 2026-09-25; environment DESIGN.md §C.2). Added by
    /// EtsTerrainBuilder.EmitQuads and EtsPrefabTerrainBuilder.BuildBorder through <see cref="AddSide"/>. A MonoBehaviour
    /// in the Ets namespace, as its folder is. The grids are not serialized: an Instantiate'd copy of an item carries
    /// none (no grass drawn twice).
    ///
    /// A quad grows nothing when (measured, N/grass/g1_*.txt, g2_bands.txt; TruckLib and the game's own field names in
    /// eurotrucks2.exe):
    ///  - its main brush's texture has no detail_vegetation_def;
    ///  - its byte 3 has bit 0x10 (TruckLib TerrainQuad.NoDetVegMask = 16: "NoDetailVegetation");
    ///  - its side has detail vegetation switched off (road head flags bit 28 right / 29 left, terrain item bits 11 / 28:
    ///    59 of 102 road sides and 23 of 35 terrain sides round Dover, the city streets);
    ///  - it lies outside the side's detail band across it. The game's own names for the two u16 are
    ///    "detail_veg_distance_min" and "detail_veg_distance" (prefab corners) and "right_dvdm" / "right_dvd" (terrain
    ///    items): the band IN WHICH grass grows, 5 .. 100 m from the side's inner edge on 1,378 of 1,418 sides measured
    ///    in four 3 km discs; the edits are almost all to the far end (31, 50, 74, 120, 150, 190, 200 m), and 118 of 318
    ///    prefab corners store 5 .. 0, i.e. none. (TruckLib calls the road's pair "NoDetailVegetationFrom/To", but its own
    ///    prefab reading of the same default 5 .. 100 is "the band in which detail vegetation will be placed".)
    /// </summary>
    public sealed class EtsDetailPatch : MonoBehaviour
    {
        private readonly List<EtsDetailVegetation.Patch> _patches = new List<EtsDetailVegetation.Patch>();
        private bool _registered;

        /// <summary>Quads with a def seen so far this visit, and why the rest were left out (for the census).</summary>
        public static int CapableQuads, SkippedQuadBit, SkippedSideFlag, SkippedBand;

        /// <summary>
        /// Main thread, from the terrain builders: the side's grid as the worker reads it, if any quad on it can grow
        /// grass. <paramref name="vertices"/> are in <paramref name="parent"/>'s space unless <paramref name="world"/>;
        /// <paramref name="lateral"/> is the distance of each row line from the side's inner edge (null: no band test);
        /// <paramref name="side"/> may be null (a bezier patch: no side flag, no band). <paramref name="quadAt"/>
        /// maps a grid quad to the stored quad for grids resampled from the stored one (prefab corners); null means the same.
        /// </summary>
        public static void AddSide(EtsMap map, EtsTerrainQuadData quads, Vector3[] vertices, int rows, int cols, int stride, Transform parent, bool world,
            ulong uid, int sideIndex, EtsTerrainSide side, IList<float> lateral, System.Func<int, int, int> quadAt = null)
        {
            if (!EtsVegetationLod.Enabled || quads == null || quads.Rows <= 0 || quads.Cols <= 0 || parent == null || vertices == null || rows <= 0 || cols <= 0)
            {
                return;
            }

            if (!EtsDetailVegetation.Loaded && EtsEngine.Files != null)
            {
                EtsDetailVegetation.Load(EtsEngine.Files);
            }

            // World positions: the side's own vertices are in its parent's space (a prefab corner's are world already).
            Matrix4x4 toWorld = world ? Matrix4x4.identity : parent.localToWorldMatrix;
            EtsDetailVegetation.Patch patch = EtsDetailVegetation.MakePatch(map, quads, vertices, toWorld, rows, cols, stride, uid, sideIndex, side, lateral, quadAt,
                out EtsDetailVegetation.Patch counts);
            CapableQuads += counts.Capable;
            SkippedQuadBit += counts.SkippedQuadBit;
            SkippedSideFlag += counts.SkippedSideFlag;
            SkippedBand += counts.SkippedBand;

            if (patch == null)
            {
                return;
            }

            // Not "??": in the editor GetComponent hands back a fake-null object that only == null sees.
            EtsDetailPatch owner = parent.GetComponent<EtsDetailPatch>();

            if (owner == null)
            {
                owner = parent.gameObject.AddComponent<EtsDetailPatch>();
            }

            owner.Add(patch);
        }

        private void Add(EtsDetailVegetation.Patch patch)
        {
            _patches.Add(patch);

            if (_registered)
            {
                EtsVegetationLod.Register(patch);
            }
        }

        private void OnEnable()
        {
            _registered = true;

            foreach (EtsDetailVegetation.Patch patch in _patches)
            {
                EtsVegetationLod.Register(patch);
            }
        }

        private void OnDisable()
        {
            _registered = false;

            foreach (EtsDetailVegetation.Patch patch in _patches)
            {
                EtsVegetationLod.Unregister(patch);
            }
        }
    }
}
