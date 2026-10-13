using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaFurnisher
    {
        // Placement order: what defines the space first, accessories last.
        private const int CenterpiecePriority = 0;
        private const int AvenuePriority = 1;
        private const int EntrancePriority = 2;
        private const int RingPriority = 2;
        private const int SeatingPriority = 3;
        private const int GrovePriority = 4;
        private const int PlantingPriority = 4;
        private const int LampPriority = 5;

        /// <summary>
        /// Motif resolver: reads the design intent (focal point, tree rows,
        /// groves, seating zones, rooms, entrances) and decides which motif
        /// realises it, in the language of the grammar.
        /// </summary>
        private static void ResolveMotifs(Context context)
        {
            var design = context.Design;
            var palette = context.Palette;
            var has = new System.Func<PlazaFurnitureKind, bool>(palette.Items.ContainsKey);

            if (palette.Centerpiece) AddCenterpiece(context);
            if (has(PlazaFurnitureKind.Tree))
            {
                AddAvenues(context);
                AddGroves(context);
            }
            if (has(PlazaFurnitureKind.Bench))
            {
                AddSeatingEdges(context);
                if (design.Grammar == PlazaGrammar.Natural) AddSeatingGroups(context);
                AddQuietSeating(context);
            }
            if (has(PlazaFurnitureKind.Bush)) AddBedPlanting(context);
            if (has(PlazaFurnitureKind.Lamp))
            {
                AddEntranceLamps(context);
                AddLampRhythms(context);
            }
        }

        private static PlazaMotif NewMotif(Context context, PlazaMotifKind kind,
            PlazaFurnitureKind item, int room, int priority)
        {
            var motif = new PlazaMotif { Kind = kind, Item = item, Room = room, Priority = priority };
            context.Motifs.Add(motif);
            return motif;
        }

        // ------------------------------------------------------ centerpiece

        /// <summary>
        /// A fountain or statue on the primary focal point when the square
        /// has room for it, with a ring of benches in the grammar's manner.
        /// </summary>
        private static void AddCenterpiece(Context context)
        {
            var design = context.Design;
            if (design.FocalPoints.Count == 0) return;
            var focal = design.FocalPoints[0];
            var main = design.Rooms[0];
            if (focal.Clearance < 5f || PlazaShape.Area(main.Boundary) < 500f) return;
            var chosen = context.Palette.CenterpieceRadius;
            // The chosen asset has its own size; it needs a walk around it.
            if (chosen > 0f && focal.Clearance < chosen + CenterpieceWalk + 1.5f) return;
            var radius = chosen > 0f ? chosen : math.clamp(focal.Clearance * 0.28f, 1.8f, 5f);
            var motif = NewMotif(context, PlazaMotifKind.Centerpiece, PlazaFurnitureKind.Bench,
                0, CenterpiecePriority);
            motif.Line.Add(focal.Position);
            motif.Spacing = radius;

            // Benches around it: on the axes (classic) or an open arc on one
            // side (modern). Natural squares sit at the gardens instead.
            if (!context.Palette.Items.ContainsKey(PlazaFurnitureKind.Bench)
                || design.Grammar == PlazaGrammar.Natural) return;
            var angles = design.Grammar == PlazaGrammar.Classic
                ? new[] { 0f, 90f, 180f, 270f }
                : new[] { 140f, 180f, 220f };
            var ring = NewMotif(context, PlazaMotifKind.SeatingRing, PlazaFurnitureKind.Bench,
                0, RingPriority);
            ring.FacingPoint = focal.Position;
            var distance = radius + CenterpieceWalk + 2.2f;
            foreach (var angle in angles)
                ring.Line.Add(focal.Position + PlazaComposer.Rotate(main.Axis,
                    math.radians(angle)) * distance);
        }

        // ------------------------------------------------------------ trees

        /// <summary>
        /// Every tree row of the design becomes an avenue. Classic rows that
        /// face each other across the main square are optimised together:
        /// both are cut to the stretch they share, so they get the same
        /// count and the same rhythm, even when an entrance interrupts one
        /// of them. Modern and natural rows may stay asymmetric.
        /// </summary>
        private static void AddAvenues(Context context)
        {
            var design = context.Design;
            var first = context.Motifs.Count;
            foreach (var row in design.TreeRows)
            {
                var motif = NewMotif(context, PlazaMotifKind.TreeAvenue, PlazaFurnitureKind.Tree,
                    0, AvenuePriority);
                motif.Line.Add(row.Start);
                motif.Line.Add(row.End);
                // The row's own step, a hair shorter so rounding keeps its count.
                motif.Spacing = row.Spacing * 0.995f;
                motif.MinimumCount = 3;
            }
            if (design.Grammar != PlazaGrammar.Classic) return;
            var center = design.Rooms[0].Center;
            for (var a = first; a < context.Motifs.Count; a++)
            for (var b = a + 1; b < context.Motifs.Count; b++)
            {
                var one = context.Motifs[a];
                var two = context.Motifs[b];
                if (one.Partner >= 0 || two.Partner >= 0) continue;
                var d1 = one.Line[1] - one.Line[0];
                var d2 = two.Line[1] - two.Line[0];
                var l1 = math.length(d1);
                var l2 = math.length(d2);
                var direction = d1 / l1;
                if (math.abs(math.dot(direction, d2 / l2)) < 0.98f) continue;
                // Opposite sides of the square, at a similar distance.
                var normal = new float2(-direction.y, direction.x);
                var s1 = math.dot((one.Line[0] + one.Line[1]) * 0.5f - center, normal);
                var s2 = math.dot((two.Line[0] + two.Line[1]) * 0.5f - center, normal);
                // One row may stand in tree pits inside the square, the other
                // in the rim outside it; both still frame the same room.
                var tolerance = math.max(3f, 0.6f * math.max(math.abs(s1), math.abs(s2)));
                if (s1 * s2 >= 0f || math.abs(math.abs(s1) - math.abs(s2)) > tolerance) continue;
                // The stretch both rows cover along the axis.
                var from = math.max(Along(one, direction, center, true),
                    Along(two, direction, center, true));
                var to = math.min(Along(one, direction, center, false),
                    Along(two, direction, center, false));
                if (to - from < one.Spacing * 2f) continue;
                one.FullLine = new List<float2>(one.Line);
                two.FullLine = new List<float2>(two.Line);
                CutTo(one, direction, center, from, to);
                CutTo(two, direction, center, from, to);
                one.Partner = b;
                two.Partner = a;
            }
        }

        private static float Along(PlazaMotif motif, float2 direction, float2 center, bool start)
        {
            var a = math.dot(motif.Line[0] - center, direction);
            var b = math.dot(motif.Line[1] - center, direction);
            return start ? math.min(a, b) : math.max(a, b);
        }

        private static void CutTo(PlazaMotif motif, float2 direction, float2 center,
            float from, float to)
        {
            var origin = motif.Line[0] - direction * math.dot(motif.Line[0] - center, direction);
            motif.Line[0] = origin + direction * from;
            motif.Line[1] = origin + direction * to;
        }

        /// <summary>Informal groups of trees inside the green zones meant for them.</summary>
        private static void AddGroves(Context context)
        {
            var design = context.Design;
            for (var g = 0; g < design.TreeGroups.Count; g++)
            {
                var polygon = design.TreeGroups[g];
                var area = PlazaShape.Area(polygon);
                var count = math.clamp((int)math.round(area / 70f * context.Palette.Density), 2, 6);
                var crown = context.Palette.Items[PlazaFurnitureKind.Tree].CrownRadius;
                var apartBy = math.max(4.5f, crown * 1.2f);
                var motif = NewMotif(context, PlazaMotifKind.Grove, PlazaFurnitureKind.Tree,
                    -1, GrovePriority);
                PolygonMath.Bounds(polygon, out var min, out var max);
                var random = new Unity.Mathematics.Random(Seeds.Mix(context.Seed,
                    0x2545f491u ^ (uint)(g * 977 + 1)));
                // The first tree stands where the zone is widest, the others
                // keep their distance: a loose cluster, not a grid.
                var start = PlazaGeometry.AreaCentroid(polygon);
                if (PolygonMath.PointInside(start, polygon)
                    && PlazaComposer.DistanceToOutline(start, polygon) >= 1.5f)
                    motif.Line.Add(start);
                for (var attempt = 0; attempt < 80 && motif.Line.Count < count; attempt++)
                {
                    var point = new float2(random.NextFloat(min.x, max.x),
                        random.NextFloat(min.y, max.y));
                    if (!PolygonMath.PointInside(point, polygon)
                        || PlazaComposer.DistanceToOutline(point, polygon) < 1.5f) continue;
                    var apart = true;
                    foreach (var other in motif.Line)
                        apart &= math.distance(point, other) >= apartBy;
                    if (apart) motif.Line.Add(point);
                }
            }
        }

        // ---------------------------------------------------------- seating

        /// <summary>
        /// Benches along every seating edge, one step into the room, facing
        /// it: evenly (classic), in pairs (modern) or loosely (natural).
        /// </summary>
        private static void AddSeatingEdges(Context context)
        {
            var design = context.Design;
            foreach (var zone in design.FurnitureZones)
            {
                if (zone.Kind != PlazaFurnitureZoneKind.EdgeSeating) continue;
                var run = zone.Polygon.Count / 2;
                if (run < 2) continue;
                var motif = NewMotif(context, PlazaMotifKind.SeatingEdge, PlazaFurnitureKind.Bench,
                    RoomAt(design, zone.Polygon[run / 2] + zone.Facing * 1.5f), SeatingPriority);
                for (var i = 0; i < run; i++)
                    motif.Line.Add(zone.Polygon[i] + zone.Facing * 1.3f);
                motif.Facing = zone.Facing;
                motif.Spacing = (design.Grammar == PlazaGrammar.Classic ? 6.5f
                    : design.Grammar == PlazaGrammar.Modern ? 4.2f : 7.5f) / context.Palette.Density;
                motif.Margin = 1.5f;
                motif.MinimumCount = 1;
            }
        }

        /// <summary>
        /// Informal seating for natural squares: two or three benches side by
        /// side with one shared orientation, on the paving where the main
        /// square meets a garden, looking out over the square.
        /// </summary>
        private static void AddSeatingGroups(Context context)
        {
            var design = context.Design;
            var main = design.Rooms[0];
            var focus = design.FocalPoints.Count > 0 ? design.FocalPoints[0].Position : main.Center;
            var bench = context.Palette.Items[PlazaFurnitureKind.Bench];
            var gardens = new List<PlazaGreenZone>();
            foreach (var zone in design.GreenZones)
                if (zone.Kind == PlazaGreenKind.Garden
                    && PlazaComposer.DistanceToOutline(PlazaComposer.ClosestOnOutline(main.Center,
                        zone.Polygon), main.Boundary) < 6f)
                    gardens.Add(zone);
            gardens.Sort((a, b) => PlazaShape.Area(b.Polygon).CompareTo(PlazaShape.Area(a.Polygon)));
            for (var g = 0; g < gardens.Count && g < 3; g++)
            {
                var edge = PlazaComposer.ClosestOnOutline(main.Center, gardens[g].Polygon);
                var inward = math.normalizesafe(main.Center - edge);
                var anchor = edge + inward * (bench.FootprintRadius + 0.8f);
                var facing = math.normalizesafe(focus - anchor);
                var along = new float2(-facing.y, facing.x);
                var motif = NewMotif(context, PlazaMotifKind.SeatingGroup, PlazaFurnitureKind.Bench,
                    0, SeatingPriority);
                motif.Facing = facing;
                var size = g == 0 ? 3 : 2;
                var step = bench.FootprintRadius * 2f + 0.6f;
                for (var i = 0; i < size; i++)
                    motif.Line.Add(anchor + along * ((i - (size - 1) * 0.5f) * step));
            }
        }

        /// <summary>
        /// Low planting gives the formal beds a purpose: planters along the
        /// spine of a modern island, at the corners of a classic parterre.
        /// </summary>
        private static void AddBedPlanting(Context context)
        {
            foreach (var zone in context.Design.GreenZones)
            {
                if (zone.Polygon.Count != 4) continue;
                if (zone.Kind == PlazaGreenKind.Island)
                {
                    var p = zone.Polygon;
                    var motif = NewMotif(context, PlazaMotifKind.BedPlanting, PlazaFurnitureKind.Bush,
                        zone.Room, PlantingPriority);
                    // Spine: between the midpoints of the two short sides.
                    var first = math.distance(p[0], p[1]) < math.distance(p[1], p[2]);
                    motif.Line.Add(first ? (p[0] + p[1]) * 0.5f : (p[1] + p[2]) * 0.5f);
                    motif.Line.Add(first ? (p[2] + p[3]) * 0.5f : (p[3] + p[0]) * 0.5f);
                    motif.Spacing = 2.6f / context.Palette.Density;
                    motif.Margin = 1.2f;
                    motif.MinimumCount = 2;
                }
                else if (zone.Kind == PlazaGreenKind.Parterre)
                {
                    var motif = NewMotif(context, PlazaMotifKind.BedPlanting, PlazaFurnitureKind.Bush,
                        zone.Room, PlantingPriority);
                    var centre = PlazaGeometry.AreaCentroid(zone.Polygon);
                    foreach (var corner in zone.Polygon)
                        motif.Line.Add(corner + math.normalizesafe(centre - corner) * 1.4f);
                }
            }
        }

        /// <summary>
        /// Garden courts and pockets without a seating edge get a few
        /// benches on their walk, facing the heart of the room.
        /// </summary>
        private static void AddQuietSeating(Context context)
        {
            var design = context.Design;
            for (var r = 1; r < design.Rooms.Count; r++)
            {
                var room = design.Rooms[r];
                if (room.Role != PlazaRoomRole.GardenCourt
                    && room.Role != PlazaRoomRole.SecondarySquare) continue;
                var hasEdgeSeating = false;
                foreach (var motif in context.Motifs)
                    hasEdgeSeating |= motif.Kind == PlazaMotifKind.SeatingEdge && motif.Room == r;
                if (hasEdgeSeating) continue;
                var quiet = NewMotif(context, PlazaMotifKind.QuietSeating, PlazaFurnitureKind.Bench,
                    r, SeatingPriority);
                quiet.FacingPoint = room.Center;
                foreach (var angle in new[] { 90f, 210f, 330f })
                {
                    var direction = PlazaComposer.Rotate(room.Axis, math.radians(angle));
                    var reach = 0f;
                    while (reach < 60f && room.Shape.Contains(room.Center + direction * (reach + 0.5f),
                               room.Boundary)) reach += 0.5f;
                    if (reach < 3f) continue;
                    quiet.Line.Add(room.Center + direction * (reach - 1.3f));
                }
            }
        }

        private static int RoomAt(PlazaDesign design, float2 point)
        {
            for (var r = 0; r < design.Rooms.Count; r++)
                if (design.Rooms[r].Role != PlazaRoomRole.Passage
                    && design.Rooms[r].Shape.Contains(point, design.Rooms[r].Boundary)) return r;
            return -1;
        }

        // ------------------------------------------------------------ lamps

        /// <summary>A pair of lamps either side of every entrance corridor.</summary>
        private static void AddEntranceLamps(Context context)
        {
            var geometry = context.Geometry;
            var design = context.Design;
            foreach (var corridor in design.MovementCorridors)
            {
                if (corridor.Entrance < 0) continue;
                var entrance = context.Movement.Entrances[corridor.Entrance];
                var edge = geometry.NearestEdge(entrance);
                var inward = geometry.InwardNormal(edge);
                var along = new float2(-inward.y, inward.x);
                var anchor = entrance + inward * 2.5f;
                var motif = NewMotif(context, PlazaMotifKind.EntranceLamps, PlazaFurnitureKind.Lamp,
                    corridor.Room, EntrancePriority);
                motif.Facing = inward;
                var offset = corridor.Width * 0.5f + 0.9f;
                motif.Line.Add(anchor + along * offset);
                motif.Line.Add(anchor - along * offset);
            }
        }

        /// <summary>
        /// Lamps in a steady rhythm along the edge of every room, and along
        /// one side of every passage between rooms.
        /// </summary>
        private static void AddLampRhythms(Context context)
        {
            var design = context.Design;
            var spacing = design.Grammar == PlazaGrammar.Classic ? 12f
                : design.Grammar == PlazaGrammar.Modern ? 9f : 14f;
            for (var r = 0; r < design.Rooms.Count; r++)
            {
                var room = design.Rooms[r];
                if (room.Role == PlazaRoomRole.Passage
                    || PlazaShape.Area(room.Boundary) < 150f) continue;
                var motif = NewMotif(context, PlazaMotifKind.LampRhythm, PlazaFurnitureKind.Lamp,
                    r, LampPriority);
                motif.Closed = true;
                motif.Spacing = spacing / context.Palette.Density;
                motif.MinimumCount = 2;
                // Just inside the room edge, counter-clockwise so the
                // facing direction (into the room) is the tangent's left.
                var outline = new List<float2>(PlazaShape.Densify(room.Boundary, 2f));
                if (PolygonMath.SignedArea(outline) < 0.0) outline.Reverse();
                foreach (var point in outline)
                    motif.Line.Add(point + math.normalizesafe(room.Center - point) * 0.8f);
            }
            foreach (var corridor in design.MovementCorridors)
            {
                if (corridor.Entrance >= 0 || corridor.Points.Count < 2
                    || PlazaMovement.PolylineLength(corridor.Points) < 8f) continue;
                var motif = NewMotif(context, PlazaMotifKind.LampRhythm, PlazaFurnitureKind.Lamp,
                    corridor.Room, LampPriority);
                motif.Spacing = 10f;
                motif.Margin = 2f;
                motif.MinimumCount = 1;
                var side = corridor.Width * 0.5f + 0.7f;
                for (var i = 0; i < corridor.Points.Count; i++)
                {
                    var before = corridor.Points[math.max(0, i - 1)];
                    var after = corridor.Points[math.min(corridor.Points.Count - 1, i + 1)];
                    var direction = math.normalizesafe(after - before);
                    motif.Line.Add(corridor.Points[i] + new float2(-direction.y, direction.x) * side);
                }
            }
        }

        // ------------------------------------------------------------ solve

        /// <summary>
        /// Lays out one motif and applies the constraints. A line motif is
        /// redistributed as a whole with one object fewer when a slot fails,
        /// so a row never shows a gap; rings, groves and lamps keep the
        /// slots that fit.
        /// </summary>
        private static void Solve(Context context, int index)
        {
            var motif = context.Motifs[index];
            if (motif.Solved) return;
            motif.Solved = true;
            switch (motif.Kind)
            {
                case PlazaMotifKind.TreeAvenue when motif.Partner >= 0:
                    SolvePair(context, index, motif.Partner);
                    return;
                case PlazaMotifKind.Centerpiece:
                {
                    var point = motif.Line[0];
                    var radius = motif.Spacing;
                    if (!Valid(context, point, radius, PlazaFurnitureKind.Bench, motif.Kind, true))
                        return;
                    motif.Centerpieces.Add(new PlazaCenterpiecePlacement
                    {
                        Kind = context.Palette.CenterpieceKind, Position = point, Radius = radius,
                    });
                    context.Placed.Add(new Occupied
                        { Position = point, Radius = radius, Motif = index, Centerpiece = true });
                    return;
                }
                case PlazaMotifKind.TreeAvenue:
                case PlazaMotifKind.SeatingEdge:
                    SolveLine(context, index, false);
                    return;
                case PlazaMotifKind.LampRhythm:
                    SolveLine(context, index, true);
                    return;
                case PlazaMotifKind.BedPlanting when motif.Line.Count == 2:
                    SolveLine(context, index, false);
                    return;
                default:
                    // Fixed slots: rings, quiet seating, groves, entrance lamps.
                    if (motif.Kind == PlazaMotifKind.SeatingRing && !HasCenterpiece(context))
                        return;
                    var tangents = new List<float2>();
                    foreach (var point in motif.Line) tangents.Add(float2.zero);
                    var accepted = Accept(context, index, motif.Line, tangents, true);
                    // An entrance is marked by a pair or not at all; a seating
                    // group needs at least two benches to be one.
                    if ((motif.Kind == PlazaMotifKind.EntranceLamps
                            || motif.Kind == PlazaMotifKind.SeatingGroup) && accepted < 2)
                        Clear(context, index);
                    return;
            }
        }

        /// <summary>
        /// Mirrored avenues are solved together: the same count at mirrored
        /// positions on the stretch both rows share. Where that does not fit
        /// (a passage crosses one of them), each row gets its full line back
        /// and the counts are matched afterwards.
        /// </summary>
        private static void SolvePair(Context context, int a, int b)
        {
            var one = context.Motifs[a];
            var two = context.Motifs[b];
            two.Solved = true;
            var length = LineLength(one.Line, false);
            var tangentsOne = new List<float2>();
            var tangentsTwo = new List<float2>();
            for (var n = LineCount(length, one.Spacing, one.Margin, false);
                 n >= one.MinimumCount; n--)
            {
                var slotsOne = LayoutLine(one.Line, false, one.Margin, n, tangentsOne);
                var slotsTwo = LayoutLine(two.Line, false, two.Margin, n, tangentsTwo);
                if (!Fits(context, one, slotsOne) || !Fits(context, two, slotsTwo)) continue;
                Accept(context, a, slotsOne, tangentsOne, false);
                Accept(context, b, slotsTwo, tangentsTwo, false);
                return;
            }
            foreach (var motif in new[] { one, two })
            {
                motif.Line.Clear();
                motif.Line.AddRange(motif.FullLine);
            }
            SolveLine(context, a, false);
            SolveLine(context, b, false);
        }

        private static bool HasCenterpiece(Context context)
        {
            foreach (var motif in context.Motifs)
                if (motif.Centerpieces.Count > 0) return true;
            return false;
        }

        private static void SolveLine(Context context, int index, bool keepPartial)
        {
            var motif = context.Motifs[index];
            var length = LineLength(motif.Line, motif.Closed);
            var count = LineCount(length, motif.Spacing, motif.Margin, motif.Closed);
            var tangents = new List<float2>();
            for (var n = count; n >= math.max(1, motif.MinimumCount); n--)
            {
                var slots = Rhythm(context, motif, LayoutLine(motif.Line, motif.Closed,
                    motif.Margin, n, tangents), tangents);
                if (Fits(context, motif, slots))
                {
                    Accept(context, index, slots, tangents, false);
                    return;
                }
            }
            if (!keepPartial || count < motif.MinimumCount) return;
            var all = LayoutLine(motif.Line, motif.Closed, motif.Margin, count, tangents);
            if (Accept(context, index, all, tangents, true) < motif.MinimumCount)
                Clear(context, index);
        }

        /// <summary>
        /// The grammar's rhythm on a line: modern seating sits in pairs with
        /// a wider pause, natural seating moves a little off the grid.
        /// </summary>
        private static List<float2> Rhythm(Context context, PlazaMotif motif,
            List<float2> slots, List<float2> tangents)
        {
            if (motif.Kind != PlazaMotifKind.SeatingEdge) return slots;
            var grammar = context.Design.Grammar;
            if (grammar == PlazaGrammar.Modern && slots.Count >= 3)
            {
                for (var i = slots.Count - 1; i >= 0; i--)
                    if (i % 3 == 2)
                    {
                        slots.RemoveAt(i);
                        tangents.RemoveAt(i);
                    }
            }
            else if (grammar == PlazaGrammar.Natural)
            {
                var random = new Unity.Mathematics.Random(Seeds.Mix(context.Seed,
                    0x9e3779b9u ^ (uint)slots.Count));
                for (var i = 0; i < slots.Count; i++)
                    slots[i] += tangents[i] * random.NextFloat(-0.8f, 0.8f);
            }
            return slots;
        }

        private static bool Fits(Context context, PlazaMotif motif, List<float2> slots)
        {
            var item = context.Palette.Items[motif.Item];
            var radius = item.FootprintRadius;
            for (var i = 0; i < slots.Count; i++)
            {
                if (!Valid(context, slots[i], radius, motif.Item, motif.Kind, false, item.CrownRadius))
                    return false;
                for (var k = 0; k < i; k++)
                    if (math.distance(slots[i], slots[k]) < radius * 2f + ItemGap) return false;
            }
            return slots.Count > 0;
        }

        /// <summary>Places the slots that pass the constraints; returns how many.</summary>
        private static int Accept(Context context, int index, List<float2> slots,
            List<float2> tangents, bool check)
        {
            var motif = context.Motifs[index];
            var item = context.Palette.Items[motif.Item];
            var random = new Unity.Mathematics.Random(Seeds.Mix(context.Seed,
                0x6c8e9cf5u ^ (uint)(index * 131 + 7)));
            var accepted = 0;
            for (var i = 0; i < slots.Count; i++)
            {
                var point = slots[i];
                if (check && !Valid(context, point, item.FootprintRadius, motif.Item, motif.Kind,
                        false, item.CrownRadius))
                    continue;
                var facing = motif.FacingPoint.HasValue
                    ? math.normalizesafe(motif.FacingPoint.Value - point)
                    : math.lengthsq(motif.Facing) > 0f ? motif.Facing
                    : motif.Kind == PlazaMotifKind.LampRhythm && motif.Closed
                        ? new float2(-tangents[i].y, tangents[i].x) : tangents[i];
                var size = item.Size;
                var rotation = RotationFacing(facing);
                if (motif.Item == PlazaFurnitureKind.Tree)
                {
                    // Trees vary a little; rows stay calmer than groves.
                    var spread = motif.Kind == PlazaMotifKind.Grove ? 0.15f : 0.05f;
                    size *= 1f + random.NextFloat(-spread, spread);
                    rotation = random.NextFloat(0f, 2f * math.PI);
                }
                motif.Placements.Add(new PlazaFurniturePlacement
                {
                    Kind = motif.Item, AssetName = item.AssetName, ArrangementId = index,
                    FootprintRadius = item.FootprintRadius, Position = point,
                    Rotation = rotation, Size = size,
                });
                context.Placed.Add(new Occupied
                {
                    Position = point, Radius = item.FootprintRadius, Crown = item.CrownRadius,
                    Motif = index,
                });
                accepted++;
            }
            return accepted;
        }

        private static void Clear(Context context, int index)
        {
            context.Motifs[index].Placements.Clear();
            context.Placed.RemoveAll(occupied => occupied.Motif == index);
        }

        /// <summary>
        /// Mirrored classic avenues show the same number of trees: the
        /// longer one is laid out again with the count of the shorter one.
        /// </summary>
        private static void MatchPartners(Context context)
        {
            for (var a = 0; a < context.Motifs.Count; a++)
            {
                var one = context.Motifs[a];
                var b = one.Partner;
                if (b <= a) continue;
                var two = context.Motifs[b];
                var target = math.min(one.Placements.Count, two.Placements.Count);
                if (target < one.MinimumCount) continue;
                foreach (var (index, motif) in new[] { (a, one), (b, two) })
                {
                    if (motif.Placements.Count == target) continue;
                    var previous = new List<PlazaFurniturePlacement>(motif.Placements);
                    Clear(context, index);
                    var tangents = new List<float2>();
                    var slots = LayoutLine(motif.Line, false, motif.Margin, target, tangents);
                    if (Fits(context, motif, slots))
                    {
                        Accept(context, index, slots, tangents, false);
                        continue;
                    }
                    // Keep the original row rather than lose it.
                    foreach (var placement in previous)
                    {
                        motif.Placements.Add(placement);
                        context.Placed.Add(new Occupied
                        {
                            Position = placement.Position, Radius = placement.FootprintRadius,
                            Crown = context.Palette.Items[motif.Item].CrownRadius, Motif = index,
                        });
                    }
                }
            }
        }
    }
}
