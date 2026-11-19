# PetFramework 2.0 · 桌面宠物通用框架

> **一套框架，多个角色。** 同一份代码驱动了三只不同的桌宠：
> 白虎「小白」（MyPet）、芙宁娜（Furina）、青龙（PetQingLonng）。
>
> 换角色只需要换模型 + 动画控制器 + 配置文件，**框架代码零改动**。
> 新增一个动作池、接入一个外部 MCP、加一页自定义面板，都不需要动核心。

---

## 1. 为什么这是「框架」而不是「一个桌宠」

一个桌宠 Demo 和一个桌宠框架的区别在于：

| | 桌宠 Demo | **PetFramework** |
| :--- | :--- | :--- |
| 换角色 | 改代码 | 换资源 + 配置 |
| 加动作 | 改状态机代码 | 写一个 `IToolSource`，挂上去 |
| 接外部能力 | 写死在脚本里 | 注册 MCP Server / 进程 / HTTP |
| 加 UI 页 | 改源码 | 实现组件，注册到面板 |
| 多形态（一个角色两套身体） | 不支持 | `PetBrain.SetAnimator()` |

**三个角色共用同一份框架代码**，这是最直接的证明——不是我写了个桌宠，
而是我抽象出了一层足够通用的模型，能让完全不同的角色跑在上面。

---

## 2. 分层架构

```
┌─────────────────────────────────────────────┐
│  表现层  毛皮 / 粒子 / 描边（VisualConfigs）  │
├─────────────────────────────────────────────┤
│  面板层  Tab 主面板：聊天 / 设置 / 工具        │
├─────────────────────────────────────────────┤
│  对话层  模型接口 / 上下文 / 记忆 / TTS        │
├─────────────────────────────────────────────┤
│  工具层  内置 / 动作池 / 进程 / HTTP / MCP     │
├─────────────────────────────────────────────┤
│  动画层  11 个抽象动作 → Animator Trigger      │
├─────────────────────────────────────────────┤
│  移动层  2D 横板走动 / 转身 / 屏幕边界钳制      │
├─────────────────────────────────────────────┤
│  取景层  按「占屏高百分比」自动算相机距离        │
├─────────────────────────────────────────────┤
│  窗口层  全屏透明 / 置顶 / 逐帧鼠标穿透         │
└─────────────────────────────────────────────┘
```

**每一层都是可替换的**：窗口层可以换（Win32 → 其他平台）、对话层可以换（不同模型）、
工具层可以无限扩展、表现层可以整套换材质。

---

## 3. 动作系统：11 个抽象动作

`PetBrain` 只认这 11 个名字，**具体播什么完全由 AnimatorController 决定**：

```
doIdle  doWalk  doRun  doDrag  doDrop  doPet
doJump  doLookBack  doSit  doSleep  doTalk
```

**新角色接入的步骤**：
1. 模型 → 动画控制器，创建同名 Trigger
2. 每个 Trigger 从 AnyState 建一条过渡
3. 完成——`PetBrain` 会自动驱动它

**两个设计细节**：

- **姿势保持**：`IsHoldingPose` 为真时（坐/睡），说话不会把姿势顶掉
- **多形态**：`PetBrain.SetAnimator(Animator)`——换形态后把大脑切到新身体的状态机，
  并补发一次当前动作。单形态角色留空即可

---

## 4. 工具系统：5 种来源，无限扩展

`ToolRegistry` 汇总所有 `IToolSource`：

| 类型 | 来源 | 默认 | 用途 |
| :--- | :--- | :--- | :--- |
| `Builtin` | 框架内置 | 开 | 走动、说话、喂文件、记忆 |
| `Action` | 角色动作池 | 开 | `doDance`、`doSit` 等 |
| `Process` | 本地脚本 | 关 | stdin/stdout 一行 JSON |
| `Http` | HTTP POST | 关 | 调用远程服务 |
| `Mcp` | MCP Server | 关 | stdio JSON-RPC |

**新增一个动作池只需要三步**（真实代码量：一个文件，实现 `IToolSource`）：

```csharp
// 1. 写一个 IToolSource
public class MyActionToolSource : IToolSource { ... }

// 2. 挂到 ToolRegistry.customSources
// 3. 在 pet_tools.json 或面板里打开
```

**参考实现就在仓库里**：[`src/Examples/FurinaActionToolSource.cs`](src/Examples/FurinaActionToolSource.cs)
（61 行，把芙宁娜的动作池接进工具注册表，每个 `do*` 动作 = 一个工具）

**扩展性证据**：·"让宠物把自己的动作暴露给模型"这个能力不是写死的，
而是走同一个工具注册表——所以模型既能调内置的"说话"，也能调角色自定义的"跳舞"，
也能调外部的 MCP Server。

---

## 5. 配置驱动，改行为不用改代码

| 文件 | 内容 | 热更新 |
| :--- | :--- | :--- |
| `config.json` | 模型接口：provider / baseUrl / model / apiKey | 面板保存即时生效 |
| `pet_profile.json` | 名字、称呼、人设、TTS 音色/语速/音调 | **每句话前重读** |
| `pet_tools.json` | 工具总闸 + 每个工具开关 | **3 秒轮询自动重载** |
| `fur_settings.txt` | F9 面板保存的材质参数 | 启动读取 |

**改人设、改音色、改工具权限都不需要重新打包。**

---

## 6. 窗口层：最大的技术坑

全屏透明穿透窗口是整个项目最难的部分，踩坑记录：

| 问题 | 解决 |
| :--- | :--- |
| 分层窗口不能靠 `WM_NCHITTEST` 穿透 | **逐帧按光标位置切整窗 `WS_EX_TRANSPARENT`** |
| 拖宠物时窗口跟着移动 | `WM_WINDOWPOSCHANGING` 强制钉回 `(0,0)` |
| DWM 扩展边框让整窗变 `HTCAPTION` | `WM_NCHITTEST` 永远返回 `HTCLIENT` |
| 高 DPI 下坐标全错 | 编译期设 DPI-aware，按物理像素换算 |
| 不占任务栏 | `ITaskbarList.DeleteTab` + 托盘图标 |

> **关键教训**：分层窗口（`WS_EX_LAYERED`）即使钩子返回 `HTTRANSPARENT`，
> 真点击仍被吃掉——`WindowFromPoint` 照样返回本窗口。唯一可靠的做法是**逐帧切整窗属性**。

---

## 7. 打包血泪清单

打包时这 6 条任何一条错都会出问题：

1. `useFlipModelSwapchain = false` —— 否则窗口 alpha 失效，**全黑**
2. 相机 `renderPostProcessing = false` —— URP 后处理会把 alpha 写成 1
3. HoyoToon 材质必须 `ShaderOptimizer.SetLockedForAllMaterials()` —— 否则**人物变品红**
4. `TransparentWindow.skipInEditor = true` —— 否则**编辑器会被改成透明弹窗**
5. `useHitTestHook = false` —— 原生回调抛异常会**带崩进程**
6. `runInBackground = true` —— 否则窗口失焦时**游戏循环完全不推进**

---

## 8. 已适配的角色

| 角色 | 模型 | 特点 |
| :--- | :--- | :--- |
| **小白**（白虎，MyPet） | `WhiteTiger_DesktopPet.fbx` | Shell Fur 毛皮着色器（Marschner BSDF） |
| **芙宁娜**（Furina） | HoyoToon 材质 | 多形态、动作池、自定义面板 |
| **青龙**（PetQingLonng） | `青龙_动作库.fbx`（11 个 clip） | 自研 URP 卡通着色器 + 描边 + 墨迹拖尾 |

**三个角色的框架代码是同一份**，只有模型、动画控制器和配置不同。

---

## 9. 如何做一个新角色（5 步）

1. **准备模型 + 动画**：至少要有 11 个动作对应的动画剪辑
2. **建 AnimatorController**：11 个同名 Trigger + AnyState 过渡
3. **写 `pet_profile.json`**：名字、人设、TTS 音色
4. **选一包材质**：或直接用基础 URP Lit
5. **挂 `PetBrain` + `TransparentWindow`**，打包

**框架代码一行不用改。**

---

## 10. 代码规模

**49 个 C# 文件**（`src/PetFramework/` + `src/Workspace/`），不含第三方包。

核心模块：
- `Core/`：`PetBrain`、`PetCameraFraming`、`ChatUI`、`TTS`、`PetMemory`
- `LocalReflex/`：`TransparentWindow`、`WindowUtil`
- `Tools/`：`ToolRegistry`、`ToolExecutor`、四种 `IToolSource` 实现
- `UI/`：`PetDashboardUI`、`PetWindowManager`、`PetTrayIcon`
- `AIProviders/`：`PetAPI`、`PetApiConfig`
- `Workspace/`：独立工作区（`MyPetWorkspace.exe`）

---

## 11. 如何运行

### Release 版本

下载 `Furina.zip`（Release 附件）——**AI 对话需要自备 API Key**：

1. 把 `Build/config.template.json` 复制成 `Build/config.json`
2. 填入 Key（模板默认指向硅基流动 `https://api.siliconflow.cn/v1`）
3. **不填也能用**——动作、拖拽、托盘都正常，只是不能聊天

### 从源码构建

需要 Unity 2022.3 + URP 14 + 模型资源 + Shell Fur 着色器包。
**本仓库只包含自写源码**，第三方资源需自行准备。

---

## 12. 已知局限

1. **仅 Windows**：窗口层深度绑定 Win32 API
2. **聊天依赖外部 LLM**：本地模型慢，远程依赖网络
3. **拖拽投喂偶发失败**：`WM_DROPFILES` 在透明窗口下有时收不到
4. **新角色仍需手工配 AnimatorController**：目前没有自动生成工具

## 后续计划

- [ ] 修复拖拽投喂的窗口消息问题
- [ ] AnimatorController 自动生成器（从动画剪辑推断 11 个动作）
- [ ] 更多 `IToolSource` 示例（截图识别、文件整理）
