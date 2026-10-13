using System;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.Areas;
using Game.Prefabs;
using ParkManager.Tools;
using ParkManager.Geometry;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace ParkManager.Assets
{
    /// <summary>
    /// Asset roles that ParkManager can resolve from Vanilla and available DLC
    /// prefabs at runtime.
    /// </summary>
    public enum ParkAssetCategory
    {
        Surface,
        Tree,
        Bush,
        Bench,
        Lamp,
        Fence,
        TrashBin,
        PlazaCenter,
        PlazaPlanter,
    }

    /// <summary>
    /// Display name and live prefab entity exposed as one selectable catalog
    /// entry.
    /// </summary>
    internal sealed class ParkAssetChoice
    {
        internal string Name;
        internal string Icon;
        internal Entity Prefab;
    }

    /// <summary>
    /// Coherent set of assets discovered on an existing Vanilla park prefab.
    /// Missing categories may be supplied by the catalog's vetted fallbacks.
    /// </summary>
    internal sealed class VanillaParkPalette
    {
        internal string Name;
        internal readonly Dictionary<ParkAssetCategory, List<ParkAssetChoice>> Choices
            = new Dictionary<ParkAssetCategory, List<ParkAssetChoice>>();

        internal VanillaParkPalette(string name)
        {
            Name = name;
            foreach (ParkAssetCategory category in Enum.GetValues(
                typeof(ParkAssetCategory)))
                Choices[category] = new List<ParkAssetChoice>();
        }
    }

    /// <summary>
    /// Scans the live prefab database, classifies usable park assets and
    /// publishes bounded picker data to the UI. It also reconstructs coherent
    /// palettes from existing Vanilla parks and resolves deterministic variants
    /// for the placement system.
    /// </summary>
    public sealed partial class ParkAssetCatalogSystem : GameSystemBase
    {
        // Keep the icon cycler payload bounded even when prefab-name matching
        // finds thousands of objects.
        private const int MaximumUiOptionsPerCategory = 120;
        // Texture packs easily add more than a hundred ground surfaces; the
        // surface row scrolls, so the picker may offer all of them.
        private const int MaximumUiSurfaces = 600;

        // Composite planters keep their dedicated plaza category even though
        // some of them carry plant metadata.
        private static readonly string[] PlanterNames =
        {
            "planter", "flowerpot", "flower pot", "raisedbed", "raised bed",
            "plantbox", "plant box",
        };

        private PrefabSystem _prefabs;
        private EntityQuery _prefabQuery;
        private bool _scanRequested = true;
        private readonly Dictionary<ParkAssetCategory, List<ParkAssetChoice>> _choices
            = new Dictionary<ParkAssetCategory, List<ParkAssetChoice>>();
        private readonly Dictionary<ParkAssetCategory, string> _selected
            = new Dictionary<ParkAssetCategory, string>();
        private readonly Dictionary<ParkAssetCategory, HashSet<string>> _multiSelected
            = new Dictionary<ParkAssetCategory, HashSet<string>>();
        private readonly List<VanillaParkPalette> _parkPalettes
            = new List<VanillaParkPalette>();

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            foreach (ParkAssetCategory category in Enum.GetValues(typeof(ParkAssetCategory)))
            {
                _choices[category] = new List<ParkAssetChoice>();
                _multiSelected[category] = new HashSet<string>(
                    StringComparer.Ordinal);
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!_scanRequested || _prefabQuery.IsEmptyIgnoreFilter) return;
            _scanRequested = false;
            try
            {
                Scan();
            }
            catch (Exception exception)
            {
                // A failed scan must not leave the panel without any lists
                // until the game restarts: publish what was collected. The
                // next tool activation requests a fresh scan.
                Mod.Log.Error(exception, "ParkManager asset catalog scan failed; "
                    + "publishing the assets found so far.");
                try
                {
                    Publish();
                }
                catch (Exception publishException)
                {
                    Mod.Log.Error(publishException,
                        "ParkManager could not publish the partial asset catalog.");
                }
            }
        }

        internal void RequestRefresh() => _scanRequested = true;

        /// <summary>
        /// Prefab entities and installed asset packs can differ between saves,
        /// so every loaded city gets its own scan.
        /// </summary>
        protected override void OnGameLoadingComplete(
            Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            _scanRequested = true;
        }

        internal bool TryGetSelected(ParkAssetCategory category,
            out Entity prefab, out string name)
        {
            prefab = Entity.Null;
            name = string.Empty;
            if (!_choices.TryGetValue(category, out var choices)
                || choices.Count == 0) return false;
            _selected.TryGetValue(category, out name);
            var choice = FindChoice(choices, name);
            if (choice == null)
            {
                name = ChooseDefault(category, choices);
                choice = FindChoice(choices, name);
            }
            return TryUse(choice, out prefab, out _);
        }

        /// <summary>
        /// Every asset the player chose for a category in the park chooser:
        /// the multi-selection when there is one, otherwise the single
        /// selection or the vetted default.
        /// </summary>
        internal List<(Entity prefab, string name)> GetChosen(ParkAssetCategory category)
        {
            var result = new List<(Entity, string)>();
            if (IsMultiCategory(category)
                && _multiSelected.TryGetValue(category, out var selectedMany)
                && selectedMany.Count > 0
                && _choices.TryGetValue(category, out var choices))
                foreach (var choice in choices)
                    if (selectedMany.Contains(choice.Name)
                        && TryUse(choice, out var prefab, out var name))
                        result.Add((prefab, name));
            if (result.Count == 0 && TryGetSelected(category, out var single, out var singleName))
                result.Add((single, singleName));
            return result;
        }

        /// <summary>
        /// The ground for a plaza's designed green: the grass surface the
        /// park default would pick, independent of the plaza's own surface.
        /// </summary>
        internal bool TryGetGreenSurface(out Entity prefab, out string name)
        {
            prefab = Entity.Null;
            name = string.Empty;
            if (!_choices.TryGetValue(ParkAssetCategory.Surface, out var choices)
                || choices.Count == 0) return false;
            name = ChooseDefault(ParkAssetCategory.Surface, choices);
            return TryUse(FindChoice(choices, name), out prefab, out _);
        }

        /// <summary>Resolves one explicit arrangement asset without falling back
        /// to a different prefab when a selection disappears.</summary>
        internal bool TryGetNamed(ParkAssetCategory category, string name,
            out Entity prefab)
        {
            prefab = Entity.Null;
            return !string.IsNullOrEmpty(name)
                && _choices.TryGetValue(category, out var choices)
                && TryUse(FindChoice(choices, name), out prefab, out _);
        }

        /// <summary>
        /// Compact picker data for the central fountain/statue selector.
        /// Only prefabs with a declared preview are offered to the picker.
        /// </summary>
        internal string GetPlazaCenterOptionsJson(
            IReadOnlyList<float2> polygon = null)
        {
            var builder = new StringBuilder("[");
            if (_choices.TryGetValue(ParkAssetCategory.PlazaCenter,
                out var choices))
            {
                var written = 0;
                for (var i = 0; i < choices.Count
                    && written < MaximumUiOptionsPerCategory; i++)
                {
                    if (!IsUsablePlazaCenter(choices[i], polygon)) continue;
                    if (written++ > 0) builder.Append(',');
                    AppendChoiceJson(builder, choices[i]);
                }
            }
            return builder.Append(']').ToString();
        }

        /// <summary>
        /// The selected fountain/statue name, including the deterministic
        /// default used by TryGetSelected when the user has not picked one.
        /// </summary>
        internal string GetSelectedPlazaCenterName(
            IReadOnlyList<float2> polygon = null)
        {
            TryGetFittingPlazaCenter(polygon, out _, out var name);
            return name;
        }

        internal bool TryGetFittingPlazaCenter(IReadOnlyList<float2> polygon,
            out Entity prefab, out string name)
        {
            prefab = Entity.Null;
            name = string.Empty;
            if (!_choices.TryGetValue(ParkAssetCategory.PlazaCenter,
                out var choices)) return false;
            _selected.TryGetValue(ParkAssetCategory.PlazaCenter,
                out var selectedName);
            if (string.IsNullOrEmpty(selectedName))
                selectedName = ChooseDefault(ParkAssetCategory.PlazaCenter,
                    choices);
            for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                if (pass == 0 && !string.Equals(choice.Name, selectedName,
                    StringComparison.Ordinal)) continue;
                if (pass == 1 && !string.IsNullOrEmpty(selectedName)
                    && string.Equals(choice.Name, selectedName,
                        StringComparison.Ordinal)) continue;
                if (!IsUsablePlazaCenter(choice, polygon)) continue;
                prefab = choice.Prefab;
                name = choice.Name;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Names offered by the UI picker of one category, in picker order.
        /// Plaza centerpieces are limited to those fitting the polygon.
        /// </summary>
        internal List<string> GetUiChoiceNames(ParkAssetCategory category,
            IReadOnlyList<float2> polygon = null)
        {
            var result = new List<string>();
            if (!_choices.TryGetValue(category, out var choices)) return result;
            for (var i = 0; i < choices.Count
                && result.Count < UiLimit(category); i++)
            {
                var usable = category == ParkAssetCategory.PlazaCenter
                    ? IsUsablePlazaCenter(choices[i], polygon)
                    : IsUiChoice(choices[i]);
                if (usable) result.Add(choices[i].Name);
            }
            return result;
        }

        /// <summary>
        /// Applies the assets of a rolled plaza variant with one UI update.
        /// Empty names keep the current selection. Unlike <see cref="Select"/>
        /// this does not refresh the decoration plan; the caller replans.
        /// </summary>
        internal void SelectPlazaVariantAssets(string surface, string fence,
            string center)
        {
            SelectIfAvailable(ParkAssetCategory.Surface, surface);
            SelectIfAvailable(ParkAssetCategory.Fence, fence);
            SelectIfAvailable(ParkAssetCategory.PlazaCenter, center);
            Publish();
        }

        private void SelectIfAvailable(ParkAssetCategory category, string name)
        {
            if (!string.IsNullOrEmpty(name)
                && _choices.TryGetValue(category, out var choices)
                && FindChoice(choices, name) != null) _selected[category] = name;
        }

        private bool IsUsablePlazaCenter(ParkAssetChoice choice,
            IReadOnlyList<float2> polygon)
        {
            if (!IsUiChoice(choice) || choice.Prefab == Entity.Null
                || !EntityManager.Exists(choice.Prefab)) return false;
            if (polygon == null || polygon.Count < 3) return true;
            return TryGetPlanarRadius(choice.Prefab, out var radius)
                && PlazaPlanner.CanFitCenterpiece(polygon, radius);
        }

        internal bool TryGetParkVariant(ParkAssetCategory category, int seed,
            uint selector, out Entity prefab, out string name)
        {
            if (IsMultiCategory(category)
                && _multiSelected.TryGetValue(category, out var selectedMany)
                && selectedMany.Count > 0
                && _choices.TryGetValue(category, out var multiChoices))
            {
                var picked = multiChoices.FindAll(
                    choice => selectedMany.Contains(choice.Name));
                if (picked.Count > 0)
                    return TryUse(picked[(int)(selector % (uint)picked.Count)],
                        out prefab, out name);
            }
            if (_selected.TryGetValue(category, out var selected)
                && !string.IsNullOrEmpty(selected)
                && TryGetSelected(category, out prefab, out name)) return true;
            if (_parkPalettes.Count > 0)
            {
                var choices = ResolveParkPalette(seed).Choices[category];
                if (choices.Count > 0)
                    return TryUse(choices[(int)(selector % (uint)choices.Count)],
                        out prefab, out name);
            }

            // A park may legitimately have no fence or furniture of one kind.
            // In that case use the vetted global default, never the old broad
            // random pool.
            return TryGetSelected(category, out prefab, out name);
        }

        /// <summary>
        /// Returns the longitudinal mesh bounds used by object line tools to
        /// place fence pieces continuously end-to-end. The Z axis is the
        /// prefab's forward axis and therefore the relevant fence length.
        /// </summary>
        internal bool TryGetLongitudinalBounds(Entity prefab,
            out float minimum, out float maximum)
        {
            minimum = 0f;
            maximum = 0f;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)
                || !_prefabs.TryGetPrefab<PrefabBase>(prefab, out var value)
                || !(value is ObjectGeometryPrefab geometry)
                || geometry.m_Meshes == null || geometry.m_Meshes.Length == 0)
                return false;

            for (var i = 0; i < geometry.m_Meshes.Length; i++)
            {
                if (!(geometry.m_Meshes[i].m_Mesh is RenderPrefab render))
                    continue;
                minimum = Math.Min(minimum, render.bounds.z.min);
                maximum = Math.Max(maximum, render.bounds.z.max);
            }
            return maximum - minimum > 0.1f;
        }

        /// <summary>
        /// Measures how far a path-side object extends from its pivot toward
        /// either side of the path. Benches and lamps receive a quarter-turn
        /// during placement, so their local Z bounds become the path-normal
        /// footprint used by the override collision system.
        /// </summary>
        internal bool TryGetPathNormalRadius(Entity prefab, out float radius)
        {
            radius = 0f;
            if (!TryGetObjectBounds(prefab, out var bounds)) return false;
            radius = Math.Max(Math.Abs(bounds.min.z), Math.Abs(bounds.max.z));
            return radius > 0.01f && math.isfinite(radius);
        }

        /// <summary>Maximum horizontal extent of a plaza centerpiece mesh.</summary>
        internal bool TryGetPlanarRadius(Entity prefab, out float radius)
        {
            radius = 0f;
            if (!TryGetObjectBounds(prefab, out var bounds)) return false;
            radius = Math.Max(
                Math.Max(Math.Abs(bounds.min.x), Math.Abs(bounds.max.x)),
                Math.Max(Math.Abs(bounds.min.z), Math.Abs(bounds.max.z)));
            return radius > 0.01f && math.isfinite(radius);
        }

        private bool TryGetObjectBounds(Entity prefab,
            out Colossal.Mathematics.Bounds3 bounds)
        {
            bounds = default;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<ObjectGeometryData>(prefab))
                return false;
            bounds = EntityManager.GetComponentData<ObjectGeometryData>(prefab)
                .m_Bounds;
            return true;
        }

        /// <summary>
        /// True for the game's spline-based fence prefabs. These use a
        /// <see cref="Game.Tools.NetCourse"/> and let the net renderer repeat
        /// the mesh along one edge instead of creating a row of object props.
        /// </summary>
        internal bool IsNetworkFence(Entity prefab)
            => prefab != Entity.Null && EntityManager.Exists(prefab)
                && (EntityManager.HasComponent<FenceData>(prefab)
                    || EntityManager.HasComponent<NetFenceData>(prefab));

        private bool HasNetworkFence()
            => _choices[ParkAssetCategory.Fence]
                .Exists(choice => IsNetworkFence(choice.Prefab));

        internal string GetParkPaletteName(int seed)
        {
            foreach (var selected in _multiSelected.Values)
                if (selected.Count > 0) return UiText.Of("palette.customMix");
            foreach (var selected in _selected.Values)
                if (!string.IsNullOrEmpty(selected))
                    return UiText.Of("palette.customSelection");
            if (_parkPalettes.Count == 0) return UiText.Of("palette.curatedFallback");
            return ResolveParkPalette(seed).Name;
        }

        /// <summary>Each park seed draws one Vanilla park style.</summary>
        private VanillaParkPalette ResolveParkPalette(int seed)
            => _parkPalettes[(int)(unchecked((uint)seed) % (uint)_parkPalettes.Count)];

        internal void Select(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            var split = payload.IndexOf('\n');
            if (split <= 0
                || !Enum.TryParse(payload.Substring(0, split), true,
                    out ParkAssetCategory category)) return;
            var remainder = payload.Substring(split + 1);
            var modeSplit = remainder.IndexOf('\n');
            var mode = modeSplit >= 0 ? remainder.Substring(0, modeSplit) : "single";
            var name = modeSplit >= 0 ? remainder.Substring(modeSplit + 1) : remainder;
            if (!_choices.TryGetValue(category, out var choices)) return;
            var known = !string.IsNullOrEmpty(name) && FindChoice(choices, name) != null;
            if (string.Equals(mode, "multi", StringComparison.OrdinalIgnoreCase)
                && IsMultiCategory(category))
            {
                // An empty name clears the mix; a known name toggles it.
                var selectedMany = _multiSelected[category];
                if (string.IsNullOrEmpty(name)) selectedMany.Clear();
                else if (!known) return;
                else if (!selectedMany.Add(name)) selectedMany.Remove(name);
                _selected[category] = string.Empty;
            }
            else if (string.IsNullOrEmpty(name) || known)
                _selected[category] = name;
            else return;
            Publish();
            Tool.RefreshDecorationPlan();
        }

        private void Scan()
        {
            foreach (var list in _choices.Values) list.Clear();
            using var entities = _prefabQuery.ToEntityArray(Allocator.Temp);
            var failed = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                // One broken prefab (a half-loaded asset pack, a missing DLC
                // reference) must not empty every list in the panel.
                try
                {
                    if (_prefabs.TryGetPrefab<PrefabBase>(entity, out var prefab)
                        && prefab != null && !string.IsNullOrWhiteSpace(prefab.name)
                        && TryClassifyParkAsset(entity, prefab, out var category))
                        Add(category, prefab, entity);
                }
                catch (Exception exception)
                {
                    if (failed++ < 5)
                        Mod.Log.Warn($"ParkManager skipped prefab {entity} "
                            + $"while scanning assets: {exception}");
                }
            }
            if (failed > 0)
                Mod.Log.Warn($"ParkManager skipped {failed} prefabs that could "
                    + "not be classified.");

            // Resolve park variants before choosing the supported fence pool.
            // Palettes are optional: without them the curated fallback is used.
            try
            {
                ScanVanillaParkPalettes(entities);
            }
            catch (Exception exception)
            {
                _parkPalettes.Clear();
                Mod.Log.Warn("ParkManager could not read the Vanilla park "
                    + $"palettes; using the curated fallback: {exception}");
            }

            // Prefer the native continuous fence system whenever the current
            // game/DLC set exposes it. Prop pieces remain a compatibility
            // fallback for installations without a network fence prefab.
            if (HasNetworkFence())
                _choices[ParkAssetCategory.Fence]
                    .RemoveAll(choice => !IsNetworkFence(choice.Prefab));

            foreach (var pair in _choices)
            {
                SortAndDeduplicate(pair.Value);
                if (!_selected.TryGetValue(pair.Key, out var selected)
                    || pair.Value.FindIndex(c => c.Name == selected
                        && IsUiChoice(c)) < 0)
                    _selected[pair.Key] = string.Empty;
                if (_multiSelected.TryGetValue(pair.Key, out var selectedMany))
                    selectedMany.RemoveWhere(name => pair.Value.FindIndex(choice =>
                        string.Equals(choice.Name, name,
                            StringComparison.Ordinal) && IsUiChoice(choice)) < 0);
            }

            // Every park needs a ground surface. Preselect the vetted default
            // so the workflow never waits for a choice the player cannot see.
            var surfaces = _choices[ParkAssetCategory.Surface];
            if (string.IsNullOrEmpty(_selected[ParkAssetCategory.Surface])
                && surfaces.Count > 0)
                _selected[ParkAssetCategory.Surface] =
                    ChooseDefault(ParkAssetCategory.Surface, surfaces);

            var summary = new StringBuilder();
            foreach (var pair in _choices)
                summary.Append(pair.Key).Append(' ').Append(pair.Value.Count)
                    .Append(" · ");
            Mod.Log.Info($"ParkManager {Mod.Version} asset catalog: {summary}"
                + $"park palettes {_parkPalettes.Count}");
            // Surface reports ("my texture pack is missing") need the names.
            var surfaceNames = new StringBuilder();
            for (var i = 0; i < surfaces.Count; i++)
                surfaceNames.Append(i == 0 ? "" : ", ").Append(surfaces[i].Name)
                    .Append(string.IsNullOrWhiteSpace(surfaces[i].Icon) ? " (no icon)" : "");
            Mod.Log.Info($"ParkManager surfaces ({surfaces.Count}): {surfaceNames}");
            foreach (var pair in _selected)
                Mod.Log.Info($"ParkManager default {pair.Key}: "
                    + (string.IsNullOrEmpty(pair.Value) ? "(none)" : pair.Value));
            Publish();
        }

        private bool IsVisibleObjectPrefab(Entity entity, PrefabBase prefab)
        {
            if (!(prefab is ObjectGeometryPrefab geometryPrefab)
                || geometryPrefab.m_Meshes == null
                || geometryPrefab.m_Meshes.Length == 0) return false;
            return EntityManager.HasComponent<ObjectData>(entity)
                && EntityManager.HasComponent<ObjectGeometryData>(entity)
                && !EntityManager.HasComponent<PlaceholderObjectData>(entity)
                && !EntityManager.HasBuffer<PlaceholderObjectElement>(entity);
        }

        private bool IsPlazaCenterPrefab(Entity entity, PrefabBase prefab,
            string lowerName)
        {
            // Center pieces must be real, renderable object prefabs with a UI
            // icon. Name matching alone is too broad: the game also contains
            // fountain effects, plant assets and bundled prop variants.
            if (!(prefab is StaticObjectPrefab)
                || !IsVisibleObjectPrefab(entity, prefab)
                // Buildings may demand a road connection even when their
                // display name suggests a decorative statue or fountain.
                || prefab is BuildingPrefab
                || EntityManager.HasComponent<BuildingData>(entity)
                || EntityManager.HasComponent<PlantData>(entity)
                // Parent geometry bounds do not include independently
                // positioned subobjects; without a combined footprint their
                // preview could claim a much smaller size than the result.
                || prefab.TryGet<ObjectSubObjects>(out var subObjects)
                    && subObjects?.m_SubObjects != null
                    && subObjects.m_SubObjects.Length > 0
                || ContainsAny(lowerName, PlanterNames)
                || ContainsAny(lowerName, "placeholder", "random", "source",
                    "effect", "particle", "spray", "splash", "decal", "bench", "seat",
                    "lamp", "light", "trash", "bin", "fence", "railing",
                    "sign", "poster", "icon", "shadow", "broken", "ruin"))
                return false;

            var isFountain = lowerName.Contains("fountain");
            var isStatue = lowerName.Contains("statue");
            return (isFountain || isStatue)
                && !string.IsNullOrWhiteSpace(GetIcon(prefab));
        }

        private void ScanVanillaParkPalettes(NativeArray<Entity> prefabs)
        {
            _parkPalettes.Clear();
            for (var i = 0; i < prefabs.Length; i++)
            {
                var entity = prefabs[i];
                if (!EntityManager.HasComponent<ParkData>(entity)
                    || !_prefabs.TryGetPrefab<PrefabBase>(entity, out var park)
                    || park == null) continue;
                var palette = new VanillaParkPalette(park.name);
                var visited = new HashSet<Entity>();
                CollectManagedSubObjects(park, palette, visited, 0);
                foreach (var pair in palette.Choices)
                    SortAndDeduplicate(pair.Value);
                if (HasNetworkFence())
                    palette.Choices[ParkAssetCategory.Fence]
                        .RemoveAll(choice => !IsNetworkFence(choice.Prefab));

                // A usable style must at least describe its vegetation. Empty
                // furniture layers can safely fall back to vetted defaults.
                if (palette.Choices[ParkAssetCategory.Tree].Count == 0
                    && palette.Choices[ParkAssetCategory.Bush].Count == 0)
                    continue;
                _parkPalettes.Add(palette);
            }
            _parkPalettes.Sort((a, b) => StringComparer.OrdinalIgnoreCase
                .Compare(a.Name, b.Name));
            Mod.Log.Info($"ParkManager discovered {_parkPalettes.Count} "
                + "Vanilla park palettes from ObjectSubObjects.");
        }

        private void CollectManagedSubObjects(PrefabBase parent,
            VanillaParkPalette palette, HashSet<Entity> visited, int depth)
        {
            if (parent == null || depth > 8
                || !parent.TryGet<ObjectSubObjects>(out var component)
                || component?.m_SubObjects == null) return;
            for (var i = 0; i < component.m_SubObjects.Length; i++)
            {
                var child = component.m_SubObjects[i]?.m_Object;
                // GetEntity throws for prefabs the game never registered,
                // e.g. sub-objects of content that is not installed.
                if (child == null
                    || !_prefabs.TryGetEntity(child, out var childEntity)) continue;
                CollectPaletteObject(childEntity, palette, visited, depth + 1);
            }
        }

        private void CollectPaletteObject(Entity entity,
            VanillaParkPalette palette, HashSet<Entity> visited, int depth)
        {
            if (entity == Entity.Null || depth > 8 || !visited.Add(entity)
                || !EntityManager.Exists(entity)) return;

            if (EntityManager.HasBuffer<PlaceholderObjectElement>(entity))
            {
                var variants = EntityManager.GetBuffer<PlaceholderObjectElement>(
                    entity, true);
                for (var i = 0; i < variants.Length; i++)
                    CollectPaletteObject(variants[i].m_Object, palette, visited,
                        depth + 1);
            }

            if (!_prefabs.TryGetPrefab<PrefabBase>(entity, out var prefab)
                || prefab == null || string.IsNullOrWhiteSpace(prefab.name)) return;
            if (TryClassifyParkAsset(entity, prefab, out var category))
            {
                var choice = Add(category, prefab, entity);
                if (choice != null) palette.Choices[category].Add(choice);
            }
            CollectManagedSubObjects(prefab, palette, visited, depth);
        }

        private bool TryClassifyParkAsset(Entity entity, PrefabBase prefab,
            out ParkAssetCategory category)
        {
            category = ParkAssetCategory.Surface;
            // Reject placeholders before the surface and metadata fast paths.
            // Otherwise surfaces return before the object-name filters below.
            if (prefab == null || string.IsNullOrWhiteSpace(prefab.name)
                || prefab.name.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            // The area type alone identifies a ground surface; asset-pack
            // surfaces do not always carry the Vanilla SurfaceData component.
            if (EntityManager.HasComponent<AreaGeometryData>(entity)
                && EntityManager.GetComponentData<AreaGeometryData>(entity).m_Type == AreaType.Surface)
                return true;
            if (prefab is NetGeometryPrefab && IsNetworkFence(entity))
            {
                category = ParkAssetCategory.Fence;
                return true;
            }
            if (TryClassifyMetadata(entity, prefab, out category)) return true;
            if (prefab is BuildingPrefab
                || EntityManager.HasComponent<BuildingData>(entity)) return false;
            if (!(prefab is StaticObjectPrefab) || !IsVisibleObjectPrefab(entity, prefab)) return false;
            var lower = prefab.name.ToLowerInvariant();
            if (ContainsAny(lower, "placeholder", "random", "source"))
                return false;
            if (IsBenchAssetName(lower))
                category = ParkAssetCategory.Bench;
            else if (ContainsAny(lower, "lamp", "light", "lantern")) category = ParkAssetCategory.Lamp;
            else if (ContainsAny(lower, "fence", "railing", "hedge", "barrier"))
                category = ParkAssetCategory.Fence;
            else if (ContainsAny(lower, "trashbin", "trash bin", "trashcan",
                "trash can", "wastebin", "waste bin", "garbagebin",
                "garbage bin", "litterbin", "litter bin"))
                category = ParkAssetCategory.TrashBin;
            else if (ContainsAny(lower, PlanterNames)
                || ContainsAny(lower, "flowerbed", "flower bed"))
                category = ParkAssetCategory.PlazaPlanter;
            else if (IsPlazaCenterPrefab(entity, prefab, lower))
                category = ParkAssetCategory.PlazaCenter;
            else return false;
            return true;
        }

        /// <summary>Uses gameplay semantics before falling back to asset names.
        /// Activity locations identify sitting props without depending on the
        /// language or naming convention of a DLC or custom asset.</summary>
        private bool TryClassifyMetadata(Entity entity, PrefabBase prefab,
            out ParkAssetCategory category)
        {
            category = ParkAssetCategory.Surface;
            if (!(prefab is StaticObjectPrefab)
                || prefab is BuildingPrefab
                || EntityManager.HasComponent<BuildingData>(entity)
                || !IsVisibleObjectPrefab(entity, prefab)) return false;
            if (EntityManager.HasComponent<PlantData>(entity))
            {
                var lower = prefab.name.ToLowerInvariant();
                if (ContainsAny(lower, PlanterNames))
                    category = ParkAssetCategory.PlazaPlanter;
                else if (EntityManager.HasComponent<TreeData>(entity))
                {
                    if (ContainsAny(lower, "stump", "dead")) return false;
                    category = ParkAssetCategory.Tree;
                }
                else category = ParkAssetCategory.Bush;
                return true;
            }
            var sittingMask = new ActivityMask(ActivityType.BenchSitting).m_Mask;
            if (EntityManager.HasBuffer<ActivityLocationElement>(entity))
            {
                var locations = EntityManager.GetBuffer<ActivityLocationElement>(entity, true);
                for (var i = 0; i < locations.Length; i++)
                    if ((locations[i].m_ActivityMask.m_Mask & sittingMask) != 0)
                    {
                        category = ParkAssetCategory.Bench;
                        return true;
                    }
            }
            // Managed metadata is also available before the ECS activity buffer
            // has been initialized by the game's prefab pipeline.
            if (prefab.TryGet<Game.Prefabs.ActivityLocation>(out var activity)
                && activity?.m_Locations != null)
                foreach (var location in activity.m_Locations)
                {
                    var types = location?.m_Activity?.m_Activities;
                    if (types != null && Array.IndexOf(types, ActivityType.BenchSitting) >= 0)
                    {
                        category = ParkAssetCategory.Bench;
                        return true;
                    }
                }
            if (EntityManager.HasComponent<StreetLightData>(entity))
            {
                category = ParkAssetCategory.Lamp;
                return true;
            }
            return false;
        }

        private static void SortAndDeduplicate(List<ParkAssetChoice> choices)
        {
            choices.Sort((a, b) => StringComparer.OrdinalIgnoreCase
                .Compare(a.Name, b.Name));
            for (var i = choices.Count - 1; i > 0; i--)
                if (string.Equals(choices[i].Name, choices[i - 1].Name,
                    StringComparison.OrdinalIgnoreCase)) choices.RemoveAt(i);
        }

        private static bool IsBenchAssetName(string lower)
        {
            // "bank" alone also matches advertised financial brands; those
            // A-stands previously appeared as benches in both choosers.
            if (!ContainsAny(lower, "bench", "seat", "parkbank",
                    "gardenbank", "sitzbank", "streetbank")) return false;
            return !ContainsAny(lower, "advert", "billboard", "poster",
                "banner", "display", "sign", "adstand", "astand",
                "commercial", "logo", "screen", "adboard");
        }

        /// <summary>
        /// Adds a classified prefab to the catalog. Bushes double as plaza
        /// planters. Objects without a preview are skipped here, in the source
        /// catalog, so automatic placement cannot select assets the manual
        /// picker would hide. Surfaces are kept without one: their previews
        /// often come from Asset Icon Library, and a missing or late icon must
        /// not leave a park without any surface to build.
        /// </summary>
        private ParkAssetChoice Add(ParkAssetCategory category, PrefabBase prefab,
            Entity entity)
        {
            var icon = GetIcon(prefab);
            if (string.IsNullOrWhiteSpace(icon)
                && category != ParkAssetCategory.Surface) return null;
            var choice = new ParkAssetChoice
            {
                Name = prefab.name,
                Icon = icon,
                Prefab = entity,
            };
            _choices[category].Add(choice);
            if (category == ParkAssetCategory.Bush)
                _choices[ParkAssetCategory.PlazaPlanter].Add(choice);
            return choice;
        }

        private static string GetIcon(PrefabBase prefab)
        {
            if (prefab == null) return string.Empty;
            if (prefab.TryGet<UIObject>(out var ui) && ui != null
                && !string.IsNullOrWhiteSpace(ui.m_Icon)) return ui.m_Icon;
            return prefab.thumbnailUrl ?? string.Empty;
        }

        private static string ChooseDefault(ParkAssetCategory category,
            List<ParkAssetChoice> choices)
        {
            if (choices.Count == 0) return string.Empty;
            var best = choices[0].Name;
            var bestScore = int.MinValue;
            for (var i = 0; i < choices.Count; i++)
            {
                var lower = choices[i].Name.ToLowerInvariant();
                var score = 0;
                if (category == ParkAssetCategory.Surface)
                {
                    if (lower.Contains("grass")) score += 100;
                    if (lower.Contains("01")) score += 10;
                    if (lower.Contains("pavement") || lower.Contains("sand")) score -= 30;
                }
                if (category == ParkAssetCategory.Tree)
                {
                    if (lower.Contains("deciduous") || lower.Contains("oak")) score += 40;
                    if (lower.Contains("dead") || lower.Contains("stump")) score -= 100;
                }
                if (category == ParkAssetCategory.Bush && lower.Contains("bush")) score += 50;
                if (category == ParkAssetCategory.Bench)
                {
                    if (lower.Contains("bench")) score += 50;
                    if (lower == "gardenbench01") score += 1000;
                    else if (lower.StartsWith("gardenbench")) score += 200;
                }
                if (category == ParkAssetCategory.Lamp)
                {
                    if (lower.Contains("park")) score += 30;
                    if (lower == "lightpolepark01") score += 1000;
                    else if (lower.StartsWith("lightpolepark")) score += 200;
                }
                if (category == ParkAssetCategory.Fence)
                {
                    if (lower.Contains("fence")) score += 50;
                    if (lower == "fenceresidentialpiecelow01") score += 1000;
                    else if (lower.Contains("residentialpiecelow")) score += 250;
                    else if (lower.Contains("piece")) score += 100;
                }
                if (category == ParkAssetCategory.TrashBin)
                {
                    if (lower.Contains("trash") || lower.Contains("waste")) score += 50;
                    if (lower.Contains("park")) score += 25;
                }
                if (category == ParkAssetCategory.PlazaCenter)
                {
                    if (lower.Contains("fountain")) score += 100;
                    if (lower.Contains("statue")) score += 80;
                    if (lower.Contains("plaza") || lower.Contains("central")) score += 30;
                }
                if (lower.Contains("placeholder") || lower.Contains("invisible")) score -= 200;
                if (score <= bestScore) continue;
                bestScore = score;
                best = choices[i].Name;
            }
            return best;
        }

        private string BuildOptionsJson()
        {
            var builder = new StringBuilder("{");
            var firstCategory = true;
            foreach (ParkAssetCategory category in Enum.GetValues(typeof(ParkAssetCategory)))
            {
                if (!firstCategory) builder.Append(',');
                firstCategory = false;
                var key = category.ToString().ToLowerInvariant();
                builder.Append('"').Append(key).Append("\":{\"selected\":");
                Json.AppendString(builder, _selected.TryGetValue(category,
                    out var value) ? value : string.Empty);
                builder.Append(",\"selectedMany\":[");
                var selectedMany = _multiSelected[category];
                var choices = _choices[category];
                var written = 0;
                for (var i = 0; i < choices.Count; i++)
                {
                    if (!selectedMany.Contains(choices[i].Name)) continue;
                    if (written++ > 0) builder.Append(',');
                    Json.AppendString(builder, choices[i].Name);
                }
                builder.Append("],\"options\":[");
                written = 0;

                // The catalog order is stable. Selection is represented only
                // by selected/selectedMany and never moves a tile in the UI.
                for (var i = 0; i < choices.Count
                    && written < UiLimit(category); i++)
                {
                    if (!IsUiChoice(choices[i])) continue;
                    if (written > 0) builder.Append(',');
                    AppendChoiceJson(builder, choices[i]);
                    written++;
                }
                builder.Append("]}");
            }
            return builder.Append('}').ToString();
        }

        private static int UiLimit(ParkAssetCategory category)
            => category == ParkAssetCategory.Surface
                ? MaximumUiSurfaces : MaximumUiOptionsPerCategory;

        private static bool IsMultiCategory(ParkAssetCategory category)
            => category == ParkAssetCategory.Tree
                || category == ParkAssetCategory.Bush
                || category == ParkAssetCategory.PlazaPlanter;

        private static void AppendChoiceJson(StringBuilder builder,
            ParkAssetChoice choice)
        {
            builder.Append("{\"name\":");
            Json.AppendString(builder, choice.Name).Append(",\"icon\":");
            Json.AppendString(builder, choice.Icon).Append('}');
        }

        /// <summary>Every catalog entry is offered; <see cref="Add"/> already
        /// dropped objects without a preview, and the UI draws a letter tile
        /// for surfaces that have none.</summary>
        private static bool IsUiChoice(ParkAssetChoice choice)
            => choice != null && !string.IsNullOrWhiteSpace(choice.Name);

        private void Publish()
        {
            World.GetOrCreateSystemManaged<ParkManagerUISystem>()
                .SetAssetOptions(BuildOptionsJson());
            // The tool filters centerpieces by the current outline.
            Tool.RefreshPlazaCenterChoices(true);
        }

        private ParkToolSystem Tool => World.GetOrCreateSystemManaged<ParkToolSystem>();

        private static ParkAssetChoice FindChoice(List<ParkAssetChoice> choices,
            string name)
            => choices.Find(choice => string.Equals(choice.Name, name,
                StringComparison.Ordinal));

        /// <summary>Resolves a catalog entry to its live prefab entity.</summary>
        private bool TryUse(ParkAssetChoice choice, out Entity prefab,
            out string name)
        {
            prefab = choice?.Prefab ?? Entity.Null;
            name = choice?.Name ?? string.Empty;
            return prefab != Entity.Null && EntityManager.Exists(prefab);
        }

        private static bool ContainsAny(string value, params string[] parts)
        {
            for (var i = 0; i < parts.Length; i++)
                if (value.Contains(parts[i])) return true;
            return false;
        }
    }
}
