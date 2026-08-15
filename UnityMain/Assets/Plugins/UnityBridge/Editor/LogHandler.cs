// ============================================================================
//  LogHandler.cs — `log` domain: read the editor's console log.
//
//  Op: log
//  (routed here by UnityBridge.Execute on the command's `domain` field)
//
//  `log` tails the bridge's in-memory console ring (captured via
//  Application.logMessageReceived, bounded to LogRingSize entries, each
//  entry carrying elapsed time and LogType). args.lines defaults to 50.
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using SimpleJSON;

namespace DSH.UnityBridge
{
    public static class LogHandler
    {
        public static object Handle(string op, JSONObject args)
        {
            switch (op)
            {
                case "log":
                    return UnityBridge.LogSnapshot(UnityBridge.GetInt(args, "lines", 50));
                default:
                    throw new Exception("unknown op '" + op + "' in domain log");
            }
        }
    }
}
#endif
