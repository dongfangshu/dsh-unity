// ============================================================================
//  UnityBridge.cs — DSH <-> Unity file-queue bridge CORE (editor automation)
// ============================================================================
//  Protocol v2 (see README at the project root for the full doc):
//
//    <project>/Library/UnityBridge/
//      in/     command files  <id>.json   (written by the agent side)
//      out/    response files <id>.json   (written here, pruned after 120s)
//      done/   processed command files    (pruned after 600s)
//      status/ heartbeat.json (every 1s) + log.json (captured console log)
//
//  The runtime queue lives under Library/ — machine-local, never version
//  controlled, auto-recreated on init, safe to wipe together with Unity's
//  own cache.
//
//  Commands are namespaced by domain; each domain has its own handler file:
//
//    domain "core"    -> CoreHandler.cs    (ping, reload, status, menuitem,
//                                           openscene, removescene, save,
//                                           play, stop, pause, resume, step)
//    domain "scene"   -> SceneHandler.cs   (open, save, play, stop, pause,
//                                           resume, step, hierarchy)
//    domain "asset"   -> AssetHandler.cs   (refresh, import, list)
//    domain "execute" -> ExecuteHandler.cs (cs — Roslyn, in-memory; the
//                                           single write path)
//    domain "read"    -> ReadHandler.cs    (assets / hierarchy / select —
//                                           the single read interface)
//    domain "log"     -> LogHandler.cs    (log — console ring tail)
//
//  Command:   { "id": "...", "domain": "scene", "op": "play", "args": { } }
//  Response:  { "id": "...", "domain": "scene", "op": "play", "ok": true,
//               "ts": ..., "result": { ... } }
//
//  This file is the CORE ONLY: the poll loop, domain routing, heartbeat, log
//  ring and file utilities. All domain logic lives in the *Handler.cs files.
//
//  The bridge only listens on the local filesystem and never opens a network
//  port. `cs` can invoke ANY code in the editor — treat the bridge
//  folder as trusted local tooling.
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SimpleJSON;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

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
            _root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "UnityBridge"));
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
                string domain = null;
                string op = null;
                try
                {
                    var cmd = BridgeCommand.Parse(text);
                    id = string.IsNullOrEmpty(cmd.id) ? ExtractId(text) : cmd.id;
                    domain = cmd.domain;
                    op = cmd.op;
                    object result = Execute(cmd.domain, cmd.op, cmd.args);
                    WriteResponse(id, domain, op, true, result, null);
                }
                catch (Exception ex)
                {
                    WriteResponse(id ?? ExtractId(text), domain ?? GetDomainHint(text), op ?? GetOpHint(text), false, null, ex.Message);
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

        static string GetDomainHint(string text)
        {
            try
            {
                var node = JSON.Parse(text);
                if (node != null && node.IsObject) return node["domain"].Value;
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
            var m = Regex.Match(text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "unknown-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static void WriteResponse(string id, string domain, string op, bool ok, object result, string error)
        {
            var resp = new BridgeResponse(id, domain, op, ok, result, error);
            AtomicWrite(Path.Combine(_outDir, id + ".json"), resp.ToJson());
        }

        // ------------------------------------------------------------------
        // Domain routing — each domain is handled by its own *Handler class.
        // Add a new domain by adding a handler class and one case here.
        // ------------------------------------------------------------------
        static object Execute(string domain, string op, JSONObject args)
        {
            switch (domain)
            {
                case "core": return CoreHandler.Handle(op, args);
                case "scene": return SceneHandler.Handle(op, args);
                case "asset": return AssetHandler.Handle(op, args);
                case "execute": return ExecuteHandler.Handle(op, args);
                case "read": return ReadHandler.Handle(op, args);
                case "log": return LogHandler.Handle(op, args);
                default: throw new Exception("unknown domain: " + domain);
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
        // Small helpers (internal so the *Handler classes can reuse them)
        // ------------------------------------------------------------------
        internal static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        internal static string GetString(JSONObject args, string key)
        {
            if (args == null || !args.HasKey(key)) return null;
            return args[key].Value;
        }

        internal static bool GetBool(JSONObject args, string key)
        {
            if (args == null || !args.HasKey(key)) return false;
            var n = args[key];
            return n.IsBoolean ? n.AsBool : (n.Value == "true" || n.Value == "1");
        }

        internal static int GetInt(JSONObject args, string key, int def)
        {
            if (args == null || !args.HasKey(key)) return def;
            return args[key].AsInt;
        }

        /// <summary>Snapshot of the captured console log ring (used by core.log).</summary>
        internal static Dictionary<string, object> LogSnapshot(int lines)
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
    }
}
#endif
