using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Phase 2: turns a composed <see cref="PlazaDesign"/> into a build-ready
    /// <see cref="PlazaPlan"/>. Objects are not scattered over the plaza; the
    /// pipeline realises the design intent step by step:
    /// motifs (why objects are there) → layout (where along their line or
    /// ring) → constraints (collisions, entrances, corridors, sight lines,
    /// green) → movement (every room stays reachable on a free path).
    /// Deterministic for equal input.
    /// </summary>
    internal static partial class PlazaFurnisher
    {
        /// <summary>Free space between two objects.</summary>
        private const float ItemGap = 0.4f;
        /// <summary>Walk kept around a centerpiece.</summary>
        private const float CenterpieceWalk = 1.2f;
        /// <summary>Width of a free path the solver guarantees through the plaza.</summary>
        internal const float PathWidth = 1.8f;
        /// <summary>Free radius in front of an entrance.</summary>
        private const float EntranceKeep = 3f;

        private struct Occupied
        {
            internal float2 Position;
            internal float Radius;
            /// <summary>Crown radius of a tree, 0 for everything else.</summary>
            internal float Crown;
            internal int Motif;
            internal bool Centerpiece;
        }

        private sealed class Context
        {
            internal PlazaDesign Design;
            internal PlazaGeometry Geometry;
            internal PlazaMovement Movement;
            internal PlazaPalette Palette;
            internal int Seed;
            internal List<PlazaMotif> Motifs;
            internal readonly List<Occupied> Placed = new List<Occupied>();
        }

        internal static PlazaFurnishing Furnish(PlazaDesign design, PlazaPalette palette, int seed,
            IReadOnlyList<PlazaFurniturePlacement> preset = null)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = new PlazaFurnishing();
            if (design == null || !design.Succeeded || design.Geometry == null)
            {
                result.Plan = EmptyPlan(seed);
                return result;
            }
            var context = new Context
            {
                Design = design, Geometry = design.Geometry, Movement = design.Movement,
                Palette = palette ?? PlazaPalette.Default(), Seed = seed, Motifs = result.Motifs,
            };
            AddPreset(context, preset);
            ResolveMotifs(context);
            // Stable by priority: the order of resolution breaks ties.
            var order = new List<int>();
            for (var i = 0; i < result.Motifs.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                var priority = result.Motifs[a].Priority.CompareTo(result.Motifs[b].Priority);
                return priority != 0 ? priority : a.CompareTo(b);
            });
            foreach (var index in order)
                if (result.Motifs[index].Kind != PlazaMotifKind.Preset) Solve(context, index);
            MatchPartners(context);
            result.RemovedForMovement = KeepWalkable(context, out result.Walkable);
            result.Plan = BuildPlan(context);
            timer.Stop();
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>Given objects stand first; the design works around them.</summary>
        private static void AddPreset(Context context, IReadOnlyList<PlazaFurniturePlacement> preset)
        {
            if (preset == null || preset.Count == 0) return;
            var motif = new PlazaMotif
            {
                Kind = PlazaMotifKind.Preset, Item = preset[0].Kind, Room = -1, Priority = 9,
            };
            context.Motifs.Add(motif);
            foreach (var placement in preset)
            {
                var copy = placement;
                copy.ArrangementId = 0;
                motif.Placements.Add(copy);
                context.Placed.Add(new Occupied
                    { Position = copy.Position, Radius = copy.FootprintRadius, Motif = 0 });
            }
        }

        private static PlazaPlan EmptyPlan(int seed) => new PlazaPlan(seed,
            PlazaCenterPlacementMode.Centered, PlazaArrangementPlacementMode.AroundCenter,
            0f, 0f, null, null);

        private static PlazaPlan BuildPlan(Context context)
        {
            var centerpieces = new List<PlazaCenterpiecePlacement>();
            var furniture = new List<PlazaFurniturePlacement>();
            foreach (var motif in context.Motifs)
            {
                centerpieces.AddRange(motif.Centerpieces);
                furniture.AddRange(motif.Placements);
            }
            return new PlazaPlan(context.Seed, PlazaCenterPlacementMode.Centered,
                PlazaArrangementPlacementMode.AroundCenter, 0f, 0f, centerpieces, furniture);
        }

        // ----------------------------------------------------------- layout

        /// <summary>
        /// Number of objects on an open line of length L with spacing s and
        /// end margin m: n = max(0, 1 + ⌊(L − 2m) / s⌋); a closed loop holds ⌊L / s⌋.
        /// </summary>
        internal static int LineCount(float length, float spacing, float margin, bool closed)
        {
            if (spacing <= 0f) return 0;
            if (closed) return (int)math.floor(length / spacing);
            var usable = length - 2f * margin;
            return usable < 0f ? 0 : 1 + (int)math.floor(usable / spacing);
        }

        /// <summary>
        /// Positions of count objects spread evenly over the line: on an open
        /// line from margin to margin (one object sits in the middle), on a
        /// closed loop at equal steps. Tangents give the local direction.
        /// </summary>
        internal static List<float2> LayoutLine(IReadOnlyList<float2> line, bool closed,
            float margin, int count, List<float2> tangents)
        {
            var result = new List<float2>();
            tangents?.Clear();
            if (count <= 0 || line.Count < 2) return result;
            var length = LineLength(line, closed);
            for (var i = 0; i < count; i++)
            {
                float at;
                if (closed) at = length * i / count;
                else if (count == 1) at = length * 0.5f;
                else at = margin + (length - 2f * margin) * i / (count - 1);
                result.Add(PointAt(line, closed, at, out var tangent));
                tangents?.Add(tangent);
            }
            return result;
        }

        private static float LineLength(IReadOnlyList<float2> line, bool closed)
        {
            var length = 0f;
            var segments = closed ? line.Count : line.Count - 1;
            for (var i = 0; i < segments; i++)
                length += math.distance(line[i], line[(i + 1) % line.Count]);
            return length;
        }

        private static float2 PointAt(IReadOnlyList<float2> line, bool closed, float at,
            out float2 tangent)
        {
            var segments = closed ? line.Count : line.Count - 1;
            for (var i = 0; i < segments; i++)
            {
                var a = line[i];
                var b = line[(i + 1) % line.Count];
                var piece = math.distance(a, b);
                if (at <= piece || i == segments - 1)
                {
                    tangent = math.normalizesafe(b - a);
                    return math.lerp(a, b, piece > 0f ? math.saturate(at / piece) : 0f);
                }
                at -= piece;
            }
            tangent = new float2(1f, 0f);
            return line[0];
        }

        /// <summary>Rotation of an object whose front faces the given direction.</summary>
        internal static float RotationFacing(float2 direction)
            => math.lengthsq(direction) < 1e-6f ? 0f : math.atan2(direction.x, direction.y);

        // ------------------------------------------------------ constraints

        /// <summary>Most a crown may reach beyond the plot, as share of its radius.</summary>
        private const float CrownOverhang = 0.4f;
        /// <summary>Closest two crowns may come, as share of their mean diameter.</summary>
        private const float CrownSeparation = 0.6f;

        /// <summary>
        /// Hard rules for one object: on the plot, clear of the boundary, of
        /// corridors, of entrances, of green (except trees and planting), of
        /// everything already placed, and trees out of the sight lines to the
        /// focal point. A tree is checked in three parts: its trunk and pit
        /// must stand on free ground, its crown may overhang paving and, a
        /// little, the plot edge, and crowns may touch but not merge.
        /// </summary>
        private static bool Valid(Context context, float2 point, float radius,
            PlazaFurnitureKind item, PlazaMotifKind motif, bool centerpiece = false,
            float crown = 0f)
        {
            var geometry = context.Geometry;
            var design = context.Design;
            if (!geometry.Contains(point) || geometry.DistanceToBoundary(point) < radius + 0.3f)
                return false;
            if (crown > 0f)
            {
                if (geometry.DistanceToBoundary(point) < crown * (1f - CrownOverhang)) return false;
                foreach (var other in context.Placed)
                    if (other.Crown > 0f && math.distance(point, other.Position)
                        < (crown + other.Crown) * CrownSeparation) return false;
            }
            var planted = item == PlazaFurnitureKind.Tree || motif == PlazaMotifKind.BedPlanting;
            if (!planted || centerpiece)
                foreach (var zone in design.GreenZones)
                    if (PolygonMath.PointInside(point, zone.Polygon)
                        || PlazaComposer.DistanceToOutline(point, zone.Polygon) < radius + 0.2f)
                        return false;
            foreach (var corridor in design.MovementCorridors)
                if (corridor.Points.Count > 1 && PlazaMovement.DistanceToPolyline(point,
                        corridor.Points) < corridor.Width * 0.5f + radius + 0.2f)
                    return false;
            if (motif != PlazaMotifKind.EntranceLamps)
                foreach (var entrance in context.Movement.Entrances)
                    if (math.distance(point, entrance) < EntranceKeep + radius) return false;
            foreach (var other in context.Placed)
            {
                var gap = ItemGap + (other.Centerpiece || centerpiece ? CenterpieceWalk : 0f);
                if (math.distance(point, other.Position) < radius + other.Radius + gap)
                    return false;
            }
            if (item == PlazaFurnitureKind.Tree && design.FocalPoints.Count > 0)
            {
                var focal = design.FocalPoints[0].Position;
                foreach (var entrance in context.Movement.Entrances)
                {
                    var from = geometry.InsideEntrance(entrance, 1f);
                    if (!geometry.SegmentInside(from, focal)) continue;
                    if (math.sqrt(PolygonMath.DistanceToSegmentSquared(point, from, focal))
                        < radius + 1f) return false;
                }
            }
            return true;
        }
    }
}
