namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Resolves Beyond MemoryPack tagged unions using exported tag maps and
/// Endfield-specific base/namespace heuristics when a tag map entry is absent.
/// </summary>
public sealed class MemoryPackUnionResolver
{
    private readonly MemoryPackSchema _schema;
    private readonly Dictionary<string, Dictionary<int, string>> _unionMaps = [];
    private readonly object _gate = new();
    private readonly Dictionary<string, MpType[]> _candidates = new(StringComparer.Ordinal);
    private MpType[]? _dataConcretes;

    public MemoryPackUnionResolver(
        MemoryPackSchema schema,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null)
    {
        _schema = schema;
        if (unionMaps == null)
            return;
        foreach (var (key, tags) in unionMaps)
            _unionMaps[key] = new Dictionary<int, string>(tags);
    }

    public bool IsTaggedUnion(string type) =>
        LookupUnionMap(type) != null || IsHardcodedTaggedUnion(type);

    public Dictionary<int, string>? LookupUnionMap(string staticType)
    {
        if (_unionMaps.TryGetValue(staticType, out var map))
            return map;

        string dotted = staticType.Replace('+', '.');
        if (dotted != staticType && _unionMaps.TryGetValue(dotted, out map))
            return map;

        string plused = staticType.Replace('.', '+');
        if (plused != staticType && _unionMaps.TryGetValue(plused, out map))
            return map;

        int dot = staticType.LastIndexOf('.');
        if (dot >= 0 && _unionMaps.TryGetValue(staticType[(dot + 1)..], out map))
            return map;

        string nested = dot >= 0 ? staticType[(dot + 1)..] : staticType;
        int plus = nested.IndexOf('+');
        if (plus >= 0)
        {
            if (_unionMaps.TryGetValue(nested, out map))
                return map;

            int lastPlus = nested.LastIndexOf('+');
            int prevPlus = nested.LastIndexOf('+', lastPlus - 1);
            string tailTwo = prevPlus >= 0 ? nested[(prevPlus + 1)..] : nested;
            if (tailTwo != nested && _unionMaps.TryGetValue(tailTwo, out map))
                return map;

            string tailOne = nested[(lastPlus + 1)..];
            if (_unionMaps.TryGetValue(tailOne, out map))
                return map;

            string tailTwoDotted = tailTwo.Replace('+', '.');
            if (_unionMaps.TryGetValue(tailTwoDotted, out map))
                return map;
        }

        return null;
    }

    public bool TryMappedConcrete(
        string staticType,
        Dictionary<int, string>? cachedDirectMap,
        int tag,
        string? hint,
        out MpType concrete)
    {
        concrete = null!;
        string? roleKey = DataRoleKey(staticType, hint);
        Dictionary<int, string>? map = roleKey != null
            ? LookupUnionMap(roleKey)
            : cachedDirectMap ?? LookupUnionMap(staticType);
        if (map == null || !map.TryGetValue(tag, out string? mapped))
            return false;
        concrete = _schema.ResolveNamed(mapped);
        return true;
    }

    /// <summary>
    /// Concrete wrappers that may appear under a Beyond MemoryPack union formatter.
    /// Bare <c>Data</c> is narrowed by a finder/validator/processor field hint.
    /// Other bases use <c>parent_type</c> and same-namespace leaves.
    /// </summary>
    public IReadOnlyList<MpType> GetCandidates(string staticType, string? hint)
    {
        if (staticType == "Data")
            return DataCandidates(hint);

        lock (_gate)
        {
            if (_candidates.TryGetValue(staticType, out MpType[]? cached))
                return cached;
            MpType[] built = BuildNamespaceUnion(staticType);
            _candidates[staticType] = built;
            return built;
        }
    }

    internal static bool IsHardcodedTaggedUnion(string type) =>
        type is "AbilityActionData"
            or "Beyond.Gameplay.Actions.ActionBase"
            or "Beyond.Gameplay.Actions.PureGetter"
            or "Beyond.Gameplay.Actions.ActionHeader"
            or "Beyond.Gameplay.Core.ModelViewStateController.AbstractBehaviorData"
            or "Beyond.Gameplay.Core.DamageProcessorBase"
            or "Beyond.Gameplay.Core.HealProcessorBase"
            or "HealProcessorBase"
            or "Beyond.Gameplay.Core.PoiseProcessorBase"
            or "PoiseProcessorBase"
            or "Beyond.Gameplay.ConditionRuntimeBase"
            or "Beyond.Gameplay.GameCondition"
            or "Beyond.Gameplay.Core.CalculationBase"
            or "CalculationBase"
            or "Finder.Data"
            or "Beyond.Gameplay.Core.Selector.Finder.Data"
            or "Beyond.Gameplay.Core.Selector+Finder+Data"
            or "Validator.Data"
            or "Beyond.Gameplay.Core.Selector.Validator.Data"
            or "Beyond.Gameplay.Core.Selector+Validator+Data"
            or "PostProcessor.Data"
            or "Beyond.Gameplay.Core.Selector.PostProcessor.Data"
            or "Beyond.Gameplay.Core.Selector+PostProcessor+Data"
            or "SpawnerPreprocess.Data"
            or "Beyond.Gameplay.SpawnerPreprocess.Data"
            or "Beyond.Gameplay.SpawnerPreprocess+Data"
            or "SpawnerActionData"
            or "Beyond.Gameplay.SpawnerActionData"
            or "Data";

    private static string? DataRoleKey(string staticType, string? hint)
    {
        if (staticType != "Data" || string.IsNullOrEmpty(hint))
            return null;
        if (hint.Contains("finder", StringComparison.OrdinalIgnoreCase))
            return "Finder.Data";
        if (hint.Contains("validator", StringComparison.OrdinalIgnoreCase))
            return "Validator.Data";
        if (hint.Contains("postprocessor", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("processor", StringComparison.OrdinalIgnoreCase))
            return "PostProcessor.Data";
        return null;
    }

    private MpType[] DataCandidates(string? hint)
    {
        lock (_gate)
        {
            _dataConcretes ??= _schema.ByTarget.TryGetValue("Data", out var list)
                ? list.Where(t => t.HasDeserialize).ToArray()
                : [];
        }

        if (string.IsNullOrEmpty(hint))
            return _dataConcretes;

        string? role = null;
        if (hint.Contains("finder", StringComparison.OrdinalIgnoreCase))
            role = "finder";
        else if (hint.Contains("validator", StringComparison.OrdinalIgnoreCase))
            role = "validator";
        else if (hint.Contains("postprocessor", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("processor", StringComparison.OrdinalIgnoreCase))
            role = "processor";
        if (role == null)
            return _dataConcretes;

        MpType[] filtered = _dataConcretes
            .Where(t =>
            {
                string w = t.WrapperType;
                return role switch
                {
                    "finder" => w.Contains("finder", StringComparison.OrdinalIgnoreCase)
                        && !w.Contains("validator", StringComparison.OrdinalIgnoreCase)
                        && !w.Contains("postprocessor", StringComparison.OrdinalIgnoreCase),
                    "validator" => w.Contains("validator", StringComparison.OrdinalIgnoreCase),
                    _ => w.Contains("processor", StringComparison.OrdinalIgnoreCase)
                        || w.Contains("filter", StringComparison.OrdinalIgnoreCase)
                        || w.Contains("sort", StringComparison.OrdinalIgnoreCase)
                        || w.Contains("convert", StringComparison.OrdinalIgnoreCase)
                        || w.Contains("shuffle", StringComparison.OrdinalIgnoreCase)
                        || w.Contains("replace", StringComparison.OrdinalIgnoreCase),
                };
            })
            .ToArray();
        return filtered.Length > 0 ? filtered : _dataConcretes;
    }

    private MpType[] BuildNamespaceUnion(string staticType)
    {
        var list = new List<MpType>();
        foreach (var group in _schema.ByTarget.Values)
        {
            foreach (MpType type in group)
            {
                if (!type.HasDeserialize || type.TargetType == staticType)
                    continue;

                if (staticType.StartsWith("Beyond.Gameplay.Actions.", StringComparison.Ordinal))
                {
                    if (!type.TargetType.StartsWith("Beyond.Gameplay.Actions.", StringComparison.Ordinal))
                        continue;
                    if (type.TargetType.Contains("ActionSerializedMap", StringComparison.Ordinal)
                        || type.TargetType.Contains("ActionMapAssetRaw", StringComparison.Ordinal)
                        || type.TargetType.Contains("ActionNodePtr", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Param`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.ParamOutput`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Getter`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.List", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.GetInteractiveProperty`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.GetLevelScriptPropertyGeneric`1", StringComparison.Ordinal))
                        continue;
                    list.Add(type);
                    continue;
                }

                if (staticType.Contains("ModelViewStateController", StringComparison.Ordinal))
                {
                    if (type.TargetType.Contains("ModelViewStateController", StringComparison.Ordinal)
                        && type.TargetType.Contains("BehaviorData", StringComparison.Ordinal)
                        && type.Wire.Length > 0)
                        list.Add(type);
                    continue;
                }

                if (staticType.Contains("DamageProcessorBase", StringComparison.Ordinal))
                {
                    if (type.TargetType.Contains("DamageProcessor", StringComparison.Ordinal))
                        list.Add(type);
                    continue;
                }

                if (staticType.Contains("ConditionRuntimeBase", StringComparison.Ordinal)
                    && ((type.TargetType.Contains("Condition", StringComparison.Ordinal)
                            && type.TargetType.EndsWith("Runtime", StringComparison.Ordinal))
                        || type.WrapperType.Contains("ConditionRuntime", StringComparison.Ordinal)))
                {
                    list.Add(type);
                    continue;
                }

                if (staticType == "Beyond.Gameplay.Core.CalculationBase"
                    && type.TargetType.EndsWith("Calculation", StringComparison.Ordinal))
                    list.Add(type);

                if (!string.IsNullOrEmpty(type.ParentType)
                    && MemoryPackSchema.ParentNameMatches(type.ParentType, staticType)
                    && !list.Contains(type))
                    list.Add(type);
            }
        }

        return list.ToArray();
    }
}
