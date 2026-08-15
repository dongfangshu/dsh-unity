# Unity Bridge

D:\DSH Unity 仓库的上下文：通过文件队列协议驱动 Unity 编辑器的 agent 桥梁。本表定义桥梁协议的能力边界语言，只含领域术语，不含实现细节。

## 能力边界

**原子不可替代基础能力**:
Bridge 的能力面只含两类 op：execute（脚本）无法替代的原子原语，以及编辑器会话操作。增删改一律走 execute；**任何按 Unity 类型划分的专属工具都属于越界**——能力的增长只发生在 scheme 与脚本内部，不在 op 面上。
_Avoid_: Unity 类型化工具、领域专属 op（gameobject 域、asset 域等旧名）

## 读

**read**:
唯一的读接口。编辑器内一切可读状态都通过它表达，按 scheme 寻址。
_Avoid_: 各域读 op（scene.hierarchy、asset.list 等旧名）

**统一读取模型**:
read 内部的单一读取引擎：地址按来源解析为编辑器对象后，引擎识别"它是什么对象"并分派转储方式；所有来源共用同一响应形状。
_Avoid_: 按 scheme 各自实现读取器

**scheme（来源）**:
read 的地址空间，只表达"对象从哪里来"：`assets:`（项目文件）、`hierarchy:`（场景对象）、`select:`（当前选中）。组件不是来源，是 hierarchy: 内的寻址深度。
_Avoid_: 域、命名空间、component: scheme（已并入 hierarchy:）

**节点信封**:
所有 read 响应的统一形状：按 kind 携带相应字段；信封中的 path 为可直接回读的规范地址。
_Avoid_: 按 scheme 各记一种响应形状

**kind**:
统一读取模型识别出的对象类别：gameObject / component / text / asset。决定转储方式与信封字段。
_Avoid_: 按 C# 类型分派

**instance**:
对象的会话级身份，用于同名歧义时的寻址与消歧。会话内稳定，跨会话不保证（场景重开、资源重导入会变化）。
_Avoid_: 名字链裸寻址（歧义时）、文件 ID

**ls 语义**:
read 对层级结构的一次读取只返回所寻址节点的直接一层；下钻需要再次 read。

## 写

**execute**:
唯一的写路径；增删改都通过它执行。C# 源码经 Roslyn 编译进内存执行，入口契约为 `public static class Entry { public static object Main(object args) }`。
_Avoid_: 特权写 op、script.cs / script.eval（旧名）

## 内置

**core**:
编辑器会话操作的命名空间，封闭集合：`ping / reload / status / menuitem / openscene / removescene / save / play / stop / pause / resume / step`。会话操作指编辑器菜单/工具栏级的能力，与对象类型无关。
`reload` 是其中语义特殊的一条：先写出响应，再请求编译（随后 domain reload）。execute 路径上若触发 reload，正在跑的程序集会被杀掉、来不及自己写响应——靠认领与打断诊断收场，而不是在脚本里调用 reload。
_Avoid_: 按 Unity 类型扩展 core、场景域/资产域 op（旧名）

**log**:
查看编辑器 Console 日志的读原语；数据源为内存环形缓冲（含时间与 LogType）。与 read 并列、独立于 core。
_Avoid_: 日志文件读取（Editor.log 是未来扩展）

## 协议

**文件队列协议**:
主机（agent）与编辑器之间唯一的通信方式：JSON 文件经 `Library/UnityBridge/` 的 in/running/out/done/status 目录单向流动，无网络端口。

**认领**:
一条命令必须先从 in/ 移入 running/ 才执行；running/ 里至多一条。认领先于执行，所以同一条命令不会因 domain reload 再跑一遍（至多一次）。

**打断**:
新 domain 起来时，running/ 里残留的文件是上一次执行被 reload 中途杀掉的命令：写出 `ok: false, error: "interrupted by domain reload"`，不再重跑。
