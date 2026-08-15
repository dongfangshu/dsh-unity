// ============================================================================
//  CoreHandler.cs — `core` domain: bridge self-inspection + lifecycle.
//
//  Ops: ping | status | reload | menu
//  (routed here by UnityBridge.Execute on the command's `domain` field)
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEditor;
using UnityEditor.Compilation;
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
                case "status":
                    return Status();
                case "reload":
                    // Recompile all scripts + domain reload (no editor restart).
                    CompilationPipeline.RequestScriptCompilation();
                    return new Dictionary<string, object> { ["reloading"] = true };
                case "menu":
                    return new Dictionary<string, object>
                    {
                        ["executed"] = EditorApplication.ExecuteMenuItem(UnityBridge.GetString(args, "item") ?? "")
                    };
                default:
                    throw new Exception("unknown op '" + op + "' in domain core");
            }
        }

        static Dictionary<string, object> Status()
        {
            var scene = SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                ["bridge"] = UnityBridge.Version,
                ["version"] = Application.unityVersion,
                ["playing"] = EditorApplication.isPlaying,
                ["paused"] = EditorApplication.isPaused,
                ["scene"] = scene.path,
                ["sceneName"] = scene.name,
                ["roots"] = scene.rootCount,
                ["selection"] = Selection.activeObject != null ? Selection.activeObject.name : null
            };
        }
    }
}
#endif
