// ============================================================================
//  SceneHandler.cs — `scene` domain: play mode + scene lifecycle + hierarchy.
//
//  Ops: open | save | play | stop | pause | resume | step | hierarchy
//  (routed here by UnityBridge.Execute on the command's `domain` field)
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DSH.UnityBridge
{
    public static class SceneHandler
    {
        public static object Handle(string op, JSONObject args)
        {
            switch (op)
            {
                case "open":
                    return Open(UnityBridge.GetString(args, "path"), UnityBridge.GetBool(args, "additive"));
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
                case "hierarchy":
                    return Hierarchy(UnityBridge.GetBool(args, "recursive"));
                default:
                    throw new Exception("unknown op '" + op + "' in domain scene");
            }
        }

        static Dictionary<string, object> Open(string path, bool additive)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("scene.open requires args.path (e.g. \"Assets/Scenes/SampleScene.unity\")");
            var scene = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new Dictionary<string, object> { ["scene"] = scene.path, ["loaded"] = scene.isLoaded };
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
    }
}
#endif
