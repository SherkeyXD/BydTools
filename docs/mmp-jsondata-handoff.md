# MemoryPack JsonData 交接

写于 2026-11-09 16:42（Asia/Shanghai）。给本机 Mac 上的下一轮工作用。云端代理没有继续做 v16，也没有改解码器或 schema。

## 接下来要做成什么样

schema 这套 JSON 本身就是通用的 MemoryPack 定义。任何理解其结构的工具，只要加上原始文件和 StringPool，就能把这个游戏里用 MemoryPack 打包的文件还原成明文。不靠游戏类型名硬编码，不靠看首字节猜两种布局，不按失败样本打补丁。

规则必须写在自描述的 schema JSON 里，至少包括：

- 完整类型身份，含嵌套类型和泛型
- 继承，以及基类字段如何进入 wire
- 字段的 wire 顺序
- union 映射
- 枚举宽度和非托管布局
- 容器、字符串、StringPool 的读取方式

解释器可以实现 schema 里写明的协议操作。碰到 schema 没声明的规则，要明确报缺失并留下证据，不能把错位掩盖成「读成功」。目标是有证据的完整规则。v15 的 ok 数不是语义正确性的证明。

StringPool 的原文不要塞进 schema。原始数据文件和 StringPool 仍然是解码输入，但读取方式必须在 schema 里声明。

## 不要做的事

- 不要再做一轮「v16 特例」去抬 ok 计数。
- 不要给 `LevelInteractiveData` 发明第 26 个字段。已审计的 native formatter 期望 25，metadata 是 14+11。332 个文件头是 26。根类型关联、生产版本、运行时覆盖都还没排除，不能认定成版本差。
- 不要把 `UnityEngine.AnimationCurve` 别名成 `Beyond.FAnimationCurve`。布局不同。
- 不要发明经典的 `[MemoryPackable]` / `[MemoryPackUnion]`。这个游戏用的是方法体 jumptable。

## 代码和正式结果

仓库是 [SherkeyXD/BydTools](https://github.com/SherkeyXD/BydTools)。PR 是 [#7](https://github.com/SherkeyXD/BydTools/pull/7)，未合并。

| 项 | 值 |
|---|---|
| 分支 | `cursor/memorypack-jsondata-f4b3` |
| 基线 | `rft` |
| 当前 tip | `47efac696fbfe687460a32f5db2ffd0327b4e906` |
| tip 时间 | 2026-11-08 04:15（Asia/Shanghai） |
| 说明 | Decode spawner unions, path hashes, and optional vectors. |

最新正式全量仍是 v15，没有 v16 结果：

| 项 | 数量 |
|---|---:|
| ok | 14486 |
| failed | 5016 |
| plain JSON | 3824 |
| total | 23326 |
| MemoryPack 可解率 | 14486 / 19502 = 74.3% |

本机拉代码：

```text
git fetch origin cursor/memorypack-jsondata-f4b3
git checkout cursor/memorypack-jsondata-f4b3
```

解码器在 `BydTools.Formats/MemoryPack/`。入口命令是 `decode-jsondata`。测试夹具 schema 是裁过的，在 `BydTools.Tests/Fixtures/MemoryPack/`。README 里的 ok 14486 对的是这次 tip，不是新规则下的结果。

旧的本地工作树不保证等于这个 tip。以 PR 分支为准。

## 本机数据在哪

云端 Linux 虚拟机读不到 Mac 路径。交接目录在本机：

```text
/Users/sunyinzhen.325/Downloads/mmp/endfield-mmp-handoff
```

对应 ZIP `endfield-mmp-handoff-20261109.zip`，SHA256：

```text
42b4068d81e58e70f96d81cadbba39499e0cbb44f0880b2334ba68cf61d1f744
```

包里有：`GameAssembly.dll`、明文 `global-metadata.dat`（v29）、既有 schema / type_index / type.cs / union maps、`beyond-mp-schema` 源码和现成 Linux ELF、23356 个未经解码重写的 VFS JsonData（含 StringPool）、README、清单、哈希、审计摘录。

审计脚本当时用过的输入哈希（路径是另一台机器上的，不要在 Mac 上照抄）：

| 文件 | SHA256 |
|---|---|
| GameAssembly.dll | `ab00f1c41e13edf9816f142b166e3c2536d5bc7707992c39e5ed52d1cc888e2c` |
| global-metadata.dat | `461321d9dca6a832bce3bd2390402efc7c83c145554e97a8058bb68fbec1c8ca` |
| il2cpp.cs | `cc63d21d63efb8564a2be3739253b8bb4af28c77729ad54bdb3b5c0c0016486a` |
| schemas.json | `d391371689b4538189cd03c95a14d4f783a0e34d10e0e0861eded390b692c134` |
| type_index.json | `6536bb1b8884d04bf56b28ebf0565bae4e6e666edbd168a697cfcd19ecdfd20d` |
| union-map.json | `dd5e7321faf63b477f9df2d862fce9c546d31842d4e0b43756f3d73f93d4707a` |
| v15 memorypack-report.json | `2015e7cf173862af253cb454cdf61ce393b440c010805bd78981200389008f4f` |

云端只另外留了一份 3.3MB 审计包 `mmp-native-audit-20261109.tar.gz`（上传名带哈希后缀）。那不是 149MiB 交接包，里面没有 GameAssembly、metadata 和 JsonData 语料。

## 已完成的原生审计（只读，未修）

静态全库规则还没抽完，不能说已经 100% 还原。

- metadata 里有 12405 个序列化 setter。旧 schema 只有 7938 个。漏了 4467 个，分布在 2007 个类里，全是下划线开头的字段。
- 嵌套类型和泛型的身份在导出时有信息损失。多个不相干的嵌套类型会被收成同一个名字 `Data`。
- `AnimationConfig` 漏了首项 `_fallbackMontages`。native 要 15 个成员，schema 是 14。独立严格探针里，24 个旧失败样本读到了准确 EOF；剩下 115 个没有证明。
- `SerializeFieldDictionary` V2 使用固定的 1 字节容器头：`FF` 是 null，`00` 不是 null。之前解码器里「presence bool / 看下一字节猜布局」的说法要作废。
- Interactive 的 332 个文件头是 26。审计到的 native 期望 25，metadata 是 14+11。原因未定性。

## v15 失败分布

这是旧解码器的报错分布，用来知道错位出现在哪，不是下一轮要逐条打补丁的清单。

| 文件夹 | MemoryPack | 可解 | 不可解 | 表面现象 |
|---|---:|---:|---:|---|
| LevelScriptData | 5505 | 2505 | 3000 | 动作图错位。最早的整齐信号是 `SetBool` / `SetInt` 在偏移 10 处为 10 字段，合成后的 wire 是 8。大量 `memberCount 254`、`Boolean 255`、非法 List count 是游标已经偏了之后的症状 |
| SkillData | 2927 | 2246 | 681 | 约 509 个是文件后部的 `Boolean 255`。`LockCameraAimActionData` 有 113 个，看到的头是 54，当时的 ability 布局是 18，偏移已经很深 |
| LevelData | 1158 | 822 | 336 | 非法 List、`Nullable<float>` 被当成对象头、`Shape` 3 对 2 |
| Interactive | 1022 | 689 | 333 | 332 个在偏移 0 就是 memberCount 26，wire 是 25 |
| CharInteractPerformCfgs | 266 | 0 | 266 | `CharInteractPerformActor` 在偏移 47–113 读到 19 / 64 / 128，wire 是 13 |
| BuffData | 3394 | 3228 | 166 | 图标和堆叠那条级联已经过去。剩下是后部 Boolean / List |
| AnimationConfig | 139 | 0 | 139 | `AnimatedShaderPropertyConfig` 20 对 1，`AnimMontageData` 4 对 2，以及读过 EOF |
| 其余 | | | 96 | `NpcRuntimeProxyData` 29 个在偏移 9 看到 121、wire 28；模板脚本 38；再往后更少 |

全是明文、没有 MemoryPack 的文件夹：MissionRuntimeAsset 1116、MapConfig 226、UILevelMapLoadConfig 23、AIConfig 6，以及若干更小的目录。SpawnerConfig 674 和 LevelConfig 240 在 v15 是 100% ok。这个 ok 同样不是新规则下的证明。

## 旧 schema 长什么样

`schemas.json` 由 `beyond-mp-schema` 生成。每条 wrapper 同时有三张表：

- `wire_order`：包装类 `__name__` 属性的 metadata 顺序。导出者注明它等于 native `Deserialize` 的调用顺序。这是二进制成员顺序。
- `members_from_wrapper_props`：同一组属性的另一份。
- `members_from_target_fields`：目标类实例字段的声明顺序和偏移。文件里写明这不是 wire 顺序。

`typedef_index` 在 `MemoryPack.Beyond.dll`，`target_typedef_index` 在 `Gameplay.Beyond.dll`。`orders_differ` 为 true 时两张表顺序不同，例如 `AssignPair`。

`LevelInteractiveData` 这条导出只有叶子上的 11 个字段，第一个字段偏移已经是 112。父类 `LevelEntityData` 的 14 个字段不在这条 `wire_order` 里。v15 解码器在代码里把父类前缀接了上去，得到 25。这是游戏专属补丁，不是 schema 自己声明的继承。

`type_index.json` 用文件名找类型：`by_basename` 和 `by_type`。

## 旧解码器里已经写死、下一轮要拿掉的东西

这些都在 `MemoryPackDecoder.cs` / `MemoryPackSchema.cs`，靠类型名分支，不符合上面的验收。

- 非托管布局按名字读：`GUIDProxy`、`GameplayTag`、`StringPathHash`、`DispelConfig`、`OrderPriorityConfig`、`ConditionValue`、`AudioBlackScreenBehaviour`、`AnimationClipAsyncInfo`、`FMontageTransitionInfo`、`Optional<Vector3>`、`UnityEngine.AnimationCurve`。
- 枚举宽度按叶子名：`LifeType` / `IdentifierType` 读 1 字节，`StackingType` / `AdditiveEffectType` 读 int16，其余读 int32。
- `List<GameplayTag>` 看首字节是不是 1，决定走 formatter 头还是裸 int32。
- 字典值用「下一字节像不像 memberCount」决定是否吃掉一个 presence 字节。审计结论是字典 V2 有固定 1 字节头，这条猜测要删。
- union 是否启用、父类前缀接多少，都写死了具体 wrapper 名：ActionBase、GameCondition、LevelEntityData、TargetFinder、Spawner、Buff 修饰器、若干 Ability 中间层等。
- 外部 union-map JSON 是从 GameAssembly jumptable 另存的，没有并进自描述 schema。

协议里已经比较清楚、可以收成 schema 声明而不是删掉的行为：对象头 `memberCount`，255 表示 null，250 后跟 int32 表示宽计数；集合用 int32 count，-1 表示 null；字符串是 StringPool id。这些都要写成声明，而不是留在 C# 类型分支里。

## 本机建议的顺序

1. 打开 `/Users/sunyinzhen.325/Downloads/mmp/endfield-mmp-handoff`，用包内清单核对 SHA256。
2. 改 `beyond-mp-schema` 的导出，把漏掉的下划线 setter、嵌套/泛型全名、继承、union、枚举宽度、非托管布局、容器头和 StringPool 读法写进 schema。缺证据的规则留空并报缺失。
3. 把 BydTools 的解码器收成这个 schema 的解释器，删掉上面那些类型名分支。
4. 用交接包里未经重写的 VFS JsonData 做对照。先证明规则，再看覆盖率。不要为了 ok 数回退到特例。
