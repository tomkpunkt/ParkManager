using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Reads the game's own validation of the temporary build. CS2 tags a
    /// temporary entity with <see cref="Error"/> when it cannot be applied
    /// (outside the city limits, overlapping, too steep, too expensive, ...)
    /// and shows a notification icon whose prefab names the error type. A
    /// build the game rejected used to end in a timeout without any reason;
    /// mods like Anarchy hide these errors, so it only failed for players
    /// without them. The first error is now kept and named in the failure.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        /// <summary>Frames the game gets to validate new temporary entities
        /// before they are applied.</summary>
        private const int ValidationSettleFrames = 3;

        /// <summary>Error types with their own text in i18n.ts.</summary>
        private static readonly System.Collections.Generic.HashSet<ErrorType>
            TranslatedErrors = new System.Collections.Generic.HashSet<ErrorType>
            {
                ErrorType.ExceedsCityLimits, ErrorType.OverlapExisting,
                ErrorType.NotEnoughMoney, ErrorType.SteepSlope, ErrorType.InWater,
                ErrorType.TightCurve, ErrorType.ShortDistance,
                ErrorType.LongDistance, ErrorType.InvalidShape, ErrorType.SmallArea,
                ErrorType.LowElevation, ErrorType.NotEnoughClearance,
            };

        private EntityQuery _tempErrorQuery;
        private EntityQuery _errorIconQuery;
        private bool _gameErrorSeen;
        private ErrorType _gameErrorType;
        private float3 _gameErrorPosition;
        private int _buildReadyFrame = -1;

        private void InitializeBuildValidation()
        {
            _tempErrorQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Error>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _errorIconQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Icon>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        private void ResetBuildValidation()
        {
            _gameErrorSeen = false;
            _gameErrorType = ErrorType.None;
            _gameErrorPosition = float3.zero;
            _buildReadyFrame = -1;
        }

        /// <summary>
        /// Records the first blocking error the game reported this frame.
        /// Error reports are short-lived, so this runs every waiting frame.
        /// </summary>
        private void CollectGameErrors()
        {
            if (_gameErrorSeen || _errorIconQuery.IsEmptyIgnoreFilter) return;
            using var icons = _errorIconQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < icons.Length; i++)
            {
                var icon = icons[i];
                var prefab = EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab;
                if (!EntityManager.HasComponent<ToolErrorData>(prefab)
                    || !BelongsToTemporaryBuild(icon)) continue;
                _gameErrorSeen = true;
                _gameErrorType = EntityManager.GetComponentData<ToolErrorData>(prefab)
                    .m_Error;
                _gameErrorPosition = EntityManager.GetComponentData<Icon>(icon)
                    .m_Location;
                return;
            }
        }

        /// <summary>Tool error icons sit on the temporary entity they report.
        /// Only this tool creates temporary entities while it builds.</summary>
        private bool BelongsToTemporaryBuild(Entity icon)
        {
            if (EntityManager.HasComponent<Temp>(icon)) return true;
            if (!EntityManager.HasComponent<Owner>(icon)) return false;
            var owner = EntityManager.GetComponentData<Owner>(icon).m_Owner;
            return owner != Entity.Null && EntityManager.Exists(owner)
                && EntityManager.HasComponent<Temp>(owner);
        }

        /// <summary>
        /// True once the temporary build is complete and the game had time to
        /// validate it. Errors are recorded, not acted on: whether CS2 still
        /// applies a marked entity differs between error types, so the build
        /// goes ahead and a failure afterwards is explained with the error.
        /// </summary>
        private bool ValidatedForApply()
        {
            CollectGameErrors();
            var frame = UnityEngine.Time.frameCount;
            if (_buildReadyFrame < 0) _buildReadyFrame = frame;
            if (frame - _buildReadyFrame < ValidationSettleFrames) return false;
            if (_gameErrorSeen || !_tempErrorQuery.IsEmptyIgnoreFilter)
                Mod.Log.Warn("ParkManager applies a build the game marked with an "
                    + $"error ({_gameErrorType} at {_gameErrorPosition}); if parts "
                    + "are missing afterwards, this is the reason.");
            return true;
        }

        /// <summary>Explains a timeout with the game's error, if it had one.</summary>
        private string GameErrorOr(string fallback)
        {
            CollectGameErrors();
            return _gameErrorSeen || !_tempErrorQuery.IsEmptyIgnoreFilter
                ? DescribeGameError() : fallback;
        }

        private string DescribeGameError()
        {
            var type = _gameErrorType;
            var position = _gameErrorPosition;
            var count = 0;
            if (!_tempErrorQuery.IsEmptyIgnoreFilter)
            {
                using var entities = _tempErrorQuery.ToEntityArray(Allocator.Temp);
                count = entities.Length;
                for (var i = 0; i < entities.Length; i++)
                    if (TryGetEntityPosition(entities[i], out var point))
                    {
                        if (_buildIssues.Count < 64) _buildIssues.Add(point);
                        if (!_gameErrorSeen && i == 0) position = point;
                    }
            }
            if (_gameErrorSeen && _buildIssues.Count < 64)
                _buildIssues.Add(_gameErrorPosition);
            var problem = UiText.Of(TranslatedErrors.Contains(type)
                ? "gameError." + type : "gameError.Other", type.ToString());
            Mod.Log.Warn($"ParkManager build rejected by the game: {type} at "
                + $"{position} ({count} temporary entities marked with Error).");
            return UiText.Of("build.gameRejected", problem, position.x, position.z);
        }

        private bool TryGetEntityPosition(Entity entity, out float3 position)
        {
            position = default;
            if (EntityManager.HasComponent<Transform>(entity))
            {
                position = EntityManager.GetComponentData<Transform>(entity).m_Position;
                return true;
            }
            if (EntityManager.HasComponent<Curve>(entity))
            {
                position = Colossal.Mathematics.MathUtils.Position(
                    EntityManager.GetComponentData<Curve>(entity).m_Bezier, 0.5f);
                return true;
            }
            if (EntityManager.HasBuffer<Game.Areas.Node>(entity))
            {
                var nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, true);
                if (nodes.Length == 0) return false;
                position = nodes[0].m_Position;
                return true;
            }
            if (EntityManager.HasComponent<Game.Net.Node>(entity))
            {
                position = EntityManager.GetComponentData<Game.Net.Node>(entity)
                    .m_Position;
                return true;
            }
            return false;
        }
    }
}
