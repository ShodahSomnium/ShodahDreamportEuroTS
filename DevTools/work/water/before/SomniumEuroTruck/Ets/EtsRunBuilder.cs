using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Buildings (item type 2) and curves (type 44): a model repeated nose to tail along the
    /// curve between two nodes. Between them they are most of what lines a road - crash barriers,
    /// fences, kerbs, walls, hedges, powerlines, the painted stripes down a car park - 9,499
    /// buildings and 3,762 curves in the nine sectors round the bay, none of which this world
    /// drew before. The two item types differ only in where the element's length comes from.
    ///
    /// The layout rule, measured on the demo's own sectors rather than assumed:
    ///
    ///   count   = max(1, ROUND(length / (step * stretch)))     for a building
    ///   count   = max(1, CEIL (length / (step * stretch)))     for a curve
    ///   spacing = length / count
    ///
    /// THE COUNT IS NOT ESTABLISHED. It was first checked against the stored height-offset array on
    /// the assumption that the array holds one entry per copy, and that assumption is false: items
    /// identical in model, variation, length to two decimals, stretch and fixed step store
    /// DIFFERENT array lengths on 19 of 51 curve groups and 3 of 15 building groups. No step
    /// explains any large group, and the ceiling under any candidate step is 52 % for buildings and
    /// 33 % for curves - so the earlier "49 % for rounding to nearest against 44 % for rounding up"
    /// was fitting noise, and the rules above are a reasonable default, not a measured fact. No
    /// other record of the count has been found in the files.
    ///
    /// What IS established is the geometry, against a validator that holds: the item header's
    /// five-axis bounding volume is the generated geometry plus a uniform 0.125 m margin, with the
    /// residual sitting at exactly -0.125 / +0.125 over 1,542 curves. The start cap, the centres and
    /// the end cap are laid nose to tail at their OWN lengths and stretched by one common factor, so
    /// a run always spans exactly the distance the map gave it with no gap and no overlap, and a run
    /// that curves has its elements bent along the path (2026-10-04, the fence flashing; see Lay).
    ///
    /// The element's nominal length is the model's "width" when the definition gives one,
    /// otherwise the length along z of the pmd part named "center" - NOT the whole model's z
    /// extent, which is wrong on nearly every model because start, center and end parts sit side
    /// by side in the same file. Where a model offers several centre parts the SHORTEST is the
    /// default and "use_15m_variant" asks for center15m.
    /// </summary>
    public static class EtsRunBuilder
    {
        public static int Buildings, BuildingsSkipped, Curves, CurvesSkipped, Elements;
        public static readonly List<string> Notes = new List<string>();

        /// <summary>Elements in one run, so a mapper's 400 m fence cannot cost a frame.</summary>
        public const int MaxElements = 200;

        // ------------------------------------------------------------------ buildings

        public static GameObject BuildBuilding(EtsMap map, EtsMapBuilding item, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats)
        {
            EtsMapNode start = map.FindNode(item.StartNode);
            EtsMapNode end = map.FindNode(item.EndNode);

            if (start == null || end == null)
            {
                BuildingsSkipped++;
                return null;
            }

            EtsSiiUnit scheme = map.BuildingScheme(item.SchemeToken);
            List<string> models = scheme != null ? scheme.GetArray("models") : null;

            if (models == null || models.Count == 0)
            {
                BuildingsSkipped++;
                Note("building scheme " + EtsToken.Decode(item.SchemeToken) + ": no models");
                return null;
            }

            // Only two of the 1,076 schemes name more than one model, and the two they name differ
            // by a size override alone, so the first is the model.
            EtsSiiUnit definition = map.BuildingModel(models[0].Trim());

            if (definition == null)
            {
                BuildingsSkipped++;
                Note("building model " + models[0] + ": not defined");
                return null;
            }

            string path = definition.GetString("model_desc");

            if (string.IsNullOrEmpty(path))
            {
                // A vegetation row - trees and hedges from a "vegetation_model" list rather than a
                // pmd. 158 of 1,081 building models are of this kind; they belong to the scatterer.
                BuildingsSkipped++;
                return null;
            }

            EtsModelData model = EtsEngine.LoadModel(path, out string failure);

            if (model == null)
            {
                BuildingsSkipped++;
                Note("building " + path + ": " + failure);
                return null;
            }

            float width = definition.GetFloat("width", -1f);
            bool wide = definition.GetFloat("use_15m_variant", 0f) > 0.5f;
            string centre = wide ? PartNamed(model, "center15m") : null;
            centre = centre ?? PartNamed(model, "center") ?? ShortestCentre(model) ?? BasePart(model, item.Seed);
            float step = width > 0.01f ? width : PartLength(model, centre);

            if (step <= 0.01f)
            {
                BuildingsSkipped++;
                Note("building " + path + ": no element length");
                return null;
            }

            var run = new GameObject("building " + models[0].Trim());
            run.transform.SetParent(parent, false);

            Lay(map, model, run.transform, options, start, end, item.Length, item.Stretch, item.Heights, step,
                centre, PartNamed(model, "start"), PartNamed(model, "end"), 1f, false, stats);

            Buildings++;
            return run;
        }

        // ------------------------------------------------------------------ curves

        public static GameObject BuildCurve(EtsMap map, EtsMapCurve item, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats)
        {
            EtsMapNode start = map.FindNode(item.NodeUid);
            EtsMapNode end = map.FindNode(item.ForwardNodeUid);

            if (start == null || end == null)
            {
                CurvesSkipped++;
                return null;
            }

            EtsSiiUnit definition = map.CurveModel(item.ModelToken);
            string path = definition != null ? definition.GetString("model_desc") : null;

            if (string.IsNullOrEmpty(path))
            {
                // 132 of the 1,192 curve models are pure vegetation rows with no pmd at all.
                CurvesSkipped++;
                return null;
            }

            EtsModelData model = EtsEngine.LoadModel(path, out string failure);

            if (model == null)
            {
                CurvesSkipped++;
                Note("curve " + path + ": " + failure);
                return null;
            }

            // Power and telephone lines are not laid nose to tail: they are poles at the nodes and every fixed step
            // between, with wires the game strings from pole to pole. See BuildPowerLine. A failure there costs the
            // item, never the stream.
            if (HasWireLocators(model))
            {
                try
                {
                    return BuildPowerLine(item, definition, model, start, end, options, parent, stats);
                }
                catch (Exception e)
                {
                    CurvesSkipped++;
                    EtsSemaphores.Warn("building a power line (" + path + ")", e);
                    return null;
                }
            }

            // The variation names the parts to repeat: an entry reads
            //   "4m | css1,css2 : 1.0"
            // - the variation's name, then one or more alternatives of comma-separated part names
            // with a weight. The element is ONE PART of the chosen alternative, laid nose to tail;
            // taking the whole alternative as the element over-counts by the number of parts in it.
            string variation = EtsToken.Decode(item.VariationToken);
            string[] parts = Variation(definition, variation, out int variationIndex) ?? new[] { "center1" };
            string element = null;
            float step = 0f;

            string flat = null;

            foreach (string name in parts)
            {
                string found = PartNamed(model, name);

                if (found == null)
                {
                    continue;
                }

                float length = PartLength(model, found);

                if (length > 0.01f)
                {
                    element = found;
                    step = length;
                    break;
                }

                flat = flat ?? found;
            }

            if (element == null)
            {
                element = ShortestCentre(model);
                step = PartLength(model, element);
            }

            // A part with no extent along z at all is a CROSS SECTION, not a segment, and it is not
            // a reason to give up on the item. power_wire_straight_crv.pmd's "center" is four
            // vertices all at z = 0, 2.5 cm by 49 cm - the profile of a wire - and its definition
            // carries fixed_step 50.0, which is the step. Every one of the 26 power-wire items near
            // the bay is between 3.1 and 22.4 m long, so the count is one: a single span between the
            // two nodes, which is what a wire is.
            if (element == null && flat != null)
            {
                element = flat;
                step = 0f;
            }

            // The item's own fixed step wins over the definition's, and either wins over the part.
            if (item.FixedStep > 0.01f)
            {
                step = item.FixedStep;
            }
            else if (definition.GetFloat("fixed_step", 0f) > 0.01f)
            {
                step = definition.GetFloat("fixed_step", 0f);
            }

            if (element == null || step <= 0.01f)
            {
                CurvesSkipped++;
                Note("curve " + path + ": no element for variation " + variation);
                return null;
            }

            // The caps: the item names one alternative from the definition's start_part and
            // end_part arrays at the variation's own index.
            string first = Cap(definition, "start_part", variationIndex, EtsToken.Decode(item.FirstPart), model);
            string last = Cap(definition, "end_part", variationIndex, EtsToken.Decode(item.LastPart), model);

            var run = new GameObject("curve " + item.Name);
            run.transform.SetParent(parent, false);

            float scale = item.Scale > 0.01f && item.Scale < 50f ? item.Scale : 1f;
            Lay(map, model, run.transform, options, start, end, item.Length, item.Stretch, item.Heights, step, element, first, last, scale, true, stats);

            Curves++;
            return run;
        }

        // ------------------------------------------------------------------ power and telephone lines

        public static int PowerLines, Poles, Wires;

        public static string PowerReport()
        {
            return PowerLines + " power lines (" + Poles + " poles, " + Wires + " wires)";
        }

        /// <summary>Whether a curve model carries wire attachment locators (w_l_n_*, w_h_n_*): a pole line.</summary>
        private static bool HasWireLocators(EtsModelData model)
        {
            foreach (EtsModelLocator locator in model.Locators)
            {
                if (WireKey(locator.Name) != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// "w_l_1_sb" -> "l1", "w_h_4_3_v1" -> "h4": which wire a locator holds - its kind (l low, h high voltage)
        /// and number, the same on every pole of the line. Null for any other locator.
        /// </summary>
        private static string WireKey(string name)
        {
            if (name == null || name.Length < 5 || name[0] != 'w' || name[1] != '_')
            {
                return null;
            }

            string[] fields = name.Split('_');

            if (fields.Length < 3 || fields[1].Length != 1 || !int.TryParse(fields[2], out int number))
            {
                return null;
            }

            return fields[1] + number;
        }

        /// <summary>A locator on a model's alternative copy ("_v1"): used only when the plain one is missing.</summary>
        private static bool IsAlternative(string name)
        {
            int at = name.LastIndexOf("_v", StringComparison.Ordinal);
            return at > 0 && at + 2 < name.Length && char.IsDigit(name[at + 2]);
        }

        /// <summary>
        /// A pole line (a curve model whose parts carry wire locators): measured on the demo's own files (probe Det3).
        /// Round Cambridge 25 curve items are /model/power_line/wooden_powerline_crv.pmd - the wooden telephone poles
        /// - and each part of that model is ONE POLE (vca, vsb, veb: 0.36 m along z, 11.3 m tall, a cross-arm 4.3 m
        /// wide) with three locators w_l_1..3 on the arm, and NO wire geometry at all; round Szczecin the one curve
        /// item is a 336 m high-voltage line, 3_x_wire_01_balt_crv, whose parts are pylons 32 m tall with eight
        /// w_h_0..7 locators. The game strings the wires between matching locators of consecutive poles; the old
        /// nose-to-tail rule laid one pole in the middle of each span and no wire.
        ///
        /// Placement: a pole at each node (the item's start and end parts - vsb, veb - or the definition's first
        /// start_part / end_part alternative when the item names none) and centre-part poles every fixed step between
        /// (the item's fixed step, else the definition's fixed_step: 80 on the telephone lines, 200 on the pylons),
        /// the span divided evenly: segments = ceil(length / step). Each pole is the part put on the path with its own
        /// z offset cancelled, exactly as the runs are (Lay), scaled by the item's scale (0.85 on the telephone poles).
        /// Wires: a catenary (parabola) between the matching locators of each pair of neighbouring poles, sag 2 % of
        /// the span on low-voltage and 3 % on high-voltage lines, 13 and 20 mm thick, one mesh per line, no collider
        /// and no shadow. The poles keep their colliders: a truck can hit them.
        /// </summary>
        private static GameObject BuildPowerLine(EtsMapCurve item, EtsSiiUnit definition, EtsModelData model, EtsMapNode start, EtsMapNode end,
            EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats)
        {
            string variation = EtsToken.Decode(item.VariationToken);
            string[] names = Variation(definition, variation, out int variationIndex);
            string centre = null;

            if (names != null)
            {
                foreach (string name in names)
                {
                    centre = PartNamed(model, name);
                    if (centre != null) break;
                }
            }

            centre = centre ?? ShortestCentre(model) ?? PartWithWires(model);
            string first = Cap(definition, "start_part", variationIndex, EtsToken.Decode(item.FirstPart), model) ?? DefaultCap(definition, "start_part", variationIndex, model) ?? centre;
            string last = Cap(definition, "end_part", variationIndex, EtsToken.Decode(item.LastPart), model) ?? DefaultCap(definition, "end_part", variationIndex, model) ?? centre;

            if (centre == null && first == null && last == null)
            {
                CurvesSkipped++;
                Note("power line " + item.Name + ": no pole part");
                return null;
            }

            centre = centre ?? first ?? last;
            float step = item.FixedStep > 0.01f ? item.FixedStep : definition.GetFloat("fixed_step", 0f) > 0.01f ? definition.GetFloat("fixed_step", 0f) : 30f;
            float scale = item.Scale > 0.01f && item.Scale < 50f ? item.Scale : 1f;

            Vector3 p0 = EtsMapBuilder.ToWorld(start.Position, options.CenterGame);
            Vector3 p1 = EtsMapBuilder.ToWorld(end.Position, options.CenterGame);
            float length = item.Length > 0.01f ? item.Length : Vector3.Distance(p0, p1);
            int segments = Mathf.Clamp(Mathf.CeilToInt(length / step - 0.001f), 1, MaxElements);
            Vector3 t0 = EtsMapBuilder.ToWorldDirection(start.Forward()) * length;
            Vector3 t1 = EtsMapBuilder.ToWorldDirection(end.Forward()) * length;

            const int Samples = 64;
            var points = new Vector3[Samples + 1];
            var arc = new float[Samples + 1];

            for (int k = 0; k <= Samples; k++)
            {
                points[k] = EtsMapBuilder.Hermite(p0, t0, p1, t1, (float)k / Samples);
                arc[k] = k == 0 ? 0f : arc[k - 1] + Vector3.Distance(points[k], points[k - 1]);
            }

            var run = new GameObject("curve " + item.Name + " (poles)");
            run.transform.SetParent(parent, false);
            var prototypes = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var attachments = new List<Dictionary<string, Vector3>>();
            float total = arc[Samples];

            for (int k = 0; k <= segments; k++)
            {
                string part = k == 0 ? first : k == segments ? last : centre;
                AtArc(points, arc, total * k / segments, p1 - p0, out Vector3 point, out Vector3 forward);

                // Upright poles: the path's heading, not its slope.
                Vector3 flat = new Vector3(forward.x, 0f, forward.z);
                forward = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
                Quaternion rotation = Quaternion.LookRotation(-forward, Vector3.up);
                PartZRange(model, part, out float zMin, out float zMax);

                // The end poles stand INSIDE the line: the first with its leading edge on the start node, the last with
                // its trailing edge on the end node (a pylon's 5.8 m footprint then ends at the node rather than
                // straddling it); the ones between are centred on their place. Against the items' own stored boxes
                // (probe Det10, 26 pole lines round the two starts) this takes the residual from median 0.25 / max
                // 2.70 m (all centred) to 0.23 / 0.34 m.
                float anchor = k == 0 ? zMin : k == segments ? zMax : 0.5f * (zMin + zMax);
                Vector3 origin = point - forward * (anchor * scale);

                GameObject prototype = Prototype(model, part, options, prototypes);

                if (prototype != null && PartHasPieces(model, part))
                {
                    GameObject copy = UnityEngine.Object.Instantiate(prototype, run.transform);
                    copy.name = "pole " + part;
                    copy.transform.position = origin;
                    copy.transform.rotation = rotation;
                    copy.transform.localScale = new Vector3(scale, scale, scale);
                    copy.SetActive(true);
                    Poles++;
                }

                attachments.Add(WirePoints(model, part, origin, rotation, scale));
            }

            foreach (KeyValuePair<string, GameObject> prototype in prototypes)
            {
                UnityEngine.Object.Destroy(prototype.Value);
            }

            BuildWires(run.transform, attachments);
            PowerLines++;
            Curves++;
            stats.Models += segments + 1;
            return run;
        }

        /// <summary>Where a pole's wires hang, in world space: its part's wire locators through the pole's own transform.</summary>
        private static Dictionary<string, Vector3> WirePoints(EtsModelData model, string partName, Vector3 origin, Quaternion rotation, float scale)
        {
            var points = new Dictionary<string, Vector3>(StringComparer.Ordinal);

            foreach (EtsModelPart part in model.Parts)
            {
                if (!string.Equals(part.Name, partName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (int pass = 0; pass < 2; pass++)
                {
                    for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < model.Locators.Length; l++)
                    {
                        EtsModelLocator locator = model.Locators[l];
                        string key = WireKey(locator.Name);

                        if (key == null || points.ContainsKey(key) || IsAlternative(locator.Name) != (pass == 1))
                        {
                            continue;
                        }

                        points[key] = origin + rotation * (EtsModelBuilder.ToUnity(locator.Position) * scale);
                    }
                }
            }

            return points;
        }

        private static bool PartHasPieces(EtsModelData model, string name)
        {
            foreach (EtsModelPart part in model.Parts)
            {
                if (string.Equals(part.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return part.PieceCount > 0;
                }
            }

            return false;
        }

        /// <summary>The first part that carries wire locators (a model whose variation names nothing usable).</summary>
        private static string PartWithWires(EtsModelData model)
        {
            foreach (EtsModelPart part in model.Parts)
            {
                for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < model.Locators.Length; l++)
                {
                    if (WireKey(model.Locators[l].Name) != null)
                    {
                        return part.Name;
                    }
                }
            }

            return null;
        }

        /// <summary>The first alternative the definition lists for a cap at this variation ("vsa | vsb" -> vsa), when it is a part.</summary>
        private static string DefaultCap(EtsSiiUnit definition, string key, int index, EtsModelData model)
        {
            List<string> entries = definition.GetArray(key);

            if (entries == null || entries.Count == 0)
            {
                return null;
            }

            string entry = entries[Mathf.Clamp(index, 0, entries.Count - 1)];

            foreach (string alternative in entry.Split('|'))
            {
                string found = PartNamed(model, alternative.Trim());

                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static Material _wireMaterial;

        /// <summary>The wires of a line as one mesh: a three-sided tube per wire, sagging as a parabola, no collider.</summary>
        private static void BuildWires(Transform run, List<Dictionary<string, Vector3>> attachments)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<int>();
            int wires = 0;

            for (int k = 0; k + 1 < attachments.Count; k++)
            {
                foreach (KeyValuePair<string, Vector3> from in attachments[k])
                {
                    if (!attachments[k + 1].TryGetValue(from.Key, out Vector3 to))
                    {
                        continue;
                    }

                    bool high = from.Key[0] == 'h';
                    AddWire(from.Value, to, high ? 0.03f : 0.02f, high ? 0.02f : 0.013f, vertices, normals, indices);
                    wires++;
                }
            }

            if (wires == 0)
            {
                return;
            }

            var holder = new GameObject("wires");
            holder.transform.SetParent(run, false);

            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] = holder.transform.InverseTransformPoint(vertices[i]);
                normals[i] = holder.transform.InverseTransformDirection(normals[i]);
            }

            var mesh = new Mesh { name = "wires" };

            if (vertices.Count > 65000)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            holder.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = holder.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = WireMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Wires += wires;
        }

        private static void AddWire(Vector3 a, Vector3 b, float sagPerMetre, float radius, List<Vector3> vertices, List<Vector3> normals, List<int> indices)
        {
            Vector3 chord = b - a;
            float span = new Vector2(chord.x, chord.z).magnitude;

            if (span < 0.05f)
            {
                return;
            }

            float sag = span * sagPerMetre;
            int segments = Mathf.Clamp(Mathf.CeilToInt(span / 4f), 4, 32);
            int first = vertices.Count;

            for (int i = 0; i <= segments; i++)
            {
                float s = (float)i / segments;
                Vector3 p = a + chord * s;
                p.y -= 4f * sag * s * (1f - s);
                Vector3 tangent = chord + new Vector3(0f, -4f * sag * (1f - 2f * s), 0f);
                tangent = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : Vector3.forward;
                Vector3 side = Vector3.Cross(tangent, Vector3.up);
                side = side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.right;
                Vector3 up = Vector3.Cross(side, tangent);

                for (int j = 0; j < 3; j++)
                {
                    float angle = j * Mathf.PI * 2f / 3f;
                    Vector3 offset = side * Mathf.Cos(angle) + up * Mathf.Sin(angle);
                    vertices.Add(p + offset * radius);
                    normals.Add(offset);
                }
            }

            // Ring i at 3i: faces (j, j+1) of rings i and i+1, wound so their front faces look outward.
            for (int i = 0; i < segments; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int a0 = first + i * 3 + j, a1 = first + i * 3 + (j + 1) % 3;
                    int b0 = a0 + 3, b1 = a1 + 3;
                    indices.Add(a0); indices.Add(b0); indices.Add(a1);
                    indices.Add(a1); indices.Add(b0); indices.Add(b1);
                }
            }
        }

        private static Material WireMaterial()
        {
            if (_wireMaterial != null)
            {
                return _wireMaterial;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");

            if (shader == null)
            {
                return null;
            }

            _wireMaterial = new Material(shader) { name = "power wire" };
            Color colour = new Color(0.08f, 0.08f, 0.085f);
            if (_wireMaterial.HasProperty("_BaseColor")) _wireMaterial.SetColor("_BaseColor", colour);
            if (_wireMaterial.HasProperty("_Color")) _wireMaterial.SetColor("_Color", colour);
            if (_wireMaterial.HasProperty("_Smoothness")) _wireMaterial.SetFloat("_Smoothness", 0.35f);
            if (_wireMaterial.HasProperty("_Metallic")) _wireMaterial.SetFloat("_Metallic", 0.3f);
            return _wireMaterial;
        }

        // ------------------------------------------------------------------ the run

        /// <summary>
        /// Lays a run along the Hermite between the two nodes: the start cap, the centre part repeated, the end cap,
        /// nose to tail, each at its OWN length and all stretched alike so the run spans exactly the item's length.
        /// One prototype is built per part and the rest are instances of it, so a straight 60 m fence costs one model
        /// load, not sixty; a run whose path bends gets its elements bent with it (BendCopy).
        ///
        /// The fence flashing (2026-10-04, probes DevTools/work/fences/probe P2): two things made neighbouring
        /// elements overlap in the same plane, and overlapping coplanar faces z-fight.
        ///
        /// 1. The caps took a centre slot. A cap is usually longer than the centre part - it carries the end post:
        ///    fence_metal_03_tr_blke_crv start 4.67 / centre 4.47 / end 4.59 m, fence_concrete_01_ibe start1 4.26 /
        ///    centre 4.11, barrier_antinoise_01_ibe end1 5.39 / centre 5.22, admin_18_de start1 8.23 / centre 4.96,
        ///    bank_metal_stone_lod start 13.59 / centre 13.08 - and a cap centred in a centre-sized slot overhung it
        ///    by half the difference at both ends: into its neighbour (the flashing) and past the node.
        /// 2. Every element was laid STRAIGHT, turned to the path's heading at its middle. On a curve two
        ///    neighbours then cross in a thin wedge on the inside of the bend, face on face.
        ///
        /// The game's own record of the geometry settles both: an item's five-axis bounding volume is its generated
        /// geometry plus 0.125 m (README, "The runs were laid wrong"). Against the stored volumes of 2,075 runs round
        /// Dover the worst-axis residual (median / p90) was 0.150 / 1.707 m for buildings and 0.098 / 1.320 m for
        /// curves as laid before; caps at their own length and elements bent along the path take it to
        /// 0.040 / 0.315 and 0.052 / 0.243 m, and on curves turning 30 degrees or more bending alone takes
        /// buildings from 0.326 to 0.097 m. Coplanar overlapping faces between elements of one run, 400 m round
        /// each site: Dover 11,566 pairs (837 m2) to 0, Prague 1,895 (446 m2) to 9 (0.02 m2), Szczecin 2,056
        /// (193 m2) to 90 (0.5 m2).
        ///
        /// The count: centres = (length / stretch - caps) / step, rounded to nearest for a building and up for a
        /// curve, as before (the rule itself is still not established; see the note at the top of the file).
        /// </summary>
        private static void Lay(EtsMap map, EtsModelData model, Transform run, EtsMapBuilder.Options options,
            EtsMapNode start, EtsMapNode end, float length, float stretch, float[] heights,
            float step, string centre, string first, string last, float scale, bool roundUp, EtsMapBuilder.Stats stats)
        {
            float span = stretch > 0.01f && stretch < 50f ? stretch : 1f;
            float capStart = first != null ? PartLength(model, first) : 0f;
            float capEnd = last != null ? PartLength(model, last) : 0f;
            float raw = (length / span - (capStart + capEnd) * scale) / (step * scale);
            int count = Mathf.Max(0, roundUp ? Mathf.CeilToInt(raw - 0.001f) : Mathf.RoundToInt(raw));

            if (count == 0 && capStart + capEnd <= 0.01f)
            {
                count = 1;
            }

            if (count > MaxElements)
            {
                count = MaxElements;
            }

            // Path metres per nominal metre: every part of the run is stretched by the same factor.
            float nominal = (capStart + capEnd + count * step) * scale;
            float k = nominal > 0.01f ? length / nominal : 1f;
            int slots = count + (first != null ? 1 : 0) + (last != null ? 1 : 0);

            Vector3 p0 = EtsMapBuilder.ToWorld(start.Position, options.CenterGame);
            Vector3 p1 = EtsMapBuilder.ToWorld(end.Position, options.CenterGame);
            Vector3 t0 = EtsMapBuilder.ToWorldDirection(start.Forward()) * length;
            Vector3 t1 = EtsMapBuilder.ToWorldDirection(end.Forward()) * length;

            // Equal steps of the Hermite parameter are not equal steps ALONG the path: measured over
            // the curves near the bay the two differ by a median of 0.10 m, p95 0.46 m and up to
            // 3.85 m on a 321 m tree wall. The stored Length is genuinely the arc length - the
            // game's own routine is an adaptive subdivision of this same Hermite - so the path is
            // sampled once here and then walked by DISTANCE.
            const int Samples = 64;
            var points = new Vector3[Samples + 1];
            var arc = new float[Samples + 1];
            float turning = 0f;

            for (int k2 = 0; k2 <= Samples; k2++)
            {
                points[k2] = EtsMapBuilder.Hermite(p0, t0, p1, t1, (float)k2 / Samples);
                arc[k2] = k2 == 0 ? 0f : arc[k2 - 1] + Vector3.Distance(points[k2], points[k2 - 1]);

                if (k2 >= 2)
                {
                    // atan2 of |a x b| and a . b, not Vector3.Angle: its float acos reads every turn under ~0.02 degrees
                    // as 0, and a gentle 64-step curve then summed to nothing (a 41 m deep quay wall left straight).
                    Vector3 a = points[k2 - 1] - points[k2 - 2], b = points[k2] - points[k2 - 1];
                    turning += Mathf.Atan2(Vector3.Cross(a, b).magnitude, Vector3.Dot(a, b)) * Mathf.Rad2Deg;
                }
            }

            // A straight run keeps its shared, instanced elements; one that turns (in plan or in slope) by more than a
            // twentieth of a degree end to end has every element bent along it, as the game lays it (round Dover 456 of the
            // 766 runs within 400 m bend; with 0.2 degrees, 418, and 101 coplanar joint overlaps stayed).
            bool bend = turning > 0.05f;
            var prototypes = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            float cursor = 0f;

            for (int i = 0; i < slots; i++)
            {
                string part = centre;
                float own = step;

                if (i == 0 && first != null)
                {
                    part = first;
                    own = capStart;
                }
                else if (i == slots - 1 && last != null)
                {
                    part = last;
                    own = capEnd;
                }

                float extent = own * scale * k;
                float middle = cursor + 0.5f * extent;
                cursor += extent;
                GameObject prototype = Prototype(model, part, options, prototypes);

                if (prototype == null)
                {
                    continue;
                }

                // An element is NOT modelled about the origin. A pmd lays its start, centre and end
                // parts side by side down one long strip, so the part that gets repeated can sit
                // anywhere: fence_metal_conc_white_01_fr_crv's "center1" is a 2 m panel modelled at
                // z -54.2..-52.2, and a curve's "s3" cap at z -36.5. The game throws that offset away
                // and puts the part ON the path, its middle on its slot's middle (harness Program1003:
                // median residual against the stored boxes 5.38 m before, 0.17 m after).
                //
                // BuildMesh writes the game z NEGATED into the mesh, and the copy is turned
                // LookRotation(-forward) and scaled (scale, scale, scale * k), so a model point at game z
                // lands at path offset  origin + z * scale * k.
                PartZRange(model, part, out float zMin, out float zMax);
                float zMid = 0.5f * (zMin + zMax);
                float lift = heights != null && i < heights.Length ? heights[i] : 0f;
                AtArc(points, arc, middle, p1 - p0, out Vector3 point, out Vector3 forward);

                GameObject copy = UnityEngine.Object.Instantiate(prototype, run);
                copy.name = part ?? "element";

                if (bend)
                {
                    copy.transform.position = point + new Vector3(0f, lift, 0f);
                    copy.transform.rotation = Quaternion.identity;
                    copy.transform.localScale = Vector3.one;
                    BendCopy(copy, points, arc, p1 - p0, middle, zMid, scale * k, scale, lift);
                    BentElements++;
                }
                else
                {
                    point -= forward * (zMid * scale * k);
                    point.y += lift;
                    copy.transform.position = point;

                    // A run is modelled along the GAME's +z - "start" sits at a lower z than its "end"
                    // on 2,614 of the 2,908 paired parts across the curve models - and ToUnity negates
                    // z, so in Unity the run's forward is the copy's local MINUS z.
                    copy.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
                    copy.transform.localScale = new Vector3(scale, scale, scale * k);
                }

                copy.SetActive(true);
                Elements++;
            }

            foreach (KeyValuePair<string, GameObject> prototype in prototypes)
            {
                UnityEngine.Object.Destroy(prototype.Value);
            }

            if (bend)
            {
                BentRuns++;
            }

            if ((first != null && capStart > 0.01f) || (last != null && capEnd > 0.01f))
            {
                CappedRuns++;
            }

            if (!_layLogged && bend && slots > 1)
            {
                _layLogged = true;
                Debug.Log("[SomniumEuroTruck] Runs: caps at their own length, curved runs bent along their path (first: " + model.Path +
                          ", " + slots + " elements, turning " + turning.ToString("0.0") + " deg).");
            }

            stats.Models += slots;
        }

        public static int BentRuns, BentElements, CappedRuns;
        private static bool _layLogged;

        /// <summary>
        /// Bends one placed copy along the path, vertex by vertex, as the game lays a run: a model point at game z rides
        /// the path at distance  middle + (z - zMid) * zScale  and keeps its x and y in the path's frame there, so two
        /// neighbours meet in one shared cross-section instead of crossing. The copy stands at its slot's middle with no
        /// rotation or scale; each of its meshes (shared with every other copy of the part) is replaced by a bent
        /// copy of its own, and a collider built on the same mesh follows it.
        /// </summary>
        private static void BendCopy(GameObject copy, Vector3[] points, float[] arc, Vector3 chord, float middle, float zMid, float zScale, float scale, float lift)
        {
            var bent = new Dictionary<Mesh, Mesh>();
            MeshFilter[] filters = copy.GetComponentsInChildren<MeshFilter>(true);

            foreach (MeshFilter filter in filters)
            {
                Mesh source = filter.sharedMesh;

                if (source == null)
                {
                    continue;
                }

                if (!bent.TryGetValue(source, out Mesh mesh))
                {
                    mesh = BentMesh(source, filter.transform.worldToLocalMatrix, points, arc, chord, middle, zMid, zScale, scale, lift);
                    bent[source] = mesh;
                }

                filter.sharedMesh = mesh;
            }

            foreach (MeshCollider collider in copy.GetComponentsInChildren<MeshCollider>(true))
            {
                Mesh source = collider.sharedMesh;

                if (source == null)
                {
                    continue;
                }

                if (!bent.TryGetValue(source, out Mesh mesh))
                {
                    mesh = BentMesh(source, collider.transform.worldToLocalMatrix, points, arc, chord, middle, zMid, zScale, scale, lift);
                    bent[source] = mesh;
                }

                collider.sharedMesh = mesh;
            }
        }

        private static Mesh BentMesh(Mesh source, Matrix4x4 worldToLocal, Vector3[] points, float[] arc, Vector3 chord, float middle, float zMid, float zScale, float scale, float lift)
        {
            Mesh mesh = UnityEngine.Object.Instantiate(source);
            mesh.name = source.name + " bent";
            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;
            Vector4[] tangents = source.tangents;
            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            Bounds before = vertices.Length > 0 ? new Bounds(vertices[0], Vector3.zero) : new Bounds();
            Bounds after = new Bounds();
            float total = arc[arc.Length - 1];
            float inverseScale = scale > 1e-4f ? 1f / scale : 1f;
            float inverseZ = zScale > 1e-4f ? 1f / zScale : 1f;

            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 local = vertices[v];
                before.Encapsulate(local);

                // The mesh holds the game's z negated (ToUnity).
                float distance = middle + (-local.z - zMid) * zScale;
                AtArc(points, arc, distance, chord, out Vector3 point, out Vector3 forward);

                if (distance < 0f)
                {
                    point += forward * distance;
                }
                else if (distance > total)
                {
                    point += forward * (distance - total);
                }

                // The frame of LookRotation(-forward, up), as the straight copies are turned.
                Vector3 axisZ = -forward;
                Vector3 axisX = Vector3.Cross(Vector3.up, axisZ);
                axisX = axisX.sqrMagnitude > 1e-8f ? axisX.normalized : Vector3.right;
                Vector3 axisY = Vector3.Cross(axisZ, axisX);

                Vector3 world = point + axisX * (local.x * scale) + axisY * (local.y * scale);
                world.y += lift;
                vertices[v] = worldToLocal.MultiplyPoint3x4(world);

                if (v == 0)
                {
                    after = new Bounds(vertices[v], Vector3.zero);
                }
                else
                {
                    after.Encapsulate(vertices[v]);
                }

                if (hasNormals)
                {
                    Vector3 n = normals[v];
                    Vector3 bentNormal = axisX * (n.x * inverseScale) + axisY * (n.y * inverseScale) + axisZ * (n.z * inverseZ);
                    normals[v] = worldToLocal.MultiplyVector(bentNormal).normalized;
                }

                if (hasTangents)
                {
                    Vector4 t = tangents[v];
                    Vector3 bentTangent = worldToLocal.MultiplyVector(axisX * (t.x * scale) + axisY * (t.y * scale) + axisZ * (t.z * zScale)).normalized;
                    tangents[v] = new Vector4(bentTangent.x, bentTangent.y, bentTangent.z, t.w);
                }
            }

            mesh.vertices = vertices;

            if (hasNormals)
            {
                mesh.normals = normals;
            }

            if (hasTangents)
            {
                mesh.tangents = tangents;
            }

            // A leaves mesh carries drawing bounds grown past its vertices by its cards; keep that growth.
            Vector3 growth = Vector3.Max(Vector3.zero, source.bounds.size - before.size);
            after.Expand(growth * Mathf.Max(scale, zScale));
            mesh.bounds = after;
            return mesh;
        }

        private static GameObject Prototype(EtsModelData model, string part, EtsMapBuilder.Options options, Dictionary<string, GameObject> cache)
        {
            if (part == null)
            {
                return null;
            }

            if (cache.TryGetValue(part, out GameObject cached))
            {
                return cached;
            }

            EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
            {
                OnlyParts = new[] { part },
                CreateLocators = false,
                AddColliders = options.Colliders,
                IncludeCollision = false,
                GameGlass = true
            }, null, EtsEngine.Materials);

            GameObject prototype = built != null ? built.Root : null;

            if (prototype != null)
            {
                prototype.SetActive(false);

                if (options.Colliders)
                {
                    EtsLayers.SetRecursively(prototype.transform, EtsLayers.Environment);
                }
            }

            cache[part] = prototype;
            return prototype;
        }

        // ------------------------------------------------------------------ definitions

        /// <summary>
        /// The parts of the chosen variation's first alternative. Entries read
        /// "&lt;name&gt; | part,part : weight | part,part : weight" - the alternatives are weighted
        /// and a seeded pick chooses between them, but only 0.3 % of curves have more than one and
        /// the first is the one the editor shows.
        /// </summary>
        private static string[] Variation(EtsSiiUnit definition, string name, out int index)
        {
            index = 0;
            List<string> entries = definition.GetArray("variation");

            if (entries == null)
            {
                return null;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                string[] fields = entries[i].Split('|');

                if (fields.Length == 0 || !string.Equals(fields[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                index = i;

                if (fields.Length < 2)
                {
                    return null;
                }

                string head = fields[1];
                int weight = head.IndexOf(':');

                if (weight >= 0)
                {
                    head = head.Substring(0, weight);
                }

                string[] names = head.Split(',');

                for (int p = 0; p < names.Length; p++)
                {
                    names[p] = names[p].Trim();
                }

                return names;
            }

            return null;
        }

        /// <summary>One cap of a curve: the item names it, the definition lists the alternatives.</summary>
        private static string Cap(EtsSiiUnit definition, string key, int index, string wanted, EtsModelData model)
        {
            if (string.IsNullOrEmpty(wanted) || wanted == "0")
            {
                return null;
            }

            List<string> entries = definition.GetArray(key);

            if (entries != null && index < entries.Count)
            {
                foreach (string alternative in entries[index].Split('|'))
                {
                    if (string.Equals(alternative.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return PartNamed(model, wanted);
                    }
                }
            }

            return PartNamed(model, wanted);
        }

        private static string PartNamed(EtsModelData model, string name)
        {
            if (name == null)
            {
                return null;
            }

            foreach (EtsModelPart part in model.Parts)
            {
                if (string.Equals(part.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return part.Name;
                }
            }

            return null;
        }

        /// <summary>
        /// The shortest part whose name starts with "center" and is not a collision twin. Models
        /// that offer both center5m and center15m default to the shorter: taking the longer put
        /// the copy count at three times the stored height-offset count at p90, where taking the
        /// shorter brought it to 1.2 and raised the agreement from 67 % to 88 %.
        /// </summary>
        private static string ShortestCentre(EtsModelData model)
        {
            string best = null;
            float shortest = float.MaxValue;

            foreach (EtsModelPart part in model.Parts)
            {
                string name = part.Name ?? string.Empty;

                if (!name.StartsWith("center", StringComparison.OrdinalIgnoreCase) || name.IndexOf("col", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                float length = PartLength(model, name);

                if (length > 0.01f && length < shortest)
                {
                    shortest = length;
                    best = part.Name;
                }
            }

            return best;
        }

        /// <summary>
        /// The point and the forward direction at a distance ALONG the sampled path, found by
        /// walking the arc table. A distance past either end is clamped onto the run, so a cap
        /// whose own offset would throw it off the end still lands on it.
        /// </summary>
        private static void AtArc(Vector3[] points, float[] arc, float distance, Vector3 chord, out Vector3 point, out Vector3 forward)
        {
            int last = points.Length - 1;
            float total = arc[last];
            float want = Mathf.Clamp(distance, 0f, total);
            int k = 0;

            while (k < last - 1 && arc[k + 1] < want)
            {
                k++;
            }

            float span = arc[k + 1] - arc[k];
            float t = span > 1e-5f ? (want - arc[k]) / span : 0f;
            point = Vector3.Lerp(points[k], points[k + 1], t);
            forward = points[k + 1] - points[k];

            if (forward.sqrMagnitude < 1e-8f)
            {
                forward = chord;
            }

            forward = forward.sqrMagnitude < 1e-8f ? Vector3.forward : forward.normalized;
        }

        /// <summary>
        /// Both ends of a part along z, in the game's frame, over the pieces it owns (0, 0 when the
        /// part has none). See the note in Lay: the game discards a part's own offset and lays it on
        /// the path, so the builder cancels it by putting the part's middle on its slot's middle.
        /// </summary>
        private static void PartZRange(EtsModelData model, string name, out float low, out float high)
        {
            low = float.MaxValue;
            high = float.MinValue;

            if (name != null)
            {
                foreach (EtsModelPart part in model.Parts)
                {
                    if (!string.Equals(part.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                    {
                        low = Mathf.Min(low, model.Pieces[p].BoundsMin[2]);
                        high = Mathf.Max(high, model.Pieces[p].BoundsMax[2]);
                    }
                }
            }

            if (high < low)
            {
                low = high = 0f;
            }
        }

        /// <summary>
        /// A wall, bank, river or weed line has no part called "center": its repeatable segments
        /// are named base0, base1, ... and one of them is laid per copy. concrete_wall_04_sc.pmd is
        /// base0..base3, each exactly 7.00 m along z and 8 m tall; barrier_pebblestone_01.pmd is
        /// 6.75, 6.65, 6.15 and 6.58 m. Checked against the item's own bounding volume, which is the
        /// generated geometry plus a uniform 0.125 m margin: a 20.2 m concrete wall predicts
        /// y 37.15..45.15 from base0 where the file stores 37.02..45.27, and a 26 m pebblestone
        /// barrier predicts 64.967..66.701 against a stored 64.973..66.703. 117 items in the nine
        /// sectors were being dropped for having no "center". Which alternative a given copy takes
        /// is not established, so the item's own seed picks one for the whole run; on 21 of the 30
        /// base models every alternative is the same length anyway.
        /// </summary>
        private static string BasePart(EtsModelData model, uint seed)
        {
            var bases = new List<string>();

            for (int i = 0; i < 16; i++)
            {
                string name = PartNamed(model, "base" + i);

                if (name == null)
                {
                    break;
                }

                bases.Add(name);
            }

            return bases.Count == 0 ? null : bases[(int)(seed % (uint)bases.Count)];
        }

        /// <summary>A part's length along z, over the bounds of the pieces it owns.</summary>
        private static float PartLength(EtsModelData model, string name)
        {
            if (name == null)
            {
                return 0f;
            }

            float low = float.MaxValue, high = float.MinValue;

            foreach (EtsModelPart part in model.Parts)
            {
                if (!string.Equals(part.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    // Both ends. This used to read BoundsMax twice, so 'low' never moved off
                    // float.MaxValue, every length came out 0, and 1,003 of the start's 1,006
                    // building runs - hedges, walls, fences, pavements - were skipped with
                    // "no element length" (client log 2026-09-21).
                    high = Mathf.Max(high, model.Pieces[p].BoundsMax[2]);
                    low = Mathf.Min(low, model.Pieces[p].BoundsMin[2]);
                }
            }

            return high > low ? high - low : 0f;
        }

        private static void Note(string message)
        {
            if (Notes.Count < 20)
            {
                Notes.Add(message);
            }
        }

        public static string Report()
        {
            // The see-through buildings (2026-09-23, harness ProgramBld1/3/4): the game draws no building effect
            // two-sided (its nocull render states belong to billboards, leaves, shadow-only and fake-shadow effects
            // only), run elements are never mirrored (LookRotation of a positive scale), and a mirrored map model is
            // drawn right by Unity's own negative-scale culling - so what remained was the window effect, the glass
            // and the alpha test (EtsMaterialCache.BuildingsReport, WithGameGlass, CutoutThreshold).
            return "buildings " + Buildings + " (" + BuildingsSkipped + " skipped), curves " + Curves +
                   " (" + CurvesSkipped + " skipped), " + Elements + " elements (" + CappedRuns + " runs with caps at their own length, " +
                   BentRuns + " curved runs bent, " + BentElements + " elements). Buildings: " + EtsMapBuilder.MirroredModels +
                   " mirrored copies (Unity flips their culling, static batches included), 0 two-sided materials in the game, " +
                   EtsMaterialCache.BuildingsReport();
        }

        public static void Reset()
        {
            Buildings = BuildingsSkipped = Curves = CurvesSkipped = Elements = BentRuns = BentElements = CappedRuns = 0;
            Notes.Clear();
        }
    }
}
