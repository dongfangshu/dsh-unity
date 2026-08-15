# DSH 侧安装:unity-bridge-control 插件

给其他 DSH 用户的分发物。接收方需要:

1. DSH 会话(带 cordis 动态插件能力的 agent 预设)
2. Unity 项目里已装好 `com.dsh.unitybridge` 包(见 `../unitybridge-repo/README.md`,
   即 `Assets/Plugins/UnityBridge/`,可 git URL 或手动拷贝安装)

## 安装步骤

把本文件内容给你的 DSH agent,让它执行一次 **cordis_define** + **cordis_run**
(按下方参数原样调用,代码放 `code.host` / `code.client`),然后批准运行即可。

插件特点:
- **路径自动推导**——默认项目路径 = 会话工作区(工作区本身或 `UnityMain/` 子目录含
  `Assets/` 时自动识别);Unity.exe 默认取 Hub 标准路径
- **设置页覆盖**——Web 设置 → Unity Bridge 可改 `unityProjectPath` /
  `unityExePath`,存到工作区根目录 `.unity-bridge-config.json`(已 gitignore)
- 工具:`unity_status` / `unity_exec` / `unity_cs` / `unity_log`

## cordis_define 参数

**plugin**: `{ "kind": "new", "idPrefix": "unity" }`
**name**: `unity-bridge-control`
**purpose**: Distributable file-queue bridge to control the Unity editor from DSH.

### code.host

```js
<见下方 "host 代码" 块>
```

### code.client

```js
<见下方 "client 代码" 块>
```

---

## host 代码

```js
return {
  name: 'unity-bridge-control',
  apply(ctx) {
    const fs = ctx.get('fs')
    const timer = ctx.get('timer')
    if (fs === undefined || timer === undefined) return

    // Machine-local paths are derived from the session workspace so this
    // plugin is distributable: no hardcoded user paths.
    const policy = ctx.get('sandboxPolicy')
    const workspaceRoot = (policy && policy.workspaceRoot) ? policy.workspaceRoot.replace(/[\\/]+$/, '') : 'D:/DSH Unity'
    const cfgFile = workspaceRoot + '/.unity-bridge-config.json'

    const sleep = (ms) => timer.timeout(ms)
    const render = (_args, value) => [{ type: 'text', text: String(value) }]

    async function readJson(path) {
      try {
        const target = await fs.resolve(path)
        const text = await fs.readText(target)
        return JSON.parse(text)
      } catch (err) {
        return null
      }
    }

    async function defaultProject() {
      try {
        if (await fs.stat(await fs.resolve(workspaceRoot + '/Assets')) !== undefined) return workspaceRoot
      } catch (err) { }
      try {
        if (await fs.stat(await fs.resolve(workspaceRoot + '/UnityMain/Assets')) !== undefined) return workspaceRoot + '/UnityMain'
      } catch (err) { }
      return workspaceRoot
    }

    let _defaultsCache = null
    async function defaults() {
      if (_defaultsCache === null) {
        _defaultsCache = {
          unityProjectPath: await defaultProject(),
          unityExePath: 'C:/Program Files/Unity/Hub/Editor/2022.3.4f1/Editor/Unity.exe',
        }
      }
      return _defaultsCache
    }

    async function readConfig() {
      const cfg = await readJson(cfgFile)
      return Object.assign({}, await defaults(), cfg || {})
    }

    async function saveConfig(patch) {
      const cfg = await readConfig()
      const next = Object.assign({}, cfg, patch || {})
      for (const key of ['unityProjectPath', 'unityExePath']) {
        if (typeof next[key] !== 'string' || next[key].trim() === '') {
          throw new Error(key + ' must be a non-empty string')
        }
      }
      const target = await fs.resolve(cfgFile)
      await fs.writeText(target, JSON.stringify(next, null, 2))
      return next
    }

    async function bridgeRoot() {
      const cfg = await readConfig()
      return (cfg.unityProjectPath || (await defaults()).unityProjectPath).replace(/[\\/]+$/, '') + '/UnityBridge'
    }

    async function heartbeat() {
      const r = await bridgeRoot()
      const hb = await readJson(r + '/status/heartbeat.json')
      if (hb === null) return { online: false, reason: 'no heartbeat file' }
      const tsMs = typeof hb.ts === 'number' ? hb.ts * 1000 : 0
      const stale = Date.now() - tsMs > 8000
      return Object.assign({ online: !stale }, hb)
    }

    async function exec(op, args, timeoutMs) {
      const wait = timeoutMs || 30000
      const hb = await heartbeat()
      if (!hb.online && op !== 'ping') {
        return {
          ok: false,
          op,
          error: 'Unity bridge offline (no fresh heartbeat). Open Unity so the bridge can start.',
          heartbeat: hb,
        }
      }
      const r = await bridgeRoot()
      const id = Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8)
      const payload = JSON.stringify({ id, op, args: args || {} })
      let target
      try {
        target = await fs.resolve(r + '/in/' + id + '.json')
        await fs.writeText(target, payload)
      } catch (err) {
        return { ok: false, op, error: 'cannot write command file: ' + err.message }
      }
      const deadline = Date.now() + wait
      while (Date.now() < deadline) {
        const resp = await readJson(r + '/out/' + id + '.json')
        if (resp !== null && resp.id === id) return resp
        await sleep(200)
      }
      return { ok: false, op, error: 'timeout after ' + wait + 'ms; no response from Unity bridge' }
    }

    ctx.effect(() => harness.handle('unity-config:get', async () => {
      try { return { ok: true, config: await readConfig() } }
      catch (err) { return { ok: false, error: String((err && err.message) || err) } }
    }))
    ctx.effect(() => harness.handle('unity-config:set', async (args) => {
      try { return { ok: true, config: await saveConfig(args && args.config) } }
      catch (err) { return { ok: false, error: String((err && err.message) || err) } }
    }))

    const tools = [
      {
        name: 'unity_status',
        description: 'Check whether the Unity editor bridge is online and return its current state (play mode, open scene, Unity version). Call this before driving Unity.',
        parameters: {},
        output: { schema: { type: 'string' }, render },
        async execute() {
          return JSON.stringify(await heartbeat())
        },
      },
      {
        name: 'unity_exec',
        description: 'Send one command to the Unity editor bridge (file-queue protocol) and wait for the response. Ops: ping, status, open_scene (args {"path":"Assets/Scenes/X.unity","additive":false}), save, play, stop, pause, resume, step, menu (args {"item":"File/Save Project"}), eval (args {"type":"Namespace.Type","method":"StaticMethod","argsJson":"[1,2]"}), cs (prefer unity_cs), reload (recompile all scripts + domain reload in place; heartbeat pauses then resumes), hierarchy (args {"recursive":true}), log (args {"lines":50}). Pass args as a JSON string.',
        parameters: {
          op: { type: 'string', required: true, description: 'Operation name, e.g. "play", "open_scene", "status", "eval", "cs", "reload", "menu", "hierarchy", "log".' },
          args: { type: 'string', description: 'Optional arguments as a JSON object string, e.g. {"path":"Assets/Scenes/SampleScene.unity"}.' },
        },
        output: { schema: { type: 'string' }, render },
        async execute(args) {
          let parsed = {}
          if (args && typeof args.args === 'string' && args.args.trim() !== '') {
            try {
              parsed = JSON.parse(args.args)
            } catch (err) {
              return JSON.stringify({ ok: false, op: args.op, error: 'args is not valid JSON: ' + err.message })
            }
          }
          return JSON.stringify(await exec(args.op, parsed))
        },
      },
      {
        name: 'unity_log',
        description: 'Return the tail of the captured Unity console log (Debug.Log / warnings / errors) recorded by the bridge. Useful to check what happened after a command.',
        parameters: {
          lines: { type: 'integer', description: 'Number of trailing lines to return (default 50, max 300).' },
        },
        output: { schema: { type: 'string' }, render },
        async execute(args) {
          const n = args && typeof args.lines === 'number' ? Math.max(1, Math.min(300, Math.floor(args.lines))) : 50
          const hb = await heartbeat()
          if (hb.online) {
            const resp = await exec('log', { lines: n }, 5000)
            if (resp.ok) return JSON.stringify(resp.result)
          }
          const r = await bridgeRoot()
          const log = await readJson(r + '/status/log.json')
          if (log === null) return JSON.stringify({ online: false, error: 'no log available (Unity offline?)' })
          const entries = Array.isArray(log.entries) ? log.entries : []
          return JSON.stringify({ count: entries.length, entries: entries.slice(-n) })
        },
      },
      {
        name: 'unity_cs',
        description: 'Compile and execute C# code inside the Unity editor with Roslyn (in-memory, no domain reload, runs on Unity main thread). Contract: the code must define a static class named Entry with `public static object Main(object args)`. Default `using` directives are auto-prepended: System, System.Collections.Generic, System.Linq, System.Text, System.IO, System.Threading, System.Text.RegularExpressions, UnityEngine, UnityEditor — pass extra namespaces via the imports parameter (comma-separated). The optional data JSON object string is parsed and passed as the args argument of Main (an object; cast to Dictionary<string,object> to read keys). The value returned by Main becomes result.value. Example: code="using UnityEngine; public static class Entry { public static object Main(object args) { Debug.Log(\\\"hi\\\"); return \\\"done\\\"; } }".',
        parameters: {
          code: { type: 'string', required: true, description: 'C# source text to compile and run in the editor. Must define public static class Entry { public static object Main(object args) { ... } }.' },
          data: { type: 'string', description: 'Optional JSON object string passed to Main as the args argument, e.g. {"x":10,"label":"cube"}.' },
          imports: { type: 'string', description: 'Optional comma-separated extra namespaces to auto-prepend as using directives, e.g. "UnityEngine.AI,UnityEngine.SceneManagement".' },
        },
        output: { schema: { type: 'string' }, render },
        async execute(args) {
          const cmdArgs = {}
          if (args && typeof args.code === 'string') cmdArgs.code = args.code
          if (args && typeof args.data === 'string') cmdArgs.data = args.data
          if (args && typeof args.imports === 'string') cmdArgs.imports = args.imports
          return JSON.stringify(await exec('cs', cmdArgs, 60000))
        },
      },
    ]

    for (const tool of tools) {
      ctx.effect(() => harness.registerTool(ctx, harness.defineTool(tool)))
    }
    console.log('[unity-bridge-control] tools registered:', tools.map((t) => t.name).join(', '))
  },
}
```

## client 代码

```js
return {
  name: 'unity-bridge-control',
  apply(ctx) {
    const slots = ctx.get('slots')
    if (slots === undefined) return

    slots.inject('settings.section', () => slots.register(
      { name: 'settings.section', id: 'unity-bridge', order: 30, label: 'Unity Bridge' },
      (props) => {
        const [cfg, setCfg] = React.useState(null)
        const [draft, setDraft] = React.useState(null)
        const [status, setStatus] = React.useState('')
        const [busy, setBusy] = React.useState(false)

        React.useEffect(() => {
          host.call('unity-config:get', {}).then((r) => {
            if (r && r.ok && r.config) {
              setCfg(r.config)
              setDraft(r.config)
            }
          }).catch((e) => setStatus('读取配置失败: ' + String(e && e.message || e)))
        }, [])

        const save = () => {
          setBusy(true)
          setStatus('')
          host.call('unity-config:set', { config: draft }).then((r) => {
            setBusy(false)
            if (r && r.ok) {
              setCfg(r.config)
              setStatus('✓ 已保存')
            } else {
              setStatus('保存失败: ' + String(r && r.error || 'unknown'))
            }
          }).catch((e) => {
            setBusy(false)
            setStatus('保存失败: ' + String(e && e.message || e))
          })
        }

        if (!draft) {
          return React.createElement('div', null, '加载 Unity Bridge 配置...')
        }

        const inputStyle = {
          display: 'block',
          width: '100%',
          boxSizing: 'border-box',
          margin: '4px 0 12px 0',
          padding: '6px 8px',
          border: '1px solid rgba(128,128,128,0.5)',
          borderRadius: '6px',
          background: 'transparent',
          color: 'inherit',
          fontFamily: 'monospace',
          fontSize: '13px',
        }
        const labelStyle = { fontSize: '13px', opacity: 0.8, display: 'block' }

        return React.createElement('div', { style: { padding: '4px 0' } },
          React.createElement('div', { style: labelStyle }, 'Unity 项目路径 (bridge 位于 <project>/UnityBridge)'),
          React.createElement('input', {
            style: inputStyle,
            value: draft.unityProjectPath || '',
            onChange: (e) => setDraft(Object.assign({}, draft, { unityProjectPath: e.target.value })),
          }),
          React.createElement('div', { style: labelStyle }, 'Unity.exe 路径'),
          React.createElement('input', {
            style: inputStyle,
            value: draft.unityExePath || '',
            onChange: (e) => setDraft(Object.assign({}, draft, { unityExePath: e.target.value })),
          }),
          React.createElement('button', {
            onClick: save,
            disabled: busy,
            style: {
              padding: '6px 16px',
              borderRadius: '6px',
              border: '1px solid rgba(128,128,128,0.5)',
              background: 'transparent',
              color: 'inherit',
              cursor: 'pointer',
            },
          }, busy ? '保存中...' : '保存配置'),
          status ? React.createElement('div', { style: { marginTop: '8px', fontSize: '13px' } }, status) : null,
        )
      },
    ))
  },
}
```
