// ============================================================================
//  CopyForAgent.cs — copy selected objects as unity-bridge address JSON.
//  Hierarchy: first-level item directly under Copy (SceneHierarchyHooks).
//  Project: first-level Assets/Copy for Agent.
// ============================================================================
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DSH.UnityBridge
{
    [InitializeOnLoad]
    public static class CopyForAgent
    {
        const string Label = "Copy for Agent";

        static CopyForAgent()
        {
            SceneHierarchyHooks.addItemsToGameObjectContextMenu += OnHierarchyContext;
        }

        static void OnHierarchyContext(GenericMenu menu, GameObject clicked)
        {
            if (Selection.gameObjects.Length == 0) return;
            InsertAfterCopy(menu, new GUIContent(Label), CopyFromHierarchy);
        }

        [MenuItem("Assets/Copy for Agent", false, 12)]
        static void CopyFromProject()
        {
            var objects = new List<object>();
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                if (obj == null) continue;
                if (obj is GameObject go && go.scene.IsValid())
                {
                    objects.Add(ReadHandler.AddressFor(go));
                    continue;
                }
                string assetPath = AssetDatabase.GetAssetPath(obj);
                objects.Add(new Dictionary<string, object>
                {
                    ["path"] = string.IsNullOrEmpty(assetPath) ? obj.name : assetPath,
                    ["kind"] = "asset",
                    ["name"] = obj.name,
                    ["type"] = obj.GetType().Name,
                    ["instance"] = obj.GetInstanceID()
                });
            }
            WriteClipboard(objects);
        }

        [MenuItem("Assets/Copy for Agent", true)]
        static bool CopyFromProjectValidate() => Selection.objects.Length > 0;

        static void CopyFromHierarchy()
        {
            var objects = new List<object>();
            foreach (GameObject go in Selection.gameObjects)
            {
                if (go == null) continue;
                objects.Add(ReadHandler.AddressFor(go));
            }
            WriteClipboard(objects);
        }

        static void InsertAfterCopy(GenericMenu menu, GUIContent content, GenericMenu.MenuFunction handler)
        {
            menu.AddItem(content, false, handler);
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var field = typeof(GenericMenu).GetField("menuItems", flags)
                            ?? typeof(GenericMenu).GetField("m_MenuItems", flags);
                var list = field != null ? field.GetValue(menu) as IList : null;
                if (list == null || list.Count < 2) return;

                string copyLabel = EditorGUIUtility.TrTextContent("Copy").text;
                int copyIndex = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    object item = list[i];
                    if (item == null) continue;
                    var contentField = item.GetType().GetField("content", flags);
                    var gc = contentField != null ? contentField.GetValue(item) as GUIContent : null;
                    if (gc != null && gc.text == copyLabel)
                    {
                        copyIndex = i;
                        break;
                    }
                }
                if (copyIndex < 0) return;

                object last = list[list.Count - 1];
                list.RemoveAt(list.Count - 1);
                list.Insert(copyIndex + 1, last);
            }
            catch
            {
                // Fallback: item stays at the end of the menu (still first-level).
            }
        }

        static void WriteClipboard(List<object> objects)
        {
            if (objects.Count == 0)
            {
                Debug.LogWarning("[UnityBridge] Copy for Agent: nothing selected");
                return;
            }
            var payload = new Dictionary<string, object>
            {
                ["unity-bridge"] = "read",
                ["objects"] = objects
            };
            EditorGUIUtility.systemCopyBuffer = BridgeJson.ToJsonNode(payload).ToString();
            Debug.Log("[UnityBridge] copied " + objects.Count + " object(s) for agent");
        }
    }
}
#endif
