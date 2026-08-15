// ============================================================================
//  ExecuteHandler.cs — dropped in/*.cs files: the bridge's single write path.
//
//  Roslyn compiles the file body in memory (no domain reload) and invokes
//  `public static class Entry { public static object Main(object args) }`.
//  `args` is always null. Extra namespaces belong in the file; defaults are
//  prepended (System, UnityEngine, UnityEditor, ...).
//
//  Capability boundary (see CONTEXT.md at the repo root): every
//  create/update/delete in the editor is expressed through this path —
//  there are no privileged write ops.
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Assembly = System.Reflection.Assembly;

namespace DSH.UnityBridge
{
    public static class ExecuteHandler
    {
        static List<MetadataReference> _refs;
        static int _refCount = -1;
        static readonly Dictionary<string, MethodInfo> _compiled = new Dictionary<string, MethodInfo>();

        static ExecuteHandler()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { }
        }

        public static object Run(string code)
        {
            if (string.IsNullOrEmpty(code)) throw new Exception("dropped .cs file is empty");

            var imports = new[]
            {
                "System", "System.Collections.Generic", "System.Linq", "System.Text",
                "System.IO", "System.Threading", "System.Text.RegularExpressions",
                "UnityEngine", "UnityEditor"
            };
            var sb = new StringBuilder();
            foreach (string ns in imports)
                sb.Append("using ").Append(ns).Append(";\n");
            sb.Append(code);
            string fullCode = sb.ToString();

            string key = Sha(fullCode);
            MethodInfo main;
            if (!_compiled.TryGetValue(key, out main))
            {
                main = Compile(fullCode);
                _compiled[key] = main;
            }

            object result;
            try
            {
                ParameterInfo[] ps = main.GetParameters();
                if (ps.Length == 0) result = main.Invoke(null, null);
                else if (ps.Length == 1 && ps[0].ParameterType == typeof(object)) result = main.Invoke(null, new object[] { null });
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

        static MethodInfo Compile(string fullCode)
        {
            // CSharpCompilation, not the Scripting API: AssemblyLoadContext is
            // stubbed out on Unity's Mono (NotImplementedException).
            var tree = CSharpSyntaxTree.ParseText(fullCode, new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create(
                "AgentScript",
                new[] { tree },
                Refs(),
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
            return main;
        }

        static List<MetadataReference> Refs()
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            int n = 0;
            foreach (Assembly a in asms)
                if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) n++;
            if (_refs != null && n == _refCount) return _refs;
            _refCount = n;
            var refs = new List<MetadataReference>(n);
            foreach (Assembly a in asms)
            {
                if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(a.Location)); }
                catch { }
            }
            return _refs = refs;
        }

        static string Sha(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
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
