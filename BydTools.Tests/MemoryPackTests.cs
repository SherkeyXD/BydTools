using System.Text;
using System.Text.Json.Nodes;
using BydTools.Formats.MemoryPack;
using Xunit;

namespace BydTools.Tests;

public class MemoryPackTests
{
    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "BydTools.Tests", "Fixtures", "MemoryPack");
            if (Directory.Exists(candidate))
                return candidate;
            candidate = Path.Combine(dir.FullName, "Fixtures", "MemoryPack");
            if (File.Exists(Path.Combine(candidate, "schemas.json")))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("MemoryPack fixtures were not found.");
    }

    private static MemoryPackSchema Schema() =>
        MemoryPackSchema.Load(
            Path.Combine(FixtureRoot(), "schemas.json"),
            Path.Combine(FixtureRoot(), "type_index.json")
        );

    private static byte[] Sample(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureRoot(), "samples", name));

    [Fact]
    public void BambooRaft_decodes_every_byte()
    {
        var decoder = new MemoryPackDecoder(Schema());
        JsonNode? node = decoder.Decode(Sample("BambooRaftTaskTable.json"), "Beyond.Gameplay.BambooRaftTaskTable");
        var table = Assert.IsType<JsonObject>(node);
        var data = Assert.IsType<JsonObject>(table["data"]);
        Assert.Equal(9, data.Count);
        JsonObject first = Assert.IsType<JsonObject>(data.First().Value);
        Assert.True(first.ContainsKey("questConfigs"));
        Assert.True(first.ContainsKey("raftId"));
        Assert.NotNull(first["raftId"]);
    }

    [Fact]
    public void Teleport_table_decodes_three_rows()
    {
        var decoder = new MemoryPackDecoder(Schema());
        JsonNode? node = decoder.Decode(
            Sample("CommonSysTeleportValidationDataTable.json"),
            "Beyond.Gameplay.TeleportValidationDataTable"
        );
        var rows = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(node)["teleportValidationDatas"]);
        Assert.Equal(3, rows.Count);
        JsonObject row = Assert.IsType<JsonObject>(rows.First().Value);
        Assert.Equal(0, row["deviation"]!.GetValue<float>());
        Assert.False(row["keepAnim"]!.GetValue<bool>());
        var position = Assert.IsType<JsonArray>(row["position"]);
        Assert.Equal(3, position.Count);
    }

    [Fact]
    public void NpcAtmospheric_empty_dictionary_is_an_object()
    {
        var decoder = new MemoryPackDecoder(Schema());
        JsonNode? node = decoder.Decode(Sample("sample_NpcAtmospheric.json"), "Beyond.Gameplay.NpcAtmosphericDataTable");
        var table = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(node)["dataTable"]);
        Assert.Empty(table);
    }

    [Fact]
    public void Union_map_loads_nested_tables_and_a_flat_legacy_object()
    {
        var maps = JsonDataMemoryPack.LoadUnionMap(Path.Combine(FixtureRoot(), "union-map.json"));
        Assert.Equal(447, maps["AbilityActionData"].Count);
        Assert.Equal(447, maps["Data"].Count);
        Assert.Equal(1364, maps["ActionBase"].Count);
        Assert.Equal(240, maps["ActionHeader"].Count);
        Assert.Equal(1108, maps["PureGetter"].Count);
        Assert.Equal(21, maps["AbstractBehaviorData"].Count);
        Assert.Equal(11, maps["DamageProcessorBase"].Count);
        Assert.Equal(50, maps["ConditionRuntimeBase"].Count);
        Assert.Equal(6, maps["CalculationBase"].Count);
        Assert.Equal("Beyond.Gameplay.Core.DefiniteValueCalculation", maps["CalculationBase"][2]);
        Assert.Equal(
            "Beyond.MemoryPack.Beyond_Gameplay_Core_Selector_HitBoxFinder_DataForMemoryPack",
            maps["Finder"][7]);
        Assert.Equal("PlayAnimationActionData", maps["AbilityActionData"][296]);

        using var dir = new TempDir();
        string flat = Path.Combine(dir.Path, "flat.json");
        File.WriteAllText(flat, """{ "296": "PlayAnimationActionData" }""");
        var legacy = JsonDataMemoryPack.LoadUnionMap(flat);
        Assert.Single(legacy);
        Assert.Equal("PlayAnimationActionData", legacy["AbilityActionData"][296]);
    }

    [Fact]
    public void Skill_and_buff_fail_with_a_typed_union_error_or_decode()
    {
        var decoder = new MemoryPackDecoder(Schema());
        AssertHardSample(decoder, Sample("sample_SkillData.json"), "Beyond.Gameplay.Core.SkillData");
        AssertHardSample(decoder, Sample("sample_BuffData.json"), "Beyond.Gameplay.Core.BuffData");
    }

    [Fact]
    public void Batch_reports_plain_json_skips_and_folder_counts()
    {
        using var input = new TempDir();
        using var output = new TempDir();
        File.WriteAllBytes(
            Path.Combine(input.Path, "BambooRaftTaskTable.json"),
            Sample("BambooRaftTaskTable.json")
        );
        Directory.CreateDirectory(Path.Combine(input.Path, "AtmosphericNpcData"));
        File.WriteAllBytes(
            Path.Combine(input.Path, "AtmosphericNpcData", "1001.json"),
            Sample("sample_NpcAtmospheric.json")
        );
        Directory.CreateDirectory(Path.Combine(input.Path, "notes"));
        File.WriteAllText(Path.Combine(input.Path, "notes", "hello.json"), "{\n  \"a\": 1\n}");
        Directory.CreateDirectory(Path.Combine(input.Path, "pools"));
        File.WriteAllBytes(Path.Combine(input.Path, "pools", "blob.json"), "USPC"u8.ToArray());

        JsonDataDecodeReport report = JsonDataMemoryPack.DecodeTree(
            Schema(),
            input.Path,
            output.Path,
            jobs: 2
        );
        Assert.Equal(2, report.Ok);
        Assert.Equal(1, report.PlainJson);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Failed);
        Assert.True(report.ByFolder.ContainsKey("AtmosphericNpcData"));
        string text = File.ReadAllText(Path.Combine(output.Path, "notes", "hello.json"));
        Assert.Contains("\"a\"", text, StringComparison.Ordinal);
        Assert.Contains("questConfigs", File.ReadAllText(Path.Combine(output.Path, "BambooRaftTaskTable.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void String_pool_resolves_block_local_ids()
    {
        string[] block0 = ["alpha", "beta"];
        string[] block1 = ["gamma"];
        var pool = new BeyondStringPool([block0, block1]);
        Assert.Equal("alpha", pool.Format(0));
        Assert.Equal("beta", pool.Format(1));
        Assert.Equal("gamma", pool.Format(2));
        Assert.Equal("", pool.Format(-1));
        Assert.Equal("", pool.Format(-2));
        Assert.Equal("#99", pool.Format(99));

        byte[] payload = StringPayload(["hello", "world"]);
        byte[] file = WrapPool(payload, compressed: false);
        Assert.Equal(["hello", "world"], BeyondStringPool.ReadBlock(file));

        byte[] lzFile = WrapPool(payload, compressed: true);
        Assert.Equal(["hello", "world"], BeyondStringPool.ReadBlock(lzFile));

        var prefixed = new byte[4 + file.Length];
        BitConverter.TryWriteBytes(prefixed.AsSpan(0), 27);
        file.CopyTo(prefixed.AsSpan(4));
        Assert.Equal(["hello", "world"], BeyondStringPool.ReadBlock(prefixed));

        using var poolDir = new TempDir();
        File.WriteAllBytes(Path.Combine(poolDir.Path, "StringPool0.byte"), prefixed);
        File.WriteAllBytes(Path.Combine(poolDir.Path, "StringPool1.byte"), file);
        File.WriteAllBytes(Path.Combine(poolDir.Path, "StringPoolHashIndex.byte"), "SHPI"u8.ToArray());
        var loaded = BeyondStringPool.Load(poolDir.Path);
        Assert.Equal(2, loaded.BlockCount);
        Assert.Equal("hello", loaded.Format(0));
        Assert.Equal("world", loaded.Format(1));
        Assert.Equal("hello", loaded.Format(2));
    }


    [Fact]
    public void GameplayTag_reads_unmanaged_int32()
    {
        var decoder = new MemoryPackDecoder(Schema());
        // tagId 0x01020304 little-endian — must NOT be read as object memberCount.
        JsonNode? node = decoder.Decode(new byte[] { 0x04, 0x03, 0x02, 0x01 }, "Beyond.Gameplay.Core.GameplayTag");
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Equal(0x01020304, obj["tagId"]!.GetValue<int>());
    }

    [Fact]
    public void AnimationClipAsyncInfo_reads_unmanaged_36_bytes()
    {
        var decoder = new MemoryPackDecoder(Schema());
        var payload = new byte[36];
        BitConverter.TryWriteBytes(payload.AsSpan(0), 0x1122334455667788L);
        BitConverter.TryWriteBytes(payload.AsSpan(8), 1.5f);
        BitConverter.TryWriteBytes(payload.AsSpan(12), 30f);
        BitConverter.TryWriteBytes(payload.AsSpan(16), 1f);
        BitConverter.TryWriteBytes(payload.AsSpan(20), 2f);
        BitConverter.TryWriteBytes(payload.AsSpan(24), 3f);
        BitConverter.TryWriteBytes(payload.AsSpan(28), 0.25f);
        payload[32] = 1;
        payload[33] = 0;
        JsonNode? node = decoder.Decode(payload, "Beyond.Montage.AnimationClipAsyncInfo");
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Equal(0x1122334455667788L, obj["montagePathHash"]!.GetValue<long>());
        Assert.Equal(1.5f, obj["length"]!.GetValue<float>());
        Assert.True(obj["isHumanoid"]!.GetValue<bool>());
        Assert.False(obj["isLooping"]!.GetValue<bool>());
    }

    [Fact]
    public void UnityAnimationCurve_reads_header3_and_28byte_keys()
    {
        var decoder = new MemoryPackDecoder(Schema());
        // count=3, pre=1, post=2, keyCount=1, one zero keyframe
        var payload = new byte[1 + 4 + 4 + 4 + 28];
        payload[0] = 3;
        BitConverter.TryWriteBytes(payload.AsSpan(1), 1);
        BitConverter.TryWriteBytes(payload.AsSpan(5), 2);
        BitConverter.TryWriteBytes(payload.AsSpan(9), 1);
        JsonNode? node = decoder.Decode(payload, "UnityEngine.AnimationCurve");
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Equal(1, obj["preWrapMode"]!.GetValue<int>());
        Assert.Equal(2, obj["postWrapMode"]!.GetValue<int>());
        Assert.Single(Assert.IsType<JsonArray>(obj["keys"]));
    }

    [Fact]
    public void FAnimationCurve_memberCount_0_yields_empty_object()
    {
        var decoder = new MemoryPackDecoder(Schema());
        // MemoryPack versioning: count 0 means every wire member is default/omitted.
        JsonNode? node = decoder.Decode(new byte[] { 0 }, "Beyond.FAnimationCurve");
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Empty(obj);
    }

    [Fact]
    public void FAnimationCurve_partial_memberCount_reads_wire_prefix()
    {
        var decoder = new MemoryPackDecoder(Schema());
        // wire_order: keys, postWrapMode, preWrapMode — count 1 reads only keys (null array).
        var payload = new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF };
        JsonNode? node = decoder.Decode(payload, "Beyond.FAnimationCurve");
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Single(obj);
        Assert.Null(obj["keys"]);
        Assert.False(obj.ContainsKey("postWrapMode"));
        Assert.False(obj.ContainsKey("preWrapMode"));
    }

    [Fact]
    public void LevelInteractiveData_includes_LevelEntityData_prefix()
    {
        // memberCount 25 = LevelEntityData wire 14 + LevelInteractiveData wire 11.
        var bytes = new List<byte> { 25 };
        bytes.AddRange(I32(0)); // aoiRadiusType
        bytes.AddRange(U64(0)); // belongLevelScriptId
        bytes.AddRange(I32(0)); // createState
        bytes.AddRange(U64(0)); // dependencyGroupId
        bytes.AddRange(I32(-1)); // entityDataIdKey
        bytes.AddRange(I32(0)); // entityType
        bytes.Add(0); // forceLoad
        bytes.Add(1); // keepCrossMap
        bytes.AddRange(U64(42)); // levelLogicId
        bytes.Add(0); // overrideSendDieEvent
        bytes.AddRange(F32(1));
        bytes.AddRange(F32(2));
        bytes.AddRange(F32(3)); // position
        bytes.AddRange(F32(0));
        bytes.AddRange(F32(0));
        bytes.AddRange(F32(0)); // rotation
        bytes.AddRange(F32(1));
        bytes.AddRange(F32(1));
        bytes.AddRange(F32(1)); // scale
        bytes.Add(0); // sendDieEvent
        bytes.AddRange(I32(0)); // componentProperties
        bytes.AddRange(I32(0)); // globalIntKeyList
        bytes.AddRange(I32(0)); // globalProperties
        bytes.Add(0); // hideInDialog
        bytes.Add(1); // isClientOnly
        bytes.Add(1); // isLocked
        bytes.AddRange(I32(0)); // mapIntKeyList
        bytes.AddRange(I32(0)); // mapProperties
        bytes.AddRange(F32(2.5f)); // modelScale
        bytes.Add(255); // progressLockCondition null
        bytes.AddRange(I32(0)); // properties

        var decoder = new MemoryPackDecoder(Schema());
        JsonObject node = Assert.IsType<JsonObject>(
            decoder.Decode(bytes.ToArray(), "Beyond.Gameplay.LevelInteractiveData")
        );
        Assert.Equal(42ul, node["levelLogicId"]!.GetValue<ulong>());
        Assert.True(node["keepCrossMap"]!.GetValue<bool>());
        Assert.True(node["isLocked"]!.GetValue<bool>());
        Assert.True(node["isClientOnly"]!.GetValue<bool>());
        Assert.Equal(2.5f, node["modelScale"]!.GetValue<float>());
        Assert.Null(node["progressLockCondition"]);
        var position = Assert.IsType<JsonArray>(node["position"]);
        Assert.Equal(1f, position[0]!.GetValue<float>());
    }

    [Fact]
    public void CalculationBase_fork_matches_the_unique_wire_length()
    {
        // BreakingAttackCalculation is the only *Calculation with memberCount 2.
        var bytes = new List<byte> { 9, 2 };
        bytes.AddRange(BlackboardDouble(1.25f, useKey: false));
        bytes.AddRange(BlackboardDouble(2.5f, useKey: true));
        var decoder = new MemoryPackDecoder(Schema());
        JsonObject node = Assert.IsType<JsonObject>(
            decoder.Decode(bytes.ToArray(), "Beyond.Gameplay.Core.CalculationBase")
        );
        Assert.Equal("Beyond.Gameplay.Core.BreakingAttackCalculation", node["$type"]!.GetValue<string>());
        Assert.Equal(9, node["$tag"]!.GetValue<int>());
        Assert.Equal(1.25f, Assert.IsType<JsonObject>(node["atkScale"])["value"]!.GetValue<float>());
        Assert.Equal(2.5f, Assert.IsType<JsonObject>(node["multiplier"])["value"]!.GetValue<float>());
    }

    [Fact]
    public void GameCondition_subclass_prefixes_the_base_wire()
    {
        // CheckCurrentDungeon: GameCondition's 4 fields, then _dungeonId.
        var bytes = new List<byte> { 5 };
        bytes.AddRange(I32(3)); // scopeMask
        bytes.AddRange(I32(-1)); // uniqueId
        bytes.Add(1); // useCurrentScope
        bytes.Add(0); // useGraphScope
        bytes.Add(0); // Param memberCount 0
        var decoder = new MemoryPackDecoder(Schema());
        JsonObject node = Assert.IsType<JsonObject>(
            decoder.Decode(bytes.ToArray(), "Beyond.Gameplay.Conditions.CheckCurrentDungeon")
        );
        Assert.Equal(3, node["scopeMask"]!.GetValue<int>());
        Assert.Equal("", node["uniqueId"]!.GetValue<string>());
        Assert.True(node["useCurrentScope"]!.GetValue<bool>());
        Assert.False(node["useGraphScope"]!.GetValue<bool>());
        Assert.IsType<JsonObject>(node["_dungeonId"]);
    }

    [Fact]
    public void Icon_stack_buff_decodes_every_byte()
    {
        var decoder = new MemoryPackDecoder(Schema());
        JsonObject buff = Assert.IsType<JsonObject>(
            decoder.Decode(Sample("buff_cc_chr_atb_recoverspeed_down_icon.json"), "Beyond.Gameplay.Core.BuffData")
        );
        Assert.True(buff["hasIcon"]!.GetValue<bool>());
        Assert.False(buff["finishOnRepatriate"]!.GetValue<bool>());
        Assert.Equal(1, buff["lifeType"]!.GetValue<int>());
        Assert.Equal("#90967", buff["id"]!.GetValue<string>());
        JsonObject duration = Assert.IsType<JsonObject>(buff["duration"]);
        Assert.Equal(3f, duration["value"]!.GetValue<float>());
        JsonObject icon = Assert.IsType<JsonObject>(buff["iconConfig"]);
        Assert.Equal("#90966", icon["_spritePath"]!.GetValue<string>());
        JsonObject order = Assert.IsType<JsonObject>(icon["_orderPriorityConfig"]);
        Assert.Equal(110, order["priorityEnum"]!.GetValue<int>());
        Assert.Equal(0, order["priorityValue"]!.GetValue<int>());
        JsonObject stacking = Assert.IsType<JsonObject>(buff["stackingSettings"]);
        Assert.Equal(1, stacking["maxStackCnt"]!.GetValue<int>());
        Assert.Equal(-1f, Assert.IsType<JsonObject>(buff["triggerInterval"])["value"]!.GetValue<float>());
    }

    [Fact]
    public void V10_bucket_wires_match_native_counts()
    {
        MemoryPackSchema schema = Schema();
        MpType vulnerable = schema.ResolveNamed(
            "Beyond.MemoryPack.Beyond_Gameplay_Core_VulnerableAction_DataForMemoryPack");
        MpType weak = schema.ResolveNamed(
            "Beyond.MemoryPack.Beyond_Gameplay_Core_WeakAction_DataForMemoryPack");
        Assert.Equal(14, vulnerable.AbilityPayload!.Length);
        Assert.Equal(13, weak.AbilityPayload!.Length);
        Assert.Equal("subType", vulnerable.AbilityPayload[^1].Name);

        MpType effect = schema.Require("EffectActionData", hint: null);
        Assert.Equal(20, effect.AbilityPayload!.Length);

        MpType step = schema.Require("PlayAnimationWithStepData", hint: null);
        Assert.Equal(30, step.AbilityPayload!.Length);
        Assert.Equal("animName", step.AbilityPayload[4].Name);

        MpType radio = schema.Require("Beyond.Gameplay.Actions.PlayRadioAndWait", hint: null);
        Assert.Equal(13, radio.Wire.Length);
        Assert.Contains(radio.Wire, m => m.Name == "_radioId");

        MpType mvsc = schema.Require(
            "Beyond.Gameplay.Core.ModelViewStateController.MVSCEffectBehaviorData",
            hint: null);
        Assert.Equal(17, mvsc.Wire.Length);

        MpType anim = schema.Require(
            "Beyond.Gameplay.Core.CharInteractPerform.CharAnimActionData",
            hint: null);
        Assert.Equal(31, anim.Wire.Length);
        Assert.Equal("actorIndex", anim.Wire[15].Name);
    }

    [Fact]
    public void Finder_tag_7_is_HitBox_not_Ability_Data()
    {
        MemoryPackSchema schema = Schema();
        MpType hitBox = schema.ResolveNamed(
            "Beyond.MemoryPack.Beyond_Gameplay_Core_Selector_HitBoxFinder_DataForMemoryPack");
        Assert.Equal(8, hitBox.Wire.Length);
        Assert.Equal(
            new[]
            {
                "autoSetTargetFaction", "checkAlive", "containsUnMarkable", "factionTarget", "targetFactionType",
                "checkIntUnSelectableTag", "shapeList", "targetObjectType",
            },
            hitBox.Wire.Select(m => m.Name).ToArray());

        var maps = JsonDataMemoryPack.LoadUnionMap(Path.Combine(FixtureRoot(), "union-map.json"));
        var decoder = new MemoryPackDecoder(schema, unionMaps: maps);
        // SelectorData: finderData tag 7, then empty post/validator lists.
        // Ability Data tag 7 is AddAIMarker; Finder tag 7 is HitBox (count 8 = TargetFinder 5 + leaf 3).
        byte[] payload =
        [
            3,
            7, 8,
            0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0,
            0, 0, 0, 0,
            1, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
        ];
        JsonObject node = Assert.IsType<JsonObject>(decoder.Decode(payload, "SelectorData"));
        JsonObject finder = Assert.IsType<JsonObject>(node["finderData"]);
        Assert.Equal(7, finder["$tag"]!.GetValue<int>());
        Assert.Contains("HitBoxFinder", finder["$type"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, finder["targetObjectType"]!.GetValue<int>());
    }

    [Fact]
    public void AdditiveEffectType_is_a_signed_int16()
    {
        var decoder = new MemoryPackDecoder(Schema());
        // memberCount 4: constValue = -1 (FF FF), idRef 0, paramSource 0, path -1.
        byte[] payload =
        [
            4, 0xFF, 0xFF,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0xFF, 0xFF, 0xFF, 0xFF,
        ];
        JsonObject node = Assert.IsType<JsonObject>(
            decoder.Decode(payload, "Beyond.Gameplay.Actions.Param`1<AdditiveEffectType>")
        );
        Assert.Equal(-1, node["constValue"]!.GetValue<int>());
        Assert.Equal(0, node["idRef"]!.GetValue<int>());
        Assert.Equal("", node["path"]!.GetValue<string>());
    }

    [Fact]
    public void DispelConfig_reads_unmanaged_8_bytes()
    {
        var decoder = new MemoryPackDecoder(Schema());
        byte[] payload = [1, 0, 0, 0, 2, 0, 0, 0];
        JsonObject obj = Assert.IsType<JsonObject>(decoder.Decode(payload, "Beyond.Gameplay.Core.DispelConfig"));
        Assert.True(obj["canBeDispelled"]!.GetValue<bool>());
        Assert.Equal(2, obj["dispelledLevel"]!.GetValue<int>());
    }

    [Fact]
    public void BlackboardImpactValue_wire_is_int_base_plus_useCustomValue()
    {
        MemoryPackSchema schema = Schema();
        MpType type = schema.Require("Beyond.Gameplay.Core.BlackboardImpactValue", hint: null);
        Assert.Equal(4, type.Wire.Length);
        Assert.Equal(
            new[] { "blackboardKey", "useBlackboardKey", "value", "useCustomValue" },
            type.Wire.Select(m => m.Name).ToArray()
        );
    }

    [Fact]
    public void BuffData_modifier_lists_use_concrete_wrappers_and_icon_is_20()
    {
        MemoryPackSchema schema = Schema();
        MpType buff = schema.Require("Beyond.Gameplay.Core.BuffData", hint: null);
        MpMember damage = buff.Wire.First(m => m.Name == "damageModifier");
        Assert.Contains("DamageModifier_DataForMemoryPack", damage.Type, StringComparison.Ordinal);
        MpType icon = schema.Require("Beyond.Gameplay.Core.BuffIconConfig", hint: null);
        Assert.Equal(20, icon.Wire.Length);
        Assert.Equal("_orderPriorityConfig", icon.Wire[0].Name);
        Assert.Equal("_spritePath", icon.Wire[1].Name);
        Assert.Equal(
            MpKind.Object,
            schema.KindOf("Beyond.MemoryPack.Beyond_Gameplay_Core_DamageModifier_DataForMemoryPack")
        );
    }

    [Fact]
    public void ContinuousFindTarget_ability_payload_is_19()
    {
        MemoryPackSchema schema = Schema();
        MpType type = schema.ResolveNamed(
            "Beyond.MemoryPack.Beyond_Gameplay_Core_ContinuousFindTargetAction_DataForMemoryPack"
        );
        Assert.NotNull(type.AbilityPayload);
        Assert.Equal(19, type.AbilityPayload!.Length);
        Assert.Equal("isEnable", type.AbilityPayload[0].Name);
        Assert.Equal("findInterval", type.AbilityPayload[^1].Name);
        Assert.Contains(type.AbilityPayload, m => m.Name == "targetGroupKey");
    }

    [Fact]
    public void ShowHideActor_and_start_dialog_teleport_include_inherited_wires()
    {
        MemoryPackSchema schema = Schema();
        MpType show = schema.Require("ShowHideActorData", hint: null);
        Assert.Equal(10, show.Wire.Length);
        Assert.Equal("isEnable", show.Wire[0].Name);
        Assert.Equal("targetSettings", show.Wire[^1].Name);

        MpType dialog = schema.Require("Beyond.Gameplay.Actions.StartDialogAction", hint: null);
        MpType teleport = schema.Require("Beyond.Gameplay.Actions.StartDialogAndTeleportAction", hint: null);
        Assert.Equal(15, dialog.Wire.Length);
        Assert.Equal(16, teleport.Wire.Length);
        Assert.Equal("_dialogId", teleport.Wire[^1].Name);
    }

    private static byte[] BlackboardDouble(float value, bool useKey)
    {
        var bytes = new List<byte> { 3 };
        bytes.AddRange(I32(-1));
        bytes.Add(useKey ? (byte)1 : (byte)0);
        bytes.AddRange(F32(value));
        return bytes.ToArray();
    }

    private static byte[] I32(int value)
    {
        var buf = new byte[4];
        BitConverter.TryWriteBytes(buf, value);
        return buf;
    }

    private static byte[] U64(ulong value)
    {
        var buf = new byte[8];
        BitConverter.TryWriteBytes(buf, value);
        return buf;
    }

    private static byte[] F32(float value)
    {
        var buf = new byte[4];
        BitConverter.TryWriteBytes(buf, value);
        return buf;
    }

    private static void AssertHardSample(MemoryPackDecoder decoder, byte[] data, string root)
    {
        var ex = Assert.Throws<MemoryPackUnionException>(() => decoder.Decode(data, root));
        Assert.Equal("AbilityActionData", ex.StaticType);
        Assert.True(ex.Tag is int tag && tag >= 0);
        Assert.True(ex.Offset > 0);
        Assert.NotEmpty(ex.Candidates);
    }

    private static byte[] StringPayload(string[] values)
    {
        byte[][] blobs = values.Select(v => Encoding.UTF8.GetBytes(v)).ToArray();
        int blobBytes = blobs.Sum(b => 4 + b.Length);
        var payload = new byte[4 + values.Length * 4 + blobBytes];
        BitConverter.TryWriteBytes(payload.AsSpan(0), values.Length);
        int cursor = 0;
        for (int i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(4 + i * 4), cursor);
            int at = 4 + values.Length * 4 + cursor;
            BitConverter.TryWriteBytes(payload.AsSpan(at), blobs[i].Length);
            blobs[i].CopyTo(payload.AsSpan(at + 4));
            cursor += 4 + blobs[i].Length;
        }

        return payload;
    }

    private static byte[] WrapPool(byte[] payload, bool compressed)
    {
        byte[] stored = compressed ? Lz4Literal(payload) : payload;
        var file = new byte[24 + stored.Length];
        "USPC"u8.CopyTo(file);
        BitConverter.TryWriteBytes(file.AsSpan(4), 1);
        BitConverter.TryWriteBytes(file.AsSpan(8), compressed ? 1 : 0);
        BitConverter.TryWriteBytes(file.AsSpan(12), BitConverter.ToInt32(payload));
        BitConverter.TryWriteBytes(file.AsSpan(16), payload.Length);
        BitConverter.TryWriteBytes(file.AsSpan(20), stored.Length);
        stored.CopyTo(file.AsSpan(24));
        return file;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "byd-mp-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] Lz4Literal(byte[] payload)
    {
        var block = new List<byte> { (byte)(Math.Min(payload.Length, 15) << 4) };
        if (payload.Length >= 15)
        {
            int rest = payload.Length - 15;
            while (rest >= 255)
            {
                block.Add(255);
                rest -= 255;
            }

            block.Add((byte)rest);
        }

        block.AddRange(payload);
        return block.ToArray();
    }

}
