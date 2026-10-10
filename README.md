# unity-mcp

An MCP server that lets Claude control the **Unity Editor**, with no Unity AI, subscription or Unity Cloud
account. It is two pieces: a Python MCP server, and a small C# editor package that runs a local HTTP bridge inside Unity.

I built it for a course project, a low-cost truck-driver training simulator in Unity 6 driven by Nintendo Switch
Joy-Cons. I wanted Claude to build scenes, write scripts and read the controllers without depending on a paid service.

*Original Spanish documentation: [README.es.md](README.es.md)*

## How it works

```
Claude  --MCP (stdio)-->  server.py (Python, FastMCP)  --HTTP 127.0.0.1:8765-->  UnityBridge (C# editor package)
```

- `UnityBridge/` is a Unity package (`com.elias.unity-mcp-bridge`). Add it to a project's `Packages/manifest.json`:
  `"com.elias.unity-mcp-bridge": "file:<path-to>/unity-mcp/UnityBridge"`
- When the project opens, the Console shows `[UnityMCP] Puente activo en http://127.0.0.1:8765/`.
- Editor menu: **Tools > Unity MCP > Reiniciar puente / Estado del puente**.
- The bridge only listens on `127.0.0.1` and is not reachable from other machines.

## Tools (26)

Scene & objects: `estado`, `jerarquia`, `ver_objeto`, `crear_objeto`, `borrar_objeto`, `mover_objeto`, `emparentar`,
`activar_objeto`, `agregar_componente`, `quitar_componente`, `cambiar_propiedades`, `color_objeto`, `seleccionar`,
`crear_prefab`, `escena`

Editor: `play`, `menu`, `consola`, `captura` (renders the game camera or Scene view to PNG), `vista_escena`

Code: `escribir_script` (writes a C# script, waits for compilation and returns the exact CS errors), `compilar`,
`leer_archivo`, `listar_archivos`

Input: `controles_conectados`, `leer_control` (live snapshot of connected gamepads, e.g. Joy-Cons)

## Install

Requirements: Windows, Unity 6 (tested on 6.3 LTS, URP), Python 3 (built with 3.14), Claude Desktop and/or Claude Code.

1. Double-click `instalar.bat` (creates `.venv`, installs `mcp<2`, checks the server loads, registers `unity` in
   Claude Desktop and Claude Code).
2. Add the `UnityBridge` package to your project (see above).
3. Fully quit and reopen Claude Desktop.

## Known pitfalls (verified)

- The Claude app times out each tool call at 60 s, so the server waits at most ~40 s; if Unity is still compiling it
  answers "call `compilar()` again".
- After a recompile, Windows sometimes does not release port 8765 in time; the bridge retries every second.
- Unity freezes Play mode when its window is in the background; enable **Run In Background**.
- In URP, `Camera.Render()` draws only the skybox; captures use `RenderPipeline.SubmitRenderRequest`.
- The Scene-view camera only updates on repaint, so `captura(vista='scene')` uses a temporary camera with the Scene-view pose.
- Optional arguments sent as `null` are stripped before execution (otherwise they arrive as empty strings).
- `mcp` 2.x changed the API (FastMCP → MCPServer); the venv pins `mcp<2`.

## License

MIT, see [LICENSE](LICENSE).
