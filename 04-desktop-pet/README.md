# DesktopPetFramework · 通用 AI 桌宠框架

> **一套框架，四个角色。** 白虎「小白」、青龙、猫、芙宁娜共用这一份代码。
> 内置三种渲染接口（毛发 / 鳞片 / 金属），加新角色、加新工具、加新面板，都不用改框架核心。

**本目录提交的是框架，不是某一个具体桌宠。** 完整技术文档见
[`src/PetFramework/README.md`](src/PetFramework/README.md)。

---

## 1. 它是什么

一个跑在 Windows 桌面上的 AI 桌宠**开发框架**：

- 宠物是**一个透明全屏窗口里的 3D 模型**
- 能看见你的屏幕（定期截图喂多模态模型）
- 能聊天、能自己溜达
- 能通过「调工具」来行动（读文件、跑脚本、调 MCP Server）

**和你见过的桌宠 Demo 的区别**：

| | 桌宠 Demo | **本框架** |
|---|---|---|
| 换形态 | 改代码 | 换 `PetFormSet` + 材质配置 |
| 加工具 | 写死在脚本里 | 改 `pet_tools.json`，**3 秒热重载** |
| 加面板页 | 改源码 | 提供组件，注册即可 |
| 终端用户配 API | 开发者改代码 | **按 Esc 直接在游戏里填** |

---

## 2. 五项核心能力

| 能力 | 实现 |
| :--- | :--- |
| **AI 驱动** | 本地 Ollama / 任意 OpenAI 兼容云端（硅基流动、DeepSeek、通义…） |
| **视觉感知** | 定期抓全屏缩略图喂多模态模型——宠物能"看见"你打开了什么 |
| **形态可换** | 内置三种渲染接口：**毛发**（ShellFur）/ **鳞片**（URP Lit）/ **金属**（MetalConfig），加新形态不用改代码 |
| **参数可调** | F9 面板实时调材质，改 JSON 就改参数，**不用重新打包** |
| **终端友好** | 按 Esc 填 API Key，不懂 Unity 的人也能用 |

---

## 3. 架构分层

```
┌──────────────────────────────────────────────┐
│ 表现层  FurConfig（毛发）/ ScaleConfig（鳞片）/ MetalConfig（金属）│
├──────────────────────────────────────────────┤
│ 面板层  PetDashboardUI：聊天 / 设置 / 工具       │
├──────────────────────────────────────────────┤
│ 对话层  PetAPI / PetMemory / TTS / ChatUI       │
├──────────────────────────────────────────────┤
│ 工具层  ToolRegistry：内置 / 进程 / HTTP / MCP   │
├──────────────────────────────────────────────┤
│ 动画层  11 个抽象动作 → AnimatorOverrideController│
├──────────────────────────────────────────────┤
│ 移动层  PetBrain：2.5D 平台式走动 / 转身 / 跳跃   │
├──────────────────────────────────────────────┤
│ 取景层  PetCameraFraming：按占屏高百分比算距离    │
├──────────────────────────────────────────────┤
│ 窗口层  TransparentWindow：全屏透明 / 置顶 / 逐帧穿透│
└──────────────────────────────────────────────┘
```

**每一层都可替换**：窗口层可以换平台、对话层可以换模型、表现层可以整套换材质。

---

## 4. 两个关键扩展点

### 4.1 工具注册中心（框架 2.0 的重点）

`pet_tools.json`（EXE 同级）就是全部配置，**改完保存 3 秒内自动重载**：

```json
{
  "enableExternalTools": true,
  "maxToolRounds": 2,
  "tools": [
    {
      "kind": "process",
      "name": "my_tool",
      "description": "告诉模型这个工具能干什么",
      "command": "python",
      "args": ["python_tools/my_tool.py"],
      "mode": "agent"
    }
  ]
}
```

- `kind`：`process`（本地脚本）/ `http` / `mcp`（stdio）
- `mode`：`chat`（执行完就结束）/ `agent`（结果回传给模型）
- `command: "python"` 会自动解析到随包的 `PythonEnv\python.exe`——**目标机器不需要装 Python**
- 外部工具**默认全局关闭**，`requireConfirm: true` 时每次执行前都弹窗确认

**安全边界**：确认弹窗 120 秒不点自动拒绝；`allowedRoots` 限制文件访问范围。

### 4.2 加一套新形态

1. 建 `PetAnimationConfig`（11 个动作的 clip 映射）
2. 用 `AnimatorOverrideController` 套在通用状态机上
3. 建 `PetFormSet`（形态清单）
4. 挂对应的 `VisualConfig`（毛发 / 鳞片 / 金属）

**加新形态不用改 UI 一行代码。**

**扩展示例**：[`src/Examples/FurinaActionToolSource.cs`](src/Examples/FurinaActionToolSource.cs)
——把角色动作池接进工具注册表，每个 `do*` 动作 = 一个模型可调用的工具。

---

## 5. 渲染接口与实际角色

**这两件事要分清**：

### 5.1 内置三种渲染接口（`VisualConfigs/`）

| 接口 | 着色方案 | 可调参数 |
| :--- | :--- | :--- |
| **毛发** | ShellFur（Marschner BSDF 物理头发） | 毛长 / 层数 / 毛根毛尖 / 光滑度 / 边缘光 / 遮罩 |
| **鳞片** | URP Lit | 基础色 / 金属度 / 光滑度 |
| **金属** | MetalConfig（URP Lit） | 金属度 / 高光 / 发光 |

**接口是「材质方案」**，任何角色都能挂其中一种——不是角色本身。

### 5.2 实际做过的四个角色

| 角色 | 渲染接口 | 说明 |
| :--- | :--- | :--- |
| **白虎「小白」**（MyPet） | 毛发 | Shell Fur 多层壳毛发 |
| **青龙**（PetQingLonng） | 鳞片 | 自研 URP 卡通着色器 + 描边 + 墨迹拖尾 |
| **猫**（PetCat） | —— | 给家人做的，接入同一框架 |
| **芙宁娜**（Furina） | 毛发 | HoyoToon 材质，多形态 + 动作池 + 名场面特效 |

**四个角色共用同一份框架代码**，只有模型、动画控制器和材质配置不同。

---

## 6. 窗口层：最硬的技术坑

| 问题 | 解决 |
| :--- | :--- |
| 分层窗口不能靠 `WM_NCHITTEST` 穿透 | **逐帧按光标位置切整窗 `WS_EX_TRANSPARENT`** |
| 拖宠物时窗口跟着跑 | `WM_WINDOWPOSCHANGING` 强制钉回 `(0,0)` |
| DWM 扩展边框让整窗变标题栏 | `WM_NCHITTEST` 永远返回 `HTCLIENT` |
| 高 DPI 坐标错位 | 编译期设 DPI-aware，按物理像素换算 |
| 不占任务栏 | `ITaskbarList.DeleteTab` + 托盘图标 |

> **关键教训**：`WS_EX_LAYERED` 窗口即使钩子返回 `HTTRANSPARENT`，
> 真点击仍被吃掉——`WindowFromPoint` 照样返回本窗口。唯一可靠做法是**逐帧切整窗属性**。

---

## 7. 打包血泪清单

1. `useFlipModelSwapchain = false` —— 否则窗口 alpha 失效，**全黑**
2. 相机 `renderPostProcessing = false` —— URP 后处理会把 alpha 写成 1
3. HoyoToon 材质必须 `ShaderOptimizer.SetLockedForAllMaterials()` —— 否则**人物变品红**
4. `TransparentWindow.skipInEditor = true` —— 否则**编辑器会被改成透明弹窗**
5. `useHitTestHook = false` —— 原生回调抛异常会**带崩进程**
6. `runInBackground = true` —— 否则窗口失焦时**游戏循环完全不推进**

---

## 8. 代码规模

**框架代码**（`src/PetFramework/`），不含第三方包：

```
src/PetFramework/
├── Core/           PetBrain / PetMemory / TTS / ChatUI / PetCameraFraming / ...
├── LocalReflex/    TransparentWindow / WindowUtil / PetDragDrop / PetFeed / ...
├── Tools/          ToolRegistry / ToolExecutor / ToolModel / Sources/*
├── UI/             PetDashboardUI / PetWindowManager
├── AIProviders/    PetAPI / PetApiConfig
├── VFX/            PetVFXController / PetVFXPanel
├── VisualConfigs/  FurConfig / ScaleConfig / MetalConfig / FurTuner / ...
└── Forms/          PetForm / PetFormSet / PetAnimationConfig
```

**来源**：`DesktopPetFramework.unitypackage`（官方导出包）的完整内容。

---

## 9. 如何运行

### Release 版本

Release 里的 `Furina.zip` 是**用本框架打包的芙宁娜角色**。

**不填 API Key 也能用**——宠物动作、拖拽、托盘都正常，只是不能聊天。
在游戏里**按 Esc 直接填 Key**（不用改配置文件）。

### 从源码构建

需要 Unity 2022.3 + URP 14 + 模型与材质资源。完整步骤见
[`src/PetFramework/README.md` §三 五分钟上手](src/PetFramework/README.md)。

---

## 10. 已知局限

1. **仅 Windows**：窗口层深度绑定 Win32 API
2. **聊天依赖外部 LLM**：本地模型慢，远程依赖网络
3. **新形态仍需手工配 AnimatorOverrideController**
4. **拖拽投喂偶发失败**：`WM_DROPFILES` 在透明窗口下有时收不到

## 后续计划

- [ ] 修复拖拽投喂的窗口消息问题
- [ ] AnimatorOverrideController 自动生成（从动画剪辑推断 11 个动作）
- [ ] 跨平台窗口层（macOS / Linux）
