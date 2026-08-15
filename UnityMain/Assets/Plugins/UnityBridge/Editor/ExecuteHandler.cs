// ============================================================================
//  ExecuteHandler.cs — `execute` domain: the bridge's single write path.
//
//  Op: cs
//  (routed here by UnityBridge.Execute on the command's `domain` field)
//
//  `cs` compiles and executes agent-written C# with Roslyn (in memory, no
//  domain reload): args.code = C# source, args.imports = extra namespaces,
//  args.data = JSON object passed to Entry.Main(object args).
//
//  Capability boundary (see CONTEXT.md at the repo root): every
//  create/update/delete in the editor is expressed through this op —
//  there are no privileged write ops.
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SimpleJSON;
using Assembly = System.Reflection.Assembly;

namespace DSH.UnityBridge
{
    public static class ExecuteHandler
    {
        public static object Handle(string op, JSONObject args)
        {
            switch (op)
            {
                case "cs":
                    return RunCs(UnityBridge.GetString(args, "code"), UnityBridge.GetString(args, "imports"), UnityBridge.GetString(args, "data"));
                default:
                    throw new Exception("unknown op '" + op + "' in domain execute");
            }
        }

        // ------------------------------------------------------------------
        // cs — compile and execute agent-written C# with Roslyn (in memory,
        // no domain reload). CSharpCompilation -> Assembly.Load -> reflection.
        // Contract: code must define `public static class Entry { public static
        // object Main(object args) { ... } }`. args = parsed `data` JSON.
        // ------------------------------------------------------------------
        static Dictionary<string, object> RunCs(string code, string importsArg, string dataArg)
        {
            if (string.IsNullOrEmpty(code)) throw new Exception("cs requires args.code (C# source text)");

            var imports = new List<string>
            {
                "System", "System.Collections.Generic", "System.Linq", "System.Text",
                "System.IO", "System.Threading", "System.Text.RegularExpressions",
                "UnityEngine", "UnityEditor"
            };
            if (!string.IsNullOrEmpty(importsArg))
                foreach (string ns in importsArg.Split(','))
                    if (!string.IsNullOrWhiteSpace(ns)) imports.Add(ns.Trim());

            // Reference every assembly currently loaded in the editor.
            var refs = new List<MetadataReference>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(a.Location)); }
                catch { }
            }

            // Prepend extra `using` directives the agent requested.
            var sb = new StringBuilder();
            foreach (string ns in imports)
                sb.Append("using ").Append(ns).Append(";\n");
            sb.Append(code);
            string fullCode = sb.ToString();

            // Roslyn needs the code-page provider for some encodings; safe to register once.
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { }

            // Compile with CSharpCompilation (NOT the Scripting API: its assembly
            // loader uses AssemblyLoadContext, which Unity's Mono stubs out with
            // NotImplementedException). Emit to a memory stream, then load with
            // Assembly.Load(byte[]) and invoke the entry point via reflection.
            var tree = CSharpSyntaxTree.ParseText(fullCode, new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create(
                "AgentScript",
                new[] { tree },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            byte[] peBytes;
            using (var pe = new MemoryStream())
            {
                var emitResult = compilation.Emit(pe);
                if (!emitResult.Success)
                {
                    var errs = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Take(10)
                        .Select(d => d.ToString());
                    throw new Exception("cs compile errors:\n" + string.Join("\n", errs));
                }
                peBytes = pe.ToArray();
            }

            Assembly asm;
            try { asm = Assembly.Load(peBytes); }
            catch (Exception ex) { throw new Exception("cs assembly load failed: " + ex); }

            Type entry = asm.GetTypes().FirstOrDefault(t => t.Name == "Entry" && t.IsClass);
            if (entry == null)
                throw new Exception("cs code must define a static class named 'Entry' (e.g. public static class Entry)");
            MethodInfo main = entry.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
            if (main == null)
                throw new Exception("cs code must define a public static method 'Main' on Entry");

            object args = null;
            if (!string.IsNullOrEmpty(dataArg))
            {
                try { args = BridgeJson.ToPlainObject(JSON.Parse(dataArg)); }
                catch { args = null; }
            }

            object result;
            try
            {
                ParameterInfo[] ps = main.GetParameters();
                if (ps.Length == 0) result = main.Invoke(null, null);
                else if (ps.Length == 1 && ps[0].ParameterType == typeof(object)) result = main.Invoke(null, new[] { args });
                else throw new Exception("Entry.Main must take no parameters or one 'object' parameter");
            }
            catch (TargetInvocationException tie)
            {
                throw new Exception("cs script error: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
            }

            return new Dictionary<string, object>
            {
                ["value"] = ToPlain(result)
            };
        }

        // ------------------------------------------------------------------
        // ToPlain — convert a script's return value into a JSON-serializable
        // plain graph: scalars pass through, IDictionary/collections recurse,
        // UnityEngine.Objects become {type,name,instance} (so agents can
        // re-address them), anything else falls back to ToString().
        // ------------------------------------------------------------------
        static object ToPlain(object value, int depth = 0)
        {
            if (value == null || depth > 20) return null;
            if (value is string || value is bool || value is int || value is long
                || value is double || value is float || value is decimal) return value;
            if (value is UnityEngine.Object uobj)
                return new Dictionary<string, object>
                {
                    ["type"] = uobj.GetType().Name,
                    ["name"] = uobj.name,
                    ["instance"] = uobj.GetInstanceID()
                };
            if (value is System.Collections.IDictionary dict)
            {
                var o = new Dictionary<string, object>();
                foreach (System.Collections.DictionaryEntry kv in dict)
                    o[Convert.ToString(kv.Key)] = ToPlain(kv.Value, depth + 1);
                return o;
            }
            if (value is System.Collections.IEnumerable en)
            {
                var list = new List<object>();
                foreach (object item in en) list.Add(ToPlain(item, depth + 1));
                return list;
            }
            return value.ToString();
        }
    }
}
#endif
