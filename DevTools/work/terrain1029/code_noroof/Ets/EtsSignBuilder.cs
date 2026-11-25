using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Signs: the map's SIGN items (type 36) and the signs a prefab descriptor carries. In the demo these are most of
    /// the street furniture - street lamps, traffic signs, yard lamps, logos, bins, the city-limit boards - and none
    /// of it was drawn before. Measured on the 1.61.1.0 demo (probes Det1, Det5, Det6):
    ///
    /// - Round the Szczecin start (1.2 km) 167 sign items: 30 no-parking signs, 57 sellplan street lamps, 12 give-way,
    ///   11 main-road, speed limits, bends, a roundabout sign, the Polish city limits, navigation boards, logos. Round
    ///   Cambridge 206: 68 street lamps, 25 Polish-style lamps, 10 T-lamps, traffic barriers, speed cameras, bins.
    ///   None is templated (no text to lay out) and none asks to follow the road's direction.
    /// - Prefab descriptors near the two starts add 190 and 172 more, almost all yard and street lamps.
    /// - The model is the "sign.&lt;token&gt;" unit of /def/world/sign*.sii (8,551 units): model_desc, look_name (the
    ///   look when the item names none, e.g. "pl"), dynamic (a sign the game knocks over when hit),
    ///   drop_sun_shadows.
    /// - A sign item stands on its node with the node's whole rotation, exactly like a model item: the model's
    ///   geometry put through that transform matches the item's own stored box to a median of 0.003 m (Szczecin)
    ///   and 0.004 m (Cambridge), 287 of 373 within 5 cm; the conjugate rotation is off by metres on the lamps
    ///   whose arm sticks out to one side.
    /// - A descriptor sign is in the prefab's own frame, rotation stored w first (the same convention the
    ///   descriptor's spawn points and semaphores have, both checked against the map), and shows with the prefab
    ///   model part it names ("vis").
    ///
    /// Cost: signs share their meshes and materials with every other copy (EtsModelBuilder's caches) and are left out
    /// of static batching; each carries a one-level LODGroup, so a sign too small on screen is not drawn at all.
    /// Colliders: a lamp post or a board on legs stops a truck (the game's "dynamic: false"); a small sign the game
    /// knocks flat ("dynamic: true") and anything under half a metre tall gets none.
    /// </summary>
    public static class EtsSignBuilder
    {
        public static int Signs, SignsSkipped, PrefabSigns, PrefabSignsSkipped, SignColliders;

        public static string Report()
        {
            return Signs + " signs" + (SignsSkipped > 0 ? " (" + SignsSkipped + " skipped)" : "") + ", " + PrefabSigns + " prefab signs" +
                   (PrefabSignsSkipped > 0 ? " (" + PrefabSignsSkipped + " skipped)" : "");
        }

        /// <summary>
        /// The one line the map viewer logs: "Details: 167 signs, 190 prefab signs, 12 traffic lights (4 profiles, 36
        /// lamps), 2 power lines (5 poles, 12 wires); queue 0".
        /// </summary>
        public static string DetailsLine()
        {
            return "[SomniumEuroTruck] Details: " + Report() + ", " + EtsSignFaces.Report() + ", " + EtsSemaphores.Report() + ", " + EtsRunBuilder.PowerReport() +
                   "; queue " + EtsDetailDriver.Pending + (EtsDetailDriver.JobFailures > 0 ? ", " + EtsDetailDriver.JobFailures + " failed builds" : "") + ".";
        }

        // ---------------------------------------------------------------- map sign items

        /// <summary>A sign item on its node. Null when it cannot be built (the viewer then never asks again).</summary>
        public static GameObject BuildSign(EtsMap map, EtsMapSign item, EtsMapBuilder.Options options, Transform parent)
        {
            try
            {
                EtsMapNode node = map.FindNode(item.NodeUid);

                if (node == null)
                {
                    SignsSkipped++;
                    return null;
                }

                GameObject root = BuildModel(map, item.ModelToken, item.LookToken, item.VariantToken, options, parent, item);

                if (root == null)
                {
                    SignsSkipped++;
                    return null;
                }

                root.transform.position = EtsMapBuilder.ToWorld(node.Position, options.CenterGame);
                root.transform.rotation = EtsModelBuilder.ToUnityRotation(node.Rotation);
                Signs++;
                return root;
            }
            catch (Exception e)
            {
                SignsSkipped++;
                EtsSemaphores.Warn("building a sign", e);
                return null;
            }
        }

        // ---------------------------------------------------------------- descriptor signs

        /// <summary>
        /// The signs a prefab's descriptor lists, queued to be built a few a frame (EtsDetailDriver) under a "detail
        /// signs" child of the prefab. <paramref name="variants"/> and <paramref name="parts"/> are what the prefab model
        /// was built with, so a sign shows exactly when the part it belongs to does.
        /// </summary>
        public static void BuildPrefabSigns(EtsMap map, EtsMapPrefab prefab, EtsPrefabDescriptor ppd, EtsModelData prefabModel,
            string[] variants, string[] parts, Transform prefabRoot, EtsMapBuilder.Options options)
        {
            if (ppd == null || ppd.Signs.Length == 0 || prefabRoot == null)
            {
                return;
            }

            Transform container = null;

            foreach (EtsPrefabDescriptor.Sign sign in ppd.Signs)
            {
                if (!PartShown(prefabModel, sign.Part, variants, parts))
                {
                    continue;
                }

                if (container == null)
                {
                    container = new GameObject("detail signs").transform;
                    container.SetParent(prefabRoot, false);
                }

                Transform parent = container;
                EtsPrefabDescriptor.Sign s = sign;

                EtsDetailDriver.Enqueue(() =>
                {
                    if (parent == null)
                    {
                        return;   // the prefab was unloaded before its turn came
                    }

                    try
                    {
                        GameObject root = BuildModel(map, s.Model, 0, 0, options, parent, null);

                        if (root == null)
                        {
                            PrefabSignsSkipped++;
                            return;
                        }

                        root.transform.localPosition = EtsModelBuilder.ToUnity(s.Position);
                        root.transform.localRotation = EtsModelBuilder.ToUnityRotation(s.Rotation);
                        PrefabSigns++;
                    }
                    catch (Exception e)
                    {
                        PrefabSignsSkipped++;
                        EtsSemaphores.Warn("building a prefab sign", e);
                    }
                });
            }
        }

        /// <summary>Whether the prefab model shows the part a descriptor sign belongs to (the builder's own visibility rule).</summary>
        private static bool PartShown(EtsModelData model, ulong partToken, string[] variants, string[] parts)
        {
            if (partToken == 0 || model == null)
            {
                return true;
            }

            string name = EtsToken.Decode(partToken);

            if (parts != null && Array.IndexOf(parts, name) >= 0)
            {
                return true;
            }

            var indexes = new List<int>();

            if (variants != null)
            {
                foreach (string v in variants)
                {
                    int index = v != null ? model.VariantIndex(v.Trim()) : -1;
                    if (index >= 0) indexes.Add(index);
                }
            }

            if (indexes.Count == 0 && model.Variants.Length > 0)
            {
                indexes.Add(0);
            }

            foreach (EtsModelPart part in model.Parts)
            {
                if (!string.Equals(part.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (indexes.Count == 0)
                {
                    return true;
                }

                foreach (int index in indexes)
                {
                    if (part.IsVisible(index)) return true;
                }

                return false;
            }

            // The model has no such part: nothing hides the sign.
            return true;
        }

        // ---------------------------------------------------------------- the model

        private static readonly Dictionary<string, float> Heights = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>
        /// A sign unit's model, unplaced, under <paramref name="parent"/>: named "detail sign ..." (the map viewer's static
        /// batching leaves "detail" objects alone), culled when small on screen, colliders as the class notes say.
        /// </summary>
        private static GameObject BuildModel(EtsMap map, ulong modelToken, ulong lookToken, ulong variantToken, EtsMapBuilder.Options options, Transform parent, EtsMapSign item)
        {
            EtsSiiUnit unit = map.SignModel(modelToken);

            if (unit == null)
            {
                EtsSemaphores.Note("sign unit sign." + EtsToken.Decode(modelToken) + " not defined");
                return null;
            }

            string path = unit.GetString("model_desc", null);

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            EtsModelData model = EtsEngine.LoadModel(path, out string failure);

            if (model == null)
            {
                EtsSemaphores.Note("sign model " + path + ": " + failure);
                return null;
            }

            float height = Height(path, model);
            bool dynamic = unit.GetBool("dynamic", false);
            bool solid = options != null && options.Colliders && !dynamic && height >= 0.5f;
            // Shadows from the tall ones only (lamp posts, gantries): 228 of the 257 signs round Szczecin would cast one at
            // 2.5 m, a shadow pass the headset does not need for a no-parking plate.
            bool shadows = unit.GetInt("drop_sun_shadows", 1) != 0 && height >= 4f;
            string look = lookToken != 0 ? EtsToken.Decode(lookToken) : unit.GetString("look_name", "default");
            string variant = variantToken != 0 ? EtsToken.Decode(variantToken) : "default";

            EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
            {
                Look = string.IsNullOrEmpty(look) ? "default" : look.Trim(),
                Variants = new[] { variant, "default" },
                CreateLocators = false,
                AddColliders = solid,
                IncludeCollision = false,
                CastShadows = shadows
            }, parent, EtsEngine.Materials);

            GameObject root = built.Root;
            root.name = "detail sign " + unit.GetString("sign_name", EtsToken.Decode(modelToken));

            // What the board says: legacy overlays now, template faces from their worker (EtsSignFaces, 2026-10-14).
            if (item != null) EtsSignFaces.Decorate(root, model, path, item);

            if (solid)
            {
                EtsLayers.SetRecursively(root.transform, EtsLayers.Environment);
                SignColliders++;
            }

            // Lamps and tall boards stay in view longer than a small plate.
            EtsSemaphores.SmallDetailCulling(root, height >= 5f ? 0.004f : 0.005f);
            return root;
        }

        /// <summary>The model's drawn height (bounds over every piece), once per path.</summary>
        private static float Height(string path, EtsModelData model)
        {
            if (Heights.TryGetValue(path, out float cached))
            {
                return cached;
            }

            float low = float.MaxValue, high = float.MinValue;

            foreach (EtsModelPiece piece in model.Pieces)
            {
                low = Mathf.Min(low, piece.BoundsMin[1]);
                high = Mathf.Max(high, piece.BoundsMax[1]);
            }

            float height = high > low ? high - low : 0f;
            Heights[path] = height;
            return height;
        }
    }
}
