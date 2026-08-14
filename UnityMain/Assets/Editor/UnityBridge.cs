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
//  Command envelope:   { "id": "...", "op": "...", "args": { ... } }
//  Response envelope:  { "id": "...", "op": "...", "ok": true,
//                        "result": {...} | "error": "..." }
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
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
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

                string id = ExtractId(text);
                try
                {
                    var cmd = BridgeJson.Parse(text) as Dictionary<string, object>;
                    if (cmd == null) throw new Exception("command is not a JSON object");
                    string op = GetString(cmd, "op");
                    if (string.IsNullOrEmpty(op)) throw new Exception("missing 'op'");
                    var args = cmd.TryGetValue("args", out var av) ? av as Dictionary<string, object> : new Dictionary<string, object>();
                    if (args == null) throw new Exception("'args' must be a JSON object");
                    object result = Execute(op, args);
                    WriteResponse(id, op, true, result, null);
                }
                catch (Exception ex)
                {
                    WriteResponse(id, GetOpHint(text), false, null, ex.Message);
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
                var d = BridgeJson.Parse(text) as Dictionary<string, object>;
                if (d != null && d.TryGetValue("op", out var v)) return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch { }
            return "?";
        }

        static string ExtractId(string text)
        {
            if (text == null) return "unknown";
            try
            {
                var d = BridgeJson.Parse(text) as Dictionary<string, object>;
                if (d != null && d.TryGetValue("id", out var v)) return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch { }
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "unknown-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static void WriteResponse(string id, string op, bool ok, object result, string error)
        {
            var resp = new Dictionary<string, object>
            {
                ["id"] = id,
                ["op"] = op ?? "",
                ["ok"] = ok,
                ["ts"] = UnixNow()
            };
            if (ok) resp["result"] = result ?? new Dictionary<string, object>();
            else resp["error"] = error ?? "unknown error";
            AtomicWrite(Path.Combine(_outDir, id + ".json"), BridgeJson.Stringify(resp));
        }

        // ------------------------------------------------------------------
        // Ops
        // ------------------------------------------------------------------
        static object Execute(string op, Dictionary<string, object> args)
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
                var parsed = BridgeJson.Parse(argsJson);
                if (!(parsed is List<object> list)) throw new Exception("argsJson must be a JSON array");
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
            if (!string.IsNullOrEmpty(dataArg)) args = BridgeJson.Parse(dataArg);

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
            AtomicWrite(Path.Combine(_statusDir, "heartbeat.json"), BridgeJson.Stringify(hb));
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
            AtomicWrite(Path.Combine(_statusDir, "log.json"), BridgeJson.Stringify(payload));
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

        static string GetString(Dictionary<string, object> args, string key)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            return null;
        }

        static bool GetBool(Dictionary<string, object> args, string key)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool b) return b;
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                return s == "true" || s == "1";
            }
            return false;
        }

        static int GetInt(Dictionary<string, object> args, string key, int def)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { }
            }
            return def;
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

    // ========================================================================
    //  Minimal JSON parser/writer (no external dependency in Unity 2022)
    // ========================================================================
    public static class BridgeJson
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int pos = 0;
            return ParseValue(text, ref pos);
        }

        static object ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            switch (c)
            {
                case '{':
                {
                    var obj = new Dictionary<string, object>();
                    pos++;
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == '}') { pos++; return obj; }
                    while (true)
                    {
                        SkipWs(s, ref pos);
                        string key = ParseString(s, ref pos);
                        SkipWs(s, ref pos);
                        if (pos < s.Length && s[pos] == ':') pos++;
                        else throw new FormatException("expected ':' at " + pos);
                        obj[key] = ParseValue(s, ref pos);
                        SkipWs(s, ref pos);
                        if (pos >= s.Length) throw new FormatException("unterminated object");
                        if (s[pos] == ',') { pos++; continue; }
                        if (s[pos] == '}') { pos++; break; }
                        throw new FormatException("expected ',' or '}' at " + pos);
                    }
                    return obj;
                }
                case '[':
                {
                    var list = new List<object>();
                    pos++;
                    SkipWs(s, ref pos);
                    if (pos < s.Length && s[pos] == ']') { pos++; return list; }
                    while (true)
                    {
                        list.Add(ParseValue(s, ref pos));
                        SkipWs(s, ref pos);
                        if (pos >= s.Length) throw new FormatException("unterminated array");
                        if (s[pos] == ',') { pos++; continue; }
                        if (s[pos] == ']') { pos++; break; }
                        throw new FormatException("expected ',' or ']' at " + pos);
                    }
                    return list;
                }
                case '"':
                    return ParseString(s, ref pos);
                case 't':
                    Expect(s, ref pos, "true");
                    return true;
                case 'f':
                    Expect(s, ref pos, "false");
                    return false;
                case 'n':
                    Expect(s, ref pos, "null");
                    return null;
                default:
                    return ParseNumber(s, ref pos);
            }
        }

        static string ParseString(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != '"') throw new FormatException("expected string at " + pos);
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (pos >= s.Length) break;
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 <= s.Length)
                            {
                                sb.Append((char)ushort.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                pos += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("unterminated string");
        }

        static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
                pos++;
            string token = s.Substring(start, pos - start);
            if (token.Length == 0) throw new FormatException("unexpected char at " + pos);
            if (token.IndexOf('.') < 0 && token.IndexOf('e') < 0 && token.IndexOf('E') < 0
                && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;
            return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        static void Expect(string s, ref int pos, string word)
        {
            if (pos + word.Length <= s.Length && s.Substring(pos, word.Length) == word) pos += word.Length;
            else throw new FormatException("expected " + word + " at " + pos);
        }

        public static string Stringify(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string s) { WriteString(sb, s); return; }
            if (value is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (value is long l) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is int i) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double d) { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is float f) { sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is decimal m) { sb.Append(m.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is Dictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (value is System.Collections.IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, value.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
#endif
