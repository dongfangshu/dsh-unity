// ============================================================================
//  ReadHandler.cs — `read` domain: the bridge's single read interface.
//
//  Ops: assets | hierarchy | select
//  (routed here by UnityBridge.Execute on the command's `domain` field)
//
//  Unified read model (see CONTEXT.md): an address is resolved by its source
//  scheme to an editor object, the engine identifies what kind of object it
//  is and dispatches the dump, and every read returns the same node envelope:
//
//    { path, kind, name?, type?, instance?, activeSelf?, components?[],
//      children?[], properties?{}, content? }
//
//  kind ∈ { scene, gameObject, component, text, asset }
//
//  - assets:    project-relative full path (Assets/... or Packages/...);
//               text assets return raw content, serialized assets dump their
//               SerializedObject properties, binary assets are rejected.
//  - hierarchy: Assets/Scenes/Main.unity/Root/Child[@instance][/Type.Name]
//               — the scene must be explicit (it defines the address space),
//               ls-style one level per read, same-name paths are rejected
//               with candidates unless disambiguated by @instance.
//  - select:    the current editor selection as node entries.
//
//  The envelope path is always a canonical address that can be read back
//  verbatim (ambiguous segments carry @instance).
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SimpleJSON;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DSH.UnityBridge
{
    public static class ReadHandler
    {
        public static object Handle(string op, JSONObject args)
        {
            switch (op)
            {
                case "assets": return Assets(UnityBridge.GetString(args, "path"));
                case "hierarchy": return Hierarchy(UnityBridge.GetString(args, "path"));
                case "select": return Select();
                default: throw new Exception("unknown op '" + op + "' in domain read");
            }
        }

        // ------------------------------------------------------------------
        // assets: — project file addressing (Assets/... or Packages/...)
        // ------------------------------------------------------------------
        static Dictionary<string, object> Assets(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("read.assets requires args.path (e.g. Assets/... or Packages/...)");

            UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj != null)
            {
                if (obj is TextAsset text)
                    return Node("text", path, text.GetInstanceID(), content: text.text);

                string full = ProjectPath(path);
                if (full != null && IsBinaryFile(full))
                    throw new Exception("binary asset at '" + path + "' — read it via execute.cs instead");
                return Node("asset", path, obj.GetInstanceID(),
                    type: obj.GetType().Name, properties: DumpProperties(obj));
            }

            // Not an imported asset — fall back to raw file text (e.g. ProjectSettings/*).
            string file = ProjectPath(path);
            if (file != null && File.Exists(file))
            {
                if (IsBinaryFile(file))
                    throw new Exception("binary file at '" + path + "' — read it via execute.cs instead");
                return Node("text", path, null, content: File.ReadAllText(file));
            }
            throw new Exception("not found: " + path);
        }

        // ------------------------------------------------------------------
        // hierarchy: — scene object addressing (one level per read)
        // ------------------------------------------------------------------
        static Dictionary<string, object> Hierarchy(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("read.hierarchy requires args.path (e.g. Assets/Scenes/Main.unity/Root/Child)");

            // Longest matching open scene defines the address space.
            Scene scene = default;
            string rest = null;
            foreach (Scene s in GetOpenScenes())
            {
                if (s.path.Length > 0 && (path == s.path || path.StartsWith(s.path + "/", StringComparison.Ordinal)))
                {
                    scene = s;
                    rest = path.Length > s.path.Length ? path.Substring(s.path.Length + 1) : null;
                    break;
                }
            }
            if (scene == default || !scene.IsValid())
                throw new Exception("scene for '" + path + "' is not open — use core.openscene first");

            // Address to the scene itself -> its root objects.
            if (string.IsNullOrEmpty(rest))
            {
                return new Dictionary<string, object>
                {
                    ["path"] = scene.path,
                    ["kind"] = "scene",
                    ["name"] = scene.name,
                    ["children"] = scene.GetRootGameObjects()
                        .Select(go => GameObjectEntry(go)).ToList()
                };
            }

            // Walk the name chain (or @instance segment) through the scene.
            string[] segments = rest.Split('/');
            GameObject current = null;
            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i];
                string name = seg;
                int? wantInstance = null;
                int at = seg.LastIndexOf('@');
                if (at > 0 && int.TryParse(seg.Substring(at + 1), out int inst))
                {
                    name = seg.Substring(0, at);
                    wantInstance = inst;
                }

                if (wantInstance.HasValue)
                {
                    current = FindByInstance(scene, wantInstance.Value);
                    if (current == null)
                        throw new Exception("no object with instance " + wantInstance.Value + " in scene " + scene.path);
                    continue;
                }

                // Component segment: only when the name chain cannot match
                // (a GameObject may share a name with a component type).
                bool lastSegment = i == segments.Length - 1;
                List<GameObject> candidates = current == null
                    ? scene.GetRootGameObjects().Where(g => g.name == name).ToList()
                    : current.transform.Cast<Transform>().Where(t => t.name == name).Select(t => t.gameObject).ToList();

                if (candidates.Count == 0 && lastSegment)
                {
                    Component comp = FindComponent(current, name);
                    if (comp != null)
                    {
                        string canonical = scene.path + "/" + CanonicalPath(current) + "/" + comp.GetType().Name;
                        return Node("component", canonical, comp.GetInstanceID(),
                            type: comp.GetType().FullName, properties: DumpProperties(comp));
                    }
                    throw new Exception("no object named '" + name + "' under '" + (current != null ? current.name : scene.path) + "'");
                }
                if (candidates.Count == 0)
                    throw new Exception("no object named '" + name + "' under '" + (current != null ? current.name : scene.path) + "'");
                if (candidates.Count > 1)
                    throw AmbiguityError(scene, candidates);

                current = candidates[0];
            }

            string nodePath = scene.path + "/" + CanonicalPath(current);
            return GameObjectNode(current, nodePath);
        }

        // ------------------------------------------------------------------
        // select: — the current editor selection as node entries
        // ------------------------------------------------------------------
        static Dictionary<string, object> Select()
        {
            var list = new List<object>();
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                if (obj == null) continue;
                if (obj is GameObject go)
                    list.Add(GameObjectEntry(go));
                else
                    list.Add(new Dictionary<string, object>
                    {
                        ["path"] = NonEmpty(AssetDatabase.GetAssetPath(obj), obj.name),
                        ["kind"] = "asset",
                        ["name"] = obj.name,
                        ["type"] = obj.GetType().Name,
                        ["instance"] = obj.GetInstanceID()
                    });
            }
            return new Dictionary<string, object> { ["selection"] = list };
        }

        // ------------------------------------------------------------------
        // Node building — ls semantics: the addressed node lists one level of
        // children as entries (no grandchildren). Descend by reading again.
        // ------------------------------------------------------------------
        static Dictionary<string, object> GameObjectNode(GameObject go, string path = null)
        {
            var node = GameObjectEntry(go, path);
            string nodePath = (string)node["path"];
            var transforms = go.transform.Cast<Transform>().ToList();
            var counts = new Dictionary<string, int>();
            foreach (Transform t in transforms)
            {
                string n = t.gameObject.name;
                int c;
                counts[n] = counts.TryGetValue(n, out c) ? c + 1 : 1;
            }
            var children = new List<object>();
            foreach (Transform t in transforms)
            {
                string seg = t.gameObject.name;
                if (counts[seg] > 1) seg += "@" + t.gameObject.GetInstanceID();
                children.Add(GameObjectEntry(t.gameObject, nodePath + "/" + seg));
            }
            node["children"] = children;
            return node;
        }

        /// <summary>Canonical node entry for a GameObject (no children). Used
        /// by read and by Copy for Agent.</summary>
        internal static Dictionary<string, object> AddressFor(GameObject go) => GameObjectEntry(go);

        static Dictionary<string, object> GameObjectEntry(GameObject go, string path = null)
        {
            string scenePath = go.scene.IsValid() ? go.scene.path : "";
            if (path == null)
                path = scenePath.Length > 0 ? scenePath + "/" + CanonicalPath(go) : go.name;

            return new Dictionary<string, object>
            {
                ["path"] = path,
                ["kind"] = "gameObject",
                ["name"] = go.name,
                ["instance"] = go.GetInstanceID(),
                ["activeSelf"] = go.activeSelf,
                ["components"] = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name).ToList()
            };
        }

        static Dictionary<string, object> Node(string kind, string path, int? instance,
            string type = null, string content = null, Dictionary<string, object> properties = null)
        {
            var n = new Dictionary<string, object> { ["path"] = path, ["kind"] = kind };
            if (type != null) n["type"] = type;
            if (instance.HasValue) n["instance"] = instance.Value;
            if (content != null) n["content"] = content;
            if (properties != null) n["properties"] = properties;
            return n;
        }

        // ------------------------------------------------------------------
        // Addressing helpers
        // ------------------------------------------------------------------
        static Exception AmbiguityError(Scene scene, List<GameObject> candidates)
        {
            var paths = candidates
                .Select(go => scene.path + "/" + CanonicalPath(go) + " (instance " + go.GetInstanceID() + ")")
                .ToList();
            return new Exception("ambiguous name — candidates: " + string.Join(", ", paths) +
                " (disambiguate with @<instance>)");
        }

        /// <summary>Canonical slash-path from the scene root down to `go`,
        /// with @instance appended to same-name segments.</summary>
        static string CanonicalPath(GameObject go)
        {
            var segs = new List<string>();
            Transform t = go.transform;
            while (t != null)
            {
                string seg = t.gameObject.name;
                Transform p = t.parent;
                if (p != null && p.Cast<Transform>().Count(x => x.gameObject.name == seg) > 1)
                    seg += "@" + t.gameObject.GetInstanceID();
                segs.Insert(0, seg);
                t = p;
            }
            return string.Join("/", segs);
        }

        static GameObject FindByInstance(Scene scene, int instance)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.GetInstanceID() == instance) return root;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    if (t.gameObject.GetInstanceID() == instance) return t.gameObject;
            }
            return null;
        }

        static Component FindComponent(GameObject go, string name)
        {
            if (go == null) return null;
            return go.GetComponents<Component>()
                .FirstOrDefault(c => c != null && (c.GetType().Name == name || c.GetType().FullName == name));
        }

        // ------------------------------------------------------------------
        // SerializedObject dump (type-agnostic, editor's visible layout)
        // ------------------------------------------------------------------
        static Dictionary<string, object> DumpProperties(UnityEngine.Object target)
        {
            var props = new Dictionary<string, object>();
            var so = new SerializedObject(target);
            var it = so.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.propertyPath.EndsWith(".Array.size", StringComparison.Ordinal)) continue;
                props[it.propertyPath] = PropToPlain(it);
            }
            return props;
        }

        static object PropToPlain(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Float: return p.doubleValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum: return p.enumValueIndex;
                case SerializedPropertyType.ObjectReference:
                {
                    var d = new Dictionary<string, object>();
                    if (p.objectReferenceValue != null)
                    {
                        d["name"] = p.objectReferenceValue.name;
                        d["instance"] = p.objectReferenceValue.GetInstanceID();
                        string assetPath = AssetDatabase.GetAssetPath(p.objectReferenceValue);
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            d["path"] = assetPath;
                            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(p.objectReferenceValue, out string guid, out long fileID))
                            {
                                d["guid"] = guid;
                                d["fileID"] = fileID;
                            }
                        }
                    }
                    return d;
                }
                case SerializedPropertyType.Vector2:
                    return new Dictionary<string, object> { ["x"] = p.vector2Value.x, ["y"] = p.vector2Value.y };
                case SerializedPropertyType.Vector3:
                    return new Dictionary<string, object> { ["x"] = p.vector3Value.x, ["y"] = p.vector3Value.y, ["z"] = p.vector3Value.z };
                case SerializedPropertyType.Vector4:
                    return new Dictionary<string, object> { ["x"] = p.vector4Value.x, ["y"] = p.vector4Value.y, ["z"] = p.vector4Value.z, ["w"] = p.vector4Value.w };
                case SerializedPropertyType.Color:
                    return new Dictionary<string, object> { ["r"] = p.colorValue.r, ["g"] = p.colorValue.g, ["b"] = p.colorValue.b, ["a"] = p.colorValue.a };
                case SerializedPropertyType.Rect:
                    return new Dictionary<string, object> { ["x"] = p.rectValue.x, ["y"] = p.rectValue.y, ["w"] = p.rectValue.width, ["h"] = p.rectValue.height };
                case SerializedPropertyType.Quaternion:
                    return new Dictionary<string, object> { ["x"] = p.quaternionValue.x, ["y"] = p.quaternionValue.y, ["z"] = p.quaternionValue.z, ["w"] = p.quaternionValue.w };
                case SerializedPropertyType.LayerMask:
                    return p.intValue;
                default:
                    return p.ToString();
            }
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------
        static IEnumerable<Scene> GetOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                yield return SceneManager.GetSceneAt(i);
        }

        static string ProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("Packages/", StringComparison.Ordinal)) return null;
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
        }

        static bool IsBinaryFile(string fullPath)
        {
            try
            {
                using (var fs = File.OpenRead(fullPath))
                {
                    var buf = new byte[Math.Min(512, fs.Length)];
                    fs.Read(buf, 0, buf.Length);
                    for (int i = 0; i < buf.Length; i++)
                        if (buf[i] == 0) return true;
                }
            }
            catch { return true; }
            return false;
        }

        static string NonEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;
    }
}
#endif
