# unity-mcp

Servidor MCP propio para que Claude controle el Editor de Unity (sin Unity AI, sin suscripcion ni Unity Cloud).
Construido el 7/oct/2026 para el simulador de tractocamion con controles de Switch.

## Como funciona
Claude  --MCP (stdio)-->  server.py (Python, .venv)  --HTTP 127.0.0.1:8765-->  UnityBridge (paquete C# dentro del editor)

- UnityBridge\ es un paquete de Unity (com.elias.unity-mcp-bridge). Un proyecto lo usa agregando en
  Packages\manifest.json:  "com.elias.unity-mcp-bridge": "file:<ruta>/unity-mcp/UnityBridge"
- Al abrir el proyecto, la Consola debe decir: [UnityMCP] Puente activo en http://127.0.0.1:8765/
- Menu del editor: Tools > Unity MCP > Reiniciar puente / Estado del puente.
- Solo escucha en 127.0.0.1 (no es accesible desde otra computadora).

## Instalacion (una vez)
1. Doble clic en instalar.bat (crea .venv, instala mcp<2, registra "unity" en Claude Desktop y Claude Code).
2. Cerrar por completo la app de Claude y volver a abrirla.

## Herramientas
estado, jerarquia, ver_objeto, crear_objeto, borrar_objeto, mover_objeto, emparentar, activar_objeto,
agregar_componente, quitar_componente, cambiar_propiedades, color_objeto, seleccionar, escena, play, menu,
consola, captura, vista_escena, crear_prefab, escribir_script, compilar, leer_archivo, listar_archivos,
controles_conectados, leer_control.

## Proyecto que lo usa
Simulador de tractocamion (Unity 6.3 LTS 6000.3.25f1, URP), proyecto aparte.

## Trampas conocidas (verificadas el 7/oct/2026)
- La app de Claude corta cada herramienta a los 60 s: el servidor espera maximo ~40 s; si Unity sigue compilando,
  devuelve "vuelve a llamar compilar()".
- Al recompilar, Windows a veces no suelta el puerto 8765 a tiempo: el puente reintenta solo cada 1 s
  (en la consola sale "[UnityMCP] Puerto 8765 ocupado... reintentando").
- Unity congela el modo Play si su ventana esta en segundo plano: el proyecto tiene Run In Background activado.
  Si no, traer Unity al frente.
- En URP, Camera.Render() NO dibuja la geometria (solo el cielo): la captura usa RenderPipeline.SubmitRenderRequest.
- La camara interna de la vista Scene solo se actualiza al repintar: captura(vista='scene') usa una camara temporal
  con la pose de la vista Scene.
- Los argumentos opcionales en null se quitan antes de ejecutar (si no, cuentan como texto vacio).
- Las teclas "mantenidas" enviadas por el control remoto de pantalla no le llegan a Unity como mantenidas
  (una pulsacion si). Para probar manejo: EntradaConductor.usarPrueba + pruebaAcelerador/pruebaVolante/pruebaFreno.
- Cambios en server.py: la app de Claude los carga hasta cerrarla por completo y volverla a abrir.
  Cambios en UnityBridge\*.cs: Unity los recompila solo (o compilar()).

## Notas
- Para logica compleja: escribir_script con un script de editor que tenga [MenuItem("Tools/...")] y luego menu("Tools/...").
- escribir_script espera la compilacion y devuelve los errores CS exactos.
- captura renderiza la camara (game) o la vista Scene a PNG en <proyecto>\Temp\UnityMCP\.
- Unidades: 1 unidad de Unity = 1 metro.
- mcp 2.x cambio la API (FastMCP -> MCPServer); el venv fija mcp<2.
- Log del servidor: salidas\server.log
