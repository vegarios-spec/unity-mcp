// Puente HTTP local entre el servidor MCP (Python) y el Editor de Unity.
// Escucha SOLO en http://127.0.0.1:8765/ . Cada peticion es POST /cmd con {"cmd": "...", "args": {...}}.
// Las peticiones se encolan y se ejecutan en el hilo principal del editor (EditorApplication.update).
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMcpBridge
{
    [InitializeOnLoad]
    public static class McpBridge
    {
        public const int Port = 8765;
        static HttpListener _listener;
        static Thread _thread;
        static volatile bool _running;
        static double _nextRetry;
        static int _retries;
        static readonly ConcurrentQueue<Pending> _queue = new ConcurrentQueue<Pending>();

        class Pending
        {
            public string Cmd;
            public JObject Args;
            public string Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static McpBridge()
        {
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            McpLog.Init();
            Start();
        }

        public static bool IsRunning => _running;

        [MenuItem("Tools/Unity MCP/Reiniciar puente")]
        public static void Restart() { Stop(); Start(); }

        [MenuItem("Tools/Unity MCP/Estado del puente")]
        public static void ShowStatus()
        {
            EditorUtility.DisplayDialog("Unity MCP",
                _running ? $"Puente activo en http://127.0.0.1:{Port}/" : "Puente detenido", "OK");
        }

        static void Start()
        {
            if (_running) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _running = true;
                _retries = 0;
                _thread = new Thread(Listen) { IsBackground = true, Name = "UnityMcpBridge" };
                _thread.Start();
                Debug.Log($"[UnityMCP] Puente activo en http://127.0.0.1:{Port}/");
            }
            catch (Exception e)
            {
                _running = false;
                try { _listener?.Close(); } catch { }
                _listener = null;
                // tras una recompilacion, Windows puede tardar unos segundos en soltar el puerto: reintentar
                _retries++;
                _nextRetry = EditorApplication.timeSinceStartup + 1.0;
                if (_retries == 1 || _retries % 30 == 0)
                    Debug.Log($"[UnityMCP] Puerto {Port} ocupado ({e.Message}); reintentando...");
            }
        }

        static void Stop()
        {
            _running = false;
            try { _listener?.Abort(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            // libera a quien estuviera esperando
            while (_queue.TryDequeue(out var p))
            {
                p.Result = Err("El editor esta recompilando o cerrandose; vuelve a intentar en unos segundos.");
                p.Done.Set();
            }
        }

        static void Listen()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            string result;
            try
            {
                string body;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
                var p = new Pending
                {
                    Cmd = (string)req["cmd"] ?? "ping",
                    Args = req["args"] as JObject ?? new JObject()
                };
                // los argumentos null (opcionales sin valor) se quitan para que cuenten como "no dados"
                foreach (var prop in new System.Collections.Generic.List<JProperty>(p.Args.Properties()))
                    if (prop.Value.Type == JTokenType.Null) prop.Remove();
                // ping responde aun si el hilo principal esta ocupado
                if (p.Cmd == "alive")
                    result = Ok(new JObject { ["alive"] = true });
                else
                {
                    _queue.Enqueue(p);
                    int timeout = (int?)p.Args["_timeout_ms"] ?? 35000;
                    result = p.Done.Wait(timeout)
                        ? p.Result
                        : Err("Tiempo de espera agotado: el editor no atendio la peticion (puede estar compilando, importando o con un dialogo abierto).");
                }
            }
            catch (Exception e) { result = Err("Peticion invalida: " + e.Message); }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(result);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        static void Pump()
        {
            if (!_running && _retries > 0 && _retries < 600 && EditorApplication.timeSinceStartup >= _nextRetry) Start();
            int n = 0;
            while (n++ < 20 && _queue.TryDequeue(out var p))
            {
                try { p.Result = Ok(Commands.Run(p.Cmd, p.Args)); }
                catch (Exception e)
                {
                    var inner = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    p.Result = Err(inner.GetType().Name + ": " + inner.Message);
                }
                p.Done.Set();
            }
        }

        public static string Ok(JToken data) =>
            new JObject { ["ok"] = true, ["data"] = data }.ToString(Formatting.None);

        public static string Err(string msg) =>
            new JObject { ["ok"] = false, ["error"] = msg }.ToString(Formatting.None);
    }
}
