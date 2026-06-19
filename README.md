# AutoRocketFuelPlanner

用于《缺氧 (Oxygen Not Included)》的火箭自动加注 Mod。

## 功能

- 自动读取火箭当前目标相关距离信息（反射匹配）。
- 根据目标距离和引擎类型自动计算需要的燃料和氧化剂质量。
- 当玩家手动修改“距离/燃料/氧化剂”中的任意一个值时，自动反算其余参数并回填。
- 默认开启“按火箭类型自动最优计算”，每种引擎自动套用独立参数档案。
- 支持“每种引擎独立可编辑参数档案”，可单独微调某一种火箭而不影响其他类型。
- 自动将计算结果写入火箭油箱/氧化剂罐的可写入目标字段。
- 在火箭详情信息中显示自动加注结果（引擎、目标距离、燃料、氧化剂）。
- 无法读取目标距离时，使用 `FallbackTargetDistance` 兜底。

## 计算公式

- 自动最优模式（默认）：每种引擎使用独立的 `DistancePerKgFuel / OxidizerPerKgFuel / Margin` 档案。
- 若开启 `EnableCustomEngineProfiles`：自动最优模式将改为读取你在配置中填写的“引擎专属参数”。
- 手动模式（关闭 `UsePerEngineOptimalProfiles`）：`FuelKg = TargetDistance / (DistancePerKgFuel * EngineDistanceFactor) * (1 + FuelMarginPercent / 100)`
- 对需要氧化剂的引擎（石油/液氢）：`OxidizerKg = FuelKg * OxidizerPerKgFuel * (1 + OxidizerMarginPercent / 100)`
- 最终结果会叠加 `GlobalFuelAdjustmentPercent` 和 `AdditionalSafetyMarginPercent`。
- 实际写入时会限制在油箱容量和 `MaxAutoFillPercent` 范围内。

## 配置项

见 `Config.cs` / `config.json`：

- `EnableAutoApply`: 是否启用自动应用
- `UsePerEngineOptimalProfiles`: 是否使用按引擎自动最优档案
- `DistancePerKgFuel`: 每千克燃料对应可飞行距离
- `SteamDistanceFactor`: 蒸汽引擎距离系数
- `PetroleumDistanceFactor`: 石油引擎距离系数
- `HydrogenDistanceFactor`: 液氢引擎距离系数
- `SugarDistanceFactor`: 糖引擎距离系数
- `RadboltDistanceFactor`: 辐射引擎距离系数
- `OxidizerPerKgFuel`: 氧化剂与燃料质量比
- `FuelMarginPercent`: 燃料冗余
- `OxidizerMarginPercent`: 氧化剂冗余
- `MaxAutoFillPercent`: 最大自动填充比例
- `FallbackTargetDistance`: 无法读取距离时的兜底值
- `GlobalFuelAdjustmentPercent`: 全局燃料量微调
- `AdditionalSafetyMarginPercent`: 全局附加安全冗余
- `EnableCustomEngineProfiles`: 自动最优模式下启用引擎专属参数
- `SteamDistancePerKgFuel`, `SteamFuelMarginPercent`
- `PetroleumDistancePerKgFuel`, `PetroleumOxidizerPerKgFuel`, `PetroleumFuelMarginPercent`, `PetroleumOxidizerMarginPercent`
- `HydrogenDistancePerKgFuel`, `HydrogenOxidizerPerKgFuel`, `HydrogenFuelMarginPercent`, `HydrogenOxidizerMarginPercent`
- `SugarDistancePerKgFuel`, `SugarFuelMarginPercent`
- `RadboltDistancePerKgFuel`, `RadboltFuelMarginPercent`

## 触发时机

- `Clustercraft.OnSpawn`
- `Clustercraft.SetDestination`
- `Clustercraft.Sim200ms`（用于侦测玩家手动改值并实时联动）

当火箭生成、切换目标或玩家手动调整参数时，自动重新计算并设置其余参数。

## 详情面板显示

在火箭详情描述里会追加一行：

- 引擎类型
- 目标距离（是否使用兜底值）
- 实际写入的燃料/氧化剂质量

## 构建

可使用以下任一命令：

```powershell
msbuild .\AutoRocketFuelPlanner.csproj /t:Build /p:Configuration=Release
```

```powershell
dotnet build .\AutoRocketFuelPlanner.csproj -c Release
```

产物：

- `bin/Release/AutoRocketFuelPlanner.dll`

打包时将以下文件放同级：

- `AutoRocketFuelPlanner.dll`
- `mod.yaml`
- `mod_info.yaml`
- `config.json`
- `README.md`
