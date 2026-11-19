# Unity 图形前端

完整 Unity 工程体积很大（含 `Library/`、模型、贴图），不适合直接放进开源仓库，
这里只提供**前端代码与打包脚本**，按下面的步骤可以还原出完整可运行版本。

## 环境

- Unity **2022.3.62f3c1**（其他 2022.3 LTS 版本基本通用）
- URP 14（模板：3D (URP) 或 3D Core 均可，UI 本身不依赖 URP）
- Android 打包需要：Android Build Support + **NDK** + **OpenJDK**（Unity Hub 里勾选）

## 还原步骤

1. 用 Unity Hub 新建一个 **3D (URP)** 工程。
2. 把 `src/Core/` 下的 5 个文件复制到 `Assets/Scripts/Poker/`：
   - `CardUtil.cs`
   - `MoveGen.cs`
   - `HandEval.cs`
   - `DDSolver.cs`
   - `ParallelSolver.cs`
3. 把本目录的 `Assets/Scripts/Poker/PokerSolverUI.cs` 放到同样位置。
4. 把 `Assets/Editor/PokerBuild.cs` 放到 `Assets/Editor/`。
5. 保存一个空场景到 `Assets/Scenes/SampleScene.unity`（清空相机即可，UI 由脚本运行时生成）。
6. 直接点 **Play** —— `PokerSolverUI` 会通过 `[RuntimeInitializeOnLoadMethod]` 自动挂载，
   并运行时生成全部 uGUI 控件（画布、按钮、输入框、滚动区域），**不需要手动拖任何 UI**。

## 为什么 UI 是运行时生成的

- 评审者拉到仓库后不用理解场景里的组件连线，**打开就能跑**。
- 全部布局写在 C# 里，改字号/位置不用进 Inspector。
- 代价是代码略长（`PokerSolverUI.cs` 约 1200 行），但可读性可控。

## 打包

菜单栏 **Poker → Build PC** / **Poker → Build Android APK**。

`PokerBuild.ApplyCommon()` 里集中配置：

| 项 | 值 |
| :--- | :--- |
| 公司名 | `LinZhongXiKe` |
| 产品名 | 残局解牌器 |
| PC 窗口 | 1600×900 窗口化（不要全屏） |
| Android 脚本后端 | **IL2CPP** |
| Android 架构 | **ARM64** |
| IL2CPP 优化 | Release |

### 为什么 Android 必须 IL2CPP + ARM64

算法核心是 `ulong`（64 位整数）密集运算。ARMv7 是 32 位架构，64 位运算需要软件模拟，
再叠加 Mono JIT 的解释开销，**同一局面实测慢 15–20 倍**（20v14 从 8 秒变成 90 秒以上）。

换成 IL2CPP（AOT 编译）+ ARM64（原生 64 位）后回到正常水平。

### 性能参数（按设备自适应）

见 `PokerSolverUI.PickDeviceProfile()`：

| 设备 | 线程 | 置换表 |
| :--- | :--- | :--- |
| PC | 4 | 2^26 |
| 手机 ≥6 GB RAM | 4 | 2^25 |
| 手机 4–6 GB | 4 | 2^24 |
| 手机 <4 GB | 2–3 | 2^23 |

## 界面功能速查

| 功能 | 操作 |
| :--- | :--- |
| 输入手牌 | 直接粘贴文本，或点 15 个点数按钮（左键 +1，长按/右键 -1） |
| 切换牌权 | 「我方先出」/「对方先出」 |
| 求解 | 「开始推算」，大局面会显示实时进度 |
| 模拟对抗 | 「模拟对抗」进入，双方轮流点按钮出牌 |
| 看对方怎么赢 | 必败时红色按钮「对方制胜：X」，点一下就让对手走那步 |
| 自由出牌 | 输入框里随便写牌（可以拆炸弹），程序只检查合法性 |
