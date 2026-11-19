# PetFramework 2.0 · 桌面宠物通用框架

> 一套框架，多个角色。`MyPet`（小白 · 白虎）、`Furina`（芙宁娜）、`PetQingLonng`（青龙）共用这一份代码。
> **改框架 = 两边一起改**（见文末「同步清单」）。

---

## 1. 一个桌宠 = 哪些东西

| 层 | 负责 | 关键脚本 |
|---|---|---|
| 窗口层 | 全屏透明、置顶、逐帧鼠标穿透 | `LocalReflex/TransparentWindow.cs`、`WindowUtil.cs` |
| 移动层 | 2D 横板走动 / 转身 / 屏幕边界钳制 | `Core/PetBrain.cs` |
| 动画层 | 11 个抽象动作 → Animator Trigger | `PetBrain` + `*.controller` |
| 取景层 | 按「占屏高百分比」算相机距离 | `Core/PetCameraFraming.cs` |
| 对话层 | 模型接口、上下文、记忆、TTS | `AIProviders/*`、`Core/ChatUI.cs`、`Core/TTS.cs` |
| 工具层 | 内置 / 进程 / HTTP / MCP / 动作池 | `Tools/*` |
| 面板层 | Tab 主面板（聊天 / 设置 / 工具） | `UI/PetDashboardUI.cs`、`PetWindowManager.cs` |
| 表现层 | 毛皮、粒子、描边等可调参数 | `VisualConfigs/*`、`VFX/*` |

---

## 2. 按键

| 键 | 作用 |
|---|---|
| `T` | 呼出/收起聊天小窗（回车发送） |
| `Tab` / `Esc` | 开关主面板（Esc 优先关聊天窗） |
| `F9` | 毛皮/材质实时调参面板（`FurTuner`，可在 Inspector 关掉） |

> 面板和桌宠**是同一个全屏窗口**。面板开着时窗口不穿透；关掉后按光标位置逐帧恢复穿透。

## 3. 配置文件（全部在 EXE 同级或工程根目录）

| 文件 | 内容 | 热更新 |
|---|---|---|
| `config.json` | 模型接口：`provider` / `baseUrl` / `model` / `apiKey` / `enableThinking` | 面板保存后即时套用 |
| `pet_profile.json` | 名字、称呼、人设、`tts_config`（音色/语速/音调/音量） | TTS 每句话前重读 |
| `pet_tools.json` | 外部工具总闸 + 每个工具开关 | 3 秒轮询自动重载 |
| `fur_settings.txt` | F9 面板保存的材质参数 | 启动读取 |

⚠️ `config.json` 里有 API Key，**不要随成品分发**；模板见 `config.template.json`。

## 4. 动作系统（PetBrain）

`PetBrain` 只认 11 个抽象动作，具体播什么由 AnimatorController 决定：

```
doIdle doWalk doRun doDrag doDrop doPet doJump doLookBack doSit doSleep doTalk
```

- 控制器里必须有同名 **Trigger** + AnyState 过渡。
- 坐/睡属于「保持姿势」：`IsHoldingPose` 为真时说话不会把姿势顶掉。
- **多形态**（一个角色两套身体）：`PetBrain.SetAnimator(Animator)` —— 换形态后把大脑切到新身体的
  状态机，并补发一次当前动作。单形态不用管，留空 `animatorOverride` 即可。

## 5. 工具系统

`ToolRegistry` 汇总所有 `IToolSource`：

| `ToolKind` | 来源 | 默认开关 |
|---|---|---|
| `Builtin` | 框架内置（走动/说话/喂文件/记忆） | 开 |
| `Action` | 角色动作池（`doDance` / `doSit` …） | 开 |
| `Process` | 本地脚本，stdin/stdout 一行 JSON | 关 |
| `Http` | HTTP POST | 关 |
| `Mcp` | MCP Server（stdio JSON-RPC） | 关 |

- `ToolRegistry.useBuiltinTools`：角色用自己的动作池时，可以关掉内置那套。
- `ToolRegistry.customSources`：挂任意实现 `IToolSource` 的组件，注册表会自动追加。
- 模型每轮最多回环 `maxToolRounds`（夹在 1~3）。
- 工具结果要不要回传：`ToolMode.Chat`（不回传）/ `ToolMode.Agent`（回传再让模型总结）。

**新增一个动作池只需要三步**：写 `IToolSource` → 挂到 `customSources` → 在 `pet_tools.json`
或面板里打开。参考 `Assets/Scripts/FurinaSoul/FurinaActionToolSource.cs`。

## 6. 大小与取景

`PetCameraFraming.SetScreenPercent(float pct)`：百分比 = 角色占屏幕高度的比例（夹 3%~60%）。
值存 `PlayerPrefs["Pet_ScreenHeightPercent"]`，下次启动自动恢复。
面板「设置」页第一行就是滑杆。

## 7. TTS

边沿 TTS，本机 `PythonEnv\python.exe tts_server.py <port>` 起一个 `http.server`，只依赖 `edge_tts`。

- 找不到服务 → 自动拉起（`python.exe`，**不要用 pythonw.exe**）。
- 再失败 → Windows SAPI 兜底（COM `SAPI.SpVoice`，丢线程池，别阻塞主循环）。
- 音色参数写在 `pet_profile.json` 的 `tts_config`，**改完不用重启**。

## 8. 打包（血泪清单）

1. `PlayerSettings.useFlipModelSwapchain = false` —— 否则窗口 alpha 失效，**全黑**。
2. 相机 `renderPostProcessing = false` —— URP 后处理会把 alpha 写成 1。
3. HoyoToon 材质必须 `ShaderOptimizer.SetLockedForAllMaterials(...)` —— 否则 shader 被剥离，**人物变品红**。
4. `TransparentWindow.skipInEditor = true` —— 否则编辑器会被改成透明弹窗（会拆掉 Unity 编辑器）。
5. `TransparentWindow.useHitTestHook = false` —— 原生回调抛异常会直接带崩进程；
   真正起作用的是逐帧切 `WS_EX_TRANSPARENT`。
6. `PlayerSettings.runInBackground = true` —— 否则窗口失焦时游戏循环完全不推进。

## 9. 同步清单（改框架后要一起改）

两个工程里这些文件**应当保持一致**（角色专属数值除外）：

```
Core/PetBrain.cs              多形态 SetAnimator
Core/PetCameraFraming.cs      大小滑杆 API
Core/TTS.cs                   port / volume / LoadConfigFromProfile
LocalReflex/TransparentWindow.cs  useHitTestHook / CursorNearPet 并集包围盒
Tools/ToolModel.cs            ToolKind.Action
Tools/ToolRegistry.cs         useBuiltinTools / customSources
Tools/ToolExecutor.cs / Sources/*
UI/PetDashboardUI.cs          设置页（大小 / 思考 / TTS）
UI/PetWindowManager.cs        typing 判定（必须要求面板已开）
AIProviders/PetApiConfig.cs   enableThinking
AIProviders/PetAPI.cs         onBeforeAsk / onAfterReply / enableProactiveIdle / 工具路由
```

**角色专属、允许不同**：`TTS` 的端口/默认音色、`pet_profile.json`、`config.json`、场景与预制体。
