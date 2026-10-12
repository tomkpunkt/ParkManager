using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaComposer
    {
        /// <summary>
        /// Everything that follows from the main plaza: secondary spaces,
        /// access corridors, green zones and the design intent.
        /// </summary>
        private static void ComposeAround(PlazaDesign design, PlazaGeometry geometry,
            PlazaMovement movement, PlazaShape core, StyleRules rules, int seed)
        {
            var coreOutline = design.MainPlaza;
            var spaces = new List<PlazaShape> { core };
            var outlines = new List<List<float2>> { coreOutline };
            AddSecondarySpaces(design, geometry, rules, spaces, outlines);

            AddCorridors(design, geometry, movement, rules, spaces, outlines);
            BuildGreenZones(design, geometry, rules, spaces, outlines);
            AddAxes(design, geometry, movement, core, spaces);
            AddFocalPoints(design, geometry, movement, core, spaces, seed);
            AddTreeRows(design, geometry, movement, core, rules, spaces, outlines);
            AddFurnitureZones(design, core, coreOutline, spaces, outlines);
        }

        // ------------------------------------------------- secondary spaces

        private static void AddSecondarySpaces(PlazaDesign design, PlazaGeometry geometry,
            StyleRules rules, List<PlazaShape> spaces, List<List<float2>> outlines)
        {
            if (rules.MaximumSecondarySpaces == 0) return;
            var rooms = geometry.RoomCentres(math.max(3f, geometry.MaximumDistance * 0.35f),
                geometry.MaximumDistance * 1.2f);
            for (var i = 0; i < rooms.Count
                && design.SecondarySpaces.Count < rules.MaximumSecondarySpaces; i++)
            {
                var room = rooms[i];
                if (InsideAny(room, spaces, outlines, GreenGap + 4f)) continue;
                var radius = geometry.DistanceToBoundary(room);
                var shape = new PlazaShape
                {
                    Kind = PlazaCoreShape.Ellipse, Center = room,
                    Axis = geometry.MajorAxis,
                };
                // Grow until it touches the rim, another space or its target size.
                var target = math.sqrt(geometry.Area * SecondaryTargetShare / math.PI);
                var size = math.min(target, radius - 1.5f);
                var fitted = false;
                for (; size >= 3f; size -= 0.5f)
                {
                    shape.HalfLength = shape.HalfWidth = size;
                    var outline = shape.Outline();
                    if (Clear(geometry, outline, 1.5f) && !Overlaps(outline, spaces,
                            outlines, GreenGap * 2f))
                    {
                        fitted = true;
                        break;
                    }
                }
                if (!fitted) continue;
                var result = shape.Outline();
                spaces.Add(shape);
                outlines.Add(result);
                design.SecondarySpaces.Add(result);
            }
        }

        private static bool Clear(PlazaGeometry geometry, List<float2> outline, float rim)
        {
            for (var i = 0; i < outline.Count; i++)
                if (!geometry.Contains(outline[i])
                    || geometry.DistanceToBoundary(outline[i]) < rim) return false;
            return true;
        }

        private static bool Overlaps(List<float2> outline, List<PlazaShape> spaces,
            List<List<float2>> outlines, float gap)
        {
            for (var i = 0; i < outline.Count; i++)
                if (InsideAny(outline[i], spaces, outlines, gap)) return true;
            return false;
        }

        /// <summary>True when point lies in a space or within gap of its outline.</summary>
        private static bool InsideAny(float2 point, List<PlazaShape> spaces,
            List<List<float2>> outlines, float gap)
        {
            for (var i = 0; i < spaces.Count; i++)
            {
                if (spaces[i].Contains(point, outlines[i])) return true;
                if (gap > 0f && DistanceToOutline(point, outlines[i]) < gap) return true;
            }
            return false;
        }

        internal static float DistanceToOutline(float2 point, IReadOnlyList<float2> outline)
        {
            var best = float.MaxValue;
            for (var i = 0; i < outline.Count; i++)
                best = math.min(best, PolygonMath.DistanceToSegmentSquared(point,
                    outline[i], outline[(i + 1) % outline.Count]));
            return math.sqrt(best);
        }

        // -------------------------------------------------------- corridors

        /// <summary>
        /// Every entrance gets a corridor into the main plaza; secondary
        /// spaces are linked to it. Corridors end where they reach a space:
        /// people may cross the open plaza, but it gets no visible paths.
        /// </summary>
        private static void AddCorridors(PlazaDesign design, PlazaGeometry geometry,
            PlazaMovement movement, StyleRules rules, List<PlazaShape> spaces,
            List<List<float2>> outlines)
        {
            for (var e = 0; e < movement.Entrances.Count; e++)
            {
                var entrance = movement.Entrances[e];
                var path = movement.ShortestPath(entrance, spaces[0].Center);
                var corridor = new PlazaCorridor { Width = rules.CorridorWidth, Entrance = e };
                if (path != null) ClipAtSpace(path, spaces[0], outlines[0], corridor.Points);
                if (corridor.Points.Count < 2)
                {
                    // The entrance opens directly onto the plaza.
                    corridor.Points.Clear();
                    corridor.Points.Add(entrance);
                    corridor.Points.Add(entrance);
                }
                design.MovementCorridors.Add(corridor);
            }
            for (var s = 1; s < spaces.Count; s++)
            {
                var path = movement.ShortestPath(spaces[s].Center, spaces[0].Center);
                if (path == null) continue;
                // Start at the secondary space's edge, end at the main plaza.
                var trimmed = new List<float2>();
                ClipAtSpace(path, spaces[0], outlines[0], trimmed);
                var corridor = new PlazaCorridor { Width = rules.CorridorWidth * 0.8f };
                var started = false;
                for (var i = 0; i < trimmed.Count; i++)
                {
                    if (!started && spaces[s].Contains(trimmed[i], outlines[s])) continue;
                    if (!started && i > 0) corridor.Points.Add(EdgeCrossing(
                        trimmed[i - 1], trimmed[i], spaces[s], outlines[s]));
                    started = true;
                    corridor.Points.Add(trimmed[i]);
                }
                if (corridor.Points.Count >= 2) design.MovementCorridors.Add(corridor);
            }
        }

        /// <summary>Copies path up to the point where it enters the space.</summary>
        private static void ClipAtSpace(List<float2> path, PlazaShape space,
            List<float2> outline, List<float2> result)
        {
            result.Add(path[0]);
            if (space.Contains(path[0], outline)) return;
            for (var i = 1; i < path.Count; i++)
            {
                if (!space.Contains(path[i], outline))
                {
                    result.Add(path[i]);
                    continue;
                }
                result.Add(EdgeCrossing(path[i - 1], path[i], space, outline));
                return;
            }
        }

        /// <summary>Point where segment a→b crosses the space outline.</summary>
        private static float2 EdgeCrossing(float2 a, float2 b, PlazaShape space,
            List<float2> outline)
        {
            var aInside = space.Contains(a, outline);
            float low = 0f, high = 1f;
            for (var step = 0; step < 20; step++)
            {
                var middle = (low + high) * 0.5f;
                if (space.Contains(math.lerp(a, b, middle), outline) == aInside) low = middle;
                else high = middle;
            }
            return math.lerp(a, b, (low + high) * 0.5f);
        }

        // ------------------------------------------------------------- axes

        private static void AddAxes(PlazaDesign design, PlazaGeometry geometry,
            PlazaMovement movement, PlazaShape core, List<PlazaShape> spaces)
        {
            switch (design.Style)
            {
                case PlazaStyle.Modern when movement.DesireLines.Count > 0:
                    // The dominant walking direction crosses the plaza.
                    var line = movement.DesireLines[StrongestDesireLine(movement)];
                    var direction = math.normalizesafe(line[line.Count - 1] - line[0]);
                    AddAxis(design, geometry, core.Center, direction, true);
                    AddAxis(design, geometry, core.Center, core.Axis, false);
                    break;
                case PlazaStyle.Sequence:
                    AddAxis(design, geometry, core.Center, core.Axis, true);
                    for (var i = 1; i < spaces.Count; i++)
                    {
                        var link = spaces[i].Center - core.Center;
                        design.CompositionAxes.Add(new PlazaAxis
                        {
                            Origin = core.Center + link * 0.5f,
                            Direction = math.normalizesafe(link),
                            HalfLength = math.length(link) * 0.5f,
                        });
                    }
                    break;
                case PlazaStyle.Promenade:
                    AddAxis(design, geometry, core.Center, core.Axis, true);
                    break;
                default:
                    AddAxis(design, geometry, core.Center, core.Axis, true);
                    AddAxis(design, geometry, core.Center, core.Normal, false);
                    break;
            }
        }

        /// <summary>Axis through origin, reaching the boundary on both sides.</summary>
        private static void AddAxis(PlazaDesign design, PlazaGeometry geometry,
            float2 origin, float2 direction, bool primary)
        {
            if (math.lengthsq(direction) < 0.5f) return;
            var forward = ReachInside(geometry, origin, direction);
            var backward = ReachInside(geometry, origin, -direction);
            design.CompositionAxes.Add(new PlazaAxis
            {
                Origin = origin + direction * ((forward - backward) * 0.5f),
                Direction = direction,
                HalfLength = (forward + backward) * 0.5f,
                Primary = primary,
            });
        }

        private static float ReachInside(PlazaGeometry geometry, float2 origin,
            float2 direction)
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
        /// visible, free inside the plaza, but off the main walking lines.
        /// </summary>
        private static void AddFocalPoints(PlazaDesign design, PlazaGeometry geometry,
            PlazaMovement movement, PlazaShape core, List<PlazaShape> spaces, int seed)
        {
            var outline = design.MainPlaza;
            var halfWidth = math.max(1f, PlazaShape.MinimumWidth(outline) * 0.5f);
            var preferred = core.Center;
            if (design.Style == PlazaStyle.Modern)
            {
                // Golden section on the plaza's own axis, side chosen by seed.
                var side = new Unity.Mathematics.Random(Seeds.Mix(seed, 0xa54ff53au))
                    .NextBool() ? 1f : -1f;
                preferred = core.Center + core.Axis * (core.HalfLength * 0.24f * side);
            }

            // Ordered styles keep the centerpiece on their axes; walking
            // lines pass around it. Free styles move it out of the flow.
            var flowPenalty = design.Style == PlazaStyle.Classic
                || design.Style == PlazaStyle.Promenade ? 0.05f
                : design.Style == PlazaStyle.Sequence ? 0.15f : 0.35f;
            var best = core.Center;
            var bestScore = float.MinValue;
            var step = math.max(1f, geometry.CellSize * 2f);
            PolygonMath.Bounds(outline, out var min, out var max);
            var candidates = new List<float2> { core.Center, preferred };
            for (var y = min.y; y <= max.y; y += step)
            for (var x = min.x; x <= max.x; x += step)
                candidates.Add(new float2(x, y));
            foreach (var point in candidates)
            {
                if (!core.Contains(point, outline)) continue;
                var room = DistanceToOutline(point, outline);
                if (room < 2f) continue;
                var composition = 1f - math.saturate(math.distance(point, preferred)
                    / halfWidth);
                var visible = 0f;
                for (var e = 0; e < movement.Entrances.Count; e++)
                    if (geometry.SegmentInside(movement.Entrances[e], point)) visible++;
                visible = movement.Entrances.Count > 0
                    ? visible / movement.Entrances.Count : 1f;
                var score = 0.4f * composition + 0.3f * visible
                    + 0.3f * math.saturate(room / halfWidth)
                    - flowPenalty * movement.At(point);
                if (score <= bestScore) continue;
                bestScore = score;
                best = point;
            }
            design.FocalPoints.Add(new PlazaFocalPoint
            {
                Position = best, Kind = PlazaFocalKind.Primary,
                Clearance = DistanceToOutline(best, outline),
            });
            for (var i = 1; i < spaces.Count; i++)
                design.FocalPoints.Add(new PlazaFocalPoint
                {
                    Position = spaces[i].Center, Kind = PlazaFocalKind.Secondary,
                    Clearance = spaces[i].HalfWidth,
                });
        }

        // -------------------------------------------------------- tree rows

        private static void AddTreeRows(PlazaDesign design, PlazaGeometry geometry,
            PlazaMovement movement, PlazaShape core, StyleRules rules,
            List<PlazaShape> spaces, List<List<float2>> outlines)
        {
            var spacing = rules.TreeSpacing;
            switch (design.Style)
            {
                case PlazaStyle.Promenade:
                    // An allée on both long sides of the promenade.
                    var offset = core.HalfWidth + 2.5f;
                    for (var side = -1; side <= 1; side += 2)
                    {
                        var shift = core.Normal * (offset * side);
                        AddRowRuns(design, geometry, core.Center - core.Axis
                            * core.HalfLength + shift, core.Center + core.Axis
                            * core.HalfLength + shift, spacing, spaces, outlines);
                    }
                    break;
                case PlazaStyle.Modern:
                case PlazaStyle.Sequence:
                {
                    // Asymmetric framing: rows along the two longest sides only.
                    var edges = BoundaryEdgesByLength(geometry);
                    var added = 0;
                    for (var i = 0; i < edges.Count && added < 2; i++)
                    {
                        var before = design.TreeRows.Count;
                        AddEdgeRow(design, geometry, edges[i], spacing, spaces, outlines);
                        if (design.TreeRows.Count > before) added++;
                    }
                    break;
                }
                default:
                    // Classic: every side of the plot is framed by a row.
                    for (var edge = 0; edge < geometry.Polygon.Count; edge++)
                        AddEdgeRow(design, geometry, edge, spacing, spaces, outlines);
                    break;
            }

            // Rows must leave the corridors and entrances free.
            for (var i = design.TreeRows.Count - 1; i >= 0; i--)
                if (design.TreeRows[i].Count < 3) design.TreeRows.RemoveAt(i);
        }

        private static List<int> BoundaryEdgesByLength(PlazaGeometry geometry)
        {
            var edges = new List<int>();
            for (var i = 0; i < geometry.Polygon.Count; i++) edges.Add(i);
            edges.Sort((a, b) =>
            {
                var la = math.distancesq(geometry.Polygon[a],
                    geometry.Polygon[(a + 1) % geometry.Polygon.Count]);
                var lb = math.distancesq(geometry.Polygon[b],
                    geometry.Polygon[(b + 1) % geometry.Polygon.Count]);
                return lb != la ? lb.CompareTo(la) : a.CompareTo(b);
            });
            return edges;
        }

        /// <summary>A row parallel to a boundary edge, in the middle of the rim.</summary>
        private static void AddEdgeRow(PlazaDesign design, PlazaGeometry geometry,
            int edge, float spacing, List<PlazaShape> spaces, List<List<float2>> outlines)
        {
            var a = geometry.Polygon[edge];
            var b = geometry.Polygon[(edge + 1) % geometry.Polygon.Count];
            var normal = geometry.InwardNormal(edge);
            // Distance from the edge to the main plaza along its midpoint.
            var mid = (a + b) * 0.5f;
            var rim = 0f;
            while (rim < geometry.MinorSpan
                && !InsideAny(mid + normal * rim, spaces, outlines, 0f)
                && geometry.Contains(mid + normal * (rim + 0.5f))) rim += 0.5f;
            if (rim < 4f) return;
            var inset = math.min(rim * 0.5f, 4f);
            var direction = math.normalizesafe(b - a);
            AddRowRuns(design, geometry, a + normal * inset + direction * inset,
                b + normal * inset - direction * inset, spacing, spaces, outlines);
        }

        /// <summary>
        /// Lays positions along a line and keeps the continuous runs that
        /// are free of spaces, corridors, entrances and the boundary.
        /// </summary>
        private static void AddRowRuns(PlazaDesign design, PlazaGeometry geometry,
            float2 start, float2 end, float spacing, List<PlazaShape> spaces,
            List<List<float2>> outlines)
        {
            var length = math.distance(start, end);
            if (length < spacing * 2f) return;
            var count = (int)math.floor(length / spacing) + 1;
            // Even distribution: the row fills its line without a short gap.
            var step = length / (count - 1);
            var direction = (end - start) / length;
            var runStart = -1;
            for (var i = 0; i <= count; i++)
            {
                var valid = i < count && TreeSpotFree(design, geometry,
                    start + direction * (step * i), spaces, outlines);
                if (valid && runStart < 0) runStart = i;
                if (valid || runStart < 0) continue;
                var runCount = i - runStart;
                if (runCount >= 3)
                    design.TreeRows.Add(new PlazaPlacementLine
                    {
                        Start = start + direction * (step * runStart),
                        End = start + direction * (step * (i - 1)),
                        Spacing = step,
                        Count = runCount,
                    });
                runStart = -1;
            }
        }

        private static bool TreeSpotFree(PlazaDesign design, PlazaGeometry geometry,
            float2 point, List<PlazaShape> spaces, List<List<float2>> outlines)
        {
            if (!geometry.Contains(point) || geometry.DistanceToBoundary(point) < 1.5f)
                return false;
            if (InsideAny(point, spaces, outlines, 1f)) return false;
            for (var i = 0; i < design.MovementCorridors.Count; i++)
            {
                var corridor = design.MovementCorridors[i];
                if (PlazaMovement.DistanceToPolyline(point, corridor.Points)
                    < corridor.Width * 0.5f + 1.5f) return false;
            }
            return true;
        }

        // -------------------------------------------------- furniture zones

        /// <summary>
        /// Seating strips along the inside of each space's edge, facing in,
        /// interrupted where a corridor arrives.
        /// </summary>
        private static void AddFurnitureZones(PlazaDesign design, PlazaShape core,
            List<float2> coreOutline, List<PlazaShape> spaces, List<List<float2>> outlines)
        {
            var depth = math.clamp(PlazaShape.MinimumWidth(coreOutline) * 0.06f, 0.8f, 2f);
            AddEdgeSeating(design, spaces[0].Center, coreOutline, 10f, depth);
            for (var i = 1; i < spaces.Count; i++)
            {
                var zone = new PlazaPlacementZone
                {
                    Kind = PlazaFurnitureZoneKind.QuietCorner,
                    Facing = float2.zero,
                };
                zone.Polygon.AddRange(outlines[i]);
                design.FurnitureZones.Add(zone);
            }
        }

        /// <summary>
        /// Seats belong where green lies behind them: looking onto the open
        /// plaza with planting at the back, never where a corridor arrives.
        /// </summary>
        private static void AddEdgeSeating(PlazaDesign design, float2 center,
            List<float2> outline, float pieceLength, float depth)
        {
            var dense = PlazaShape.Densify(outline, 1f);
            var run = new List<float2>();
            for (var i = 0; i <= dense.Count; i++)
            {
                var point = dense[i % dense.Count];
                var behind = point + math.normalizesafe(point - center) * (GreenGap + 1.5f);
                var free = i < dense.Count && !NearCorridor(design, point, 3f)
                    && InGreen(design, behind);
                if (free) run.Add(point);
                if ((free && i < dense.Count) || run.Count == 0) continue;
                if (PlazaMovement.PolylineLength(run) >= pieceLength) AddStrip(run);
                run.Clear();
            }

            void AddStrip(List<float2> edge)
            {
                var zone = new PlazaPlacementZone { Kind = PlazaFurnitureZoneKind.EdgeSeating };
                for (var i = 0; i < edge.Count; i++) zone.Polygon.Add(edge[i]);
                for (var i = edge.Count - 1; i >= 0; i--)
                    zone.Polygon.Add(edge[i] + math.normalizesafe(center - edge[i]) * depth);
                var middle = edge[edge.Count / 2];
                zone.Facing = math.normalizesafe(center - middle);
                design.FurnitureZones.Add(zone);
            }
        }

        private static bool InGreen(PlazaDesign design, float2 point)
        {
            for (var i = 0; i < design.GreenZones.Count; i++)
                if (PolygonMath.PointInside(point, design.GreenZones[i])) return true;
            return false;
        }

        private static bool NearCorridor(PlazaDesign design, float2 point, float extra)
        {
            for (var i = 0; i < design.MovementCorridors.Count; i++)
            {
                var corridor = design.MovementCorridors[i];
                var end = corridor.Points[corridor.Points.Count - 1];
                if (math.distance(point, end) < corridor.Width * 0.5f + extra) return true;
            }
            return false;
        }
    }
}
