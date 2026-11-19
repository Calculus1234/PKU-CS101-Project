# FBX 替换指南 —— 用混元 AI 3D 生成的模型替换占位几何体

> 当前场景中的所有可见几何体（除监控屏 Canvas / HUD）都是 Unity 基本体（Cube / Cylinder），
> 目的是先跑通流程与玩法。本文档说明如何用外部生成的 FBX 模型逐件替换。

---

## 1. 总原则

1. **只替换"视觉壳"，不动逻辑组件**：每个占位物上的脚本（门上的 `DoorController`、床上的 `HidingSpot`、物品的 `ItemPickup` 等）都留在**父节点**上，替换时删子网格、换模型即可。
2. **一次替换一个对象，替换后立即 Play 验证**（门能不能开、红点是否正常、交互提示是否还在）。
3. **不要删除**：`Systems`、`MeetingArea`、监控相关节点（MonitorCamera / DisplayCanvas / MonitorDisplay）、`RoomArea` 触发区。
4. 重建场景（`HilbertLoop → Build Main Scene`）会**还原成占位几何体**——大规模替换完成后建议以现有 MainScene 为准，不要再跑重建；若仍要重建，请把 FBX 替换做成对 `MainSceneBuilder.cs` 的修改（见第 7 节）。

---

## 2. 生成 FBX 的规格建议（喂给混元 AI 3D 的约束）

| 项目 | 建议 |
| --- | --- |
| 单位与比例 | 1 Unity 单位 = 1 米；门高约 2.2 m、桌面高 0.75 m、床长约 2 m 作为参照 |
| 朝向 | 面向 +Z（与 Unity 朝向一致，减少摆放旋转） |
| 原点 | 物体底部中心（方便贴地摆放） |
| 拆件粒度 | 每类物件一个文件：`bed.fbx`、`table.fbx`、`window.fbx`、`mirror.fbx`、`drying_rack.fbx`、`chair.fbx`、`door.fbx`、`corpse.fbx`、`dagger.fbx`… |
| 面数 | 单件 500–5000 三角面足够（恐怖游戏室内近景） |
| 贴图 | 建议内嵌或同目录 PBR 贴图；Unity 导入后材质转换为 **URP/Lit** |

导入路径建议：`Assets/Models/`（新建），与 `Assets/Materials/HL_*`（现有占位材质）分开存放。

---

## 3. 场景对象结构与替换点对照

运行时层级（由 MainSceneBuilder 生成）：

```
MainScene
├─ Ring_Left / Ring_Right                ← ∞ 形走廊（两个环）
│   ├─ Floor_i      (24 段环形地板)      ← 替换为整环地板模型，或保留
│   ├─ Ceiling_i    (24 段环形天花板)    ← 位于 31 层（监控忽略），替换时注意
│   ├─ InnerWall (无开口环墙)
│   ├─ OuterWall (带门洞与大厅开口)
│   │   ├─ DoorPier / DoorTop            ← 门垛、门楣
│   │   ├─ Door_1..Door_9                ← 门扇节点（DoorController 在这里）
│   │   │   └─ Leaf                      ← ★ 替换为门板 FBX
│   │   └─ Sign_x                        ← 红色门牌号（世界空间 TMP 文字，保留）
│   └─ Room_1 .. Room_9                  ← 每个房间
│       ├─ Floor / Ceiling / Wall_L/R / BackWall_*   ← 墙板（可整间替换）
│       ├─ Window                        ← 窗（WindowController 在 WindowHinge 上）
│       │   ├─ WindowHinge → WindowLeaf  ← ★ 替换为窗框 + 玻璃 FBX
│       │   └─ MagnetVisual              ← 磁铁视觉体
│       ├─ Bed → …                       ← ★ 床 FBX（HidingSpot 在 Bed 节点上）
│       ├─ Table / DryingRack / Mirror   ← ★ 各自替换
│       ├─ RoomArea                      ← 触发区（不可删）
│       ├─（9 号房）Corpse               ← ★ 尸体 FBX
│       └─ 物品（ItemPickup 的占位立方体）← ★ 各道具 FBX
├─ Lobby（大厅 + 连接走廊）
├─ MeetingArea
│   ├─ RoundTable (Top/Pedestal/Base)    ← ★ 圆桌 FBX
│   ├─ Chair_1..Chair_9                  ← ★ 椅子 FBX（编号是子节点上的 TMP 文字）
│   └─ MonitorDisplay                    ← 监控屏（勿动结构；Frame 可换壳）
├─ MonitorCamera                         ← 监控摄像机（勿动）
├─ Player（胶囊 + 摄像机）               ← 后续可换角色模型（仅视觉层）
├─ NPC_x（胶囊）                         ← ★ 可换角色 FBX（NPCPlaceholder 在根上）
└─ Systems（GameManager / MonitorSystem / Bridges 等空节点）
```

替换某个占位物的通用步骤：

1. 在 Hierarchy 中展开目标（例如 `Room_3 → Bed`）。
2. 删除其**子网格物体**（保留挂了脚本的父节点，例如 `Bed` 本身）。
3. 把 FBX（或其实例）拖入场景，作为该父节点的子物体。
4. 位置归零（`Transform → Reset Position/Rotation`，按第 2 节的"底部中心原点"约定对齐）。
5. 给 FBX 挂 **BoxCollider**（或勾选 MeshCollider/Convex）保证可交互、可挡人；床底藏匿点需要留出脚部空间。
6. 保存场景，Play 验证。

---

## 4. 门 / 窗的精确对齐参数（替换时的关键数值）

- 门洞净宽 **1.0 m**，高 **2.2 m**；门扇挂在铰链节点（`Door_x`）下，替换的门板 FBX 应满足：
  - 宽 1.0 m、高 2.2 m、厚约 0.06 m；
  - **左边缘贴铰链原点**（门扇中心在铰链局部坐标 x = +0.5 处），否则开门动画会偏移。
- 窗洞 **1.2 m × 1.2 m**（离地 0.9 m），窗扇挂在 `WindowHinge` 下，**左边缘贴铰链**、外向开合。
- 9 号房窗额外挂有 `MagnetVisual` 磁铁，替换窗框时保留该节点或把它挂到新窗框上。

门/窗的世界坐标（可直接用于摆放核对）：

| 房间 | 门位置（门外走廊侧） | 房间中心 |
| --- | --- | --- |
| 1 | (15.2, 0, -6.8) | (16.2, 0, -8.8) |
| 2 | (6.6, 0, -5.2) | (5.0, 0, -6.7) |
| 3 | (-18.0, 0, -4.6) | (-19.7, 0, -5.9) |
| 4 | (-7.4, 0, -6.0) | (-6.1, 0, -7.7) |
| 5 | (-6.1, 0, 4.6) | (-4.3, 0, 5.9) |
| 6 | (-16.6, 0, 6.0) | (-17.9, 0, 7.7) |
| 7 | (13.4, 0, 7.4) | (13.8, 0, 9.5) |
| 8 | (19.4, 0, 1.0) | (21.6, 0, 1.3) |
| 9 | (5.4, 0, 3.6) | (3.5, 0, 4.6) |

---

## 5. 监控系统相关的层与可见性（最容易踩的坑）

- 31 层（`MonitorIgnore`）的对象**监控摄像机看不到**，包括：所有天花板、灯具、玩家、NPC、悬浮屏、红点。
- 替换天花板 / 灯具 / 显示屏外壳时，**必须把新物体的 Layer 设为 31**，否则监控俯视画面里会看到一堵"屋顶"，红点地图会失效。
- 房间天花板、走廊天花板节点已在 31 层；换 FBX 后检查 Inspector 右上角 Layer 即可。

---

## 6. 材质替换

- 占位材质 `Assets/Materials/HL_*` 由场景构建器自动生成（URP/Lit）。
- FBX 导入后：选中模型 → Materials 选项卡 → **Extract Materials…** 导出到 `Assets/Models/Materials`，避免使用内嵌材质。
- 深红色木墙 / 深红地毯 / 淡绿走廊灯的氛围基调与 **明亮的房间** 对比是本作美术要求，替换贴图时保持整体色调。
- 自发光灯罩可用 Emission；灯具的实际照明已经由点光源提供，不用重复加光。

---

## 7. 如果想"重建场景后保留 FBX"（可选进阶）

在 `Assets/Editor/MainSceneBuilder.cs` 中，把对应 `CreateBox/CreateCylinder` 调用替换为"加载 FBX 预制体并实例化"的代码，例如：

```csharp
static GameObject PlaceModel(Transform parent, string name, string fbxPath, Vector3 localPos, Vector3 euler)
{
    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
    if (prefab == null) return CreateBox(parent, name, localPos, Quaternion.Euler(euler), Vector3.one, GetMaterial("HL_Wood"));
    var go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
    go.name = name;
    go.transform.localPosition = localPos;
    go.transform.localRotation = Quaternion.Euler(euler);
    return go;
}
```

这样 `Build Main Scene` 仍然是一键幂等的，并且优先使用已导入的 FBX。

---

## 8. 替换清单（按优先级建议）

1. 门板（9 扇）——最常被看到，且开门动画需要新模型对齐铰链。
2. 床 / 桌子 / 椅子（9 + 9 + 9）——房间与大厅的主体。
3. 窗（9 个）+ 镜子 + 晾衣架。
4. 走廊 / 房间墙体与地板（整环或整间替换，注意 31 层天花板）。
5. 圆桌、监控屏外壳。
6. 玩家 / NPC 角色（保持根节点与挂载脚本不变，仅换网格）。
7. 道具（匕首、锤子、绳子、磁铁、钉子、纸条、面具斗篷）——小物件，最后替换。
