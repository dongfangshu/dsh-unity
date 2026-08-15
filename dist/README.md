# DSH Unity Bridge — 分发套件

让 DSH agent 控制 Unity 编辑器的完整套件(桥 + Roslyn 脚本 + DSH 工具 + 设置页)。

## 结构

```
dist/
├── unitybridge-repo/        # Unity 侧仓库(可 push 到 git,对方 git URL 安装)
│   ├── README.md
│   └── Assets/Plugins/UnityBridge/   # UPM 包本体
│       ├── package.json
│       ├── Editor/UnityBridge.cs
│       ├── Editor/Roslyn/*.dll       # 12 DLL(Roslyn 3.8 + 依赖)
│       └── Runtime/                  # 预留(当前纯编辑器)
├── dsh/
│   └── install.md           # DSH 侧插件安装文档(含完整代码,agent 可直接执行)
└── LICENSE                  # MIT
```

## 接收方安装(2 步)

1. **Unity 侧**:把 `unitybridge-repo` 推到 git 后,
   `Package Manager → + → Add package from git URL`:
   `https://<host>/<user>/<repo>.git?path=/Assets/Plugins/UnityBridge`
   (或直接把 `Assets/Plugins/UnityBridge/` 拷进目标项目的 `Assets/Plugins/`)
   打开 Unity 2022.3+ → 桥自动启动,`<project>/UnityBridge/` 出现心跳文件
2. **DSH 侧**:把 `dsh/install.md` 给你的 agent,让它 cordis_define + run 即可
   (批准运行);路径默认从工作区自动推导,也可在 设置 → Unity Bridge 覆盖
3. **验证**:对 agent 说"查一下 Unity 状态"或直接 `unity_status`

## 能力速览

`unity_status` · `unity_exec`(status/play/open_scene/eval/menu/reload/hierarchy/log …)
· `unity_cs`(Roslyn 内存编译执行 C#) · `unity_log`(控制台日志尾)

## 安全

`eval`/`cs` 可在编辑器内执行任意代码;桥只走本地文件、不开端口。
`unityProjectPath` / `unityExePath` 保存在工作区 `.unity-bridge-config.json`(gitignore)。

## 给分发者的备注

- 直接把本仓库分享给对方即可;`UnityBridge/` 队列目录和配置文件不会被 git 收录
- DSH 插件是进程级的,接收方每次 DSH 重启后需重新 define(配置文件和 Unity 侧不受影响)
- 如需做成"复制即用"的 agent preset,需要把插件发布为 npm 包后以行引用挂载(二期)
