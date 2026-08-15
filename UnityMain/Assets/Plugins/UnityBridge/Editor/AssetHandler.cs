// ============================================================================
//  AssetHandler.cs — `asset` domain: project asset database operations.
//
//  Ops: refresh | import | list
//  (routed here by UnityBridge.Execute on the command's `domain` field)
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEditor;

namespace DSH.UnityBridge
{
    public static class AssetHandler
    {
        public static object Handle(string op, JSONObject args)
        {
            switch (op)
            {
                case "refresh":
                    AssetDatabase.Refresh();
                    return new Dictionary<string, object> { ["refreshed"] = true };
                case "import":
                    return Import(UnityBridge.GetString(args, "path"));
                case "list":
                    return List(UnityBridge.GetString(args, "path"), UnityBridge.GetInt(args, "max", 200));
                default:
                    throw new Exception("unknown op '" + op + "' in domain asset");
            }
        }

        /// <summary>Import a single asset by project path (e.g. "Assets/Foo.png").</summary>
        static Dictionary<string, object> Import(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("asset.import requires args.path (e.g. \"Assets/Foo.png\")");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.Default);
            return new Dictionary<string, object> { ["imported"] = path };
        }

        /// <summary>List asset paths under a folder (default "Assets", capped by args.max).</summary>
        static Dictionary<string, object> List(string folderArg, int max)
        {
            string folder = string.IsNullOrEmpty(folderArg) ? "Assets" : folderArg;
            string[] guids = AssetDatabase.FindAssets("", new[] { folder });
            var paths = new List<string>();
            foreach (string guid in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(p);
                    if (max > 0 && paths.Count >= max) break;
                }
            }
            return new Dictionary<string, object>
            {
                ["path"] = folder,
                ["count"] = paths.Count,
                ["assets"] = paths
            };
        }
    }
}
#endif
