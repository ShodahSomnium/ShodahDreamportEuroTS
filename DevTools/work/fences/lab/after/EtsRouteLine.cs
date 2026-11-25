using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The route advisor's line: a translucent ribbon drawn above the road along the route the
    /// graph found, following each road's own curve. Also answers how far is left along it from
    /// wherever the truck is.
    /// </summary>
    public class EtsRouteLine : MonoBehaviour
    {
        public Color Color = new Color(0.25f, 0.9f, 0.55f, 0.7f);
        public float Width = 1.6f;
        public float Height = 0.6f;

        private LineRenderer _line;
        private readonly List<Vector3> _points = new List<Vector3>();
        private readonly List<float> _cumulative = new List<float>();
        private int _nearest;

        public bool Visible => _line != null && _line.enabled && _points.Count > 1;
        public float TotalLength => _cumulative.Count > 0 ? _cumulative[_cumulative.Count - 1] : 0f;

        /// <summary>The route's polyline in world space, and the distance along it at each point.</summary>
        public IReadOnlyList<Vector3> Points => _points;

        public IReadOnlyList<float> Cumulative => _cumulative;

        public void Show(EtsMap map, List<EtsRoadGraph.Step> path, Vector3 centerGame)
        {
            _points.Clear();
            _cumulative.Clear();
            _nearest = 0;

            if (path == null)
            {
                Hide();
                return;
            }

            EtsMapNode previous = null;

            for (int k = 0; k < path.Count; k++)
            {
                EtsRoadGraph.Step step = path[k];
                EtsMapNode node = map.FindNode(step.NodeUid);
                if (node == null) continue;
                Vector3 here = EtsMapBuilder.ToWorld(node.Position, centerGame);

                if (previous != null && step.Road != null)
                {
                    // Along the road's own curve: forwards from its start node or backwards from its end,
                    // on the lane a truck keeps to rather than the centre line - many city roads have a
                    // concrete median there (job 1 of 2026-09-22 drove into one following the line).
                    float length = Mathf.Max(1f, step.Road.Length);
                    float sign = step.RoadReversed ? -1f : 1f;
                    float lateral = KeepRight(map, step.Road, step.RoadReversed);
                    Vector3 p0 = EtsMapBuilder.ToWorld(previous.Position, centerGame);
                    Vector3 t0 = EtsMapBuilder.ToWorldDirection(previous.Forward()) * length * sign;
                    Vector3 t1 = EtsMapBuilder.ToWorldDirection(node.Forward()) * length * sign;
                    int samples = Mathf.Max(2, Mathf.CeilToInt(length / 8f));

                    for (int i = 1; i <= samples; i++)
                    {
                        float u = (float)i / samples;
                        Vector3 point = EtsMapBuilder.Hermite(p0, t0, here, t1, u);

                        if (lateral != 0f)
                        {
                            point += RightOf(EtsLaneGraph.HermiteTangent(p0, t0, here, t1, u)) * lateral;
                        }

                        Add(point);
                    }
                }
                else
                {
                    // The start, or the far side of a junction: the node itself, moved onto the lane the
                    // next road leaves it on, so the line crosses the junction lane to lane.
                    Vector3 point = here;
                    EtsRoadGraph.Step next = k + 1 < path.Count ? path[k + 1] : null;

                    if (next != null && next.Road != null)
                    {
                        float lateral = KeepRight(map, next.Road, next.RoadReversed);
                        Vector3 leaving = EtsMapBuilder.ToWorldDirection(node.Forward()) * (next.RoadReversed ? -1f : 1f);
                        point += RightOf(leaving) * lateral;
                    }

                    // Through the corner rather than across it: both ends sit on right-hand lanes, so
                    // one straight chord cuts a right turn's inside corner (job 1's first junction,
                    // prefab 102: through a solid hedge and 0.6 m past an office - review MAPGEO-3).
                    if (previous != null && _points.Count >= 2 && Apex(map, node, next, here, point, centerGame, out Vector3 apex))
                    {
                        Add(apex);
                    }

                    Add(point);
                }

                previous = node;
            }

            if (_points.Count < 2)
            {
                Hide();
                return;
            }

            if (_line == null)
            {
                _line = gameObject.AddComponent<LineRenderer>();
                _line.useWorldSpace = true;
                _line.alignment = LineAlignment.View;
                _line.textureMode = LineTextureMode.Stretch;
                _line.numCornerVertices = 2;
                _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _line.receiveShadows = false;
                _line.material = MakeMaterial(out Color vertexColor);
                _line.startColor = vertexColor;
                _line.endColor = vertexColor;
            }

            _line.widthMultiplier = Width;
            _line.positionCount = _points.Count;
            _line.SetPositions(_points.ToArray());
            _line.enabled = true;
        }

        /// <summary>
        /// Metres right of the direction of travel of the outermost lane a road has that way - the
        /// lane rule the traffic measured (EtsLaneGraph.AddSide): two-way lane i at road_offset +
        /// 4.5 (i + 0.5), one-way lane i at road_offset + 4.5 (i + 0.5 - ceil(n / 2)), plus the
        /// look's lane_offsets, mirrored on left-hand roads. 0 (the centre line) for a road with no
        /// lanes that way, e.g. the "narrow way no lanes" tracks.
        /// </summary>
        private static float KeepRight(EtsMap map, EtsMapRoad road, bool reversed)
        {
            EtsSiiUnit look = map.RoadLook(road.LookToken);

            if (look == null)
            {
                return 0f;
            }

            List<string> right = look.GetArray("lanes_right"), left = look.GetArray("lanes_left");
            List<string> lanes = reversed ? left : right;
            int n = lanes.Count;

            if (n == 0)
            {
                return 0f;
            }

            int i = n - 1;
            float extra = 0f;
            List<string> offsets = look.GetArray(reversed ? "lane_offsets_left" : "lane_offsets_right");

            if (i < offsets.Count)
            {
                float[] xy = EtsSiiValue.ParseFloats(offsets[i]);
                if (xy.Length > 0) extra = xy[0];
            }

            float roadOffset = look.GetFloat("road_offset", 0f);
            bool oneWay = right.Count == 0 || left.Count == 0;
            float offset = oneWay
                ? roadOffset + EtsLaneGraph.LaneWidth * (i + 0.5f - Mathf.Ceil(n / 2f)) + extra
                : roadOffset + EtsLaneGraph.LaneWidth * (i + 0.5f) + extra;

            return (road.Flags & EtsLaneGraph.LeftHandFlag) != 0 ? -offset : offset;
        }

        /// <summary>
        /// Shortest leg either side of an apex, metres. EtsRouteAdvisor.NextTurn steps over any
        /// segment under 1 m, so a shorter leg would hide the turn the apex carries; the chord
        /// already bends at its end then, as before.
        /// </summary>
        private const float MinApexLeg = 1f;

        /// <summary>
        /// The corner of a junction crossing that ends at <paramref name="point"/>: where the line
        /// the route came in on (the last segment, run on) meets the line it leaves on (back from
        /// the point along the node's forward, turned toward the next node). A real turn then bends
        /// once, at the apex, by its full angle - 90 degrees at mod_ger_63, so NextTurn still
        /// announces it - instead of twice by part of it at the chord's ends. None for a near
        /// straight crossing or a U-turn (the lines are within 11.5 degrees of parallel), for a
        /// bend over 120 degrees (see below), or when they meet behind either end or more than 1.5
        /// chords away. Measured offline with this code on both logged jobs (probes 1579, 1771,
        /// 1816): obstacles the truck box sweeps along the line fall from 5 to 1 on job 1 (the
        /// prefab-102 hedges, office and railing go) and stay at 1 on job 2 (a road-side container,
        /// not a junction); bends over 35 degrees fall from 9 to 6 and 8 to 6. Over 1,840 depot
        /// routes (probe 1816), NextTurn asked before each of the 14,344 junctions that turn the
        /// route more than 45 degrees names 13,838 (13,403 before), misses 33 (456) and names the
        /// wrong side 288 (300); junctions that turn under 20 degrees but showed a bend over 35 - a
        /// false "Turn" - fall from 2,108 to 922.
        /// </summary>
        private bool Apex(EtsMap map, EtsMapNode node, EtsRoadGraph.Step next, Vector3 here, Vector3 point, Vector3 centerGame, out Vector3 apex)
        {
            apex = Vector3.zero;
            Vector3 a0 = _points[_points.Count - 1];
            a0.y -= Height;   // stored points already carry Height; Add puts it back on the apex
            Vector3 din = _points[_points.Count - 1] - _points[_points.Count - 2];
            din.y = 0f;
            Vector3 dout = EtsMapBuilder.ToWorldDirection(node.Forward());
            dout.y = 0f;
            EtsMapNode following = next != null ? map.FindNode(next.NodeUid) : null;
            Vector3 toward = following != null ? EtsMapBuilder.ToWorld(following.Position, centerGame) - here : point - a0;

            if (Vector3.Dot(dout, toward) < 0f)
            {
                dout = -dout;
            }

            Vector3 w = point - a0;
            w.y = 0f;
            float chord = w.magnitude;

            if (chord <= 2f || din.sqrMagnitude <= 1e-6f || dout.sqrMagnitude <= 1e-6f)
            {
                return false;
            }

            din.Normalize();
            dout.Normalize();

            // Not past 120 degrees: NextTurn reads a bend over 120 as "Turn around", and one apex
            // bending that far mostly stands for a junction that turns less (probe 1816, 1,840
            // routes: 196 of the 249 "Turn around" prompts it made were at junctions turning 45-120
            // degrees, e.g. ch_4202o, a 95-degree left drawn as a 125-degree bend; bends over 120
            // went from 24 to 397). The chord keeps its two partial bends there, as before (24).
            if (Vector3.Dot(din, dout) < -0.5f)
            {
                return false;
            }

            float den = din.x * dout.z - din.z * dout.x;

            if (Mathf.Abs(den) <= 0.2f)
            {
                return false;
            }

            float s = (w.x * dout.z - w.z * dout.x) / den;   // on along the incoming line from a0
            float t = (din.x * w.z - din.z * w.x) / den;     // back along the outgoing line from point

            if (s < MinApexLeg || t < MinApexLeg || s >= 1.5f * chord || t >= 1.5f * chord)
            {
                return false;
            }

            apex = a0 + din * s;
            apex.y = (a0.y + point.y) * 0.5f;
            return true;
        }

        private static Vector3 RightOf(Vector3 direction)
        {
            var right = new Vector3(direction.z, 0f, -direction.x);
            float length = right.magnitude;
            return length > 1e-5f ? right / length : Vector3.zero;
        }

        private void Add(Vector3 point)
        {
            point.y += Height;

            if (_points.Count > 0)
            {
                _cumulative.Add(_cumulative[_cumulative.Count - 1] + Vector3.Distance(_points[_points.Count - 1], point));
            }
            else
            {
                _cumulative.Add(0f);
            }

            _points.Add(point);
        }

        /// <summary>
        /// The world's own decal shader draws the ribbon, so the line ships with the world. That
        /// shader reads vertex colour as the game does, doubled, so the colour is handed over halved.
        /// </summary>
        private Material MakeMaterial(out Color vertexColor)
        {
            Shader shader = EtsMaterialCache.DecalShader;
            vertexColor = Color;

            if (shader != null)
            {
                vertexColor = new Color(Color.r * 0.5f, Color.g * 0.5f, Color.b * 0.5f, Color.a * 0.5f);
            }
            else
            {
                shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            }

            var material = new Material(shader) { name = "route line" };
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", Texture2D.whiteTexture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            return material;
        }

        public void Hide()
        {
            if (_line != null)
            {
                _line.enabled = false;
            }

            _points.Clear();
            _cumulative.Clear();
        }

        /// <summary>Metres of route left from a position, measured to the nearest point of the ribbon.</summary>
        public float RemainingDistance(Vector3 position)
        {
            if (_points.Count < 2)
            {
                return 0f;
            }

            int best = _nearest;
            float bestDistance = float.MaxValue;
            int from = Mathf.Max(0, _nearest - 40), to = Mathf.Min(_points.Count - 1, _nearest + 80);

            for (int i = from; i <= to; i++)
            {
                float d = (position - _points[i]).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }

            if (bestDistance > 300f * 300f)
            {
                for (int i = 0; i < _points.Count; i++)
                {
                    float d = (position - _points[i]).sqrMagnitude;
                    if (d < bestDistance) { bestDistance = d; best = i; }
                }
            }

            _nearest = best;
            return TotalLength - _cumulative[best] + Mathf.Sqrt(bestDistance);
        }
    }
}
