// ============================================================================
//  ScriptHandler.cs — `script` domain: run agent-written code in the editor.
//
//  Ops: eval | cs
//  (routed here by UnityBridge.Execute on the command's `domain` field)
//
//  `eval` invokes any static method in the editor by name.
//  `cs` compiles and executes agent-written C# with Roslyn (in memory, no
//  domain reload): args.code = C# source, args.imports = extra namespaces,
//  args.data = JSON object passed to Entry.Main(object args).
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
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
    public static class ScriptHandler
    {
        public static object Handle(string op, JSONObject args)
        {
            switch (op)
            {
                case "eval":
                    return Eval(UnityBridge.GetString(args, "type"), UnityBridge.GetString(args, "method"), UnityBridge.GetString(args, "argsJson"));
                case "cs":
                    return EvalCs(UnityBridge.GetString(args, "code"), UnityBridge.GetString(args, "imports"), UnityBridge.GetString(args, "data"));
                default:
                    throw new Exception("unknown op '" + op + "' in domain script");
            }
        }

        // ------------------------------------------------------------------
        // eval — invoke any static method (public or private) by name
        // ------------------------------------------------------------------
        static Dictionary<string, object> Eval(string typeName, string methodName, string argsJson)
        {
            if (string.IsNullOrEmpty(typeName)) throw new Exception("eval requires args.type");
            if (string.IsNullOrEmpty(methodName)) throw new Exception("eval requires args.method");

            Type type = ResolveType(typeName);
            if (type == null) throw new Exception("type not found: " + typeName);

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo method = type.GetMethod(methodName, flags);
            if (method == null) throw new Exception("static method not found: " + typeName + "." + methodName);

            object[] rawArgs = new object[0];
            if (!string.IsNullOrEmpty(argsJson))
            {
                var parsed = JSON.Parse(argsJson);
                if (parsed == null || !parsed.IsArray) throw new Exception("argsJson must be a JSON array");
                var list = new List<object>();
                foreach (JSONNode item in (JSONArray)parsed)
                    list.Add(BridgeJson.ToPlainObject(item));
                rawArgs = list.ToArray();
            }

            ParameterInfo[] ps = method.GetParameters();
            if (rawArgs.Length > ps.Length) throw new Exception("too many arguments: " + methodName + " takes " + ps.Length);
            var converted = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                if (i < rawArgs.Length && rawArgs[i] != null)
                    converted[i] = Convert.ChangeType(rawArgs[i], p.ParameterType, CultureInfo.InvariantCulture);
                else if (p.HasDefaultValue)
                    converted[i] = p.DefaultValue;
                else
                    converted[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            }

            object result;
            try { result = method.Invoke(null, converted); }
            catch (TargetInvocationException tie)
            {
                throw new Exception("eval threw: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message));
            }
            return new Dictionary<string, object> { ["value"] = Simplify(result) };
        }

        static object Simplify(object value)
        {
            if (value == null) return null;
            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string || value is decimal || value is DateTime) return value;
            return value.ToString();
        }

        // ------------------------------------------------------------------
        // cs — compile and execute agent-written C# with Roslyn (in memory,
        // no domain reload). CSharpCompilation -> Assembly.Load -> reflection.
        // Contract: code must define `public static class Entry { public static
        // object Main(object args) { ... } }`. args = parsed `data` JSON.
        // ------------------------------------------------------------------
        static Dictionary<string, object> EvalCs(string code, string importsArg, string dataArg)
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
                ["value"] = Simplify(result)
            };
        }

        static Type ResolveType(string name)
        {
            Type t = Type.GetType(name);
            if (t != null) return t;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(name);
                if (t != null) return t;
                t = asm.GetType("UnityEngine." + name);
                if (t != null) return t;
                t = asm.GetType("UnityEditor." + name);
                if (t != null) return t;
            }
            return null;
        }
    }

    // ========================================================================
    //  Globals type exposed to Roslyn scripts (accessible by name in code,
    //  e.g. `Args`, `Args["key"]`).
    // ========================================================================
    public class CsGlobals
    {
        public object Args;
    }
}
#endif
