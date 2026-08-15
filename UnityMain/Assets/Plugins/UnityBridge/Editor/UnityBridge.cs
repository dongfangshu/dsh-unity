// ============================================================================
//  UnityBridge.cs — DSH <-> Unity file-queue bridge (editor automation)
// ============================================================================
//  Protocol v1 (see UnityBridge/README in the project root for the full doc):
//
//    <project>/UnityBridge/
//      in/     command files  <id>.json   (written by the agent side)
//      out/    response files <id>.json   (written here, pruned after 120s)
//      done/   processed command files    (pruned after 600s)
//      status/ heartbeat.json (every 1s) + log.json (captured console log)
//
//  Ops: ping | status | open_scene | save | play | stop | pause | resume |
//       step | menu | eval | cs | reload | hierarchy | log
//
//  `cs` compiles and executes agent-written C# with Roslyn (in memory, no
//  domain reload): args.code = C# source, args.imports = extra namespaces,
//  args.data = JSON object passed to Entry.Main(object args).
//
//  `reload` recompiles all scripts and domain-reloads the editor in place
//  (no restart) — use it after editing C# files.
//
//  This bridge only listens on the local filesystem and never opens a
//  network port. The `eval` op can invoke ANY static method in the editor —
//  treat the bridge folder as trusted local tooling.
//
//  File layout:
//    UnityBridgeDto.cs  — wire protocol DTOs (BridgeCommand/BridgeResponse)
//                         + JSONNode <-> plain object converters
//    SimpleJSON.cs      — MIT JSON library (Bunny83) used for all JSON work
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SimpleJSON;
using Assembly = System.Reflection.Assembly;

namespace DSH.UnityBridge
{
    [InitializeOnLoad]
    public static class UnityBridge
    {
        public const string Version = "1.0.0";
        public const float PollInterval = 0.15f;        // command folder poll
        public const float HeartbeatInterval = 1.0f;    // status file refresh
        public const int LogRingSize = 300;

        static bool _enabled = true;
        static string _root;
        static string _inDir;
        static string _outDir;
        static string _doneDir;
        static string _statusDir;
        static float _lastPoll;
        static float _lastHeartbeat;
        static float _lastLogWrite;
        static readonly List<string> LogRing = new List<string>();
        static bool _logDirty;

        static UnityBridge()
        {
            _root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "UnityBridge"));
            _inDir = Path.Combine(_root, "in");
            _outDir = Path.Combine(_root, "out");
            _doneDir = Path.Combine(_root, "done");
            _statusDir = Path.Combine(_root, "status");
            Directory.CreateDirectory(_inDir);
            Directory.CreateDirectory(_outDir);
            Directory.CreateDirectory(_doneDir);
            Directory.CreateDirectory(_statusDir);

            Application.logMessageReceived += OnLog;
            EditorApplication.update += Update;
            Debug.Log("[UnityBridge] file-queue bridge " + Version + " ready at " + _root);
        }

        [MenuItem("Tools/Unity Bridge/Enable")]
        public static void Enable() { _enabled = true; Debug.Log("[UnityBridge] enabled"); }

        [MenuItem("Tools/Unity Bridge/Disable")]
        public static void Disable() { _enabled = false; Debug.Log("[UnityBridge] disabled"); }

        [MenuItem("Tools/Unity Bridge/Open bridge folder")]
        public static void OpenFolder() { EditorUtility.RevealInFinder(_root); }

        // ------------------------------------------------------------------
        // Update loop: poll commands, refresh heartbeat, flush log
        // ------------------------------------------------------------------
        static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (_enabled && now - _lastPoll >= PollInterval)
            {
                _lastPoll = now;
                try { ProcessCommands(); }
                catch (Exception ex) { Debug.LogWarning("[UnityBridge] poll error: " + ex.Message); }
            }
            if (now - _lastHeartbeat >= HeartbeatInterval)
            {
                _lastHeartbeat = now;
                try { WriteHeartbeat(); PruneOldFiles(); }
                catch (Exception ex) { Debug.LogWarning("[UnityBridge] heartbeat error: " + ex.Message); }
            }
            if (_logDirty && now - _lastLogWrite >= 0.5f)
            {
                _lastLogWrite = now;
                _logDirty = false;
                try { WriteLogFile(); }
                catch { _logDirty = true; }
            }
        }

        // ------------------------------------------------------------------
        // Command processing
        // ------------------------------------------------------------------
        static void ProcessCommands()
        {
            string[] files = Directory.GetFiles(_inDir, "*.json");
            if (files.Length == 0) return;

            foreach (string file in files.OrderBy(File.GetLastWriteTime))
            {
                string text = null;
                try { text = File.ReadAllText(file); }
                catch { continue; } // locked or vanished; try next tick

                string id = null;
                string op = null;
                try
                {
                    var cmd = BridgeCommand.Parse(text);
                    id = string.IsNullOrEmpty(cmd.id) ? ExtractId(text) : cmd.id;
                    op = cmd.op;
                    object result = Execute(cmd.op, cmd.args);
                    WriteResponse(id, op, true, result, null);
                }
                catch (Exception ex)
                {
                    WriteResponse(id ?? ExtractId(text), op ?? GetOpHint(text), false, null, ex.Message);
                }
                finally
                {
                    try { File.Move(file, Path.Combine(_doneDir, Path.GetFileName(file))); }
                    catch { try { File.Delete(file); } catch { } }
                }
            }
        }

        static string GetOpHint(string text)
        {
            try
            {
                var node = JSON.Parse(text);
                if (node != null && node.IsObject) return node["op"].Value;
            }
            catch { }
            return "?";
        }

        static string ExtractId(string text)
        {
            if (text == null) return "unknown";
            try
            {
                var node = JSON.Parse(text);
                if (node != null && node.IsObject)
                {
                    string v = node["id"].Value;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "unknown-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static void WriteResponse(string id, string op, bool ok, object result, string error)
        {
            var resp = new BridgeResponse(id, op, ok, result, error);
            AtomicWrite(Path.Combine(_outDir, id + ".json"), resp.ToJson());
        }

        // ------------------------------------------------------------------
        // Ops
        // ------------------------------------------------------------------
        static object Execute(string op, JSONObject args)
        {
            switch (op)
            {
                case "ping":
                    return new Dictionary<string, object> { ["pong"] = true, ["bridge"] = Version };
                case "status":
                    return Status();
                case "open_scene":
                    return OpenScene(GetString(args, "path"), GetBool(args, "additive"));
                case "save":
                    return new Dictionary<string, object> { ["saved"] = EditorSceneManager.SaveOpenScenes() };
                case "play":
                    if (!EditorApplication.isPlaying) EditorApplication.EnterPlaymode();
                    return new Dictionary<string, object> { ["playing"] = EditorApplication.isPlaying };
                case "stop":
                    if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
                    return new Dictionary<string, object> { ["playing"] = EditorApplication.isPlaying };
                case "pause":
                    EditorApplication.isPaused = true;
                    return new Dictionary<string, object> { ["paused"] = EditorApplication.isPaused };
                case "resume":
                    EditorApplication.isPaused = false;
                    return new Dictionary<string, object> { ["paused"] = EditorApplication.isPaused };
                case "step":
                    EditorApplication.Step();
                    return new Dictionary<string, object> { ["paused"] = EditorApplication.isPaused };
                case "menu":
                    return new Dictionary<string, object>
                    {
                        ["executed"] = EditorApplication.ExecuteMenuItem(GetString(args, "item") ?? "")
                    };
                case "eval":
                    return Eval(GetString(args, "type"), GetString(args, "method"), GetString(args, "argsJson"));
                case "cs":
                    return EvalCs(GetString(args, "code"), GetString(args, "imports"), GetString(args, "data"));
                case "reload":
                    // Recompile all scripts + domain reload (no editor restart).
                    CompilationPipeline.RequestScriptCompilation();
                    return new Dictionary<string, object> { ["reloading"] = true };
                case "hierarchy":
                    return Hierarchy(GetBool(args, "recursive"));
                case "log":
                    return LogSnapshot(GetInt(args, "lines", 50));
                default:
                    throw new Exception("unknown op: " + op);
            }
        }

        static Dictionary<string, object> Status()
        {
            var scene = SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                ["bridge"] = Version,
                ["version"] = Application.unityVersion,
                ["playing"] = EditorApplication.isPlaying,
                ["paused"] = EditorApplication.isPaused,
                ["scene"] = scene.path,
                ["sceneName"] = scene.name,
                ["roots"] = scene.rootCount,
                ["selection"] = Selection.activeObject != null ? Selection.activeObject.name : null
            };
        }

        static Dictionary<string, object> OpenScene(string path, bool additive)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("open_scene requires args.path (e.g. \"Assets/Scenes/SampleScene.unity\")");
            var scene = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new Dictionary<string, object> { ["scene"] = scene.path, ["loaded"] = scene.isLoaded };
        }

        static Dictionary<string, object> Eval(string typeName, string methodName, string argsJson)
        {
            if (string.IsNullOrEmpty(typeName)) throw new Exception("eval requires args.type");
            if (string.IsNullOrEmpty(methodName)) throw new Exception("eval requires args.method");

            Type type = ResolveType(typeName);
            if (type == null) throw new Exception("type not found: " + typeName);

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo method = type.GetMethod(methodName, flags);
            if (method == null) throw new Exception("static method not found: " + typeName + "." + methodName);

            object[] rawArgs = new object[0];
            if (!string.IsNullOrEmpty(argsJson))
            {
                var parsed = JSON.Parse(argsJson);
                if (parsed == null || !parsed.IsArray) throw new Exception("argsJson must be a JSON array");
                var list = new List<object>();
                foreach (JSONNode item in (JSONArray)parsed)
                    list.Add(BridgeJson.ToPlainObject(item));
                rawArgs = list.ToArray();
            }

            ParameterInfo[] ps = method.GetParameters();
            if (rawArgs.Length > ps.Length) throw new Exception("too many arguments: " + methodName + " takes " + ps.Length);
            var converted = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                if (i < rawArgs.Length && rawArgs[i] != null)
                    converted[i] = Convert.ChangeType(rawArgs[i], p.ParameterType, CultureInfo.InvariantCulture);
                else if (p.HasDefaultValue)
                    converted[i] = p.DefaultValue;
                else
                    converted[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            }

            object result;
            try { result = method.Invoke(null, converted); }
            catch (TargetInvocationException tie)
            {
                throw new Exception("eval threw: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
            }
            return new Dictionary<string, object> { ["value"] = Simplify(result) };
        }

        static object Simplify(object value)
        {
            if (value == null) return null;
            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string || value is decimal || value is DateTime) return value;
            return value.ToString();
        }

        // ------------------------------------------------------------------
        // cs — compile and execute agent-written C# with Roslyn (in memory,
        // no domain reload). CSharpCompilation -> Assembly.Load -> reflection.
        // Contract: code must define `public static class Entry { public static
        // object Main(object args) { ... } }`. args = parsed `data` JSON.
        // ------------------------------------------------------------------
        static object EvalCs(string code, string importsArg, string dataArg)
        {
            if (string.IsNullOrEmpty(code)) throw new Exception("cs requires args.code (C# source text)");

            var imports = new List<string>
            {
                "System", "System.Collections.Generic", "System.Linq", "System.Text",
                "System.IO", "System.Threading", "System.Text.RegularExpressions",
                "UnityEngine", "UnityEditor"
            };
            if (!string.IsNullOrEmpty(importsArg))
                foreach (string ns in importsArg.Split(','))
                    if (!string.IsNullOrWhiteSpace(ns)) imports.Add(ns.Trim());

            // Reference every assembly currently loaded in the editor.
            var refs = new List<MetadataReference>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(a.Location)); }
                catch { }
            }

            // Prepend extra `using` directives the agent requested.
            var sb = new StringBuilder();
            foreach (string ns in imports)
                sb.Append("using ").Append(ns).Append(";\n");
            sb.Append(code);
            string fullCode = sb.ToString();

            // Roslyn needs the code-page provider for some encodings; safe to register once.
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { }

            // Compile with CSharpCompilation (NOT the Scripting API: its assembly
            // loader uses AssemblyLoadContext, which Unity's Mono stubs out with
            // NotImplementedException). Emit to a memory stream, then load with
            // Assembly.Load(byte[]) and invoke the entry point via reflection.
            var tree = CSharpSyntaxTree.ParseText(fullCode, new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create(
                "AgentScript",
                new[] { tree },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            byte[] peBytes;
            using (var pe = new MemoryStream())
            {
                var emitResult = compilation.Emit(pe);
                if (!emitResult.Success)
                {
                    var errs = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Take(10)
                        .Select(d => d.ToString());
                    throw new Exception("cs compile errors:\n" + string.Join("\n", errs));
                }
                peBytes = pe.ToArray();
            }

            Assembly asm;
            try { asm = Assembly.Load(peBytes); }
            catch (Exception ex) { throw new Exception("cs assembly load failed: " + ex); }

            Type entry = asm.GetTypes().FirstOrDefault(t => t.Name == "Entry" && t.IsClass);
            if (entry == null)
                throw new Exception("cs code must define a static class named 'Entry' (e.g. public static class Entry)");
            MethodInfo main = entry.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
            if (main == null)
                throw new Exception("cs code must define a public static method 'Main' on Entry");

            object args = null;
            if (!string.IsNullOrEmpty(dataArg))
            {
                try { args = BridgeJson.ToPlainObject(JSON.Parse(dataArg)); }
                catch { args = null; }
            }

            object result;
            try
            {
                ParameterInfo[] ps = main.GetParameters();
                if (ps.Length == 0) result = main.Invoke(null, null);
                else if (ps.Length == 1 && ps[0].ParameterType == typeof(object)) result = main.Invoke(null, new[] { args });
                else throw new Exception("Entry.Main must take no parameters or one 'object' parameter");
            }
            catch (TargetInvocationException tie)
            {
                throw new Exception("cs script error: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
            }

            return new Dictionary<string, object>
            {
                ["value"] = Simplify(result)
            };
        }

        static Type ResolveType(string name)
        {
            Type t = Type.GetType(name);
            if (t != null) return t;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(name);
                if (t != null) return t;
                t = asm.GetType("UnityEngine." + name);
                if (t != null) return t;
                t = asm.GetType("UnityEditor." + name);
                if (t != null) return t;
            }
            return null;
        }

        static List<object> Hierarchy(bool recursive)
        {
            var result = new List<object>();
            var scene = SceneManager.GetActiveScene();
            foreach (GameObject go in scene.GetRootGameObjects())
                Collect(go, scene.name + "/" + go.name, result, recursive);
            return result;
        }

        static void Collect(GameObject go, string path, List<object> result, bool recursive)
        {
            result.Add(new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["path"] = path,
                ["active"] = go.activeSelf,
                ["children"] = go.transform.childCount
            });
            if (!recursive) return;
            foreach (Transform child in go.transform)
                Collect(child.gameObject, path + "/" + child.name, result, true);
        }

        static Dictionary<string, object> LogSnapshot(int lines)
        {
            lock (LogRing)
            {
                int n = Mathf.Min(lines <= 0 ? 50 : lines, LogRing.Count);
                return new Dictionary<string, object>
                {
                    ["count"] = LogRing.Count,
                    ["entries"] = LogRing.Skip(LogRing.Count - n).ToList()
                };
            }
        }

        // ------------------------------------------------------------------
        // Heartbeat, log capture, pruning
        // ------------------------------------------------------------------
        static void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (LogRing)
            {
                LogRing.Add(Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture) + " [" + type + "] " + condition);
                if (LogRing.Count > LogRingSize) LogRing.RemoveRange(0, LogRing.Count - LogRingSize);
                _logDirty = true;
            }
        }

        static void WriteHeartbeat()
        {
            var scene = SceneManager.GetActiveScene();
            Dictionary<string, object> hb;
            lock (LogRing)
            {
                hb = new Dictionary<string, object>
                {
                    ["ts"] = UnixNow(),
                    ["bridge"] = Version,
                    ["version"] = Application.unityVersion,
                    ["enabled"] = _enabled,
                    ["playing"] = EditorApplication.isPlaying,
                    ["paused"] = EditorApplication.isPaused,
                    ["scene"] = scene.path,
                    ["roots"] = scene.rootCount,
                    ["logCount"] = LogRing.Count
                };
            }
            AtomicWrite(Path.Combine(_statusDir, "heartbeat.json"), BridgeJson.ToJsonNode(hb).ToString());
        }

        static void WriteLogFile()
        {
            Dictionary<string, object> payload;
            lock (LogRing)
            {
                payload = new Dictionary<string, object>
                {
                    ["ts"] = UnixNow(),
                    ["count"] = LogRing.Count,
                    ["entries"] = new List<object>(LogRing)
                };
            }
            AtomicWrite(Path.Combine(_statusDir, "log.json"), BridgeJson.ToJsonNode(payload).ToString());
        }

        static void PruneOldFiles()
        {
            Prune(_outDir, TimeSpan.FromSeconds(120));
            Prune(_doneDir, TimeSpan.FromSeconds(600));
            Prune(_inDir, TimeSpan.FromSeconds(600)); // stale unclaimed commands
        }

        static void Prune(string dir, TimeSpan age)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - age;
                foreach (string f in Directory.GetFiles(dir, "*.json"))
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        static void AtomicWrite(string finalPath, string content)
        {
            // Avoid File.Move(string,string,bool) so this compiles under both
            // the .NET Framework and .NET Standard 2.1 compatibility profiles.
            string tmp = finalPath + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmp, finalPath);
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------
        static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        static string GetString(JSONObject args, string key)
        {
            if (args == null || !args.HasKey(key)) return null;
            return args[key].Value;
        }

        static bool GetBool(JSONObject args, string key)
        {
            if (args == null || !args.HasKey(key)) return false;
            var n = args[key];
            return n.IsBoolean ? n.AsBool : (n.Value == "true" || n.Value == "1");
        }

        static int GetInt(JSONObject args, string key, int def)
        {
            if (args == null || !args.HasKey(key)) return def;
            return args[key].AsInt;
        }
    }

    // ========================================================================
    //  Globals type exposed to Roslyn scripts (accessible by name in code,
    //  e.g. `Args`, `Args["key"]`).
    // ========================================================================
    public class CsGlobals
    {
        public object Args;
    }
}
#endif
