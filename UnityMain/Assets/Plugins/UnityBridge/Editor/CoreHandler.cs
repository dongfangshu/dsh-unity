// ============================================================================
//  CoreHandler.cs — `core` domain: editor-session operations (a closed set).
//
//  Ops: ping | reload | status | menuitem | openscene | removescene |
//       savescene | saveassets | play | stop | pause | resume | step
//  (routed here by UnityBridge.Execute on the command's `domain` field)
//
//  The capability boundary (see CONTEXT.md): core holds editor-session
//  operations — menu/toolbar-level actions that are independent of object
//  types. It is a closed set: new capabilities are expressed through
//  execute.cs or new read schemes, not new core ops.
//
//  `reload` is the one op that is allowed to kill the running domain: the
//  queue claims the command, this handler returns `{reloading:true}`, the
//  response is written, then compilation proceeds. An execute.cs that
//  triggers a reload itself never gets to write its own response — leftovers
//  in running/ are reaped as "interrupted by domain reload".
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SimpleJSON;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DSH.UnityBridge
{
    public static class CoreHandler
    {
        public static object Handle(string op, JSONObject args)
        {
            switch (op)
            {
                case "ping":
                    return new Dictionary<string, object> { ["pong"] = true, ["bridge"] = UnityBridge.Version };
                case "reload":
                    // Import + compile only. Does not save scenes or assets —
                    // persist first with savescene / saveassets if you want
                    // in-memory edits to survive. Respond first; compilation
                    // happens after this returns (the queue has claimed it).
                    AssetDatabase.Refresh();
                    CompilationPipeline.RequestScriptCompilation();
                    return new Dictionary<string, object> { ["reloading"] = true };
                case "status":
                    return Status();
                case "menuitem":
                    return MenuItem(UnityBridge.GetString(args, "item"));
                case "openscene":
                    return OpenScene(UnityBridge.GetString(args, "path"), UnityBridge.GetString(args, "mode"));
                case "removescene":
                    return RemoveScene(UnityBridge.GetString(args, "path"));
                case "savescene":
                    return SaveScene(UnityBridge.GetString(args, "path"));
                case "saveassets":
                    AssetDatabase.SaveAssets();
                    return new Dictionary<string, object> { ["saved"] = true };
                case "play":
                    EditorApplication.EnterPlaymode();
                    return new Dictionary<string, object> { ["playing"] = true };
                case "stop":
                    EditorApplication.ExitPlaymode();
                    return new Dictionary<string, object> { ["playing"] = false };
                case "pause":
                    EditorApplication.isPaused = true;
                    return new Dictionary<string, object> { ["paused"] = true };
                case "resume":
                    EditorApplication.isPaused = false;
                    return new Dictionary<string, object> { ["paused"] = false };
                case "step":
                    EditorApplication.Step();
                    return new Dictionary<string, object> { ["paused"] = EditorApplication.isPaused };
                default:
                    throw new Exception("unknown op '" + op + "' in domain core");
            }
        }

        static Dictionary<string, object> MenuItem(string item)
        {
            if (string.IsNullOrEmpty(item))
                throw new Exception("core.menuitem requires args.item (exact path, e.g. File/Save Project)");
            if (!MenuExists(item))
                throw new Exception("no menu named '" + item + "'");
            if (!Menu.GetEnabled(item))
                throw new Exception("menu is disabled: " + item);
            if (!EditorApplication.ExecuteMenuItem(item))
                throw new Exception("menu item did not execute: " + item);
            return new Dictionary<string, object> { ["executed"] = true };
        }

        static bool MenuExists(string path)
        {
            var method = typeof(Menu).GetMethod("MenuItemExists", BindingFlags.NonPublic | BindingFlags.Static);
            if (method != null)
                return (bool)method.Invoke(null, new object[] { path });
            return Menu.GetEnabled(path);
        }

        static Dictionary<string, object> Status()
        {
            var active = SceneManager.GetActiveScene();
            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                scenes.Add(new Dictionary<string, object> { ["path"] = s.path, ["name"] = s.name });
            }
            return new Dictionary<string, object>
            {
                ["bridge"] = UnityBridge.Version,
                ["unityVersion"] = Application.unityVersion,
                ["projectPath"] = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                ["playing"] = EditorApplication.isPlaying,
                ["paused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["activeScene"] = active.path,
                ["openScenes"] = scenes,
                ["selection"] = Selection.objects.Where(o => o != null).Select(o => o.name).ToList(),
                ["buildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString()
            };
        }

        static Dictionary<string, object> OpenScene(string path, string mode)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("core.openscene requires args.path (e.g. Assets/Scenes/Main.unity)");
            string resolved = ResolveScenePath(path);
            bool additive = string.Equals(mode, "additive", StringComparison.OrdinalIgnoreCase);
            Scene s = EditorSceneManager.OpenScene(resolved, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new Dictionary<string, object> { ["scene"] = s.path, ["loaded"] = s.IsValid() };
        }

        static Dictionary<string, object> RemoveScene(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "all")
            {
                int closed = 0;
                while (SceneManager.sceneCount > 0)
                {
                    Scene s = SceneManager.GetSceneAt(0);
                    if (s == SceneManager.GetActiveScene() && SceneManager.sceneCount == 1) break;
                    if (s == SceneManager.GetActiveScene())
                        EditorSceneManager.SetActiveScene(SceneManager.GetSceneAt(1));
                    if (EditorSceneManager.CloseScene(s, true)) closed++;
                    else break;
                }
                return new Dictionary<string, object> { ["closed"] = closed };
            }
            Scene target = SceneManager.GetSceneByPath(ResolveScenePath(path));
            if (!target.IsValid()) throw new Exception("scene not open: " + path);
            // Unity refuses to close the active scene; move activation first.
            if (target == SceneManager.GetActiveScene())
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene other = SceneManager.GetSceneAt(i);
                    if (other != target) { EditorSceneManager.SetActiveScene(other); break; }
                }
                if (target == SceneManager.GetActiveScene())
                    throw new Exception("cannot close the last open scene: " + path);
            }
            bool removed = EditorSceneManager.CloseScene(target, true);
            return new Dictionary<string, object> { ["closed"] = removed };
        }

        static Dictionary<string, object> SaveScene(string path)
        {
            if (string.IsNullOrEmpty(path))
                return new Dictionary<string, object> { ["saved"] = EditorSceneManager.SaveOpenScenes() };
            Scene target = SceneManager.GetSceneByPath(ResolveScenePath(path));
            if (!target.IsValid()) throw new Exception("scene not open: " + path);
            return new Dictionary<string, object> { ["saved"] = EditorSceneManager.SaveScene(target) };
        }

        static string ResolveScenePath(string path)
        {
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return path;
            string candidate = path + ".unity";
            return AssetDatabase.LoadAssetAtPath<SceneAsset>(candidate) != null ? candidate : path;
        }
    }
}
#endif
