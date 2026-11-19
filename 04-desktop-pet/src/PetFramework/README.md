# DesktopPetFramework · 桌面宠物开发框架

一个跑在 Windows 桌面上的 **AI 桌宠框架**。宠物是一个透明全屏窗口里的 3D 模型，
能看见你的屏幕、能聊天、能自己溜达，还能通过"调工具"来行动。

- **AI 驱动**：本地 Ollama / 任意 OpenAI 兼容云端接口（硅基流动、DeepSeek、通义…）
- **视觉感知**：定期抓一张全屏缩略图喂给多模态模型
- **形态可换**：白虎（毛发）/ 青龙（鳞片）/ 机甲（金属）… 加新形态不用改代码
- **参数可调**：F9 面板实时调材质，改 JSON 就能改参数，**不用重新打包**
- **终端用户友好**：按 Esc 填 API Key，不懂 Unity 的人也能用

---

## 一、目录结构

```
Assets/PetFramework/
├── Core/            核心逻辑
│   ├── PetBrain.cs            状态机 / 移动 / 转身 / 跳跃（2.5D 平台式）
│   ├── PetMemory.cs           对话历史 + 长期记忆 + 行为约定，持久化
│   ├── PetContext.cs          环境感知（前台窗口 / 键鼠闲置 / 全屏判定）+ 抓屏
│   ├── PetCameraFraming.cs    相机取景（宠物占屏幕高度的比例）
│   ├── PetAppSettings.cs      项目胶水：把 pet_visual.json 里 AI 相关项套到上面几位
│   ├── PetPathUtil.cs         配置文件路径解析（EXE 同级 + 向上一级回退）
│   ├── TTS.cs                 语音合成（Edge-TTS；便携 Python → 系统 Python → SAPI 三级降级）
│   ├── ChatUI.cs              聊天输入框（按 T / 回车呼出）
│   ├── PetConfigUI.cs         ★ Esc 接口配置面板（给终端用户）
│   ├── PetProfileConfig.cs    ★ 角色设定（名字 / 昵称 / 人设）→ pet_profile.json → System Prompt
│   └── PetPathUtil.cs
├── AIProviders/     接口层
│   ├── PetAPI.cs              ★ 双协议大脑：Ollama 原生 + OpenAI 兼容，工具调用在此
│   └── PetApiConfig.cs        config.json 读写 + 套用到 PetAPI
├── VisualConfigs/   材质参数
│   ├── PetVisualConfig.cs     ★ 形态配置抽象基类 + 参数模型
│   ├── FurConfig.cs           毛发形态（ShellFur）
│   ├── ScaleConfig.cs         鳞片形态（URP Lit）
│   ├── MetalConfig.cs         金属形态（URP Lit）
│   ├── PetVisualRoot.cs       ★ 形态根组件：读 JSON、套材质、热重载、多形态入口
│   ├── FurTuner.cs            ★ F9 实时调参面板（完全动态，不认识具体形态）
│   └── Presets/               实际的配置资源（.asset）
├── UI/              ★ 主面板（uGUI，运行时用代码生成，不需要在场景里摆控件）
│   ├── PetDashboardUI.cs      5 页主面板：聊天历史 / 基础与角色 / 材质与渲染 / 粒子特效 / 工具与 MCP
│   └── PetWindowManager.cs    ★ Tab / Esc 开关面板；主面板与桌宠共用同一个全屏窗口
├── VFX/             粒子特效控制
│   ├── PetVFXController.cs    按 Animator 状态切发射率（互动 / 待机 / 移动 / 拖拽 / 落地）
│   └── PetVFXPanel.cs         F10 临时调参面板（正式参数请用主面板的「粒子特效」页）
├── Tools/           ★ 框架 2.0：通用工具注册中心
│   ├── ToolRegistry.cs        汇总所有 IToolSource，生成 tools 数组 + 提示词清单，热重载 pet_tools.json
│   ├── ToolExecutor.cs        超时 / 截断 / 路径白名单 / 确认弹窗 / 杀进程树
│   ├── ToolConfirmUI.cs       ★ 模态确认弹窗（模型跑本地进程前必须过这一关）
│   ├── ToolModel.cs           ToolKind / ToolMode / ToolDescriptor / IToolSource
│   └── Sources/               内置 / 本地进程 / HTTP / MCP(stdio) 四种来源
├── Forms/           多形态（Multi-Form）
│   ├── PetForm.cs             一个形态：模型 + 材质配置 + 动作配置
│   ├── PetFormSet.cs          形态清单（第一个 = 默认形态）
│   ├── PetAnimationConfig.cs  ★ 单形态的动作映射（AnimatorOverrideController）
│   └── Presets/
└── LocalReflex/     本地反射层（不经过 AI 的即时行为 + 平台适配）
    ├── TransparentWindow.cs   透明窗口 / 鼠标穿透 / 点击命中
    ├── WindowUtil.cs          Win32 窗口小工具
    ├── PetDragDrop.cs         拖放接收
    ├── PetOleDrop.cs          OLE 拖放（跨进程，比 WM_DROPFILES 可靠）
    ├── PetFeed.cs             "吃掉"文件 → 移入回收站（可恢复）
    └── PerformanceLock.cs     锁 30 帧，防止核显风扇起飞
```

> 所有框架代码都在 **`namespace PetFramework`** 下，导入别人的工程不会撞名。

---

## 二、依赖

| 依赖 | 说明 |
|---|---|
| **Unity 2022.3 LTS** | 其它版本未测试 |
| **URP**（Universal Render Pipeline） | 透明窗口 + 后处理用到 |
| 一个会动的角色模型 | 需要带 Animator，见下方"动作绑定" |
| 可选：**ShellFur URP** 毛发 Shader | 只有 `FurConfig` 用得到 |
| 可选：**Ollama** | 想用本地模型就装；只用云端则不需要 |
| 可选：**Edge-TTS 本地服务** | 想让它说话就装（`127.0.0.1:8000`）。**框架启动时会自动把它拉起来**，开机忘了开也不怕 |

---

## 三、五分钟上手

1. 新建场景，把宠物模型拖进去，命名（默认框架找 `WhiteTiger_DesktopPet`，可在组件上改）
2. 建一个空物体，挂上这些组件：
   - `PetVisualRoot`（视觉）→ 拖一个形态配置进去
   - `PetAPI`（大脑）
   - `PetApiConfig`（给终端用户的接口配置）
   - `PetBrain`（行为）
   - `TransparentWindow`（透明窗口）
   - `PetDashboardUI` + `PetWindowManager`（主面板，**面板本身不用摆任何控件**）
   - `ChatUI`（T 键小对话框）
3. 主相机上挂 `PerformanceLock`
4. 运行 → **按 Tab / Esc** 呼出主面板 → **按 T** 聊天 → **按 F9** 调材质
   （主面板的「材质与渲染」页和 F9 是同一套参数，只是画得更规整）

> ⚠️ `PetConfigUI`（老 IMGUI 面板）已经废弃，保留代码只是为了兼容旧工程；
> 新工程**不要**再挂它，用 `PetDashboardUI` 即可。

### 主面板（Dashboard）怎么工作

- **5 页**：聊天历史 / 基础与角色 / 材质与渲染 / 粒子特效 / 工具与 MCP
- 「材质与渲染」页**完全数据驱动**：遍历 `PetVisualRoot.ActiveConfig.parameters`，
  按 `PetParamKind` 自动生成滑杆 / 勾选框 / RGB 三条 / XYZ 三条。
  **加新形态（比如 MetalConfig）不用改 UI 一行代码。**
- 「粒子特效」页直接绑 `PetVFXController` 字段、粒子材质属性、`TrailRenderer` 参数，拖动实时生效。
- 面板和桌宠共用一个全屏透明窗口：面板关闭 = 隐藏，桌宠继续跑。
- 调试：`MyPet.exe -dashboard` 启动时直接打开面板（截图 / 验收用）。
- 穿透规则（这是桌面宠物最容易翻车的地方，代码里已经处理好了）：
  **光标在面板/小白身上 → 窗口可点；其余任何位置 → 整窗 `WS_EX_TRANSPARENT` 放行给桌面。**
  ⚠️ 不要试图只靠 `WM_NCHITTEST` 返回 `HTTRANSPARENT` 来穿透 —— 分层窗口上不可靠。

---

## 四、怎么换模型（`PetFormSet`）

框架是**多形态**设计：一只宠物可以有多个样子（兽人形态 / 虎形态…），随时切换。

### 1) 建一个动作配置 `PetAnimationConfig`

> 菜单：`Assets → Create → 桌面宠物 → 动作配置 (PetAnimationConfig)`

- **Base Controller**：填你的 Animator 状态机。
  ⚠️ 触发名必须是：`doIdle / doWalk / doRun / doDrag / doDrop / doPet / doJump / doLookBack / doSit / doSleep / doTalk`
- **Slots**：11 个槽位，把该形态的 AnimationClip 拖进去；**留空的槽位就用基础状态机里的原片段**

### 2) 为什么用 `AnimatorOverrideController`？

`PetBrain` 是靠**触发名**驱动 Animator 的。如果让配置自由定义槽位，`PetBrain` 就不知道触发什么了。

所以框架的做法是：**基础状态机（连同触发名）当地基，配置只负责把片段换掉**。

```
好处：① PetBrain 一行都不用改
     ② 龙 / 机甲做一个同样 11 状态的状态机就能复用
     ③ 换形态 = 换一套 clip，不是换一套逻辑
```

### 3) 建形态清单 `PetFormSet`

> 菜单：`Assets → Create → 桌面宠物 → 形态清单 (PetFormSet)`

每个 `PetForm`：

| 字段 | 说明 |
|---|---|
| `formName` | 形态 id（英文，给 AI 和代码用），如 `tiger` |
| `displayName` | 给人看的名字，如「虎形态」 |
| `modelPrefab` | 该形态的模型；**留空 = 用场景里现有的那个** |
| `visualConfig` | 该形态的材质配置（`FurConfig` / `ScaleConfig` / `MetalConfig`） |
| `animationConfig` | 该形态的动作映射 |

把 `PetFormSet` 拖到 `PetVisualRoot.formSet` 上即可。
**只放一个 = 单形态**（就是"单形态打包成多形态列表第一个元素"）；加第二个就是多形态。

> ⚠️ **当前版本**：`SwitchForm()` 只切形态指针 + 打日志，**外观不会变**。
> 真正的换模型/换动画/变身过渡还没实现（等有第二个模型再做）。

---

## 五、怎么配 API（按 **Esc**）

按 **Esc** 呼出「接口配置」面板：

| 字段 | 填什么 |
|---|---|
| **协议** | `本地 Ollama`（不需要 Key） / `云端`（OpenAI 兼容） |
| **BaseUrl** | 本地：`http://localhost:11434`　云端：`https://api.siliconflow.cn/v1` |
| **Model** | 本地：`qwen3.5:4b`　云端：`Qwen/Qwen2.5-7B-Instruct`（填服务商的完整模型名） |
| **ApiKey** | 云端必填，如硅基流动的 `sk-xxxx` |

点 **「测试连接」** 会真发一个最小请求，把 HTTP 状态码和原始报错直接显示出来 —— **建议先测再保存**。
点 **「保存并生效」** 写入 `config.json` 并立刻生效，不用重启。

### 重置记忆（出厂设置）

面板最下面有红色的 **「重置记忆」**，点了会弹二次确认，显示"将要清空：对话 N 轮 / 长期记忆 N 条 / 行为约定 N 条"。

确认后清空对话历史、长期记忆、行为约定三样，小白回到刚认识的初始状态。

> ⚠️ **只动 `PetMemory.json`**。接口配置 `config.json` 和形态参数 `pet_visual.json` 完全不受影响 —— 重置记忆不会把你的 API Key 弄丢。

### BaseUrl 不用填得很精确

框架会自动补全路径：

| 你填的 | 实际请求 |
|---|---|
| `http://localhost:11434` | `http://localhost:11434/api/chat` |
| `https://api.siliconflow.cn/v1` | `https://api.siliconflow.cn/v1/chat/completions` |
| `https://api.siliconflow.cn` | `https://api.siliconflow.cn/v1/chat/completions` |

面板上会实时显示「**实际请求地址**」，填错一眼就能看出来。

### config.json

带 **向上一级回退** 的查找（编辑器里读工程根目录、成品里读 EXE 同级，两边都能工作）：

```json
{
  "provider": "ollama",
  "baseUrl": "http://localhost:11434",
  "model": "qwen3.5:4b",
  "apiKey": ""
}
```

文件不存在时会**自动生成模板**。改这个文件（游戏运行时也行，2 秒内自动重载）等效于在面板里改。

---

## 六、怎么调材质参数（按 **F9**）

F9 面板是**完全动态**的：它不认识任何具体形态，只照着 `PetVisualConfig.parameters` 生成滑块。

- 挂 `FurConfig` → 就是毛发面板（毛长 / 层数 / 毛根毛尖 / 光滑度 / 边缘光…）
- 挂 `MetalConfig` → 就是金属面板（金属度 / 高光 / 发光…）
- **加新形态，不用改面板一行代码**

### 加一套自己的形态配置

```csharp
[CreateAssetMenu(fileName = "MyConfig", menuName = "桌面宠物/形态配置/我的形态")]
public class MyConfig : PetFramework.PetVisualConfig
{
    protected override void BuildDefaults()
    {
        parameters = new List<PetVisualParam>
        {
            new PetVisualParam("Shine", "光泽", "_Shine", 0f, 1f, 0.5f, "反光", "拖起来看看"),
            new PetVisualParam("BaseColor", "基础色", "_BaseColor", 0,1,0, "颜色").AsColor(),
        };
    }
}
```

### 非开发者也能调：直接改 JSON

点面板上的「**保存到 JSON**」，会在 EXE 同级生成 `pet_visual.json`：

```json
{
  "_readme": "改完保存，游戏里 2 秒内自动生效。删掉某行 = 恢复默认值。",
  "_组_毛的长度与层间距": "↓ 以下属于【毛的长度与层间距】",
  "TotalShellStep": 0.015,
  "ShellAmount": 5,
  "LockedViewDir": [0.4914971, 0.2425291, 0.8364271]
}
```

- **一个参数一行**，直接拿记事本改，保存后 2 秒内生效
- **删掉某行** = 该项恢复默认值
- 认不出的键会被跳过 ⇒ **旧文件向后兼容**
- ⚠️ 面板保存时会**保留不属于它的键**（比如 AI 配置），不会互相覆盖

---

## 七、AI 工具（模型能自己调用的动作）

框架内置这些工具，模型可以自由组合调用：

| 工具 | 作用 |
|---|---|
| `wander_around` | 随机挑个地方溜达一圈（不用给坐标） |
| `move_to` | 走到屏幕指定位置 |
| `play_animation` | 播动作（Idle / Jump / Sit / Sleep…） |
| `say_and_speak` | 说一句话（触发 TTS） |
| `remember_rule` | **把主人的长期要求记成"行为约定"**，从此每轮都提醒自己照做 |
| `feed_file` | 吃掉一个文件（移入回收站，可恢复） |
| `switch_form` | **预留**，默认关闭；等多形态模型做好再开 |

「行为约定」是个挺有意思的机制：你对它说「以后多四处走走」，它会调 `remember_rule` 记下来，
反复强调还会加权重。**聊天框打 `/rules` 看它记住了什么，`/forget` 一键清空。**

---

## 八、常见问题

**Q：聊天框显示「没收到回复」？**
模型回了一段大白话而不是工具调用。框架会把它当回话收下；如果连不上，按 Esc →「测试连接」看报错。

**Q：改了 JSON 但没生效？**
确认改的是面板底部显示的那个路径。查找顺序是「显式路径 → EXE 同级 → EXE 上一级」。

**Q：宠物不动 / 像木桩？**
检查 ① 模型是不是每次都只回文本 ② 本地兜底有没有触发（连续 3 轮没动会强制溜达一圈）。

**Q：怎么换成更大的模型？**
按 Esc 改 Model 名即可，**不用重新打包**。

**Q：TTS 没声音 / 开机忘了开 Python 服务？**
框架**启动时会自己救活它**：先探 `127.0.0.1:8000`，连不上就静默拉起 `tts_server.py`，最多等 10 秒。
Python 的查找顺序是 **① 组件里显式填的路径 → ② EXE 旁的 `PythonEnv\python.exe`（便携版）
→ ③ 系统 PATH → ④ `D:\python\python.exe`**；四者都没有就退回 **Windows 自带的 SAPI 语音**（能说话，只是音色普通）。
> ⚠️ `pythonPath` / `serverScript` 留空才会走自动查找；在 Inspector 里填死路径会盖掉便携版。

**Q：小白走到屏幕最上面时头和上半身出画？**
`PetBrain` 会**在启动时自动量出"头顶比脚底高多少"**（用 `renderer.bounds`），
顶部边界会减去这个高度。换模型不用手调；要强制指定可以填 `bodyHeightAboveFoot`。

---

## 九、框架 2.0：工具注册中心与便携 Python

### 9.1 加工具不用改代码

`pet_tools.json`（EXE 同级）就是全部配置。改完保存，运行中的桌宠 **3 秒内自动重载**。

```json
{
  "enableExternalTools": true,
  "maxToolRounds": 2,
  "defaults": { "timeoutSec": 30, "requireConfirm": true, "allowedRoots": [] },
  "tools": [
    {
      "kind": "process",
      "name": "my_tool",
      "description": "告诉模型这个工具能干什么",
      "command": "python",
      "args": ["python_tools/my_tool.py"],
      "expose": "auto",
      "mode": "agent"
    }
  ]
}
```

- `kind`：`process`（本地脚本）/ `http` / `mcp`（stdio）
- `mode`：`chat`（执行完就结束，走动说话用）/ `agent`（结果回传给模型，干活用）
- `command: "python"` 会自动解析到随包的 `PythonEnv\python.exe`，目标机器**不需要装 Python**
- 外部工具**默认全局关闭**；`requireConfirm: true` 时每次执行前都会弹窗
- `maxToolRounds` 默认 2、强制夹在 1–3，防止死循环烧 token

### 9.2 安全边界

| 机制 | 作用 |
|---|---|
| 确认弹窗 `ToolConfirmUI` | 模型跑任何外部进程前必须你点「允许」，120 秒不点自动拒绝 |
| `allowedRoots` | 路径白名单（前缀匹配），空数组 = 不限制 |
| 超时 + `taskkill /T` | 脚本挂死会连子进程一起杀掉，不会把宠物拖住 |
| 输出截断 | 超过 `maxOutputChars` 就截断，避免上下文被撑爆 |

### 9.3 角色设定

`pet_profile.json`（EXE 同级）：

```json
{ "petName": "小白", "userName": "白虎大哥", "persona": "..." }
```

它会被拼到 System Prompt **最前面**，并标明「以此为准」。所有 UI（聊天框、Esc 面板、记忆记录）
都从这里取名字，**不再有任何硬编码的人名**。

---

## 十、未来规划

- **多形态变身**：`PetFormSet` / `SwitchForm()` 的数据结构和入口已就位，等第二套模型做完即可实现真正的变身（换 prefab / 换 `AnimatorOverrideController` / 播过渡动画）
- **移动端**：框架的 AI 逻辑、记忆系统、工具调用已与平台层解耦，未来可轻松移植到 Android/iOS 的悬浮窗或小组件中。移动端只需替换透明窗口层和触摸输入层，并强制接入云端 API 即可。
- 动作槽位可视化编辑器
- 多宠物同屏

---

## 十一、许可

内部项目，暂未指定开源协议。
