// Lectura de la Consola del editor (via la clase interna UnityEditor.LogEntries, por reflexion),
// con respaldo en un buffer propio si esa API interna cambia.
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpBridge
{
    public static class McpLog
    {
        static readonly List<JObject> _buffer = new List<JObject>();
        static readonly object _lock = new object();
        static bool _init;

        public static void Init()
        {
            if (_init) return;
            _init = true;
            Application.logMessageReceivedThreaded += (msg, stack, type) =>
            {
                lock (_lock)
                {
                    _buffer.Add(new JObject
                    {
                        ["type"] = type.ToString(),
                        ["message"] = msg,
                        ["stack"] = type == LogType.Log ? "" : Trim(stack, 1500)
                    });
                    if (_buffer.Count > 500) _buffer.RemoveRange(0, _buffer.Count - 500);
                }
            };
        }

        static string Trim(string s, int n) => s == null ? "" : (s.Length > n ? s.Substring(0, n) + "..." : s);

        static string Classify(int mode)
        {
            const int compileError = 1 << 11, compileWarning = 1 << 12;
            const int errorBits = 1 | 2 | 16 | 64 | 256 | (1 << 17) | (1 << 21);
            const int warnBits = 128 | 512 | compileWarning;
            if ((mode & compileError) != 0) return "CompileError";
            if ((mode & errorBits) != 0) return "Error";
            if ((mode & warnBits) != 0) return (mode & compileWarning) != 0 ? "CompileWarning" : "Warning";
            return "Log";
        }

        /// Devuelve las ultimas n entradas de la consola. tipos: "all" | "errors" | "warnings" | "logs" | "compile"
        public static JArray Read(int n, string tipos)
        {
            var res = new JArray();
            try
            {
                var asm = typeof(EditorWindow).Assembly;
                var logEntries = asm.GetType("UnityEditor.LogEntries");
                var logEntryT = asm.GetType("UnityEditor.LogEntry");
                var start = logEntries.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var end = logEntries.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var getEntry = logEntries.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);
                var fMsg = logEntryT.GetField("message");
                var fMode = logEntryT.GetField("mode");
                var fFile = logEntryT.GetField("file");
                var fLine = logEntryT.GetField("line");
                int count = (int)start.Invoke(null, null);
                try
                {
                    var all = new List<JObject>();
                    var entry = Activator.CreateInstance(logEntryT);
                    for (int i = 0; i < count; i++)
                    {
                        getEntry.Invoke(null, new object[] { i, entry });
                        string msg = (string)fMsg.GetValue(entry);
                        int mode = (int)fMode.GetValue(entry);
                        string kind = Classify(mode);
                        if (!Match(kind, tipos)) continue;
                        all.Add(new JObject
                        {
                            ["type"] = kind,
                            ["message"] = Trim(msg, 2500),
                            ["file"] = (string)fFile.GetValue(entry),
                            ["line"] = (int)fLine.GetValue(entry)
                        });
                    }
                    for (int i = Math.Max(0, all.Count - n); i < all.Count; i++) res.Add(all[i]);
                }
                finally { end.Invoke(null, null); }
                return res;
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    var filtered = _buffer.FindAll(o => Match((string)o["type"], tipos));
                    for (int i = Math.Max(0, filtered.Count - n); i < filtered.Count; i++) res.Add(filtered[i]);
                }
                return res;
            }
        }

        static bool Match(string kind, string tipos)
        {
            switch ((tipos ?? "all").ToLowerInvariant())
            {
                case "errors": return kind.Contains("Error") || kind == "Exception" || kind == "Assert";
                case "warnings": return kind.Contains("Warning");
                case "logs": return kind == "Log";
                case "compile": return kind.StartsWith("Compile");
                default: return true;
            }
        }

        public static void Clear()
        {
            try
            {
                var t = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                t.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
            }
            catch { }
            lock (_lock) _buffer.Clear();
        }
    }
}
