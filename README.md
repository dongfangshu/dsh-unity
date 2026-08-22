# DSH ↔ Unity Bridge（v3）

用本地文件队列驱动 Unity 编辑器。没有网络端口、没有 SDK：Agent 把命令写进工程里的 `Library/UnityBridge/`，编辑器执行后把 JSON 回包写出来。

本仓库的 Unity 工程在 `UnityMain/`。队列目录是机器本地缓存（和 Unity 自己的 `Library/` 一样），不会进版本库，初始化时会自动重建。

## 开始使用

1. 用 Unity 打开 `UnityMain/`（桥梁以插件形式在 `Assets/Plugins/UnityBridge/`）。
2. 确认菜单 **Tools > Unity Bridge > Enable** 已打开。编辑器加载后会自动启动。
3. 看心跳文件是否在刷新（大约每秒一次）：

   `UnityMain/Library/UnityBridge/status/heartbeat.json`

   文件不存在，或超过约 15 秒没更新，说明编辑器没开、插件没装，或桥梁被关掉了。**先打开工程**，不要尝试从外面启动 Unity。

4. 让 Agent 控制编辑器时，加载技能 `skills/unity-bridge/SKILL.md`（按需加载，不要默认塞进所有对话）。技能只发现当前工作区里的工程，不会自己启动 Unity。

Hierarchy 里右键 **Copy for Agent**（在 Copy 下面），可以把对象地址贴给 Agent；Agent 用 `read.hierarchy` 读 `path`，同名物体用 `@instance` 消歧。

## 仓库里有什么

| 位置 | 作用 |
|---|---|
| `UnityMain/` | Unity 工程 |
| `UnityMain/Assets/Plugins/UnityBridge/` | 编辑器侧插件：轮询 `in/`、在主线程执行、写回包和心跳 |
| `skills/unity-bridge/SKILL.md` | 教 Agent 怎么走协议 |
| `skills/unity-bridge/capture-view.cs` | 截 Scene / Game 视图的脚本模板 |
| `skills/unity-bridge/test-bridge.ps1` | 协议自测 |
| `CONTEXT.md` | 领域术语（能力边界语言） |

## 怎么发一条命令

队列在 `<工程>/Library/UnityBridge/`：

| 目录 | 含义 |
|---|---|
| `in/` | Agent 写入的命令：`<op>-<yyyyMMdd-HHmmssfff>.json` 或 `.cs` |
| `running/` | 正在执行的命令（执行前移入，同时最多一条） |
| `out/` | Unity 写的回包，文件名与命令 stem 相同（约 120 秒后清理） |
| `archive/` | 已完成的指令 + 回包，永久归档（不自动清理，可手动删除） |
| `status/heartbeat.json` | 在线检测 |
| `status/log.json` | 最近 300 条 Console |

规则：

- 文件名是关联 id（本地时间，带毫秒，避免同一秒撞名）。JSON **不要** 写 `id` 字段。
- **先写 `*.tmp` 再改名为最终文件**，不要直接写正式路径，否则半截文件会被认领并解析失败。
- 读回包：轮询 `out/<stem>.json`，`resp.id` 必须和你写的 stem 一致。
- 执行看的是 `domain` / `op`（或 `.cs` 正文），不是文件名里的 op 前缀。
- 每条指令执行完会连同回包归档到 `archive/`（指令原文 + `<stem>.response.json`），永不自动删除；它是完整的执行历史，空间不足时可整体清空（和 `Library/` 一样是机器本地缓存）。

PowerShell 示例（原子写入 + 等回包）：

```powershell
$in  = "UnityMain\Library\UnityBridge\in"
$out = "UnityMain\Library\UnityBridge\out"
$stem = "status-$(Get-Date -Format 'yyyyMMdd-HHmmssfff')"
$tmp = "$in\$stem.json.tmp"
Set-Content -Path $tmp -Value '{"domain":"core","op":"status","args":{}}' -Encoding UTF8 -NoNewline
Move-Item -LiteralPath $tmp -Destination "$in\$stem.json"
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
  if (Test-Path "$out\$stem.json") { Get-Content "$out\$stem.json" -Raw; break }
  Start-Sleep -Milliseconds 300
}
```

成功回包形如：

```json
{ "id": "status-20260816-003712189", "domain": "core", "op": "status", "ok": true, "ts": 1786811641.8, "result": { ... } }
```

失败则 `"ok": false`，错误信息在 `"error"`。

## 四种能力

桥梁只暴露四个域：**read / execute / log / core**。不要为 Unity 类型再加专用 op；新能力写进 `execute.cs`，或扩展 `read` 的寻址。

### read — 唯一读接口

所有读取返回同一套节点信封：

```
{ "path", "kind", "name"?, "type"?, "instance"?, "activeSelf"?,
  "components"?[], "children"?[], "properties"?{}, "content"? }
```

`kind` 为 `scene` / `gameObject` / `component` / `text` / `asset`。信封里的 `path` 可以直接再读一次。

| op | 地址 | 返回 |
|---|---|---|
| `read.assets` | `assets:<工程相对路径>`（必须是 `Assets/...` 或 `Packages/...`） | 文本资源 → `kind:"text"` + `content`；序列化资源（`.prefab` / `.asset` / `.unity`）→ `kind:"asset"` + `properties`；二进制会报错 |
| `read.hierarchy` | `hierarchy:<场景>/<名字>/<名字>[@instance][/Type.Name]` | 场景 → 根物体；物体 → 子节点一层 + 组件类型名；末段是组件类型 → 组件属性 |
| `read.select` | `select:` | 当前选中；空选中 → `[]` |

层级必须带场景路径（没开就先 `core.openscene`）。一次只返回一层（ls 语义），往下再读。同名兄弟用 `@instance`（会话内稳定，场景重开会变）。

JSON 命令示例：

```json
{ "domain": "read", "op": "hierarchy", "args": { "path": "Assets/Scenes/SampleScene.unity/Player" } }
```

### execute — 唯一写路径

增删改都丢 `.cs` 文件到 `in/`，文件名 `cs-<yyyyMMdd-HHmmssfff>.cs`（同样先 `.tmp` 再改名）。没有 JSON 信封。

脚本必须提供：

```csharp
public static class Entry {
  public static object Main(object args) {
    var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
    c.name = "agent-cube";
    return "created " + c.name;
  }
}
```

- `args` 恒为 `null`。返回值变成 `result.value`。
- 已自动 `using`：`System`、集合/IO/正则、`UnityEngine`、`UnityEditor`。其它命名空间写在文件里。
- 编辑器里已加载的程序集都能引用（含工程脚本）。
- 编辑模式里创建的物体**不会自动持久化**，domain reload 或关场景会丢；要留下就 `core.savescene` / `core.saveassets`。
- 截图：把 `skills/unity-bridge/capture-view.cs` 拷成一条 `execute.cs`。Play 模式拍 `Camera.main`，编辑模式拍 Scene 视图。PNG 写到 `Library/UnityBridge/status/view.png`，回包 `result.value.kind` 为 `"image"` 时按**图片**去读该路径，不要把 PNG 塞进 JSON。

### log — Console

| op | 参数 | 作用 |
|---|---|---|
| `log.log` | `lines?`（默认 50，最大 300） | 环形缓冲末尾 |

### core — 编辑器会话（封闭集合）

| op | 参数 | 作用 |
|---|---|---|
| `core.ping` | — | 连通性（`result.pong` = true） |
| `core.status` | — | 是否在播、是否在编译、当前场景、选中、工程路径等 |
| `core.refresh` | — | `AssetDatabase.Refresh(ForceUpdate)`，**不保存**。改了工程里已有 `.cs` 之后再发，等心跳恢复（10–30 秒） |
| `core.menuitem` | `item` | 按完整菜单路径执行，例如 `"File/Save Project"` |
| `core.openscene` | `path`，`mode?`（`single` / `additive`） | 打开场景 |
| `core.removescene` | `path` 或 `"all"` | 关闭场景（丢弃未保存）；拒绝关掉最后一个 |
| `core.savescene` | `path?` | 保存打开的场景 |
| `core.saveassets` | — | 保存脏资产（不含场景） |
| `core.play` / `stop` | — | 进入 / 退出 Play |
| `core.pause` / `resume` / `step` | — | Play 步进 |

改了场景或资产要先保存并等到 `ok`，再 `refresh`。**不要把 save 和 refresh 排在一起。** `refresh` 可能触发 domain reload；正在跑的 `execute.cs` 会被杀掉，回包 `"interrupted by domain reload"`，不会自动重试。

## 常见流程

- **Unity 在不在？** 读 `heartbeat.json`，或发 `core.status`。
- **看场景** → `core.status` 拿当前场景，再 `read.hierarchy`，一层一层往下。
- **看工程文件** → `read.assets`。
- **看组件数值** → `read.hierarchy` 读到 `.../物体/组件类型`。
- **创建 / 改 / 删** → `execute.cs`。资产要持久化：脚本里 `EditorUtility.SetDirty`，再 `core.saveassets`。
- **留下当前场景** → `core.savescene`。
- **截图** → 丢 `capture-view.cs`，再读 `status/view.png`。
- **Play 测试** → `core.play`，等约 3 秒，用 read / log / 截图检查，再 `core.stop`。

## 自测与排障

编辑器开着时，在 `skills/unity-bridge/` 下：

```powershell
pwsh ./test-bridge.ps1
```

`-SkipPlay` / `-SkipRefresh` 可跳过对应步骤；`-IncludeSceneOps` 会测 `openscene`（可能弹出保存对话框把测试卡住）。

| 现象 | 处理 |
|---|---|
| 超时或 `"ok": false` | 编辑器没开、插件没装、或 Tools 菜单里关了桥梁 |
| `core.refresh` 后没回包 | 正常：脚本重导入会 reload；等心跳文件重新刷新 |
| `"interrupted by domain reload"` | 命令执行到一半被杀掉了，需要的话再发一次 |
| C# 编译失败 | `error` 里有 `(行, 列)` |
| 刚写入的 JSON 解析失败 | 半截文件被认领了，必须 `.tmp` 再改名 |
| `read.hierarchy` 说场景没开 | 先 `core.openscene` |
| Safe Mode | 工程脚本编不过，桥梁会停；修好脚本并退出 Safe Mode |

## 安全

`execute.cs` 在编辑器里跑任意代码，拥有完整 Editor API。只在你信任的工程上用，把 `Library/UnityBridge/` 当作本机受信工具目录。
