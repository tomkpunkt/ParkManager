using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaComposer
    {
        private static void AddDesignIntent(Context context)
        {
            AddAxes(context);
            AddFocalPoints(context);
            AddTreeRows(context);
            DropEmptyTreeStrips(context);
            AddTreeGroups(context);
            AddSeating(context);
        }

        // ------------------------------------------------------------- axes

        private static void AddAxes(Context context)
        {
            var design = context.Design;
            var main = design.Rooms[0];
            var normal = new float2(-main.Axis.y, main.Axis.x);
            if (design.Grammar == PlazaGrammar.Modern && context.Movement.DesireLines.Count > 0)
            {
                // The dominant walking direction cuts across the square.
                var line = context.Movement.DesireLines[StrongestDesireLine(context.Movement)];
                AddAxis(context, main.Center,
                    math.normalizesafe(line[line.Count - 1] - line[0]), true);
                AddAxis(context, main.Center, main.Axis, false);
            }
            else
            {
                AddAxis(context, main.Center, main.Axis, true);
                if (design.Grammar == PlazaGrammar.Classic
                    && design.Topology != PlazaTopology.Linear)
                    AddAxis(context, main.Center, normal, false);
            }
            // Rooms of a sequence are tied together by their links.
            for (var r = 1; r < design.Rooms.Count; r++)
            {
                var room = design.Rooms[r];
                if (room.Role == PlazaRoomRole.Passage
                    || room.Role == PlazaRoomRole.EntranceCourt) continue;
                var link = room.Center - main.Center;
                design.CompositionAxes.Add(new PlazaAxis
                {
                    Origin = main.Center + link * 0.5f,
                    Direction = math.normalizesafe(link),
                    HalfLength = math.length(link) * 0.5f,
                });
            }
        }

        private static void AddAxis(Context context, float2 origin, float2 direction,
            bool primary)
        {
            if (math.lengthsq(direction) < 0.5f) return;
            var forward = ReachInside(context.Geometry, origin, direction);
            var backward = ReachInside(context.Geometry, origin, -direction);
            context.Design.CompositionAxes.Add(new PlazaAxis
            {
                Origin = origin + direction * ((forward - backward) * 0.5f),
                Direction = direction,
                HalfLength = (forward + backward) * 0.5f,
                Primary = primary,
            });
        }

        private static float ReachInside(PlazaGeometry geometry, float2 origin, float2 direction)
        {
            var step = math.max(0.5f, geometry.CellSize);
            var reach = 0f;
            while (reach < geometry.MajorSpan * 2f
                && geometry.Contains(origin + direction * (reach + step))) reach += step;
            return reach;
        }

        // ----------------------------------------------------- focal points

        /// <summary>
        /// The primary focal point is where a centerpiece would stand: well
        /// visible, free, never on green; ordered grammars keep it on their
        /// axes, the modern one moves it out of the main flow.
        /// </summary>
        private static void AddFocalPoints(Context context)
        {
            var design = context.Design;
            var geometry = context.Geometry;
            var movement = context.Movement;
            var main = design.Rooms[0];
            var outline = main.Boundary;
            var halfWidth = math.max(1f, PlazaShape.MinimumWidth(outline) * 0.5f);
            var preferred = main.Center;
            var flowPenalty = design.Grammar == PlazaGrammar.Classic ? 0.05f
                : design.Grammar == PlazaGrammar.Natural ? 0.15f : 0.35f;
            if (design.Grammar == PlazaGrammar.Modern)
            {
                var side = new Unity.Mathematics.Random(Seeds.Mix(context.Seed, 0xa54ff53au))
                    .NextBool() ? 1f : -1f;
                preferred = main.Center + main.Axis * (main.Shape.HalfLength * 0.24f * side);
            }

            var best = main.Center;
            var bestScore = float.MinValue;
            var step = math.max(1f, geometry.CellSize * 2f);
            PolygonMath.Bounds(outline, out var min, out var max);
            var candidates = new List<float2> { main.Center, preferred };
            for (var y = min.y; y <= max.y; y += step)
            for (var x = min.x; x <= max.x; x += step)
                candidates.Add(new float2(x, y));
            foreach (var point in candidates)
            {
                if (!main.Shape.Contains(point, outline) || InGreen(context, point)) continue;
                var room = DistanceToOutline(point, outline);
                foreach (var zone in design.GreenZones)
                    room = math.min(room, DistanceToOutline(point, zone.Polygon));
                if (room < 2f) continue;
                var composition = 1f - math.saturate(math.distance(point, preferred) / halfWidth);
                var visible = 0f;
                foreach (var entrance in movement.Entrances)
                    if (geometry.SegmentInside(entrance, point)) visible++;
                visible = movement.Entrances.Count > 0 ? visible / movement.Entrances.Count : 1f;
                var score = 0.4f * composition + 0.3f * visible
                    + 0.3f * math.saturate(room / halfWidth) - flowPenalty * movement.At(point);
                if (score <= bestScore) continue;
                bestScore = score;
                best = point;
            }
            design.FocalPoints.Add(new PlazaFocalPoint
            {
                Position = best, Kind = PlazaFocalKind.Primary,
                Clearance = DistanceToOutline(best, outline),
            });
            for (var r = 1; r < design.Rooms.Count; r++)
            {
                var room = design.Rooms[r];
                // A garden court has a garden at its heart, not a centerpiece.
                if (room.Role != PlazaRoomRole.SecondarySquare) continue;
                design.FocalPoints.Add(new PlazaFocalPoint
                {
                    Position = room.Center, Kind = PlazaFocalKind.Secondary,
                    Clearance = DistanceToOutline(room.Center, room.Boundary),
                });
            }
        }

        // -------------------------------------------------------- tree rows

        /// <summary>
        /// A tree row follows its room edge on the centre line of its lawn
        /// strip. Trees never stand in the paving: without room for a strip
        /// there is no row, and the row breaks where its strip does.
        /// </summary>
        private static void AddTreeRows(Context context)
        {
            var design = context.Design;
            for (var r = 0; r < design.Rooms.Count; r++)
            {
                var room = design.Rooms[r];
                foreach (var edge in room.Edges)
                {
                    if (edge.Role != PlazaEdgeRole.TreeRow || edge.Rim < MinimumStripRim) continue;
                    var outward = edge.Outward;
                    var tangent = new float2(-outward.y, outward.x);
                    float tMin = float.MaxValue, tMax = float.MinValue, oMax = float.MinValue;
                    foreach (var point in edge.Points)
                    {
                        var d = point - room.Center;
                        tMin = math.min(tMin, math.dot(d, tangent));
                        tMax = math.max(tMax, math.dot(d, tangent));
                        oMax = math.max(oMax, math.dot(d, outward));
                    }
                    var o = oMax + GreenGap + TreeStripDepth(edge) * 0.5f;
                    var start = room.Center + tangent * (tMin + 2f) + outward * o;
                    var end = room.Center + tangent * (tMax - 2f) + outward * o;
                    AddRowRuns(context, start, end, context.Rules.TreeSpacing);
                }
            }
        }

        /// <summary>A tree strip whose row did not fit is just a stray bed: it goes.</summary>
        private static void DropEmptyTreeStrips(Context context)
        {
            var design = context.Design;
            design.GreenZones.RemoveAll(zone =>
            {
                if (zone.Kind != PlazaGreenKind.TreeStrip) return false;
                foreach (var row in design.TreeRows)
                    for (var i = 0; i < row.Count; i++)
                        if (PolygonMath.PointInside(math.lerp(row.Start, row.End,
                                row.Count > 1 ? i / (row.Count - 1f) : 0f), zone.Polygon)) return false;
                return true;
            });
        }

        private static void AddRowRuns(Context context, float2 start, float2 end,
            float spacing)
        {
            var length = math.distance(start, end);
            if (length < spacing * 2f) return;
            var count = (int)math.floor(length / spacing) + 1;
            var step = length / (count - 1);
            var direction = (end - start) / length;
            var runStart = -1;
            for (var i = 0; i <= count; i++)
            {
                var valid = i < count && TreeSpotFree(context,
                    start + direction * (step * i));
                if (valid && runStart < 0) runStart = i;
                if (valid || runStart < 0) continue;
                var runCount = i - runStart;
                if (runCount >= 3)
                    context.Design.TreeRows.Add(new PlazaPlacementLine
                    {
                        Start = start + direction * (step * runStart),
                        End = start + direction * (step * (i - 1)),
                        Spacing = step,
                        Count = runCount,
                    });
                runStart = -1;
            }
        }

        private static bool TreeSpotFree(Context context, float2 point)
        {
            var geometry = context.Geometry;
            if (!geometry.Contains(point) || geometry.DistanceToBoundary(point) < 1.5f)
                return false;
            var rooms = context.Design.Rooms;
            for (var r = 0; r < rooms.Count; r++)
            {
                if (rooms[r].Shape.Contains(point, rooms[r].Boundary)
                    || DistanceToOutline(point, rooms[r].Boundary) < 1f) return false;
            }
            return !NearCorridor(context, point, 1.5f)
                && !NearEntrance(context, point, EntranceClearance + 1f)
                && InGreen(context, point);
        }

        /// <summary>Informal tree groups inside gardens, islands and deep beds.</summary>
        private static void AddTreeGroups(Context context)
        {
            foreach (var zone in context.Design.GreenZones)
            {
                var area = PlazaShape.Area(zone.Polygon);
                var groupable = zone.Kind == PlazaGreenKind.Garden
                    || zone.Kind == PlazaGreenKind.Island
                    || zone.Kind == PlazaGreenKind.Border
                        && context.Design.Grammar != PlazaGrammar.Classic;
                if (groupable && area >= 80f
                    && PlazaShape.MinimumWidth(zone.Polygon) >= 5f)
                    context.Design.TreeGroups.Add(new List<float2>(zone.Polygon));
            }
        }

        // ---------------------------------------------------------- seating

        private static void AddSeating(Context context)
        {
            var design = context.Design;
            for (var r = 0; r < design.Rooms.Count; r++)
            {
                var room = design.Rooms[r];
                if (room.Role == PlazaRoomRole.Passage) continue;
                var depth = math.clamp(PlazaShape.MinimumWidth(room.Boundary) * 0.06f, 0.8f, 2f);
                foreach (var edge in room.Edges)
                {
                    if (edge.Role != PlazaEdgeRole.SeatingEdge) continue;
                    // Leave the corners and arriving corridors free.
                    var run = new List<float2>();
                    var trim = math.max(1, edge.Points.Count / 8);
                    for (var i = trim; i < edge.Points.Count - trim; i++)
                    {
                        var point = edge.Points[i];
                        if (!NearCorridor(context, point, 2f)) run.Add(point);
                        else Flush();
                    }
                    Flush();

                    void Flush()
                    {
                        if (run.Count >= 2 && PlazaMovement.PolylineLength(run) >= 5f)
                        {
                            var zone = new PlazaPlacementZone
                            {
                                Kind = PlazaFurnitureZoneKind.EdgeSeating,
                                Facing = -edge.Outward,
                            };
                            foreach (var p in run) zone.Polygon.Add(p);
                            for (var k = run.Count - 1; k >= 0; k--)
                                zone.Polygon.Add(run[k] - edge.Outward * depth);
                            design.FurnitureZones.Add(zone);
                        }
                        run.Clear();
                    }
                }
                if (room.Role == PlazaRoomRole.GardenCourt
                    || room.Role == PlazaRoomRole.SecondarySquare)
                {
                    var quiet = new PlazaPlacementZone { Kind = PlazaFurnitureZoneKind.QuietCorner };
                    quiet.Polygon.AddRange(room.Boundary);
                    design.FurnitureZones.Add(quiet);
                }
            }
        }
    }
}
