// UnityBridgeSample.cs — Simple demo for the DSH Unity Bridge package.
//
// Drives the bridge directly through its file-queue protocol, no agent or
// DSH involved: writes a JSON command to <project>/Library/UnityBridge/in/,
// polls out/ for the response, and logs it.
//
// Requires: the bridge active (editor open with the package installed —
// <project>/Library/UnityBridge/status/heartbeat.json exists and is fresh).
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class UnityBridgeSample
{
    const string BridgeDir = "UnityBridge";
    const float TimeoutSeconds = 30f;

    static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", BridgeDir));
    static string InDir => Path.Combine(Root, "in");
    static string OutDir => Path.Combine(Root, "out");
    static string HeartbeatFile => Path.Combine(Root, "status", "heartbeat.json");

    [MenuItem("Tools/Unity Bridge/Samples/Ping")]
    public static void SamplePing() => Send("core", "ping", "{}", null);

    [MenuItem("Tools/Unity Bridge/Samples/Print Status")]
    public static void SampleStatus() => Send("core", "status", "{}", null);

    [MenuItem("Tools/Unity Bridge/Samples/Create Cube (Roslyn cs)")]
    public static void SampleCube()
    {
        const string script =
            "using UnityEngine; public static class Entry { public static object Main(object args) {" +
            " var c = GameObject.CreatePrimitive(PrimitiveType.Cube); c.name = \"sample-cube\";" +
            " return \"created \" + c.name; } }";
        Send("script", "cs", null, script);
    }

    /// <summary>Write one command and log the response when it arrives.</summary>
    static void Send(string domain, string op, string plainArgsJson, string csCode)
    {
        if (!IsBridgeOnline())
        {
            Debug.LogWarning("[UnityBridge Sample] bridge offline — is the editor running with the package installed? " +
                             "Looked for " + HeartbeatFile);
            return;
        }

        string id = "sample-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string args = csCode != null
            ? "{\"code\":" + JsonUtility.ToJson(csCode) + "}"
            : (plainArgsJson ?? "{}");
        string payload = "{\"id\":\"" + id + "\",\"domain\":\"" + domain + "\",\"op\":\"" + op + "\",\"args\":" + args + "}";

        try
        {
            Directory.CreateDirectory(InDir);
            File.WriteAllText(Path.Combine(InDir, id + ".json"), payload);
        }
        catch (Exception ex)
        {
            Debug.LogError("[UnityBridge Sample] failed to write command: " + ex.Message);
            return;
        }

        Debug.Log("[UnityBridge Sample] sent " + domain + "." + op + " (" + id + ")");
        Poll(id, domain + "." + op);
    }

    static bool IsBridgeOnline()
    {
        try { return File.Exists(HeartbeatFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(HeartbeatFile)).TotalSeconds < 10; }
        catch { return false; }
    }

    /// <summary>Poll out/<id>.json on the editor update loop until it appears or times out.</summary>
    static void Poll(string id, string label)
    {
        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        EditorApplication.update += Handler;

        void Handler()
        {
            string path = Path.Combine(OutDir, id + ".json");
            if (File.Exists(path))
            {
                EditorApplication.update -= Handler;
                Debug.Log("[UnityBridge Sample] " + label + " -> " + File.ReadAllText(path));
                return;
            }
            if (Time.realtimeSinceStartup > deadline)
            {
                EditorApplication.update -= Handler;
                Debug.LogWarning("[UnityBridge Sample] " + label + " timed out after " + TimeoutSeconds + "s");
            }
        }
    }
}
#endif
