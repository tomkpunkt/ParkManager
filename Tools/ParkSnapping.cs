// Snapping interaction adapted from ParkingLotTool (GPL-3.0).
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Simulation;
using Game.Tools;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    public sealed partial class ParkToolSystem
    {
        private const float SnapDistance = 8f;
        private const float SnapGuideLength = 40f;
        private const float SnapLevelDirection = 0f;
        private const float SnapLevelGuide = 0f;
        private const float SnapLevelZoneGrid = 0f;
        private const float SnapLevelNet = 1f;
        private const float SnapLevelObject = 1f;
        private const float SnapLevelArea = 2f;

        internal enum SnapKind
        {
            None,
            RoadEdge,
            ObjectSide,
            AreaEdge,
            Direction,
            Guide,
            ZoneGrid,
            Crossing,
        }

        private struct SnapCandidate
        {
            internal float3 Position;
            internal float2 Direction;
            internal float2 Priority;
            internal SnapKind Kind;
            internal bool HasGuide;
            internal float2 GuideDirection;
        }

        private struct SnapLine
        {
            internal float3 Position;
            internal float2 Direction;
            internal float Level;
            internal bool IsGuide;
        }

        private readonly List<SnapLine> _snapLines = new List<SnapLine>();
        private readonly List<float2> _pointAxes = new List<float2>();
        private float2 _hoverAxis;
        private float2 _dragStartAxis;
        private Snap _snapSelection = SupportedSnapKinds;

        internal SnapKind LastSnap { get; private set; }
        internal bool HasSnapGuide { get; private set; }
        internal Line3.Segment SnapGuide { get; private set; }

        internal static Snap SupportedSnapKinds
            => Snap.ExistingGeometry | Snap.StraightDirection
                | Snap.NetSide | Snap.ObjectSide
                | Snap.GuideLines | Snap.ZoneGrid;

        public override Snap selectedSnap
        {
            get => _snapSelection;
            set => _snapSelection = value & SupportedSnapKinds;
        }

        // The controls live in the ParkManager header. Returning None prevents
        // CS2 from rendering a second snap-options window for the same state.
        public override void GetAvailableSnapMask(out Snap onMask, out Snap offMask)
        {
            onMask = Snap.None;
            offMask = Snap.None;
        }

        private void ConfigureSnapping()
        {
            m_SnapOnMask = SupportedSnapKinds;
            m_SnapOffMask = SupportedSnapKinds;
            _ui?.SetSnapMask((int)SupportedSnapKinds);
        }

        private void ClearSnapFeedback()
        {
            LastSnap = SnapKind.None;
            HasSnapGuide = false;
            _hoverAxis = float2.zero;
        }

        private float3 ApplySnapping(float3 raw)
        {
            ClearSnapFeedback();
            _snapLines.Clear();

            // Closing the polygon must remain possible even when another
            // high-priority target lies next to the first point.
            if (!_closed && _points.Count >= 3
                && math.distance(raw.xz, _points[0]) <= CloseDistance) return raw;

            var snap = GetActualSnap();
            var best = new SnapCandidate
            {
                Position = raw,
                Priority = new float2(float.MinValue, float.MinValue),
                Kind = SnapKind.None,
            };

            if ((snap & Snap.NetSide) != 0) CollectRoadEdges(raw, ref best);
            if ((snap & Snap.ObjectSide) != 0) CollectObjectSides(raw, ref best);
            if ((snap & Snap.ExistingGeometry) != 0) CollectAreaEdges(raw, ref best);
            if ((snap & Snap.ZoneGrid) != 0) CollectZoneGrid(raw, ref best);
            if ((snap & Snap.GuideLines) != 0) CollectGuideLines(raw, ref best);
            if ((snap & Snap.StraightDirection) != 0) CollectDirections(raw, ref best);

            if (best.Kind == SnapKind.None) return raw;
            LastSnap = best.Kind;
            _hoverAxis = best.Direction;
            if (best.HasGuide)
            {
                var from = best.Position;
                var to = best.Position;
                from.xz -= best.GuideDirection * SnapGuideLength;
                to.xz += best.GuideDirection * SnapGuideLength;
                SnapGuide = new Line3.Segment(from, to);
                HasSnapGuide = true;
            }
            return best.Position;
        }

        private void RegisterSnap(ref SnapCandidate best, float3 hit, float level,
            SnapKind kind, float3 position, float2 direction, bool guide = false)
        {
            if (!math.all(math.isfinite(position))
                || math.lengthsq(direction) < 0.5f) return;
            Offer(ref best, hit, level, 1f, new SnapCandidate
            {
                Position = position,
                Direction = direction,
                Kind = kind,
                HasGuide = guide,
                GuideDirection = direction,
            });
            AddSnapLine(ref best, hit, new SnapLine
            {
                Position = position,
                Direction = direction,
                Level = level,
                IsGuide = guide,
            });
        }

        private void AddSnapLine(ref SnapCandidate best, float3 hit, SnapLine line)
        {
            for (var i = 0; i < _snapLines.Count; i++)
            {
                var other = _snapLines[i];
                if (math.abs(math.dot(line.Direction, other.Direction)) > 0.999999f)
                    continue;
                var first = new Line2(line.Position.xz,
                    line.Position.xz + line.Direction);
                var second = new Line2(other.Position.xz,
                    other.Position.xz + other.Direction);
                if (!MathUtils.Intersect(first, second, out var t)) continue;
                var leading = line.Level >= other.Level;
                var source = leading ? line : other;
                var position = source.Position;
                position.xz += source.Direction * (leading ? t.x : t.y);
                Offer(ref best, hit, math.max(line.Level, other.Level), 2f,
                    new SnapCandidate
                    {
                        Position = position,
                        Direction = source.Direction,
                        Kind = SnapKind.Crossing,
                        HasGuide = line.IsGuide || other.IsGuide,
                        GuideDirection = line.IsGuide
                            ? line.Direction : other.Direction,
                    });
            }
            _snapLines.Add(line);
        }

        private static void Offer(ref SnapCandidate best, float3 hit, float level,
            float weight, SnapCandidate proposal)
        {
            proposal.Priority = CalculateSnapPriority(level, weight, hit,
                proposal.Position, proposal.Direction);
            if (proposal.Priority.x > best.Priority.x
                || proposal.Priority.x == best.Priority.x
                && proposal.Priority.y > best.Priority.y) best = proposal;
        }

        private static float2 CalculateSnapPriority(float level, float weight,
            float3 hit, float3 snapped, float2 direction)
        {
            var delta = snapped - hit;
            var offset = new float3(math.dot(delta.xz, direction), delta.y,
                math.dot(delta.xz, MathUtils.Right(direction))) / SnapDistance;
            offset *= offset;
            var sum = math.min(1f, offset.x + offset.z);
            var worst = math.max(offset.x, offset.z)
                + math.min(offset.x, offset.z) * 0.001f;
            return new float2(level, weight * (2f - sum - worst));
        }

        private void CollectDirections(float3 raw, ref SnapCandidate best)
        {
            SyncSnapAxes();
            var count = _points.Count;
            if (count == 0) return;
            var distance = float.MaxValue;
            var position = raw;
            var direction = float2.zero;

            if (_dragPoint >= 0 && count >= 3)
            {
                var previous = (_dragPoint - 1 + count) % count;
                var before = (_dragPoint - 2 + count) % count;
                var next = (_dragPoint + 1) % count;
                var after = (_dragPoint + 2) % count;
                DirectionSnap(ref distance, ref position, ref direction, raw,
                    _points[previous],
                    math.normalizesafe(_points[before] - _points[previous]));
                DirectionSnap(ref distance, ref position, ref direction, raw,
                    _points[next], math.normalizesafe(_points[after] - _points[next]));
            }
            else if (_dragPoint < 0)
            {
                var anchor = _points[count - 1];
                DirectionSnap(ref distance, ref position, ref direction, raw,
                    anchor, _pointAxes[count - 1]);
                if (count >= 2)
                    DirectionSnap(ref distance, ref position, ref direction, raw,
                        anchor, math.normalizesafe(_points[count - 2] - anchor));
            }
            if (math.lengthsq(direction) < 0.5f) return;
            var heightData = _terrainSystem.GetHeightData();
            position.y = TerrainUtils.SampleHeight(ref heightData,
                new float3(position.x, raw.y, position.z));
            RegisterSnap(ref best, raw, SnapLevelDirection, SnapKind.Direction,
                position, direction, true);
        }

        private static void DirectionSnap(ref float bestDistance,
            ref float3 resultPosition, ref float2 resultDirection,
            float3 raw, float2 origin, float2 axis)
        {
            if (math.lengthsq(axis) < 0.5f) return;
            var across = MathUtils.Right(axis);
            var alongLine = new Line2(origin, origin + axis);
            var acrossLine = new Line2(origin, origin + across);
            var alongDistance = MathUtils.Distance(alongLine, raw.xz, out var along);
            var acrossDistance = MathUtils.Distance(acrossLine, raw.xz, out var side);
            if (alongDistance < bestDistance && alongDistance < SnapDistance)
            {
                bestDistance = alongDistance;
                resultDirection = along < 0f ? -axis : axis;
                resultPosition.xz = MathUtils.Position(alongLine, along);
            }
            if (acrossDistance < bestDistance && acrossDistance < SnapDistance)
            {
                bestDistance = acrossDistance;
                resultDirection = side < 0f ? -across : across;
                resultPosition.xz = MathUtils.Position(acrossLine, side);
            }
        }

        private void CollectGuideLines(float3 raw, ref SnapCandidate best)
        {
            SyncSnapAxes();
            if (_points.Count < 2) return;
            var heightData = _terrainSystem.GetHeightData();
            for (var i = 0; i < _points.Count; i++)
            {
                if (i == _dragPoint || _dragPoint < 0 && i == _points.Count - 1)
                    continue;
                var distance = float.MaxValue;
                var position = raw;
                var direction = float2.zero;
                DirectionSnap(ref distance, ref position, ref direction, raw,
                    _points[i], _pointAxes[i]);
                DirectionSnap(ref distance, ref position, ref direction, raw,
                    _points[i], math.normalizesafe(
                        _points[(i + 1) % _points.Count] - _points[i]));
                if (math.lengthsq(direction) < 0.5f) continue;
                position.y = TerrainUtils.SampleHeight(ref heightData,
                    new float3(position.x, raw.y, position.z));
                RegisterSnap(ref best, raw, SnapLevelGuide, SnapKind.Guide,
                    position, direction, true);
            }
        }

        private void SyncSnapAxes()
        {
            while (_pointAxes.Count > _points.Count)
                _pointAxes.RemoveAt(_pointAxes.Count - 1);
            while (_pointAxes.Count < _points.Count) _pointAxes.Add(float2.zero);
        }

        private void StoreSnapAxis(int index)
        {
            SyncSnapAxes();
            if (index >= 0 && index < _pointAxes.Count)
                _pointAxes[index] = _hoverAxis;
        }

        private void InsertSnapAxis(int index)
        {
            // Call after the point was inserted: before synchronization the
            // axis list still represents the old point order.
            var oldCount = _points.Count - 1;
            while (_pointAxes.Count > oldCount)
                _pointAxes.RemoveAt(_pointAxes.Count - 1);
            while (_pointAxes.Count < oldCount) _pointAxes.Add(float2.zero);
            _pointAxes.Insert(math.clamp(index, 0, _pointAxes.Count), float2.zero);
        }

        private void RemoveSnapAxis(int index)
        {
            // Call before or after point removal; remove the matching slot,
            // then let the common length guard repair any legacy mismatch.
            if (index >= 0 && index < _pointAxes.Count)
                _pointAxes.RemoveAt(index);
            SyncSnapAxes();
        }

        private void RestoreSnapAxis(int index, float2 axis)
        {
            SyncSnapAxes();
            if (index >= 0 && index < _pointAxes.Count) _pointAxes[index] = axis;
        }

        private float2 GetSnapAxis(int index)
        {
            SyncSnapAxes();
            return index >= 0 && index < _pointAxes.Count
                ? _pointAxes[index] : float2.zero;
        }
    }
}
