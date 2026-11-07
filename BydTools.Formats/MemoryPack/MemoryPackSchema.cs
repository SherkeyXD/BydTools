using System.Text.Json;

namespace BydTools.Formats.MemoryPack;

public enum MpKind
{
    Bool,
    Int32,
    Int64,
    UInt64,
    Float32,
    Float64,
    String,
    Enum,
    Vector2,
    Vector3,
    Vector4,
    Vector2Int,
    Vector3Int,
    List,
    Dictionary,
    Nullable,
    Object,
    AnimationCurve,
}

public sealed class MpMember
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required MpKind Kind { get; init; }
}

/// <summary>One <c>*ForMemoryPack</c> wrapper. <see cref="Wire"/> is the exporter wire_order.</summary>
public sealed class MpType
{
    public required string TargetType { get; init; }
    public required string WrapperType { get; init; }
    public required bool HasDeserialize { get; init; }
    public required MpMember[] Wire { get; set; }
    public required MpMember[] Own { get; set; }
    public MpMember[]? AbilityPayload { get; set; }
    /// <summary>Ability base + Own when Own is longer than Wire (PlaySound etc.).</summary>
    public MpMember[]? AbilityPayloadAlt { get; set; }
    public int? MinFieldOffset { get; init; }
    public bool SerializeMarker { get; set; }
}

public sealed class MemoryPackSchema
{
    private readonly Dictionary<string, List<MpType>> _byTarget;
    private readonly Dictionary<string, MpType> _byWrapper;
    private readonly Dictionary<string, List<string>> _byBasename;
    private readonly Dictionary<string, bool> _marker = new(StringComparer.Ordinal);
    private readonly MpType[] _abilityConcretes;
    private readonly Dictionary<string, string> _kinds;
    private readonly Dictionary<int, MpType> _abilityTags = [];
    private readonly object _tagGate = new();
    private readonly Dictionary<string, MpType[]> _unionCandidates = new(StringComparer.Ordinal);
    private MpType[]? _dataConcretes;

    private MemoryPackSchema(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper,
        Dictionary<string, List<string>> byBasename,
        Dictionary<string, string> kinds,
        MpType[] abilityConcretes
    )
    {
        _byTarget = byTarget;
        _byWrapper = byWrapper;
        _byBasename = byBasename;
        _kinds = kinds;
        _abilityConcretes = abilityConcretes;
    }

    public static MemoryPackSchema Load(string schemasPath, string typeIndexPath)
    {
        if (!File.Exists(schemasPath))
            throw new FileNotFoundException("schemas.json was not found.", schemasPath);
        if (!File.Exists(typeIndexPath))
            throw new FileNotFoundException("type_index.json was not found.", typeIndexPath);

        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var byBasename = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var markers = new HashSet<string>(StringComparer.Ordinal);
        using (var indexDoc = JsonDocument.Parse(File.ReadAllBytes(typeIndexPath)))
        {
            var root = indexDoc.RootElement;
            if (root.TryGetProperty("by_type", out var byType))
            {
                foreach (var typeProp in byType.EnumerateObject())
                {
                    if (typeProp.Value.TryGetProperty("serialize_marker", out var marker) && marker.ValueKind == JsonValueKind.True)
                        markers.Add(typeProp.Name);
                    if (!typeProp.Value.TryGetProperty("entries", out var entries))
                        continue;
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("wire", out var wire))
                            continue;
                        foreach (var member in wire.EnumerateArray())
                        {
                            if (!member.TryGetProperty("type", out var typeEl) || !member.TryGetProperty("kind", out var kindEl))
                                continue;
                            string? type = typeEl.GetString();
                            string? kind = kindEl.GetString();
                            if (type != null && kind != null)
                                kinds.TryAdd(type, kind);
                        }
                    }
                }
            }

            if (root.TryGetProperty("by_basename", out var basenames))
            {
                foreach (var prop in basenames.EnumerateObject())
                {
                    var names = new List<string>();
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        string? name = item.GetString();
                        if (name != null)
                            names.Add(name);
                    }

                    if (names.Count > 0)
                        byBasename[prop.Name] = names;
                }
            }
        }

        var byTarget = new Dictionary<string, List<MpType>>(StringComparer.Ordinal);
        var byWrapper = new Dictionary<string, MpType>(StringComparer.Ordinal);
        using (var schemaDoc = JsonDocument.Parse(File.ReadAllBytes(schemasPath)))
        {
            if (!schemaDoc.RootElement.TryGetProperty("wrappers", out var wrappers))
                throw new InvalidDataException("schemas.json has no wrappers array.");

            var pending = new List<(MpType Type, List<(string Name, string Type)> Extras)>();
            foreach (var wrapper in wrappers.EnumerateArray())
            {
                string target = Required(wrapper, "target_type");
                string wrapperType = Required(wrapper, "wrapper_type");
                bool hasDeserialize = wrapper.TryGetProperty("has_deserialize", out var deser) && deser.ValueKind == JsonValueKind.True;
                var wire = ReadMembers(wrapper, "wire_order", kinds);
                var extras = new List<(string Name, string Type)>();
                int? minOffset = null;
                if (wrapper.TryGetProperty("members_from_target_fields", out var fields))
                {
                    var wireNames = new HashSet<string>(wire.Select(m => m.Name), StringComparer.Ordinal);
                    foreach (var field in fields.EnumerateArray())
                    {
                        string? name = field.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        string? type = field.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                        if (field.TryGetProperty("offset", out var offsetEl) && offsetEl.TryGetInt32(out int offset))
                            minOffset = minOffset is int current ? Math.Min(current, offset) : offset;
                        if (name == null || type == null || wireNames.Contains(name) || IsBackingField(name))
                            continue;
                        extras.Add((name, type));
                        wireNames.Add(name);
                    }
                }

                var typeObj = new MpType
                {
                    TargetType = target,
                    WrapperType = wrapperType,
                    HasDeserialize = hasDeserialize,
                    Wire = wire,
                    Own = wire,
                    MinFieldOffset = minOffset,
                    SerializeMarker = markers.Contains(target),
                };
                pending.Add((typeObj, extras));
                if (!byTarget.TryGetValue(target, out var list))
                {
                    list = [];
                    byTarget[target] = list;
                }

                list.Add(typeObj);
                byWrapper[wrapperType] = typeObj;
            }

            foreach (var (type, extras) in pending)
            {
                if (extras.Count == 0)
                    continue;
                type.Own = type.Wire.Select(m => (m.Name, m.Type)).Concat(extras)
                    .OrderBy(m => m.Name.ToLowerInvariant(), StringComparer.Ordinal)
                    .Select(m => new MpMember
                    {
                        Name = m.Name,
                        Type = m.Type,
                        Kind = Classify(m.Type, kinds),
                    })
                    .ToArray();
            }

            // ActionBase concretes omit inherited NodeBase/ActionBase members from wire_order.
            // Native Deserialize always writes: NodeBase privates + NodeBase wire props + _nextID
            // + concrete '_' fields (alpha) + concrete wire_order.
            SynthesizeActionBaseWires(byTarget, pending, kinds);
            SynthesizeActionHeaderWires(byTarget, pending, kinds);
            // GameCondition children start at field offset 144 (after useGraphScope).
            // wire_order omits the base's four serialized members.
            SynthesizeGameConditionWires(byTarget, pending, kinds);
        }

        // AbilityActionData base prefix, then each concrete's own members.
        MpMember[] abilityBase = byTarget.TryGetValue("AbilityActionData", out var bases) && bases.Count == 1
            ? bases[0].Wire
            : [];
        var concretes = new List<MpType>();
        foreach (var type in byWrapper.Values)
        {
            if (!IsAbilityConcrete(type))
                continue;
            // Prefer wire_order leaf (EffectActionData 4+16=20). Keep Own-based alt when longer
            // (PlaySoundActionData count 22 = 4+18 Own) for incomplete wire_order exports.
            MpMember[] leafWire = type.Wire.Length > 0 ? type.Wire : type.Own;
            var wireNames = new HashSet<string>(leafWire.Select(m => m.Name), StringComparer.Ordinal);
            type.AbilityPayload = abilityBase.Where(m => !wireNames.Contains(m.Name)).Concat(leafWire).ToArray();
            if (type.Own.Length > leafWire.Length)
            {
                var ownNames = new HashSet<string>(type.Own.Select(m => m.Name), StringComparer.Ordinal);
                type.AbilityPayloadAlt = abilityBase.Where(m => !ownNames.Contains(m.Name)).Concat(type.Own).ToArray();
            }
            concretes.Add(type);
        }

        // Blackboard* wrappers ship an empty wire_order; the closed generic base is the payload.
        AliasEmpty(byTarget, "BlackboardDouble", "BlackboardParamBase`2<float, double>");
        AliasEmpty(byTarget, "BlackboardInt", "BlackboardParamBase`2<int, int>");
        AliasEmpty(byTarget, "BlackboardString", "BlackboardParamBase`2<string, string>");
        // BlackboardCustomWithDropdownIntValue adds useCustomValue after BlackboardInt (native count 4).
        PrefixWithBaseWire(byTarget, "BlackboardCustomWithDropdownIntValue", "BlackboardInt");
        AliasDropdownBlackboard(byTarget, "Beyond.Gameplay.Core.BlackboardImpactValue");
        AliasDropdownBlackboard(byTarget, "Beyond.Gameplay.Core.BlackboardSuperArmorValue");
        AliasDropdownBlackboard(byTarget, "BlackboardCharBlowOffPriorityValue");
        AliasEmpty(byTarget, "Beyond.Gameplay.Core.Conditions.BlackboardBuffId", "BlackboardString");
        // CreateBuffActionInput wire is BuffInput (3) then its own fields (2) = 5.
        PrefixWithBaseWire(byTarget, "Beyond.Gameplay.Core.CreateBuffActionInput", "Beyond.Gameplay.Core.BuffInput");
        // StartDialogAndTeleport extends StartDialogAction. Native count 16 = StartDialog's 15
        // plus a second _dialogId at offset 272 (the parent already has one at 216).
        AppendDuplicateChildField(
            byTarget,
            "Beyond.Gameplay.Actions.StartDialogAndTeleportAction",
            "Beyond.Gameplay.Actions.StartDialogAction",
            "_dialogId"
        );
        // ShowHideActorData fields start at offset 32, right after AbilityActionData.
        // Native memberCount is 10 = 4 base fields + the 6 leaf fields.
        PrefixAbilityBase(byTarget, "ShowHideActorData", abilityBase);
        PrefixAbilityBase(byTarget, "FootStepChangeData", abilityBase);
        PrefixAbilityBase(byTarget, "HideEffectOnTargetExceptTagData", abilityBase);
        // ContinuousFindTarget / EffectFindTarget are FindTargetActionData (14) + one leaf.
        // Native count 19 = ability base 4 + FindTarget 14 + leaf.
        PrefixFindTargetData(byTarget, byWrapper, "Beyond.MemoryPack.Beyond_Gameplay_Core_ContinuousFindTargetAction_DataForMemoryPack", abilityBase);
        PrefixFindTargetData(byTarget, byWrapper, "Beyond.MemoryPack.Beyond_Gameplay_Core_EffectFindTargetAction_DataForMemoryPack", abilityBase);
        // KeywordAction middles. Weak/Slow/Speedup/Shelter are ability + Keyword (13).
        // Vulnerable/Enhanced add KeywordActionWithSubType.subType (14).
        ApplyKeywordActionMiddles(byTarget, byWrapper);
        // PlayAnimationWithStep = ability base + PlayAnimationActionData + its own 14.
        PrefixPlayAnimationWithStep(byTarget, abilityBase);
        // PlayRadioAndWait serializes the same 13 members as PlayRadio.
        CopyWire(byTarget, "Beyond.Gameplay.Actions.PlayRadioAndWait", "Beyond.Gameplay.Actions.PlayRadio");
        // MVSC behaviors start at offset 40, after MVSCBehaviorData (6). Effect is 6+11=17.
        PrefixMvscBehaviors(byTarget);
        // Char interact acts at offset 120 are ActionData (15) + leaf.
        // Acts at 128 also take ActorActData.actorIndex. CharAnim is 15+1+15=31.
        PrefixCharInteractPerform(byTarget);
        // HitBoxFinder fields start at offset 32, after TargetFinder (5). Native count 8 = 5+3.
        // ShapeFinder is the same boundary.
        PrefixTargetFinderChildren(byWrapper);
        // BuffData lists were exported as List<Data>; native elements are *Modifier.Data.
        RewriteBuffModifierDataLists(byTarget, byWrapper);
        SynthesizeBuffIconConfigWire(byTarget, kinds);
        // Level*Data first fields sit at offset 112, immediately after LevelEntityData
        // (last field keepCrossMap at 106). Native formatters write the base wire, then the leaf.
        PrefixWithBaseWire(byTarget, "Beyond.Gameplay.LevelInteractiveData", "Beyond.Gameplay.LevelEntityData");
        PrefixWithBaseWire(byTarget, "Beyond.Gameplay.LevelEnemyData", "Beyond.Gameplay.LevelEntityData");
        PrefixWithBaseWire(byTarget, "Beyond.Gameplay.LevelFactoryRegionData", "Beyond.Gameplay.LevelEntityData");
        PrefixWithBaseWire(byTarget, "Beyond.Gameplay.LevelNpcData", "Beyond.Gameplay.LevelEntityData");

        var schema = new MemoryPackSchema(byTarget, byWrapper, byBasename, kinds, concretes.ToArray());
        foreach (var name in markers)
            schema._marker[name] = true;
        return schema;
    }

    public MpType? FindAbilityTag(int tag)
    {
        lock (_tagGate)
            return _abilityTags.TryGetValue(tag, out var type) ? type : null;
    }

    public void RememberAbilityTag(int tag, MpType type)
    {
        lock (_tagGate)
            _abilityTags.TryAdd(tag, type);
    }

    public IReadOnlyList<MpType> AbilityConcretes => _abilityConcretes;

    /// <summary>
    /// Concrete wrappers that may appear under a Beyond MemoryPack union formatter.
    /// AbilityActionData keeps the ActionData/Data heuristic. ActionBase-like bases use
    /// same-namespace leaves. Bare <c>Data</c> is narrowed by a finder/validator/processor field hint.
    /// </summary>
    public IReadOnlyList<MpType> UnionCandidates(string staticType, string? hint)
    {
        if (staticType == "AbilityActionData")
            return _abilityConcretes;

        if (staticType == "Data")
            return DataCandidates(hint);

        lock (_tagGate)
        {
            if (_unionCandidates.TryGetValue(staticType, out MpType[]? cached))
                return cached;
            MpType[] built = BuildNamespaceUnion(staticType);
            _unionCandidates[staticType] = built;
            return built;
        }
    }

    private MpType[] DataCandidates(string? hint)
    {
        lock (_tagGate)
        {
            _dataConcretes ??= _byTarget.TryGetValue("Data", out var list)
                ? list.Where(t => t.HasDeserialize).ToArray()
                : [];
        }

        if (string.IsNullOrEmpty(hint))
            return _dataConcretes;

        string h = hint.ToLowerInvariant();
        string? role = null;
        if (h.Contains("finder", StringComparison.Ordinal))
            role = "finder";
        else if (h.Contains("validator", StringComparison.Ordinal))
            role = "validator";
        else if (h.Contains("postprocessor", StringComparison.Ordinal) || h.Contains("processor", StringComparison.Ordinal))
            role = "processor";
        if (role == null)
            return _dataConcretes;

        MpType[] filtered = _dataConcretes
            .Where(t =>
            {
                string w = t.WrapperType.ToLowerInvariant();
                return role switch
                {
                    "finder" => w.Contains("finder", StringComparison.Ordinal)
                        && !w.Contains("validator", StringComparison.Ordinal)
                        && !w.Contains("postprocessor", StringComparison.Ordinal),
                    "validator" => w.Contains("validator", StringComparison.Ordinal),
                    _ => w.Contains("processor", StringComparison.Ordinal)
                        || w.Contains("filter", StringComparison.Ordinal)
                        || w.Contains("sort", StringComparison.Ordinal)
                        || w.Contains("convert", StringComparison.Ordinal)
                        || w.Contains("shuffle", StringComparison.Ordinal)
                        || w.Contains("replace", StringComparison.Ordinal),
                };
            })
            .ToArray();
        return filtered.Length > 0 ? filtered : _dataConcretes;
    }

    private MpType[] BuildNamespaceUnion(string staticType)
    {
        var list = new List<MpType>();
        foreach (var group in _byTarget.Values)
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

                // CalculationBase has no fields of its own. Concretes are *Calculation.
                if (staticType == "Beyond.Gameplay.Core.CalculationBase"
                    && type.TargetType.EndsWith("Calculation", StringComparison.Ordinal))
                    list.Add(type);
            }
        }

        return list.ToArray();
    }

    public MpKind KindOf(string type)
    {
        MpKind kind = Classify(type, _kinds);
        if (kind == MpKind.Object && !_byTarget.ContainsKey(type))
        {
            // Wrapper FQCNs are objects. Without this, BuffData list elements are eaten as int32.
            if (_byWrapper.ContainsKey(type))
                return MpKind.Object;
            return MpKind.Enum;
        }
        return kind;
    }

    public MpType ResolveNamed(string name)
    {
        if (_byWrapper.TryGetValue(name, out MpType? wrapped))
            return wrapped;
        if (name.StartsWith("Beyond.MemoryPack.", StringComparison.Ordinal) && _byWrapper.TryGetValue(name, out wrapped))
            return wrapped;
        return Require(name, null);
    }

    public MpType Require(string targetType, string? hint)
    {
        return TryResolve(targetType, hint, out var type, out string? error)
            ? type!
            : throw new MemoryPackTypeException(error ?? $"No MemoryPack schema for {targetType}.");
    }

    public bool TryResolve(string targetType, string? hint, out MpType? type, out string? error)
    {
        type = null;
        error = null;
        if (!_byTarget.TryGetValue(targetType, out var list) || list.Count == 0)
        {
            error = $"No MemoryPack wrapper for \"{targetType}\".";
            return false;
        }

        if (list.Count == 1)
        {
            type = list[0];
            return true;
        }

        if (!string.IsNullOrEmpty(hint))
        {
            string needle = hint.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
            MpType? best = null;
            int bestScore = 0;
            bool tie = false;
            foreach (var candidate in list)
            {
                string leaf = candidate.WrapperType.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                int score = needle.Length > 0 && leaf.Contains(needle, StringComparison.Ordinal) ? 60 + needle.Length : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    tie = false;
                }
                else if (score == bestScore && score > 0)
                {
                    tie = true;
                }
            }

            if (bestScore > 0 && !tie)
            {
                type = best;
                return true;
            }

            if (tie)
            {
                error = $"Type name \"{targetType}\" is ambiguous for field \"{hint}\".";
                return false;
            }
        }

        var marker = list.FirstOrDefault(t => t.SerializeMarker);
        if (marker != null && list.Count(t => t.SerializeMarker) == 1)
        {
            type = marker;
            return true;
        }

        error = $"Type name \"{targetType}\" matches {list.Count} wrappers.";
        return false;
    }

    public bool TryBasename(string basename, string? folderHint, out string? targetType, out string? error)
    {
        targetType = null;
        error = null;
        if (_byBasename.TryGetValue(basename, out var exact) && exact.Count > 0)
        {
            targetType = Prefer(exact);
            return targetType != null || Fail(exact.Count, basename, out error);
        }

        string? suffix = LongestSuffix(basename);
        if (suffix != null && _byBasename.TryGetValue(suffix, out var suffixed))
        {
            targetType = Prefer(suffixed);
            return true;
        }

        if (!string.IsNullOrEmpty(folderHint) && _byBasename.TryGetValue(folderHint, out var hinted))
        {
            targetType = Prefer(hinted);
            return targetType != null || Fail(hinted.Count, folderHint, out error);
        }

        error = $"No type_index basename for \"{basename}\""
            + (folderHint == null ? "." : $" or folder hint \"{folderHint}\".");
        return false;
    }

    private string? Prefer(List<string> names)
    {
        string? marked = null;
        foreach (string name in names)
        {
            if (_marker.ContainsKey(name) || (_byTarget.TryGetValue(name, out var types) && types.Any(t => t.SerializeMarker)))
            {
                if (marked != null && !string.Equals(marked, name, StringComparison.Ordinal))
                    return null;
                marked = name;
            }
        }

        if (marked != null)
            return marked;
        return names.Count == 1 ? names[0] : null;
    }

    private static bool Fail(int count, string name, out string? error)
    {
        error = $"Basename \"{name}\" matches {count} types and more than one is an IMemoryPackSerialize root.";
        return false;
    }

    private string? LongestSuffix(string basename)
    {
        string? best = null;
        foreach (string key in _byBasename.Keys)
        {
            if (key.Length < 10 || key.Length >= basename.Length)
                continue;
            if (!basename.EndsWith(key, StringComparison.Ordinal))
                continue;
            if (best == null || key.Length > best.Length)
                best = key;
        }

        return best;
    }

    /// <summary>
    /// GameCondition's serializable members are scopeMask, uniqueId, useCurrentScope, useGraphScope.
    /// Subclass instance fields begin at offset 144, and their wire_order does not repeat the base.
    /// </summary>
    private static void SynthesizeGameConditionWires(
        Dictionary<string, List<MpType>> byTarget,
        List<(MpType Type, List<(string Name, string Type)> Extras)> pending,
        Dictionary<string, string> kinds)
    {
        if (!byTarget.TryGetValue("Beyond.Gameplay.GameCondition", out var bases) || bases.Count != 1)
            return;
        MpMember[] prefix = bases[0].Wire;
        if (prefix.Length == 0)
            return;

        var extrasByType = new Dictionary<MpType, List<(string Name, string Type)>>();
        foreach (var (type, extras) in pending)
            extrasByType[type] = extras;

        foreach (var group in byTarget.Values)
        {
            foreach (MpType type in group)
            {
                if (!IsGameConditionConcrete(type))
                    continue;

                extrasByType.TryGetValue(type, out List<(string Name, string Type)>? extras);
                extras ??= [];
                var wireNames = new HashSet<string>(prefix.Select(m => m.Name), StringComparer.Ordinal);
                foreach (MpMember member in type.Wire)
                    wireNames.Add(member.Name);
                MpMember[] underscore = extras
                    .Where(e => e.Name.Length > 1
                        && e.Name[0] == '_'
                        && e.Name[1] != '_'
                        && !e.Name.StartsWith("m_", StringComparison.Ordinal)
                        && !wireNames.Contains(e.Name))
                    .OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                    .Select(e => new MpMember
                    {
                        Name = e.Name,
                        Type = e.Type,
                        Kind = Classify(e.Type, kinds),
                    })
                    .ToArray();

                var synthesized = new MpMember[prefix.Length + underscore.Length + type.Wire.Length];
                Array.Copy(prefix, 0, synthesized, 0, prefix.Length);
                Array.Copy(underscore, 0, synthesized, prefix.Length, underscore.Length);
                Array.Copy(type.Wire, 0, synthesized, prefix.Length + underscore.Length, type.Wire.Length);
                type.Wire = synthesized;
                type.Own = synthesized;
            }
        }
    }

    private static bool IsGameConditionConcrete(MpType type)
    {
        if (!type.HasDeserialize || type.MinFieldOffset != 144)
            return false;
        string target = type.TargetType;
        if (target.StartsWith("Beyond.Gameplay.Conditions.", StringComparison.Ordinal))
            return true;
        return target is "Beyond.Gameplay.CheckActivityConditionalStageStatus"
            or "Beyond.Gameplay.CombineCondition"
            or "Beyond.Gameplay.GameConditionServerPlaceHolder";
    }

    private static void AppendDuplicateChildField(
        Dictionary<string, List<MpType>> byTarget,
        string derived,
        string baseName,
        string fieldName)
    {
        if (!byTarget.TryGetValue(derived, out var derivedList) || derivedList.Count != 1)
            return;
        if (!byTarget.TryGetValue(baseName, out var baseList) || baseList.Count != 1)
            return;
        MpType child = derivedList[0];
        MpMember? extra = null;
        foreach (MpMember member in child.Wire)
        {
            if (member.Name == fieldName)
                extra = member;
        }

        if (extra == null)
            return;
        child.Wire = baseList[0].Wire.Concat([extra]).ToArray();
        child.Own = child.Wire;
    }

    private static void PrefixAbilityBase(
        Dictionary<string, List<MpType>> byTarget,
        string derived,
        MpMember[] abilityBase)
    {
        if (abilityBase.Length == 0)
            return;
        if (!byTarget.TryGetValue(derived, out var list) || list.Count != 1)
            return;
        MpType type = list[0];
        if (type.Wire.Any(m => m.Name == "isEnable"))
            return;
        type.Wire = abilityBase.Concat(type.Wire).ToArray();
        type.Own = type.Wire;
    }

    private static void ApplyKeywordActionMiddles(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper)
    {
        if (!byTarget.TryGetValue("KeywordActionData", out var keywordList) || keywordList.Count != 1)
            return;
        MpMember[] keyword = keywordList[0].AbilityPayload is { Length: > 0 } payload
            ? payload
            : keywordList[0].Wire;
        MpMember[] subType = byTarget.TryGetValue("KeywordActionWithSubTypeData", out var subList) && subList.Count == 1
            ? subList[0].Wire
            : [];
        foreach (MpType type in byWrapper.Values)
        {
            string wrapper = type.WrapperType;
            bool fourteen = wrapper.Contains("VulnerableAction_Data", StringComparison.Ordinal)
                || wrapper.Contains("EnhancedAction_Data", StringComparison.Ordinal);
            bool thirteen = wrapper.Contains("WeakAction_Data", StringComparison.Ordinal)
                || wrapper.Contains("SlowAction_Data", StringComparison.Ordinal)
                || wrapper.Contains("SpeedupAction_Data", StringComparison.Ordinal)
                || wrapper.Contains("ShelterAction_Data", StringComparison.Ordinal);
            if (!fourteen && !thirteen)
                continue;
            MpMember[] wire = fourteen ? keyword.Concat(subType).ToArray() : keyword;
            type.AbilityPayload = wire;
            type.Wire = wire;
            type.Own = wire;
        }
    }

    private static void PrefixPlayAnimationWithStep(
        Dictionary<string, List<MpType>> byTarget,
        MpMember[] abilityBase)
    {
        if (abilityBase.Length == 0)
            return;
        if (!byTarget.TryGetValue("PlayAnimationActionData", out var anim) || anim.Count != 1)
            return;
        if (!byTarget.TryGetValue("PlayAnimationWithStepData", out var step) || step.Count != 1)
            return;
        MpType type = step[0];
        var leafNames = new HashSet<string>(type.Wire.Select(m => m.Name), StringComparer.Ordinal);
        MpMember[] payload = abilityBase
            .Concat(anim[0].Wire)
            .Where(m => !leafNames.Contains(m.Name))
            .Concat(type.Wire)
            .ToArray();
        type.AbilityPayload = payload;
    }

    private static void CopyWire(Dictionary<string, List<MpType>> byTarget, string derived, string sourceName)
    {
        if (!byTarget.TryGetValue(derived, out var derivedList) || derivedList.Count != 1)
            return;
        if (!byTarget.TryGetValue(sourceName, out var sourceList) || sourceList.Count != 1)
            return;
        if (sourceList[0].Wire.Length == 0)
            return;
        MpMember[] wire = sourceList[0].Wire.ToArray();
        derivedList[0].Wire = wire;
        derivedList[0].Own = wire;
    }

    private static void PrefixMvscBehaviors(Dictionary<string, List<MpType>> byTarget)
    {
        const string behavior = "Beyond.Gameplay.Core.ModelViewStateController.MVSCBehaviorData";
        if (!byTarget.TryGetValue(behavior, out var bases) || bases.Count != 1 || bases[0].Wire.Length == 0)
            return;
        foreach (var group in byTarget.Values)
        {
            foreach (MpType type in group)
            {
                if (type.TargetType == behavior || type.MinFieldOffset != 40)
                    continue;
                if (!type.TargetType.Contains(".MVSC", StringComparison.Ordinal)
                    || !type.TargetType.Contains("BehaviorData", StringComparison.Ordinal))
                    continue;
                var names = new HashSet<string>(type.Wire.Select(m => m.Name), StringComparer.Ordinal);
                MpMember[] merged = bases[0].Wire.Where(m => !names.Contains(m.Name)).Concat(type.Wire).ToArray();
                type.Wire = merged;
                type.Own = merged;
            }
        }
    }

    private static void PrefixCharInteractPerform(Dictionary<string, List<MpType>> byTarget)
    {
        const string action = "Beyond.Gameplay.Core.CharInteractPerform.CharInteractPerformActionData";
        const string actor = "Beyond.Gameplay.Core.CharInteractPerform.ActorActData";
        if (!byTarget.TryGetValue(action, out var actionList) || actionList.Count != 1)
            return;
        MpMember[] actionWire = actionList[0].Wire;
        MpMember[] actorWire = byTarget.TryGetValue(actor, out var actorList) && actorList.Count == 1
            ? actorList[0].Wire
            : [];
        foreach (var group in byTarget.Values)
        {
            foreach (MpType type in group)
            {
                if (!type.TargetType.Contains(".CharInteractPerform.", StringComparison.Ordinal))
                    continue;
                if (type.TargetType == action)
                    continue;
                MpMember[] prefix = type.MinFieldOffset switch
                {
                    128 => actionWire.Concat(actorWire).ToArray(),
                    120 => actionWire,
                    _ => [],
                };
                if (prefix.Length == 0)
                    continue;
                var names = new HashSet<string>(type.Wire.Select(m => m.Name), StringComparer.Ordinal);
                MpMember[] merged = prefix.Where(m => !names.Contains(m.Name)).Concat(type.Wire).ToArray();
                type.Wire = merged;
                type.Own = merged;
            }
        }
    }

    private static void PrefixTargetFinderChildren(Dictionary<string, MpType> byWrapper)
    {
        const string targetFinder = "Beyond.MemoryPack.Beyond_Gameplay_Core_Selector_TargetFinder_DataForMemoryPack";
        if (!byWrapper.TryGetValue(targetFinder, out MpType? parent) || parent.Wire.Length == 0)
            return;
        foreach (MpType type in byWrapper.Values)
        {
            if (type.MinFieldOffset != 32)
                continue;
            if (!type.WrapperType.Contains("_Selector_", StringComparison.Ordinal)
                || !type.WrapperType.Contains("Finder_DataForMemoryPack", StringComparison.Ordinal))
                continue;
            if (type.WrapperType == targetFinder)
                continue;
            var names = new HashSet<string>(type.Wire.Select(m => m.Name), StringComparer.Ordinal);
            MpMember[] merged = parent.Wire.Where(m => !names.Contains(m.Name)).Concat(type.Wire).ToArray();
            type.Wire = merged;
            type.Own = merged;
            type.AbilityPayload = null;
        }
    }

    private static void PrefixFindTargetData(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper,
        string wrapperName,
        MpMember[] abilityBase)
    {
        if (!byWrapper.TryGetValue(wrapperName, out MpType? type))
            return;
        if (!byTarget.TryGetValue("FindTargetActionData", out var bases) || bases.Count != 1)
            return;
        MpMember[] middle = bases[0].Wire;
        if (middle.Length == 0 || abilityBase.Length == 0)
            return;
        var leafNames = new HashSet<string>(type.Wire.Select(m => m.Name), StringComparer.Ordinal);
        MpMember[] payload = abilityBase
            .Concat(middle)
            .Where(m => !leafNames.Contains(m.Name))
            .Concat(type.Wire)
            .ToArray();
        type.AbilityPayload = payload;
    }

    /// <summary>
    /// Impact/SuperArmor/CharBlowOff inherit BlackboardCustomWithDropdownIntValue:
    /// BlackboardInt wire (3) + useCustomValue.
    /// </summary>
    private static void AliasDropdownBlackboard(Dictionary<string, List<MpType>> byTarget, string derived)
    {
        if (byTarget.ContainsKey("BlackboardCustomWithDropdownIntValue")
            && byTarget["BlackboardCustomWithDropdownIntValue"].Count == 1
            && byTarget["BlackboardCustomWithDropdownIntValue"][0].Wire.Length > 0)
        {
            AliasEmpty(byTarget, derived, "BlackboardCustomWithDropdownIntValue");
            return;
        }

        AliasEmpty(byTarget, derived, "BlackboardInt");
        if (!byTarget.TryGetValue(derived, out var list) || list.Count != 1)
            return;
        MpType type = list[0];
        if (type.Wire.Any(m => m.Name == "useCustomValue"))
            return;
        MpMember extra = new()
        {
            Name = "useCustomValue",
            Type = "bool",
            Kind = MpKind.Bool,
        };
        type.Wire = type.Wire.Concat([extra]).ToArray();
        type.Own = type.Wire;
    }

    /// <summary>
    /// schemas.json collapses nested Modifier.Data to bare Data. BuffData modifier lists
    /// are the concrete Modifier.Data wrappers, not AbilityAction Data unions.
    /// </summary>
    private static void RewriteBuffModifierDataLists(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper)
    {
        if (!byTarget.TryGetValue("Beyond.Gameplay.Core.BuffData", out var list) || list.Count != 1)
            return;
        MpType buff = list[0];
        (string Field, string Wrapper)[] map =
        [
            ("damageModifier", "Beyond.MemoryPack.Beyond_Gameplay_Core_DamageModifier_DataForMemoryPack"),
            ("healModifier", "Beyond.MemoryPack.Beyond_Gameplay_Core_HealModifier_DataForMemoryPack"),
            ("poiseModifier", "Beyond.MemoryPack.Beyond_Gameplay_Core_PoiseModifier_DataForMemoryPack"),
            ("globalModifier", "Beyond.MemoryPack.Beyond_Gameplay_Core_GlobalModifier_DataForMemoryPack"),
        ];
        var wire = buff.Wire.ToArray();
        bool changed = false;
        for (int i = 0; i < wire.Length; i++)
        {
            foreach (var (field, wrapper) in map)
            {
                if (wire[i].Name != field)
                    continue;
                if (!wire[i].Type.Contains("List`1<Data>", StringComparison.Ordinal))
                    continue;
                if (!byWrapper.ContainsKey(wrapper))
                    continue;
                wire[i] = new MpMember
                {
                    Name = wire[i].Name,
                    Type = $"System.Collections.Generic.List`1<{wrapper}>",
                    Kind = MpKind.List,
                };
                changed = true;
            }
        }

        if (!changed)
            return;
        buff.Wire = wire;
        buff.Own = wire;
    }

    /// <summary>
    /// BuffIconConfig wrapper exposes _spritePath / _orderPriorityConfig, which the exporter
    /// skipped. Native count is 20 = those two (alpha) + 18 public wire props.
    /// </summary>
    private static void SynthesizeBuffIconConfigWire(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, string> kinds)
    {
        if (!byTarget.TryGetValue("Beyond.Gameplay.Core.BuffIconConfig", out var list) || list.Count != 1)
            return;
        MpType type = list[0];
        if (type.Wire.Any(m => m.Name is "_spritePath" or "_orderPriorityConfig"))
            return;
        string orderType = byTarget.ContainsKey("Beyond.Gameplay.Core.BuffIconConfig.OrderPriorityConfig")
            ? "Beyond.Gameplay.Core.BuffIconConfig.OrderPriorityConfig"
            : byTarget.ContainsKey("OrderPriorityConfig")
                ? "OrderPriorityConfig"
                : "OrderPriorityConfig";
        MpMember[] prefix =
        [
            new()
            {
                Name = "_orderPriorityConfig",
                Type = orderType,
                Kind = byTarget.ContainsKey(orderType) ? MpKind.Object : Classify(orderType, kinds),
            },
            new()
            {
                Name = "_spritePath",
                Type = "string",
                Kind = MpKind.String,
            },
        ];
        type.Wire = prefix.Concat(type.Wire).ToArray();
        type.Own = type.Wire;
    }

    private static void PrefixWithBaseWire(
        Dictionary<string, List<MpType>> byTarget,
        string derived,
        string baseName)
    {
        if (!byTarget.TryGetValue(derived, out var derivedList) || derivedList.Count != 1)
            return;
        if (!byTarget.TryGetValue(baseName, out var baseList) || baseList.Count != 1)
            return;
        MpType d = derivedList[0];
        MpType b = baseList[0];
        if (b.Wire.Length == 0)
            return;
        var names = new HashSet<string>(d.Wire.Select(m => m.Name), StringComparer.Ordinal);
        MpMember[] merged = b.Wire.Where(m => !names.Contains(m.Name)).Concat(d.Wire).ToArray();
        d.Wire = merged;
        d.Own = merged;
    }

    private static void AliasEmpty(Dictionary<string, List<MpType>> byTarget, string derived, string baseName)
    {
        if (!byTarget.TryGetValue(derived, out var derivedList) || derivedList.Count != 1)
            return;
        if (!byTarget.TryGetValue(baseName, out var baseList) || baseList.Count != 1)
            return;
        var source = derivedList[0];
        if (source.Wire.Length != 0)
            return;
        var aliased = new MpType
        {
            TargetType = source.TargetType,
            WrapperType = source.WrapperType,
            HasDeserialize = source.HasDeserialize,
            Wire = baseList[0].Wire,
            Own = baseList[0].Own,
            AbilityPayload = source.AbilityPayload,
            AbilityPayloadAlt = source.AbilityPayloadAlt,
            MinFieldOffset = source.MinFieldOffset,
            SerializeMarker = source.SerializeMarker,
        };
        derivedList[0] = aliased;
    }


    /// <summary>
    /// ActionHeader concretes (ScriptEvent/EntityEvent/…) inherit NodeBase + ActionHeader fields,
    /// not ActionBase._nextID alone. OnLeaderEnterTriggerVolume native count is 18 =
    /// NodeBase(7) + ActionHeader(7) + ScriptEventHeader(2) + leaf(2).
    /// </summary>
    private static void SynthesizeActionHeaderWires(
        Dictionary<string, List<MpType>> byTarget,
        List<(MpType Type, List<(string Name, string Type)> Extras)> pending,
        Dictionary<string, string> kinds)
    {
        MpMember[] nodePrefix = BuildNodeBasePrefix(kinds);
        MpMember[] actionHeader = UnderscoreMembers(
            [
                ("_filterLevel", "Beyond.Gameplay.Actions.FilterLevel"),
                ("_filterMask", "Beyond.Gameplay.Actions.FilterMask"),
                ("_filterMode", "bool"),
                ("_nextID", "int"),
                ("_priority", "int"),
                ("_triggerActiveDuring", "Beyond.Gameplay.Actions.TriggerActiveDuring"),
                ("_validate", "Beyond.Gameplay.Actions.Param`1<bool>"),
            ],
            kinds
        );
        MpMember[] scriptEvent = UnderscoreMembers(
            [
                ("_targetScript", "Beyond.Gameplay.Actions.Param`1<Beyond.Gameplay.Core.LevelScriptPtr>"),
                ("_triggerTarget", "TriggerTarget"),
            ],
            kinds
        );
        MpMember[] entityEvent = UnderscoreMembers(
            [
                ("_targetEntity", "Beyond.Gameplay.Actions.Param`1<Beyond.Gameplay.Core.EntityPtr>"),
                ("_targetEntityList", "Beyond.Gameplay.Actions.Param`1<System.Collections.Generic.List`1<Beyond.Gameplay.Core.EntityPtr>>"),
                ("_targetEntityListOutput", "Beyond.Gameplay.Actions.ParamOutput`1<Beyond.Gameplay.Core.EntityPtr>"),
                ("_triggerTarget", "TriggerTarget"),
            ],
            kinds
        );

        var extrasByType = new Dictionary<MpType, List<(string Name, string Type)>>();
        foreach (var (type, extras) in pending)
            extrasByType[type] = extras;

        foreach (var group in byTarget.Values)
        {
            foreach (MpType type in group)
            {
                if (!type.HasDeserialize || !IsActionHeaderLineage(type.TargetType))
                    continue;
                if (type.TargetType is "Beyond.Gameplay.Actions.ActionHeader"
                    or "Beyond.Gameplay.Actions.ScriptEventHeader"
                    or "Beyond.Gameplay.Actions.EntityEventHeader"
                    or "Beyond.Gameplay.Actions.LevelEventHeader"
                    or "Beyond.Gameplay.Actions.MissionEventHeader")
                    continue;

                extrasByType.TryGetValue(type, out List<(string Name, string Type)>? extras);
                extras ??= [];
                var wireNames = new HashSet<string>(type.Wire.Select(m => m.Name), StringComparer.Ordinal);
                MpMember[] leafUnderscore = extras
                    .Where(e => e.Name.Length > 1
                        && e.Name[0] == '_'
                        && e.Name[1] != '_'
                        && !e.Name.StartsWith("m_", StringComparison.Ordinal)
                        && !wireNames.Contains(e.Name))
                    .OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                    .Select(e => new MpMember
                    {
                        Name = e.Name,
                        Type = e.Type,
                        Kind = Classify(e.Type, kinds),
                    })
                    .ToArray();

                MpMember[] intermediate =
                    type.TargetType.Contains(".ScriptEvent.", StringComparison.Ordinal) ? scriptEvent
                    : type.TargetType.Contains(".EntityEvent.", StringComparison.Ordinal) ? entityEvent
                    : [];

                var synthesized = new MpMember[
                    nodePrefix.Length + actionHeader.Length + intermediate.Length
                    + leafUnderscore.Length + type.Wire.Length];
                int at = 0;
                Array.Copy(nodePrefix, 0, synthesized, at, nodePrefix.Length);
                at += nodePrefix.Length;
                Array.Copy(actionHeader, 0, synthesized, at, actionHeader.Length);
                at += actionHeader.Length;
                Array.Copy(intermediate, 0, synthesized, at, intermediate.Length);
                at += intermediate.Length;
                Array.Copy(leafUnderscore, 0, synthesized, at, leafUnderscore.Length);
                at += leafUnderscore.Length;
                Array.Copy(type.Wire, 0, synthesized, at, type.Wire.Length);
                type.Wire = synthesized;
                type.Own = synthesized;
            }
        }
    }

    private static MpMember[] BuildNodeBasePrefix(Dictionary<string, string> kinds) =>
        UnderscoreMembers(
            [
                ("_dontLogWarning", "bool"),
                ("_ID", "int"),
                ("_releaseWhenExecutionFinished", "bool"),
                ("_uid", "string"),
                ("scopeMask", "Beyond.GEnums.ScopeName"),
                ("useCurrentScope", "bool"),
                ("useGraphScope", "bool"),
            ],
            kinds
        );

    private static MpMember[] UnderscoreMembers(
        (string Name, string Type)[] members,
        Dictionary<string, string> kinds) =>
        members
            .Select(m => new MpMember
            {
                Name = m.Name,
                Type = m.Type,
                Kind = Classify(m.Type, kinds),
            })
            .ToArray();

        /// <summary>
    /// Recover ActionBase MemoryPack wire for wrappers whose schemas.json wire_order is empty
    /// or only lists leaf props. Confirmed against WaitForSeconds/Split/CallServer/SwitchInt/
    /// IfElseAction/StartDialogAction formatter Deserialize (memberCount + setter order).
    /// </summary>
    private static void SynthesizeActionBaseWires(
        Dictionary<string, List<MpType>> byTarget,
        List<(MpType Type, List<(string Name, string Type)> Extras)> pending,
        Dictionary<string, string> kinds)
    {
        MpMember[] prefix = BuildActionBasePrefix(kinds);
        var extrasByType = new Dictionary<MpType, List<(string Name, string Type)>>();
        foreach (var (type, extras) in pending)
            extrasByType[type] = extras;

        foreach (var group in byTarget.Values)
        {
            foreach (MpType type in group)
            {
                if (!IsActionBaseConcrete(type.TargetType, type.HasDeserialize))
                    continue;

                extrasByType.TryGetValue(type, out List<(string Name, string Type)>? extras);
                extras ??= [];
                var wireNames = new HashSet<string>(type.Wire.Select(m => m.Name), StringComparer.Ordinal);
                MpMember[] underscore = extras
                    .Where(e => e.Name.Length > 1
                        && e.Name[0] == '_'
                        && e.Name[1] != '_'
                        && !e.Name.StartsWith("m_", StringComparison.Ordinal)
                        && !wireNames.Contains(e.Name))
                    .OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                    .Select(e => new MpMember
                    {
                        Name = e.Name,
                        Type = e.Type,
                        Kind = Classify(e.Type, kinds),
                    })
                    .ToArray();

                var synthesized = new MpMember[prefix.Length + underscore.Length + type.Wire.Length];
                Array.Copy(prefix, 0, synthesized, 0, prefix.Length);
                Array.Copy(underscore, 0, synthesized, prefix.Length, underscore.Length);
                Array.Copy(type.Wire, 0, synthesized, prefix.Length + underscore.Length, type.Wire.Length);
                type.Wire = synthesized;
                type.Own = synthesized;
            }
        }
    }

    private static MpMember[] BuildActionBasePrefix(Dictionary<string, string> kinds)
    {
        (string Name, string Type)[] members =
        [
            ("_dontLogWarning", "bool"),
            ("_ID", "int"),
            ("_releaseWhenExecutionFinished", "bool"),
            ("_uid", "string"),
            ("scopeMask", "Beyond.GEnums.ScopeName"),
            ("useCurrentScope", "bool"),
            ("useGraphScope", "bool"),
            ("_nextID", "int"),
        ];
        return members
            .Select(m => new MpMember
            {
                Name = m.Name,
                Type = m.Type,
                Kind = Classify(m.Type, kinds),
            })
            .ToArray();
    }

    private static bool IsActionBaseConcrete(string targetType, bool hasDeserialize)
    {
        if (!hasDeserialize)
            return false;
        if (!targetType.StartsWith("Beyond.Gameplay.Actions.", StringComparison.Ordinal))
            return false;
        if (targetType is "Beyond.Gameplay.Actions.ActionBase" or "Beyond.Gameplay.Actions.NodeBase")
            return false;
        if (IsActionHeaderLineage(targetType))
            return false;
        if (targetType.Contains("ActionSerializedMap", StringComparison.Ordinal)
            || targetType.Contains("ActionMapAssetRaw", StringComparison.Ordinal)
            || targetType.Contains("ActionNodePtr", StringComparison.Ordinal)
            || targetType.StartsWith("Beyond.Gameplay.Actions.Param`1", StringComparison.Ordinal)
            || targetType.StartsWith("Beyond.Gameplay.Actions.Getter`1", StringComparison.Ordinal)
            || targetType.StartsWith("Beyond.Gameplay.Actions.List", StringComparison.Ordinal)
            || targetType.StartsWith("Beyond.Gameplay.Actions.GetInteractiveProperty`1", StringComparison.Ordinal)
            || targetType.StartsWith("Beyond.Gameplay.Actions.GetLevelScriptPropertyGeneric`1", StringComparison.Ordinal)
            || targetType.StartsWith("Beyond.Gameplay.Actions.IParam", StringComparison.Ordinal))
            return false;
        return true;
    }

    private static bool IsActionHeaderLineage(string targetType) =>
        targetType is "Beyond.Gameplay.Actions.ActionHeader"
            or "Beyond.Gameplay.Actions.ScriptEventHeader"
            or "Beyond.Gameplay.Actions.EntityEventHeader"
            or "Beyond.Gameplay.Actions.LevelEventHeader"
            or "Beyond.Gameplay.Actions.MissionEventHeader"
        || targetType.Contains(".ScriptEvent.", StringComparison.Ordinal)
        || targetType.Contains(".EntityEvent.", StringComparison.Ordinal)
        || targetType.Contains(".LevelEvent.", StringComparison.Ordinal)
        || targetType.Contains(".MissionEvent.", StringComparison.Ordinal)
        || targetType.StartsWith("Beyond.Gameplay.Actions.On", StringComparison.Ordinal);

    private static bool IsAbilityConcrete(MpType type)
    {
        if (type.TargetType == "AbilityActionData")
            return false;
        string wrapper = type.WrapperType;
        if (wrapper.Contains("Beyond_Gameplay_Actions_", StringComparison.Ordinal)
            || wrapper.Contains("Beyond_Gameplay_AI_", StringComparison.Ordinal))
            return false;
        if (type.MinFieldOffset is int offset && offset < 32)
            return false;
        // Finder/Validator/PostProcessor Data is not AbilityActionData, even when the
        // schema collapsed the nested type to bare Data.
        if (type.WrapperType.Contains("_Selector_", StringComparison.Ordinal)
            && (type.WrapperType.Contains("Finder", StringComparison.Ordinal)
                || type.WrapperType.Contains("Validator", StringComparison.Ordinal)
                || type.WrapperType.Contains("Processor", StringComparison.Ordinal)))
            return false;
        return type.TargetType == "Data" || type.TargetType.EndsWith("ActionData", StringComparison.Ordinal);
    }

    private static bool IsBackingField(string name) =>
        name.StartsWith('<') && name.Contains("k__BackingField", StringComparison.Ordinal);

    private static MpMember[] ReadMembers(JsonElement wrapper, string property, Dictionary<string, string> kinds)
    {
        if (!wrapper.TryGetProperty(property, out var array))
            return [];
        var members = new List<MpMember>();
        foreach (var item in array.EnumerateArray())
        {
            string? name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            string? type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (name == null || type == null)
                continue;
            members.Add(new MpMember { Name = name, Type = type, Kind = Classify(type, kinds) });
        }

        return members.ToArray();
    }

    internal static MpKind Classify(string type, Dictionary<string, string>? kinds = null)
    {
        switch (type)
        {
            case "bool" or "System.Boolean":
                return MpKind.Bool;
            case "sbyte" or "byte" or "short" or "ushort" or "int" or "uint"
                or "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "UnityEngine.LayerMask":
                return MpKind.Int32;
            case "long" or "System.Int64":
                return MpKind.Int64;
            case "ulong" or "System.UInt64":
                return MpKind.UInt64;
            case "float" or "System.Single":
                return MpKind.Float32;
            case "double" or "System.Double":
                return MpKind.Float64;
            case "string" or "System.String":
                return MpKind.String;
            case "UnityEngine.Vector2":
                return MpKind.Vector2;
            case "UnityEngine.Vector3":
                return MpKind.Vector3;
            case "UnityEngine.Vector4" or "UnityEngine.Quaternion" or "UnityEngine.Color":
                return MpKind.Vector4;
            case "UnityEngine.Vector2Int":
                return MpKind.Vector2Int;
            case "UnityEngine.Vector3Int":
                return MpKind.Vector3Int;
            case "UnityEngine.AnimationCurve":
                return MpKind.AnimationCurve;
        }

        if (type.EndsWith("[]", StringComparison.Ordinal) || type.StartsWith("System.Collections.Generic.List`1<", StringComparison.Ordinal))
            return MpKind.List;
        if (type.StartsWith("System.Collections.Generic.Dictionary`2<", StringComparison.Ordinal)
            || type.StartsWith("Beyond.SerializeFieldDictionary`2<", StringComparison.Ordinal))
            return MpKind.Dictionary;
        if (type.StartsWith("System.Nullable`1<", StringComparison.Ordinal))
            return MpKind.Nullable;

        if (kinds != null && kinds.TryGetValue(type, out string? kind))
        {
            if (kind == "enum")
                return MpKind.Enum;
            if (kind is "int32" or "uint32")
                return MpKind.Int32;
            if (kind == "float32")
                return MpKind.Float32;
            if (kind == "bool")
                return MpKind.Bool;
            if (kind == "string")
                return MpKind.String;
        }

        return MpKind.Object;
    }

    private static string Required(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.GetString() is not { } text)
            throw new InvalidDataException($"schemas.json wrapper is missing {name}.");
        return text;
    }
}
