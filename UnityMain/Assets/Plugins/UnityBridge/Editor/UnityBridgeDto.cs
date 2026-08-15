// ============================================================================
//  UnityBridgeDto.cs — transport DTOs and JSON converters for the DSH bridge
// ============================================================================
//  JSON parsing/serialization is delegated to SimpleJSON (MIT, Bunny83) —
//  see SimpleJSON.cs in this folder. This file only shapes the wire protocol
//  and converts between JSONNode and plain C# objects.
//
//  Protocol v2 — ops are namespaced by domain:
//
//    Command:   { "id": "...", "domain": "scene", "op": "play", "args": { ... } }
//    Response:  { "id": "...", "domain": "scene", "op": "play", "ok": true,
//                 "ts": ..., "result": { ... } }
//               { "id": "...", "domain": "scene", "op": "play", "ok": false,
//                 "ts": ..., "error": "..." }
//
//  Domains: read | execute | log | core. Each domain is handled by its own
//  *Handler.cs file; the core routes on the `domain` field. Unknown
//  domains/ops are rejected with an error response.
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using SimpleJSON;

namespace DSH.UnityBridge
{
    /// <summary>Command envelope written by the agent into
    /// &lt;project&gt;/Library/UnityBridge/in/.</summary>
    public class BridgeCommand
    {
        public string id;
        public string domain;
        public string op;
        public JSONObject args;

        public static BridgeCommand Parse(string text)
        {
            var node = JSON.Parse(text);
            if (node == null || !node.IsObject) throw new Exception("command is not a JSON object");
            var obj = (JSONObject)node;
            var cmd = new BridgeCommand
            {
                id = obj["id"].Value,
                domain = obj["domain"].Value,
                op = obj["op"].Value,
                args = (obj["args"] as JSONObject) ?? new JSONObject(),
            };
            if (string.IsNullOrEmpty(cmd.domain)) throw new Exception("missing 'domain' (read|execute|log|core)");
            if (string.IsNullOrEmpty(cmd.op)) throw new Exception("missing 'op'");
            return cmd;
        }
    }

    /// <summary>Response envelope written to &lt;project&gt;/Library/UnityBridge/out/.</summary>
    public class BridgeResponse
    {
        public string id;
        public string domain;
        public string op;
        public bool ok;
        public double ts;
        public JSONNode result;   // set when ok
        public string error;      // set when !ok

        public BridgeResponse(string id, string domain, string op, bool ok, object result, string error)
        {
            this.id = id ?? "";
            this.domain = domain ?? "";
            this.op = op ?? "";
            this.ok = ok;
            this.ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            this.error = error;
            if (ok) this.result = BridgeJson.ToJsonNode(result ?? new Dictionary<string, object>());
        }

        public string ToJson()
        {
            var o = new JSONObject();
            o["id"] = id;
            o["domain"] = domain;
            o["op"] = op;
            o["ok"] = ok;
            o["ts"] = ts;
            if (ok) o["result"] = result;
            else o["error"] = error ?? "unknown error";
            return o.ToString();
        }
    }

    /// <summary>JSONNode &lt;-&gt; plain C# object converters. Parsing and
    /// serialization are handled by SimpleJSON.</summary>
    public static class BridgeJson
    {
        /// <summary>Plain object (primitives, Dictionary&lt;string,object&gt;,
        /// IEnumerable) to a SimpleJSON node tree.</summary>
        public static JSONNode ToJsonNode(object value)
        {
            if (value == null) return JSONNull.CreateOrGet();
            if (value is string s) return new JSONString(s);
            if (value is bool b) return new JSONBool(b);
            if (value is int i) return new JSONNumber((double)i);
            if (value is long l) return new JSONNumber((double)l);
            if (value is double d) return new JSONNumber(d);
            if (value is float f) return new JSONNumber(f);
            if (value is decimal m) return new JSONNumber((double)m);
            if (value is Dictionary<string, object> dict)
            {
                var o = new JSONObject();
                foreach (var kv in dict) o[kv.Key] = ToJsonNode(kv.Value);
                return o;
            }
            if (value is IEnumerable en)
            {
                var a = new JSONArray();
                foreach (var item in en) a.Add(ToJsonNode(item));
                return a;
            }
            return new JSONString(value.ToString());
        }

        /// <summary>SimpleJSON node to a plain object graph (Dictionary /
        /// List / string / bool / long / double / null) — what Roslyn scripts
        /// receive as their `args`.</summary>
        public static object ToPlainObject(JSONNode node)
        {
            if (node == null || node.IsNull) return null;
            if (node.IsObject)
            {
                var dict = new Dictionary<string, object>();
                foreach (KeyValuePair<string, JSONNode> kv in (JSONObject)node)
                    dict[kv.Key] = ToPlainObject(kv.Value);
                return dict;
            }
            if (node.IsArray)
            {
                var list = new List<object>();
                foreach (JSONNode item in (JSONArray)node)
                    list.Add(ToPlainObject(item));
                return list;
            }
            if (node.IsBoolean) return node.AsBool;
            if (node.IsNumber)
            {
                double d = node.AsDouble;
                if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) <= long.MaxValue)
                    return (long)d;
                return d;
            }
            return node.Value;
        }
    }
}
#endif
