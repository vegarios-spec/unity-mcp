"""Servidor MCP propio para que Claude controle el Editor de Unity.

Habla con el puente C# (paquete UnityBridge) que corre dentro del editor en http://127.0.0.1:8765/.
Stack: Python + mcp 1.x (FastMCP). Sin dependencias de Unity Cloud ni suscripciones.
"""
from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request
from pathlib import Path

from mcp.server.fastmcp import FastMCP, Image

URL = os.environ.get("UNITY_MCP_URL", "http://127.0.0.1:8765/")
AQUI = Path(__file__).resolve().parent
LOG = AQUI / "salidas" / "server.log"
LOG.parent.mkdir(exist_ok=True)

mcp = FastMCP("unity")


def _log(msg: str) -> None:
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + msg + "\n")
    except OSError:
        pass


class UnityError(RuntimeError):
    pass


def _post(cmd: str, args: dict | None = None, timeout: float = 38.0) -> dict:
    body = json.dumps({"cmd": cmd, "args": args or {}}).encode("utf-8")
    req = urllib.request.Request(URL + "cmd", data=body, headers={"Content-Type": "application/json"})
    # Sin proxy: es localhost
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def _vivo() -> bool:
    try:
        return bool(_post("alive", timeout=3).get("ok"))
    except Exception:
        return False


def _esperar_editor(max_s: float = 30) -> bool:
    """Espera a que el puente responda y el editor no este compilando/importando."""
    t0 = time.time()
    while time.time() - t0 < max_s:
        if _vivo():
            try:
                st = _post("status", timeout=10)
                if st.get("ok") and not st["data"]["compilando"] and not st["data"]["actualizando_assets"]:
                    return True
            except Exception:
                pass
        time.sleep(1.0)
    return False


def call(cmd: str, args: dict | None = None, reintentos: int = 1):
    """Manda un comando al editor. Si el editor esta recompilando (recarga de dominio), espera y reintenta."""
    ultimo = None
    for intento in range(reintentos + 1):
        try:
            r = _post(cmd, args)
            if r.get("ok"):
                return r.get("data")
            err = r.get("error", "error desconocido")
            if "recompilando" in err and intento < reintentos:
                _esperar_editor(20)
                continue
            raise UnityError(err)
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError) as e:
            ultimo = e
            if intento < reintentos and _esperar_editor(15):
                continue
    raise UnityError(
        "No hay conexion con Unity en " + URL + f" ({ultimo}). Revisa que el Editor de Unity este abierto con el "
        "proyecto que tiene el paquete 'Unity MCP Bridge' y que en la Consola salga '[UnityMCP] Puente activo'."
    )


def _txt(data) -> str:
    return data if isinstance(data, str) else json.dumps(data, ensure_ascii=False, indent=1)


def _proyecto() -> Path:
    return Path(call("status")["proyecto"])


def _ruta_en_proyecto(ruta: str) -> Path:
    p = Path(ruta)
    if not p.is_absolute():
        p = _proyecto() / ruta
    p = p.resolve()
    raiz = _proyecto().resolve()
    if raiz not in p.parents and p != raiz:
        raise UnityError(f"La ruta {p} esta fuera del proyecto {raiz}")
    return p


# =============================================================================== herramientas
@mcp.tool()
def estado() -> str:
    """Estado del Editor de Unity: version, proyecto, escena abierta, si esta en Play, compilando o con errores de compilacion."""
    if not _vivo():
        return ("Unity no responde en " + URL + ". Abre el proyecto en Unity (con el paquete Unity MCP Bridge). "
                "Si esta abierto, revisa la Consola o usa el menu Tools > Unity MCP > Reiniciar puente.")
    return _txt(call("status"))


@mcp.tool()
def jerarquia(raiz: str | None = None, profundidad: int = 4, componentes: bool = True) -> str:
    """Arbol de objetos de la escena (nombre, id, posicion, rotacion/escala si no son por defecto, componentes, hijos).
    raiz: ruta 'Padre/Hijo' para ver solo esa rama."""
    return _txt(call("hierarchy", {"raiz": raiz, "profundidad": profundidad, "componentes": componentes}))


@mcp.tool()
def ver_objeto(ruta: str, componente: str | None = None) -> str:
    """Detalle de un objeto: transform y propiedades serializadas de sus componentes (como el Inspector).
    ruta: 'Padre/Hijo', nombre simple o id. componente: limitar a un tipo (p. ej. 'WheelCollider')."""
    return _txt(call("get_object", {"ruta": ruta, "componente": componente}))


@mcp.tool()
def crear_objeto(tipo: str = "Empty", nombre: str | None = None, padre: str | None = None,
                 posicion: list[float] | None = None, rotacion: list[float] | None = None,
                 escala: list[float] | None = None, color: str | list[float] | None = None,
                 componentes: list[str] | None = None, local: bool | None = None) -> str:
    """Crea un objeto en la escena.
    tipo: Empty, Cube, Sphere, Capsule, Cylinder, Plane, Quad, Camera, Light, o ruta 'Assets/....prefab'.
    posicion/rotacion(grados)/escala: [x,y,z] en metros (1 unidad de Unity = 1 m). Con padre, son locales salvo local=False.
    color: '#RRGGBB' o [r,g,b] (crea un material en Assets/Materiales). componentes: tipos a agregar (p. ej. ['Rigidbody'])."""
    a = {"tipo": tipo, "nombre": nombre, "padre": padre, "posicion": posicion, "rotacion": rotacion,
         "escala": escala, "color": color, "componentes": componentes, "local": local}
    return _txt(call("create_object", {k: v for k, v in a.items() if v is not None}))


@mcp.tool()
def borrar_objeto(ruta: str) -> str:
    """Borra un objeto de la escena (con Undo)."""
    return _txt(call("delete_object", {"ruta": ruta}))


@mcp.tool()
def mover_objeto(ruta: str, posicion: list[float] | None = None, rotacion: list[float] | None = None,
                 escala: list[float] | None = None, local: bool = False) -> str:
    """Cambia posicion, rotacion (grados) y/o escala de un objeto. local=True usa coordenadas respecto al padre."""
    a = {"ruta": ruta, "posicion": posicion, "rotacion": rotacion, "escala": escala, "local": local}
    return _txt(call("set_transform", {k: v for k, v in a.items() if v is not None}))


@mcp.tool()
def emparentar(ruta: str, padre: str | None, mantener_mundo: bool = True) -> str:
    """Pone un objeto como hijo de otro (padre=None lo saca a la raiz)."""
    return _txt(call("set_parent", {"ruta": ruta, "padre": padre or "", "mantener_mundo": mantener_mundo}))


@mcp.tool()
def activar_objeto(ruta: str, activo: bool = True) -> str:
    """Activa o desactiva un objeto."""
    return _txt(call("set_active", {"ruta": ruta, "activo": activo}))


@mcp.tool()
def agregar_componente(ruta: str, tipo: str, propiedades: dict | None = None) -> str:
    """Agrega un componente (Rigidbody, WheelCollider, BoxCollider, AudioSource, un script tuyo...) y opcionalmente
    le pone propiedades {nombre: valor}. Si ya lo tiene, solo aplica las propiedades."""
    return _txt(call("add_component", {"ruta": ruta, "tipo": tipo, "propiedades": propiedades or {}}))


@mcp.tool()
def quitar_componente(ruta: str, tipo: str) -> str:
    """Quita un componente de un objeto."""
    return _txt(call("remove_component", {"ruta": ruta, "tipo": tipo}))


@mcp.tool()
def cambiar_propiedades(ruta: str, componente: str, propiedades: dict) -> str:
    """Cambia propiedades de un componente, como en el Inspector. Ejemplos:
    componente='Rigidbody', propiedades={'mass': 8000, 'linearDamping': 0.05}
    componente='WheelCollider', propiedades={'radius': 0.53, 'suspensionDistance': 0.25}
    componente='MiScript', propiedades={'objetivo': 'Tractocamion/Cabina', 'material': 'Assets/X.mat'}
    Vectores como [x,y,z], colores '#RRGGBB', enums por nombre, referencias por ruta de escena o 'Assets/...'.
    Para referenciar un componente de otro objeto: 'Ruta/Objeto:TipoComponente'. componente='GameObject' para el objeto."""
    return _txt(call("set_property", {"ruta": ruta, "componente": componente, "propiedades": propiedades}))


@mcp.tool()
def color_objeto(ruta: str, color: str | list[float], material: str | None = None) -> str:
    """Pone color a un objeto (crea/usa un material .mat en Assets/Materiales o en la ruta dada)."""
    return _txt(call("set_material_color", {"ruta": ruta, "color": color, "material": material}))


@mcp.tool()
def seleccionar(ruta: str, enfocar: bool = True) -> str:
    """Selecciona un objeto en el editor y opcionalmente centra la vista Scene en el."""
    return _txt(call("select", {"ruta": ruta, "enfocar": enfocar}))


@mcp.tool()
def escena(accion: str = "info", ruta: str | None = None, vacia: bool = False, en_build: bool = False) -> str:
    """Manejo de escenas. accion: info | nueva | abrir | guardar. ruta: 'Assets/Scenes/Simulador.unity'.
    vacia=True crea sin camara ni luz. en_build=True la agrega a Build Settings."""
    return _txt(call("scene", {"accion": accion, "ruta": ruta, "vacia": vacia, "en_build": en_build}))


@mcp.tool()
def play(accion: str = "play") -> str:
    """Modo Play del editor. accion: play | stop | pause | step. Los cambios hechos en Play se pierden al detener."""
    r = call("play", {"accion": accion})
    time.sleep(1.5)
    _esperar_editor(20)
    try:
        st = call("status")
        return f"{r}. Jugando ahora: {st['jugando']}, pausa: {st['en_pausa']}"
    except UnityError:
        return _txt(r)


@mcp.tool()
def menu(item: str) -> str:
    """Ejecuta un elemento de menu del editor, p. ej. 'GameObject/3D Object/Terrain', 'File/Save',
    o un [MenuItem] de un script de editor propio ('Tools/Simulador/Construir camion')."""
    return _txt(call("menu", {"item": item}))


@mcp.tool()
def consola(n: int = 40, tipos: str = "all", limpiar: bool = False) -> str:
    """Ultimos mensajes de la Consola de Unity. tipos: all | errors | warnings | logs | compile. limpiar=True la borra despues."""
    r = call("console", {"n": n, "tipos": tipos})
    if limpiar:
        call("clear_console")
    if not r:
        return "(sin mensajes de ese tipo)"
    return "\n".join(
        f"[{m['type']}] {m['message']}" + (f"  ({m['file']}:{m['line']})" if m.get("file") else "") for m in r)


@mcp.tool()
def captura(vista: str = "game", camara: str | None = None, ancho: int = 1280, alto: int = 720):
    """Renderiza una imagen para revisar visualmente el resultado.
    vista: 'game' (camara principal o la indicada en camara) | 'scene' (la vista Scene del editor).
    Usar SIEMPRE para verificar cambios visuales en vez de suponer que quedaron bien."""
    r = call("capture", {"vista": vista, "camara": camara, "ancho": ancho, "alto": alto})
    ruta = Path(r["archivo"])
    for _ in range(20):
        if ruta.exists() and ruta.stat().st_size > 0:
            break
        time.sleep(0.1)
    return [Image(path=str(ruta)), f"camara: {r['camara']} | jugando: {r['jugando']} | archivo: {ruta}"]


@mcp.tool()
def vista_escena(enfocar: str | None = None, pivote: list[float] | None = None, rotacion: list[float] | None = None,
                 distancia: float | None = None, orto: bool | None = None) -> str:
    """Mueve la camara de la vista Scene (para captura(vista='scene')). enfocar: ruta de objeto a encuadrar."""
    a = {"enfocar": enfocar, "pivote": pivote, "rotacion": rotacion, "distancia": distancia, "orto": orto}
    return _txt(call("scene_view", {k: v for k, v in a.items() if v is not None}))


@mcp.tool()
def crear_prefab(ruta: str, destino: str | None = None) -> str:
    """Guarda un objeto de la escena como prefab (por defecto Assets/Prefabs/<nombre>.prefab)."""
    return _txt(call("create_prefab", {"ruta": ruta, "destino": destino}))


# ------------------------------------------------------------------------------- archivos y scripts
@mcp.tool()
def escribir_script(ruta: str, contenido: str, esperar_compilacion: bool = True) -> str:
    """Crea o reemplaza un archivo dentro del proyecto (ruta relativa al proyecto, p. ej. 'Assets/Scripts/Camion.cs')
    y, si es .cs, espera a que Unity compile y devuelve los errores de compilacion (o 'sin errores').
    Para scripts de editor (MenuItem) usa una carpeta 'Editor'."""
    p = _ruta_en_proyecto(ruta)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(contenido, encoding="utf-8")
    _log(f"escribir_script {p}")
    if not esperar_compilacion:
        return f"escrito {p}"
    return f"escrito {p}\n" + _compilar()


def _compilar() -> str:
    try:
        call("clear_console")
        call("refresh")
    except UnityError:
        pass
    time.sleep(2.0)
    # la app de Claude corta cada herramienta a los 60 s: esperar maximo ~40 s y pedir que se vuelva a consultar
    if not _esperar_editor(40):
        return ("Unity sigue compilando/importando (o recargando). Vuelve a llamar compilar() en unos segundos "
                "para ver el resultado; si tarda mucho, la ventana de Unity puede estar en segundo plano.")
    st = call("status")
    errores = call("console", {"n": 30, "tipos": "compile"})
    errs = [m for m in errores if m["type"] == "CompileError"]
    if errs or st.get("compilacion_fallida"):
        return "ERRORES DE COMPILACION:\n" + "\n".join(f"{m['file']}:{m['line']}  {m['message']}" for m in errs)
    warns = [m for m in errores if m["type"] == "CompileWarning"]
    return "Compilo sin errores." + (f" ({len(warns)} advertencias)" if warns else "")


@mcp.tool()
def compilar() -> str:
    """Refresca los assets y espera la compilacion de scripts; devuelve errores si los hay."""
    return _compilar()


@mcp.tool()
def leer_archivo(ruta: str, max_caracteres: int = 20000) -> str:
    """Lee un archivo de texto del proyecto (scripts, .inputactions, .asset en texto, etc.)."""
    p = _ruta_en_proyecto(ruta)
    t = p.read_text(encoding="utf-8", errors="replace")
    return t[:max_caracteres] + ("\n...(recortado)" if len(t) > max_caracteres else "")


@mcp.tool()
def listar_archivos(carpeta: str = "Assets", patron: str = "*", recursivo: bool = True) -> str:
    """Lista archivos del proyecto (sin .meta)."""
    base = _ruta_en_proyecto(carpeta)
    it = base.rglob(patron) if recursivo else base.glob(patron)
    raiz = _proyecto()
    res = [str(x.relative_to(raiz)).replace("\\", "/") for x in it if x.is_file() and x.suffix != ".meta"]
    return "\n".join(sorted(res)[:800]) or "(vacio)"


# ------------------------------------------------------------------------------- controles
@mcp.tool()
def controles_conectados() -> str:
    """Lista los dispositivos de entrada que ve el Input System de Unity (Pro Controller, Joy-Con, Xbox, teclado...)."""
    return _txt(call("input_devices"))


@mcp.tool()
def leer_control(segundos: float = 5.0, intervalo: float = 0.25, incluir_teclado_mouse: bool = False) -> str:
    """Observa los controles durante unos segundos y reporta que botones/sticks/gatillos se movieron.
    Pide al usuario que mueva el control mientras corre. Nota: con el editor fuera de foco, Unity puede no leer el control
    (Project Settings > Input System > Background Behavior / 'Run In Background')."""
    vistos: dict[str, dict] = {}
    fin = time.time() + max(0.5, min(segundos, 50))
    n = 0
    while time.time() < fin:
        snap = call("input_snapshot", {"incluir_teclado_mouse": incluir_teclado_mouse})
        n += 1
        for d in snap:
            reg = vistos.setdefault(d["dispositivo"] + f" ({d['layout']})", {})
            for k, v in d["activos"].items():
                reg[k] = v
        time.sleep(intervalo)
    if not vistos:
        return "No hay dispositivos de juego conectados."
    lineas = [f"Muestras: {n}"]
    for dev, ctrls in vistos.items():
        lineas.append(f"- {dev}: " + (", ".join(f"{k}={v}" for k, v in ctrls.items()) if ctrls else "sin movimiento"))
    return "\n".join(lineas)


if __name__ == "__main__":
    _log("servidor iniciado")
    mcp.run()
