// Comandos que el servidor MCP puede pedirle al editor. Todos corren en el hilo principal.
#pragma warning disable CS0618 // InstanceIDToObject/GetInstanceID: obsoletos en Unity 6.3 pero siguen funcionando
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityMcpBridge
{
    public static class Commands
    {
        public static JToken Run(string cmd, JObject a)
        {
            switch (cmd)
            {
                case "ping": return Status();
                case "status": return Status();
                case "hierarchy": return Hierarchy(a);
                case "get_object": return GetObject(a);
                case "create_object": return CreateObject(a);
                case "delete_object": return DeleteObject(a);
                case "set_transform": return SetTransform(a);
                case "set_parent": return SetParent(a);
                case "set_active": return SetActive(a);
                case "add_component": return AddComponent(a);
                case "remove_component": return RemoveComponent(a);
                case "set_property": return SetProperty(a);
                case "set_material_color": return SetMaterialColor(a);
                case "menu": return Menu(a);
                case "play": return PlayMode(a);
                case "refresh": AssetDatabase.Refresh(); return Status();
                case "console": return McpLog.Read((int?)a["n"] ?? 50, (string)a["tipos"] ?? "all");
                case "clear_console": McpLog.Clear(); return "ok";
                case "capture": return Capture(a);
                case "scene_view": return SceneViewCmd(a);
                case "scene": return SceneCmd(a);
                case "select": return Select(a);
                case "input_devices": return InputDevices();
                case "input_snapshot": return InputSnapshot(a);
                case "create_prefab": return CreatePrefab(a);
                default: throw new ArgumentException("Comando desconocido: " + cmd);
            }
        }

        // ------------------------------------------------------------------ estado
        static JObject Status()
        {
            var s = SceneManager.GetActiveScene();
            return new JObject
            {
                ["unity"] = Application.unityVersion,
                ["proyecto"] = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/'),
                ["escena"] = s.path,
                ["escena_nombre"] = s.name,
                ["escena_sin_guardar"] = s.isDirty,
                ["jugando"] = EditorApplication.isPlaying,
                ["en_pausa"] = EditorApplication.isPaused,
                ["compilando"] = EditorApplication.isCompiling,
                ["actualizando_assets"] = EditorApplication.isUpdating,
                ["compilacion_fallida"] = EditorUtility.scriptCompilationFailed,
                ["render_pipeline"] = GraphicsSettings.currentRenderPipeline ? GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in",
                ["seleccion"] = Selection.activeGameObject ? PathOf(Selection.activeGameObject) : null
            };
        }

        // ------------------------------------------------------------------ busqueda
        public static string PathOf(GameObject go)
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        static IEnumerable<GameObject> Roots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.isLoaded) foreach (var r in sc.GetRootGameObjects()) yield return r;
            }
        }

        public static GameObject Find(string path, bool required = true)
        {
            if (string.IsNullOrEmpty(path)) { if (required) throw new ArgumentException("Falta la ruta del objeto"); return null; }
            if (int.TryParse(path, out int id))
            {
                var o = EditorUtility.InstanceIDToObject(id);
                if (o is GameObject g) return g;
                if (o is Component c) return c.gameObject;
            }
            var parts = path.Trim('/').Split('/');
            foreach (var r in Roots())
            {
                if (r.name != parts[0]) continue;
                var t = r.transform;
                for (int i = 1; i < parts.Length && t != null; i++) t = t.Find(parts[i]);
                if (t != null) return t.gameObject;
            }
            // busqueda por nombre simple en cualquier nivel
            if (parts.Length == 1)
                foreach (var r in Roots())
                    foreach (var t in r.GetComponentsInChildren<Transform>(true))
                        if (t.name == parts[0]) return t.gameObject;
            if (required) throw new ArgumentException("No encontre el objeto: " + path);
            return null;
        }

        public static Type FindType(string name, Type mustDerive = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Falta el tipo");
            Type best = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                {
                    if (t.FullName != name && t.Name != name) continue;
                    if (mustDerive != null && !mustDerive.IsAssignableFrom(t)) continue;
                    if (t.FullName == name) return t;
                    if (best == null || (t.Namespace ?? "").StartsWith("UnityEngine")) best = t;
                }
            }
            if (best == null) throw new ArgumentException("No encontre el tipo: " + name + " (si es un script tuyo, revisa que compilo sin errores)");
            return best;
        }

        // ------------------------------------------------------------------ jerarquia
        static JToken Hierarchy(JObject a)
        {
            int depth = (int?)a["profundidad"] ?? 4;
            bool comps = (bool?)a["componentes"] ?? true;
            var res = new JArray();
            if (a["raiz"] != null && (string)a["raiz"] != "")
                res.Add(Node(Find((string)a["raiz"]), depth, comps));
            else
                foreach (var r in Roots()) res.Add(Node(r, depth, comps));
            return res;
        }

        static JObject Node(GameObject go, int depth, bool comps)
        {
            var t = go.transform;
            var o = new JObject
            {
                ["nombre"] = go.name,
                ["id"] = go.GetInstanceID(),
                ["activo"] = go.activeSelf,
                ["pos"] = V(t.position),
            };
            if (t.localEulerAngles != Vector3.zero) o["rot"] = V(t.localEulerAngles);
            if (t.localScale != Vector3.one) o["escala"] = V(t.localScale);
            if (comps)
                o["componentes"] = new JArray(go.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name));
            if (t.childCount > 0)
            {
                if (depth > 0)
                {
                    var ch = new JArray();
                    foreach (Transform c in t) ch.Add(Node(c.gameObject, depth - 1, comps));
                    o["hijos"] = ch;
                }
                else o["hijos_ocultos"] = t.childCount;
            }
            return o;
        }

        static JToken GetObject(JObject a)
        {
            var go = Find((string)a["ruta"]);
            string only = (string)a["componente"];
            var t = go.transform;
            var o = new JObject
            {
                ["ruta"] = PathOf(go),
                ["id"] = go.GetInstanceID(),
                ["activo"] = go.activeSelf,
                ["tag"] = go.tag,
                ["capa"] = LayerMask.LayerToName(go.layer),
                ["posicion"] = V(t.position),
                ["posicion_local"] = V(t.localPosition),
                ["rotacion_local"] = V(t.localEulerAngles),
                ["escala_local"] = V(t.localScale),
            };
            var comps = new JArray();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) { comps.Add(new JObject { ["tipo"] = "(script faltante)" }); continue; }
                if (c is Transform) continue;
                if (!string.IsNullOrEmpty(only) && c.GetType().Name != only) continue;
                var co = new JObject { ["tipo"] = c.GetType().Name };
                var props = new JObject();
                var so = new SerializedObject(c);
                var it = so.GetIterator();
                bool enter = true;
                int k = 0;
                while (it.NextVisible(enter) && k++ < 80)
                {
                    enter = false;
                    if (it.name == "m_Script") continue;
                    props[it.name] = PropValue(it);
                }
                co["propiedades"] = props;
                comps.Add(co);
            }
            o["componentes"] = comps;
            return o;
        }

        static JToken PropValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.intValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return Math.Round(p.floatValue, 5);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Color: return C(p.colorValue);
                case SerializedPropertyType.ObjectReference:
                    if (!p.objectReferenceValue) return null;
                    var ap = AssetDatabase.GetAssetPath(p.objectReferenceValue);
                    return string.IsNullOrEmpty(ap) ? p.objectReferenceValue.name + " (" + p.objectReferenceValue.GetType().Name + ")" : ap;
                case SerializedPropertyType.Enum:
                    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length ? p.enumDisplayNames[p.enumValueIndex] : p.intValue.ToString();
                case SerializedPropertyType.Vector2: return V(p.vector2Value);
                case SerializedPropertyType.Vector3: return V(p.vector3Value);
                case SerializedPropertyType.Vector4: return new JArray(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.Quaternion: return V(p.quaternionValue.eulerAngles);
                case SerializedPropertyType.LayerMask: return p.intValue;
                default:
                    if (p.isArray) return "[" + p.arraySize + " elementos]";
                    return "(" + p.propertyType + ")";
            }
        }

        // ------------------------------------------------------------------ crear / borrar
        static JToken CreateObject(JObject a)
        {
            string tipo = ((string)a["tipo"] ?? "Empty");
            GameObject go;
            if (tipo.EndsWith(".prefab"))
            {
                var pf = AssetDatabase.LoadAssetAtPath<GameObject>(tipo);
                if (pf == null) throw new ArgumentException("No existe el prefab " + tipo);
                go = (GameObject)PrefabUtility.InstantiatePrefab(pf);
            }
            else if (tipo.Equals("Empty", StringComparison.OrdinalIgnoreCase) || tipo.Equals("Vacio", StringComparison.OrdinalIgnoreCase))
                go = new GameObject();
            else if (Enum.TryParse(tipo, true, out PrimitiveType prim))
                go = GameObject.CreatePrimitive(prim);
            else if (tipo.Equals("Camera", StringComparison.OrdinalIgnoreCase))
            { go = new GameObject("Camera"); go.AddComponent<Camera>(); }
            else if (tipo.Equals("DirectionalLight", StringComparison.OrdinalIgnoreCase) || tipo.Equals("Light", StringComparison.OrdinalIgnoreCase))
            { go = new GameObject("Directional Light"); var l = go.AddComponent<Light>(); l.type = LightType.Directional; }
            else throw new ArgumentException("Tipo no valido: " + tipo + ". Usa Empty, Cube, Sphere, Capsule, Cylinder, Plane, Quad, Camera, Light o la ruta de un .prefab");

            Undo.RegisterCreatedObjectUndo(go, "MCP crear " + tipo);
            if (a["nombre"] != null) go.name = (string)a["nombre"];
            if (a["padre"] != null && (string)a["padre"] != "") go.transform.SetParent(Find((string)a["padre"]).transform, false);
            ApplyTransform(go.transform, a);
            if (a["componentes"] is JArray comps)
                foreach (var c in comps) Undo.AddComponent(go, FindType((string)c, typeof(Component)));
            if (a["color"] != null) Colorize(go, a["color"], (string)a["material"]);
            MarkDirty(go);
            return new JObject { ["ruta"] = PathOf(go), ["id"] = go.GetInstanceID() };
        }

        static JToken DeleteObject(JObject a)
        {
            var go = Find((string)a["ruta"]);
            string p = PathOf(go);
            MarkDirty(go);
            Undo.DestroyObjectImmediate(go);
            return "borrado " + p;
        }

        static JToken CreatePrefab(JObject a)
        {
            var go = Find((string)a["ruta"]);
            string dest = (string)a["destino"] ?? ("Assets/Prefabs/" + go.name + ".prefab");
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            PrefabUtility.SaveAsPrefabAssetAndConnect(go, dest, InteractionMode.AutomatedAction);
            return dest;
        }

        // ------------------------------------------------------------------ transform
        static void ApplyTransform(Transform t, JObject a)
        {
            bool local = (bool?)a["local"] ?? (a["padre"] != null);
            if (a["posicion"] != null) { var v = ToVec3(a["posicion"]); if (local) t.localPosition = v; else t.position = v; }
            if (a["rotacion"] != null) { var v = ToVec3(a["rotacion"]); if (local) t.localEulerAngles = v; else t.eulerAngles = v; }
            if (a["escala"] != null) t.localScale = ToVec3(a["escala"]);
        }

        static JToken SetTransform(JObject a)
        {
            var go = Find((string)a["ruta"]);
            Undo.RecordObject(go.transform, "MCP transform");
            ApplyTransform(go.transform, a);
            MarkDirty(go);
            return Node(go, 0, false);
        }

        static JToken SetParent(JObject a)
        {
            var go = Find((string)a["ruta"]);
            string padre = (string)a["padre"];
            Transform p = string.IsNullOrEmpty(padre) ? null : Find(padre).transform;
            Undo.SetTransformParent(go.transform, p, "MCP parent");
            if (!((bool?)a["mantener_mundo"] ?? true)) { go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; }
            MarkDirty(go);
            return PathOf(go);
        }

        static JToken SetActive(JObject a)
        {
            var go = Find((string)a["ruta"]);
            Undo.RecordObject(go, "MCP active");
            go.SetActive((bool?)a["activo"] ?? true);
            MarkDirty(go);
            return go.activeSelf;
        }

        // ------------------------------------------------------------------ componentes
        static JToken AddComponent(JObject a)
        {
            var go = Find((string)a["ruta"]);
            var t = FindType((string)a["tipo"], typeof(Component));
            var c = go.GetComponent(t);
            if (c == null || ((bool?)a["permitir_duplicado"] ?? false)) c = Undo.AddComponent(go, t);
            if (a["propiedades"] is JObject props)
                foreach (var kv in props) SetOne(c, kv.Key, kv.Value);
            MarkDirty(go);
            return t.Name + " en " + PathOf(go);
        }

        static JToken RemoveComponent(JObject a)
        {
            var go = Find((string)a["ruta"]);
            var t = FindType((string)a["tipo"], typeof(Component));
            if (!go.TryGetComponent(t, out var c)) throw new ArgumentException(go.name + " no tiene " + t.Name);
            Undo.DestroyObjectImmediate(c);
            MarkDirty(go);
            return "quitado " + t.Name;
        }

        static Component GetComp(GameObject go, string tipo)
        {
            var t = FindType(tipo, typeof(Component));
            if (!go.TryGetComponent(t, out var c)) throw new ArgumentException(go.name + " no tiene el componente " + t.Name);
            return c;
        }

        static JToken SetProperty(JObject a)
        {
            var go = Find((string)a["ruta"]);
            Object target = string.IsNullOrEmpty((string)a["componente"]) || (string)a["componente"] == "GameObject"
                ? (Object)go : GetComp(go, (string)a["componente"]);
            var res = new JObject();
            if (a["propiedades"] is JObject props)
                foreach (var kv in props) res[kv.Key] = SetOne(target, kv.Key, kv.Value);
            else
                res[(string)a["propiedad"]] = SetOne(target, (string)a["propiedad"], a["valor"]);
            MarkDirty(go);
            return res;
        }

        /// Cambia una propiedad: primero por SerializedObject (como el Inspector), si no, por reflexion.
        static string SetOne(Object target, string name, JToken val)
        {
            var so = new SerializedObject(target);
            var sp = so.FindProperty(name) ?? so.FindProperty("m_" + char.ToUpper(name[0]) + name.Substring(1));
            if (sp != null && SetSerialized(sp, val))
            {
                so.ApplyModifiedProperties();
                return "ok (" + sp.name + ")";
            }
            Undo.RecordObject(target, "MCP set " + name);
            var t = target.GetType();
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var prop = t.GetProperty(name, bf);
            if (prop != null && prop.CanWrite) { prop.SetValue(target, Convert(val, prop.PropertyType)); EditorUtility.SetDirty(target); return "ok (propiedad)"; }
            var field = t.GetField(name, bf);
            if (field != null) { field.SetValue(target, Convert(val, field.FieldType)); EditorUtility.SetDirty(target); return "ok (campo)"; }
            throw new ArgumentException($"{t.Name} no tiene la propiedad '{name}'");
        }

        static bool SetSerialized(SerializedProperty p, JToken v)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: p.intValue = v.Value<int>(); return true;
                case SerializedPropertyType.Boolean: p.boolValue = v.Value<bool>(); return true;
                case SerializedPropertyType.Float: p.floatValue = v.Value<float>(); return true;
                case SerializedPropertyType.String: p.stringValue = v.Value<string>(); return true;
                case SerializedPropertyType.Color: p.colorValue = ToColor(v); return true;
                case SerializedPropertyType.Vector2: { var x = ToVec3(v); p.vector2Value = new Vector2(x.x, x.y); return true; }
                case SerializedPropertyType.Vector3: p.vector3Value = ToVec3(v); return true;
                case SerializedPropertyType.Quaternion: p.quaternionValue = Quaternion.Euler(ToVec3(v)); return true;
                case SerializedPropertyType.LayerMask: p.intValue = v.Type == JTokenType.Integer ? v.Value<int>() : LayerMask.GetMask(v.ToObject<string[]>()); return true;
                case SerializedPropertyType.Enum:
                    if (v.Type == JTokenType.Integer) { p.intValue = v.Value<int>(); return true; }
                    {
                        string s = v.Value<string>();
                        int i = Array.FindIndex(p.enumNames, e => string.Equals(e, s, StringComparison.OrdinalIgnoreCase));
                        if (i < 0) i = Array.FindIndex(p.enumDisplayNames, e => string.Equals(e, s, StringComparison.OrdinalIgnoreCase));
                        if (i < 0) throw new ArgumentException($"Valor '{s}' no valido. Opciones: {string.Join(", ", p.enumNames)}");
                        p.enumValueIndex = i; return true;
                    }
                case SerializedPropertyType.ObjectReference:
                    p.objectReferenceValue = v.Type == JTokenType.Null ? null : ResolveObject(v.Value<string>(), typeof(Object));
                    return true;
                default: return false;
            }
        }

        static object Convert(JToken v, Type t)
        {
            if (t == typeof(Vector3)) return ToVec3(v);
            if (t == typeof(Vector2)) { var x = ToVec3(v); return new Vector2(x.x, x.y); }
            if (t == typeof(Quaternion)) return Quaternion.Euler(ToVec3(v));
            if (t == typeof(Color)) return ToColor(v);
            if (t.IsEnum) return v.Type == JTokenType.Integer ? Enum.ToObject(t, v.Value<int>()) : Enum.Parse(t, v.Value<string>(), true);
            if (typeof(Object).IsAssignableFrom(t)) return v.Type == JTokenType.Null ? null : ResolveObject(v.Value<string>(), t);
            return v.ToObject(t);
        }

        /// "Assets/..." -> asset; si no, ruta de objeto en escena (y su componente del tipo pedido)
        static Object ResolveObject(string s, Type t)
        {
            if (s.StartsWith("Assets/") || s.StartsWith("Packages/"))
            {
                var o = t == typeof(Object) ? AssetDatabase.LoadMainAssetAtPath(s) : AssetDatabase.LoadAssetAtPath(s, t);
                return o ? o : throw new ArgumentException("No existe el asset " + s);
            }
            string comp = null;
            int idx = s.IndexOf(':');
            if (idx > 0) { comp = s.Substring(idx + 1); s = s.Substring(0, idx); }
            var go = Find(s);
            if (comp != null) return GetComp(go, comp);
            if (t == typeof(GameObject) || t == typeof(Object)) return go;
            if (typeof(Component).IsAssignableFrom(t)) return go.GetComponent(t);
            return go;
        }

        // ------------------------------------------------------------------ materiales
        static JToken SetMaterialColor(JObject a)
        {
            var go = Find((string)a["ruta"]);
            Colorize(go, a["color"], (string)a["material"]);
            MarkDirty(go);
            return "ok";
        }

        static void Colorize(GameObject go, JToken color, string matPath)
        {
            if (!go.TryGetComponent<Renderer>(out var r)) throw new ArgumentException(go.name + " no tiene Renderer");
            var c = ToColor(color);
            matPath = matPath ?? ("Assets/Materiales/" + Sanitize(go.name) + ".mat");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                Shader sh = GraphicsSettings.currentRenderPipeline && GraphicsSettings.currentRenderPipeline.defaultMaterial
                    ? GraphicsSettings.currentRenderPipeline.defaultMaterial.shader
                    : Shader.Find("Standard");
                mat = new Material(sh);
                Directory.CreateDirectory(Path.GetDirectoryName(matPath));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
            Undo.RecordObject(r, "MCP material");
            r.sharedMaterial = mat;
        }

        static string Sanitize(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        // ------------------------------------------------------------------ editor
        static JToken Menu(JObject a)
        {
            string item = (string)a["item"];
            bool ok = EditorApplication.ExecuteMenuItem(item);
            if (!ok) throw new ArgumentException("No existe o no esta disponible el menu: " + item);
            return "ejecutado " + item;
        }

        static JToken PlayMode(JObject a)
        {
            string acc = ((string)a["accion"] ?? "play").ToLowerInvariant();
            switch (acc)
            {
                case "play": case "jugar": EditorApplication.isPlaying = true; break;
                case "stop": case "detener": EditorApplication.isPlaying = false; break;
                case "pause": case "pausa": EditorApplication.isPaused = !EditorApplication.isPaused; break;
                case "step": EditorApplication.Step(); break;
                default: throw new ArgumentException("accion: play | stop | pause | step");
            }
            return "pedido: " + acc + " (el cambio de modo termina en el siguiente cuadro del editor)";
        }

        static JToken Select(JObject a)
        {
            var go = Find((string)a["ruta"]);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            if ((bool?)a["enfocar"] ?? true) SceneView.lastActiveSceneView?.FrameSelected();
            return PathOf(go);
        }

        static JToken SceneViewCmd(JObject a)
        {
            var sv = SceneView.lastActiveSceneView ?? throw new InvalidOperationException("No hay una ventana Scene abierta");
            if (a["enfocar"] != null)
            {
                var go = Find((string)a["enfocar"]);
                var rs = go.GetComponentsInChildren<Renderer>();
                var b = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.one);
                foreach (var r in rs) b.Encapsulate(r.bounds);
                sv.Frame(b, true);
            }
            if (a["pivote"] != null) sv.pivot = ToVec3(a["pivote"]);
            if (a["rotacion"] != null) sv.rotation = Quaternion.Euler(ToVec3(a["rotacion"]));
            if (a["distancia"] != null) sv.size = (float)a["distancia"];
            if (a["orto"] != null) sv.orthographic = (bool)a["orto"];
            sv.Repaint();
            return new JObject { ["pivote"] = V(sv.pivot), ["rotacion"] = V(sv.rotation.eulerAngles), ["distancia"] = sv.size };
        }

        static JToken SceneCmd(JObject a)
        {
            string acc = ((string)a["accion"] ?? "info").ToLowerInvariant();
            string path = (string)a["ruta"];
            switch (acc)
            {
                case "nueva":
                    if (EditorApplication.isPlaying) throw new InvalidOperationException("Deten el modo Play primero");
                    var sc = EditorSceneManager.NewScene(((bool?)a["vacia"] ?? false) ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                    if (!string.IsNullOrEmpty(path)) { Directory.CreateDirectory(Path.GetDirectoryName(path)); EditorSceneManager.SaveScene(sc, path); }
                    break;
                case "abrir":
                    if (EditorApplication.isPlaying) throw new InvalidOperationException("Deten el modo Play primero");
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    break;
                case "guardar":
                    var s = SceneManager.GetActiveScene();
                    if (!string.IsNullOrEmpty(path)) { Directory.CreateDirectory(Path.GetDirectoryName(path)); EditorSceneManager.SaveScene(s, path); }
                    else if (string.IsNullOrEmpty(s.path)) throw new ArgumentException("La escena no tiene ruta; pasa ruta='Assets/Scenes/X.unity'");
                    else EditorSceneManager.SaveScene(s);
                    break;
                case "info": break;
                default: throw new ArgumentException("accion: info | nueva | abrir | guardar");
            }
            if ((bool?)a["en_build"] ?? false)
            {
                var p = SceneManager.GetActiveScene().path;
                var list = EditorBuildSettings.scenes.ToList();
                if (!list.Any(x => x.path == p)) { list.Insert(0, new EditorBuildSettingsScene(p, true)); EditorBuildSettings.scenes = list.ToArray(); }
            }
            return Status();
        }

        // ------------------------------------------------------------------ captura
        static JToken Capture(JObject a)
        {
            string vista = ((string)a["vista"] ?? "game").ToLowerInvariant();
            int w = (int?)a["ancho"] ?? 1280, h = (int?)a["alto"] ?? 720;
            Camera cam;
            GameObject temporal = null;
            if (vista == "scene" || vista == "escena")
            {
                // la camara interna de la vista Scene solo se actualiza al repintar; se usa una camara temporal con su pose
                var sv = SceneView.lastActiveSceneView ?? throw new InvalidOperationException("No hay ventana Scene abierta");
                temporal = new GameObject("MCP_CamaraCaptura") { hideFlags = HideFlags.HideAndDontSave };
                cam = temporal.AddComponent<Camera>();
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 5000f;
                cam.orthographic = sv.orthographic;
                cam.orthographicSize = sv.size;
                temporal.transform.SetPositionAndRotation(sv.pivot - sv.rotation * Vector3.forward * sv.cameraDistance, sv.rotation);
            }
            else if (!string.IsNullOrEmpty((string)a["camara"]))
                { if (!Find((string)a["camara"]).TryGetComponent<Camera>(out cam)) throw new ArgumentException("Ese objeto no tiene Camera"); }
            else
                {
                cam = Camera.main;
                if (cam == null) cam = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).FirstOrDefault(c => c.enabled);
                if (cam == null) throw new InvalidOperationException("No hay camara en la escena");
            }

            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                bool hecho = false;
                if (GraphicsSettings.currentRenderPipeline != null)
                {
                    // URP/HDRP: Camera.Render() no dibuja la geometria; se usa una peticion de render estandar
                    var req = new RenderPipeline.StandardRequest { destination = rt };
                    if (RenderPipeline.SupportsRenderRequest(cam, req))
                    {
                        RenderPipeline.SubmitRenderRequest(cam, req);
                        hecho = true;
                    }
                }
                if (!hecho)
                {
                    cam.targetTexture = rt;
                    cam.Render();
                }
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
            string nombreCam = cam.name;
            if (temporal != null) { nombreCam = "Vista Scene"; Object.DestroyImmediate(temporal); }
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "UnityMCP");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"captura_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return new JObject { ["archivo"] = file.Replace('\\', '/'), ["camara"] = nombreCam, ["jugando"] = EditorApplication.isPlaying };
        }

        // ------------------------------------------------------------------ entrada (controles)
        static JToken InputDevices()
        {
            var res = new JArray();
            foreach (var d in InputSystem.devices)
                res.Add(new JObject
                {
                    ["nombre"] = d.name,
                    ["nombre_visible"] = d.displayName,
                    ["layout"] = d.layout,
                    ["interfaz"] = d.description.interfaceName,
                    ["producto"] = d.description.product,
                    ["fabricante"] = d.description.manufacturer,
                    ["es_gamepad"] = d is Gamepad,
                    ["habilitado"] = d.enabled
                });
            return res;
        }

        /// Controles que no estan en reposo (botones presionados, sticks movidos, gatillos) en cada dispositivo de juego.
        static JToken InputSnapshot(JObject a)
        {
            bool todos = (bool?)a["incluir_teclado_mouse"] ?? false;
            float umbral = (float?)a["umbral"] ?? 0.15f;
            var res = new JArray();
            foreach (var d in InputSystem.devices)
            {
                if (!todos && (d is Keyboard || d is Mouse || d is Pen || d is Touchscreen)) continue;
                var activos = new JObject();
                foreach (var c in d.allControls)
                {
                    if (c.noisy || c.synthetic || c.parent is Vector2Control || (c.children.Count > 0 && !(c is Vector2Control))) continue;
                    try
                    {
                        if (c is ButtonControl b) { if (b.isPressed) activos[c.path.Substring(d.path.Length + 1)] = Math.Round(b.ReadValue(), 2); }
                        else if (c is Vector2Control v2) { var v = v2.ReadValue(); if (v.magnitude > umbral) activos[c.path.Substring(d.path.Length + 1)] = new JArray(Math.Round(v.x, 2), Math.Round(v.y, 2)); }
                        else if (c is AxisControl ax) { var v = ax.ReadValue(); if (Math.Abs(v) > umbral) activos[c.path.Substring(d.path.Length + 1)] = Math.Round(v, 2); }
                    }
                    catch { }
                }
                res.Add(new JObject { ["dispositivo"] = d.displayName ?? d.name, ["layout"] = d.layout, ["activos"] = activos });
            }
            return res;
        }

        // ------------------------------------------------------------------ util
        static void MarkDirty(GameObject go)
        {
            if (!EditorApplication.isPlaying) EditorSceneManager.MarkSceneDirty(go.scene);
        }

        static JArray V(Vector3 v) => new JArray(Math.Round(v.x, 3), Math.Round(v.y, 3), Math.Round(v.z, 3));
        static JArray V(Vector2 v) => new JArray(Math.Round(v.x, 3), Math.Round(v.y, 3));
        static JArray C(Color c) => new JArray(Math.Round(c.r, 3), Math.Round(c.g, 3), Math.Round(c.b, 3), Math.Round(c.a, 3));

        static Vector3 ToVec3(JToken t)
        {
            if (t is JArray arr)
                return new Vector3(arr.Count > 0 ? (float)arr[0] : 0, arr.Count > 1 ? (float)arr[1] : 0, arr.Count > 2 ? (float)arr[2] : 0);
            if (t is JObject o) return new Vector3((float?)o["x"] ?? 0, (float?)o["y"] ?? 0, (float?)o["z"] ?? 0);
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) { float f = (float)t; return new Vector3(f, f, f); }
            throw new ArgumentException("Vector no valido: " + t);
        }

        static Color ToColor(JToken t)
        {
            if (t.Type == JTokenType.String)
            {
                string s = (string)t;
                if (ColorUtility.TryParseHtmlString(s.StartsWith("#") ? s : "#" + s, out var c)) return c;
                if (ColorUtility.TryParseHtmlString(s, out c)) return c;
                throw new ArgumentException("Color no valido: " + s);
            }
            if (t is JArray arr)
            {
                float k = arr.Any(x => (float)x > 1f) ? 255f : 1f;
                return new Color((float)arr[0] / k, (float)arr[1] / k, (float)arr[2] / k, arr.Count > 3 ? (float)arr[3] / k : 1f);
            }
            throw new ArgumentException("Color no valido: " + t);
        }
    }
}
