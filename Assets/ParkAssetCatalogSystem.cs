using System;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.Areas;
using Game.Prefabs;
using Game.Rendering;
using ParkManager.Tools;
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
        private const int MaximumParkPaletteOptions = 80;

        private PrefabSystem _prefabs;
        private EntityQuery _prefabQuery;
        private bool _scanRequested = true;
        private bool _ready;
        private string _summary = "Assetkatalog wird aufgebaut …";
        private string _optionsJson = "{}";
        private string _parkPaletteOptionsJson = "[]";
        private string _selectedParkPalette = string.Empty;
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
            Scan();
        }

        internal void RequestRefresh() => _scanRequested = true;

        internal bool TryGetSelected(ParkAssetCategory category,
            out Entity prefab, out string name)
        {
            prefab = Entity.Null;
            name = string.Empty;
            if (!_choices.TryGetValue(category, out var choices)
                || choices.Count == 0) return false;
            _selected.TryGetValue(category, out name);
            for (var i = 0; i < choices.Count; i++)
                if (string.Equals(choices[i].Name, name, StringComparison.Ordinal))
                {
                    prefab = choices[i].Prefab;
                    return prefab != Entity.Null && EntityManager.Exists(prefab);
                }
            name = ChooseDefault(category, choices);
            for (var i = 0; i < choices.Count; i++)
                if (string.Equals(choices[i].Name, name, StringComparison.Ordinal))
                {
                    prefab = choices[i].Prefab;
                    return prefab != Entity.Null && EntityManager.Exists(prefab);
                }
            return false;
        }

        internal bool TryGetVariant(ParkAssetCategory category, uint selector,
            out Entity prefab, out string name)
        {
            prefab = Entity.Null;
            name = string.Empty;
            if (!_choices.TryGetValue(category, out var choices)
                || choices.Count == 0) return false;
            var choice = choices[(int)(selector % (uint)choices.Count)];
            prefab = choice.Prefab;
            name = choice.Name;
            return prefab != Entity.Null && EntityManager.Exists(prefab);
        }

        internal bool TryGetParkVariant(ParkAssetCategory category, int seed,
            uint selector, out Entity prefab, out string name)
        {
            prefab = Entity.Null;
            name = string.Empty;
            if (IsMultiCategory(category)
                && _multiSelected.TryGetValue(category, out var selectedMany)
                && selectedMany.Count > 0
                && _choices.TryGetValue(category, out var multiChoices))
            {
                var count = 0;
                for (var i = 0; i < multiChoices.Count; i++)
                    if (selectedMany.Contains(multiChoices[i].Name)) count++;
                if (count > 0)
                {
                    var target = (int)(selector % (uint)count);
                    for (var i = 0; i < multiChoices.Count; i++)
                    {
                        var choice = multiChoices[i];
                        if (!selectedMany.Contains(choice.Name)) continue;
                        if (target-- > 0) continue;
                        prefab = choice.Prefab;
                        name = choice.Name;
                        return prefab != Entity.Null && EntityManager.Exists(prefab);
                    }
                }
            }
            if (_selected.TryGetValue(category, out var selected)
                && !string.IsNullOrEmpty(selected)
                && TryGetSelected(category, out prefab, out name)) return true;
            if (_parkPalettes.Count > 0)
            {
                var palette = ResolveParkPalette(seed);
                var choices = palette.Choices[category];
                if (choices.Count > 0)
                {
                    var choice = choices[(int)(selector % (uint)choices.Count)];
                    prefab = choice.Prefab;
                    name = choice.Name;
                    return prefab != Entity.Null && EntityManager.Exists(prefab);
                }
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
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<ObjectGeometryData>(prefab))
                return false;
            var bounds = EntityManager.GetComponentData<ObjectGeometryData>(prefab)
                .m_Bounds;
            radius = Math.Max(Math.Abs(bounds.min.z), Math.Abs(bounds.max.z));
            return radius > 0.01f && !float.IsNaN(radius)
                && !float.IsInfinity(radius);
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

        internal string GetParkPaletteName(int seed)
        {
            foreach (var selected in _multiSelected.Values)
                if (selected.Count > 0) return "individuelle Mischung";
            foreach (var selected in _selected.Values)
                if (!string.IsNullOrEmpty(selected)) return "individuelle Auswahl";
            if (_parkPalettes.Count == 0) return "kuratierter Fallback";
            return ResolveParkPalette(seed).Name;
        }

        internal void SelectParkPalette(string name)
        {
            name = name ?? string.Empty;
            if (!string.IsNullOrEmpty(name)
                && _parkPalettes.FindIndex(p => string.Equals(p.Name, name,
                    StringComparison.Ordinal)) < 0) return;
            _selectedParkPalette = name;
            Publish();
            World.GetOrCreateSystemManaged<ParkToolSystem>()
                .RefreshDecorationPlan();
        }

        private VanillaParkPalette ResolveParkPalette(int seed)
        {
            if (!string.IsNullOrEmpty(_selectedParkPalette))
            {
                var selected = _parkPalettes.Find(p => string.Equals(p.Name,
                    _selectedParkPalette, StringComparison.Ordinal));
                if (selected != null) return selected;
            }
            var index = (int)(unchecked((uint)seed) % (uint)_parkPalettes.Count);
            return _parkPalettes[index];
        }

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
            if (string.Equals(mode, "multi", StringComparison.OrdinalIgnoreCase)
                && IsMultiCategory(category))
            {
                var selectedMany = _multiSelected[category];
                if (string.IsNullOrEmpty(name)) selectedMany.Clear();
                else if (choices.FindIndex(choice => string.Equals(choice.Name,
                    name, StringComparison.Ordinal)) >= 0)
                {
                    if (!selectedMany.Add(name)) selectedMany.Remove(name);
                }
                else return;
                _selected[category] = string.Empty;
                Publish();
                World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .RefreshDecorationPlan();
                return;
            }
            if (string.IsNullOrEmpty(name))
            {
                _selected[category] = string.Empty;
                Publish();
                World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .RefreshDecorationPlan();
                return;
            }
            for (var i = 0; i < choices.Count; i++)
                if (string.Equals(choices[i].Name, name, StringComparison.Ordinal))
                {
                    _selected[category] = name;
                    Publish();
                    World.GetOrCreateSystemManaged<ParkToolSystem>()
                        .RefreshDecorationPlan();
                    return;
                }
        }

        private void Scan()
        {
            foreach (var list in _choices.Values) list.Clear();
            using var entities = _prefabQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (!_prefabs.TryGetPrefab<PrefabBase>(entity, out var prefab)
                    || prefab == null || !prefab.isBuiltin
                    || string.IsNullOrWhiteSpace(prefab.name)) continue;
                var name = prefab.name;

                if (EntityManager.HasComponent<AreaGeometryData>(entity)
                    && EntityManager.HasComponent<SurfaceData>(entity)
                    && EntityManager.GetComponentData<AreaGeometryData>(entity).m_Type
                        == AreaType.Surface
                    && prefab.TryGet<UIObject>(out var surfaceUi)
                    && surfaceUi != null
                    && !string.IsNullOrWhiteSpace(surfaceUi.m_Icon))
                    Add(ParkAssetCategory.Surface, name, entity);

                if (EntityManager.HasComponent<PlantData>(entity)
                    && EntityManager.HasComponent<ObjectGeometryData>(entity))
                {
                    var lowerPlant = name.ToLowerInvariant();
                    if (IsVisibleObjectPrefab(entity, prefab)
                        && !ContainsAny(lowerPlant, "placeholder", "stump", "dead",
                            "planter", "flowerpot", "flower pot", "raisedbed",
                            "raised bed", "plantbox", "plant box"))
                        Add(EntityManager.HasComponent<TreeData>(entity)
                            ? ParkAssetCategory.Tree : ParkAssetCategory.Bush,
                            name, entity);
                }

                // Industrial chain-link and similar fences are real network
                // prefabs. They must be collected before the StaticObjectPrefab
                // filter or the chooser only sees barriers and loose fence props.
                if (prefab is NetGeometryPrefab && IsNetworkFence(entity))
                {
                    Add(ParkAssetCategory.Fence, name, entity);
                    continue;
                }

                if (!(prefab is StaticObjectPrefab)) continue;
                var lower = name.ToLowerInvariant();
                if (!IsVisibleObjectPrefab(entity, prefab)
                    || ContainsAny(lower, "placeholder", "random", "source"))
                    continue;
                if (ContainsAny(lower, "bench", "seat", "bank"))
                    Add(ParkAssetCategory.Bench, name, entity);
                if (ContainsAny(lower, "lamp", "light", "lantern"))
                    Add(ParkAssetCategory.Lamp, name, entity);
                if (ContainsAny(lower, "fence", "hedge", "railing", "barrier"))
                    Add(ParkAssetCategory.Fence, name, entity);
                if (ContainsAny(lower, "trashbin", "trash bin", "trashcan",
                    "trash can", "wastebin", "waste bin", "garbagebin",
                    "garbage bin", "litterbin", "litter bin"))
                    Add(ParkAssetCategory.TrashBin, name, entity);
            }


            // Prefer the native continuous fence system whenever the current
            // game/DLC set exposes it. Prop pieces remain a compatibility
            // fallback for installations without a network fence prefab.
            var networkFenceCount = _choices[ParkAssetCategory.Fence]
                .FindAll(choice => IsNetworkFence(choice.Prefab)).Count;
            if (networkFenceCount > 0)
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

            ScanVanillaParkPalettes(entities);

            _ready = true;
            _summary = $"Flächen {_choices[ParkAssetCategory.Surface].Count} · "
                + $"Bäume {_choices[ParkAssetCategory.Tree].Count} · "
                + $"Büsche {_choices[ParkAssetCategory.Bush].Count} · "
                + $"Bänke {_choices[ParkAssetCategory.Bench].Count} · "
                + $"Lampen {_choices[ParkAssetCategory.Lamp].Count} · "
                + $"Zäune {_choices[ParkAssetCategory.Fence].Count} · "
                + $"Mülleimer {_choices[ParkAssetCategory.TrashBin].Count} · "
                + $"Parkpaletten {_parkPalettes.Count}";
            Mod.Log.Info("ParkManager 0.4 asset catalog: " + _summary);
            foreach (ParkAssetCategory category in Enum.GetValues(
                typeof(ParkAssetCategory)))
                Mod.Log.Info($"ParkManager default {category}: "
                    + (_selected.TryGetValue(category, out var selected)
                        ? selected : "(none)"));
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

        private void ScanVanillaParkPalettes(NativeArray<Entity> prefabs)
        {
            _parkPalettes.Clear();
            for (var i = 0; i < prefabs.Length; i++)
            {
                var entity = prefabs[i];
                if (!EntityManager.HasComponent<ParkData>(entity)
                    || !_prefabs.TryGetPrefab<PrefabBase>(entity, out var park)
                    || park == null || !park.isBuiltin) continue;
                var palette = new VanillaParkPalette(park.name);
                var visited = new HashSet<Entity>();
                CollectManagedSubObjects(park, palette, visited, 0);
                foreach (var pair in palette.Choices)
                    SortAndDeduplicate(pair.Value);
                if (_choices[ParkAssetCategory.Fence]
                    .Exists(choice => IsNetworkFence(choice.Prefab)))
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
            if (!string.IsNullOrEmpty(_selectedParkPalette)
                && _parkPalettes.FindIndex(p => string.Equals(p.Name,
                    _selectedParkPalette, StringComparison.Ordinal)) < 0)
                _selectedParkPalette = string.Empty;
            _parkPaletteOptionsJson = BuildParkPaletteOptionsJson();
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
                if (child == null) continue;
                CollectPaletteObject(_prefabs.GetEntity(child), palette,
                    visited, depth + 1);
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
                || prefab == null) return;
            if (TryClassifyParkAsset(entity, prefab, out var category))
                palette.Choices[category].Add(new ParkAssetChoice
                {
                    Name = prefab.name,
                    Icon = GetIcon(prefab),
                    Prefab = entity,
                });
            CollectManagedSubObjects(prefab, palette, visited, depth);
        }

        private bool TryClassifyParkAsset(Entity entity, PrefabBase prefab,
            out ParkAssetCategory category)
        {
            category = ParkAssetCategory.Surface;
            if (prefab is NetGeometryPrefab && IsNetworkFence(entity))
            {
                category = ParkAssetCategory.Fence;
                return true;
            }
            if (!IsVisibleObjectPrefab(entity, prefab)) return false;
            var lower = prefab.name.ToLowerInvariant();
            if (ContainsAny(lower, "placeholder", "random", "source"))
                return false;
            if (EntityManager.HasComponent<PlantData>(entity))
            {
                if (EntityManager.HasComponent<TreeData>(entity))
                {
                    if (ContainsAny(lower, "stump", "dead")) return false;
                    category = ParkAssetCategory.Tree;
                    return true;
                }
                // Planters and flower pots are composite props, not shrubs.
                if (ContainsAny(lower, "planter", "flowerpot", "flower pot",
                    "raisedbed", "raised bed", "plantbox", "plant box"))
                    return false;
                category = ParkAssetCategory.Bush;
                return true;
            }
            if (ContainsAny(lower, "bench", "seat", "bank"))
                category = ParkAssetCategory.Bench;
            else if (ContainsAny(lower, "lightpole", "gardenlight", "lamp",
                "lantern")) category = ParkAssetCategory.Lamp;
            else if (ContainsAny(lower, "fence", "railing", "hedge"))
                category = ParkAssetCategory.Fence;
            else if (ContainsAny(lower, "trashbin", "trash bin", "trashcan",
                "trash can", "wastebin", "waste bin", "garbagebin",
                "garbage bin", "litterbin", "litter bin"))
                category = ParkAssetCategory.TrashBin;
            else return false;
            return true;
        }

        private static void SortAndDeduplicate(List<ParkAssetChoice> choices)
        {
            choices.Sort((a, b) => StringComparer.OrdinalIgnoreCase
                .Compare(a.Name, b.Name));
            for (var i = choices.Count - 1; i > 0; i--)
                if (string.Equals(choices[i].Name, choices[i - 1].Name,
                    StringComparison.OrdinalIgnoreCase)) choices.RemoveAt(i);
        }

        private void Add(ParkAssetCategory category, string name, Entity prefab)
            => _choices[category].Add(new ParkAssetChoice
            {
                Name = name,
                Icon = _prefabs.TryGetPrefab<PrefabBase>(prefab, out var value)
                    ? GetIcon(value) : string.Empty,
                Prefab = prefab,
            });

        private static string GetIcon(PrefabBase prefab)
        {
            if (prefab == null) return string.Empty;
            if (prefab.TryGet<UIObject>(out var ui) && ui != null
                && !string.IsNullOrEmpty(ui.m_Icon)) return ui.m_Icon;
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
                builder.Append('"').Append(key).Append("\":{");
                builder.Append("\"selected\":\"")
                    .Append(Escape(_selected.TryGetValue(category, out var value)
                        ? value : string.Empty)).Append("\",\"selectedMany\":[");
                var selectedMany = _multiSelected[category];
                var selectedManyWritten = 0;
                var categoryChoices = _choices[category];
                for (var i = 0; i < categoryChoices.Count; i++)
                {
                    if (!selectedMany.Contains(categoryChoices[i].Name)) continue;
                    if (selectedManyWritten++ > 0) builder.Append(',');
                    builder.Append('"').Append(Escape(categoryChoices[i].Name))
                        .Append('"');
                }
                builder.Append("],\"options\":[");
                var choices = _choices[category];
                var written = 0;

                // The catalog order is stable. Selection is represented only
                // by selected/selectedMany and never moves a tile in the UI.
                for (var i = 0; i < choices.Count
                    && written < MaximumUiOptionsPerCategory; i++)
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

        private static bool IsMultiCategory(ParkAssetCategory category)
            => category == ParkAssetCategory.Tree
                || category == ParkAssetCategory.Bush;

        private static void AppendChoiceJson(StringBuilder builder,
            ParkAssetChoice choice)
        {
            if (choice == null)
            {
                builder.Append("{\"name\":\"\",\"icon\":\"\"}");
                return;
            }
            builder.Append("{\"name\":\"").Append(Escape(choice.Name))
                .Append("\",\"icon\":\"").Append(Escape(choice.Icon))
                .Append("\"}");
        }

        private static bool IsUiChoice(ParkAssetChoice choice)
            => choice != null
                && !string.IsNullOrWhiteSpace(choice.Name)
                && !string.IsNullOrWhiteSpace(choice.Icon);

        private string BuildParkPaletteOptionsJson()
        {
            var builder = new StringBuilder("[");
            var count = Math.Min(_parkPalettes.Count, MaximumParkPaletteOptions);
            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append('"').Append(Escape(_parkPalettes[i].Name)).Append('"');
            }
            return builder.Append(']').ToString();
        }

        private void Publish()
        {
            _optionsJson = BuildOptionsJson();
            World.GetOrCreateSystemManaged<ParkManagerUISystem>()
                .SetAssetCatalogState(_ready, _summary, _optionsJson,
                    _parkPaletteOptionsJson, _selectedParkPalette);
        }

        private static string Escape(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n");

        private static bool ContainsAny(string value, params string[] parts)
        {
            for (var i = 0; i < parts.Length; i++)
                if (value.Contains(parts[i])) return true;
            return false;
        }
    }
}
