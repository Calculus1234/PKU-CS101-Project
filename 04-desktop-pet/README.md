# 白虎桌面宠物「小白」（MyPet）

> Windows 桌面常驻的白虎宠物：透明穿透窗口 + 毛皮着色器 + 可对话 + 可执行工具。
> **已交付给真实用户使用，并接过定制单。**

## 一句话概括

一个**全屏透明覆盖层窗口**（`WS_EX_LAYERED` + `WS_EX_TRANSPARENT`），
里面跑着一只 Unity 渲染的白虎，会跟着鼠标跑、被拖动、被摸头、聊天（LLM + TTS），
还能调用工具（读写文件、执行命令、MCP）。

---

## 技术亮点

### 1. Windows 透明穿透窗口

这是整个项目最困难的部分：

| 问题 | 解决 |
| :--- | :--- |
| 窗口全屏但不能挡住桌面操作 | 逐帧按光标位置切 `WS_EX_TRANSPARENT` |
| 拖宠物时窗口跟着移动 | `WM_WINDOWPOSCHANGING` 强制钉回 `(0,0)`，`WM_NCHITTEST` 永远返回 `HTCLIENT` |
| 高 DPI 下坐标全错 | 编译期设置 DPI-aware，坐标按物理像素换算 |
| 不占任务栏 | `ITaskbarList.DeleteTab` 移除窗口按钮，改用托盘图标 |

> **关键教训**：分层窗口（`WS_EX_LAYERED`）不能靠 `WM_NCHITTEST` 返回
> `HTTRANSPARENT` 来穿透——`WindowFromPoint` 依然返回本窗口。
> 必须逐帧切整窗的 `WS_EX_TRANSPARENT`。

### 2. Shell Fur 毛皮着色器

使用 Shell Fur URP（多层壳渲染）：

- 每根毛的物理光照模型（Marschner BSDF）；
- **踩过的坑**：物理头发模型让「屏幕位置不同 → 明暗不同」，因为相机角度改变 N·V。
  解决方式是加 `_FurViewLock` 参数，把 N·V 锁定为固定方向（用参数 lerp 而不是 shader_feature）。
- 短裤区域用 `_FurLengthMap` 遮罩，避免「裤子反光像锡纸」。

### 3. 工具调用框架

`src/PetFramework/Tools/`：

- 工具注册中心（`pet_tools.json` 热加载）；
- 四种工具源：内置、进程、HTTP、MCP stdio；
- 权限三档：开放 / 智能（只读自动、修改类询问）/ 逐条确认；
- 便携 Python 运行时（`PythonEnv/`，纯标准库，约 52MB）。

### 4. 独立工作区

`MyPetWorkspace.exe`：

- 桌宠启动时预启动（隐藏窗口）；
- `F8` 唤起同一个窗口（单实例，绝不重复启动）；
- 桌宠退出时工作区跟着退出（`-ownerPid` 看门狗）。

---

## 真实使用场景

- **给父亲做的功能**：把图片转换成表格（接单过程中实现的定制需求）。
- **接过的定制单**：基于这个框架修改出的其他桌宠角色。
- **日常使用**：开机自启，常驻桌面。

这是四个项目里唯一有**真实用户反馈驱动迭代**的项目。

---

## 代码规模

**49 个 C# 文件**（`src/PetFramework/` + `src/Workspace/`），不含第三方包。

---

## 如何运行

### 环境

- Unity 2022.3.62f3c1 + URP 14
- Windows 10/11
- 成品：`Build/MyPet.exe`（不包含在本仓库）

### 说明

**本仓库只包含自写源码。** 完整可运行版本需要：

- 白虎模型（`WhiteTiger_DesktopPet.fbx`）
- Shell Fur URP 着色器包
- 便携 Python 运行时
- LLM API 密钥（`config.json`）

---

## 局限

1. **仅支持 Windows**：透明窗口逻辑深度绑定 Win32 API。
2. **LLM 延迟明显**：本地模型慢，远程模型依赖网络。
3. **拖拽投喂仍不稳定**：`WM_DROPFILES` 在透明窗口下偶发收不到。

## 后续计划

- [ ] 修复拖拽投喂的窗口消息问题
- [ ] 多角色支持（同一框架换皮）
- [ ] 更多工具源（截图识别、文件整理）
