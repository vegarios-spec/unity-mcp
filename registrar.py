"""Registra el servidor 'unity' en Claude Desktop y en Claude Code (nivel usuario). Hace respaldo antes."""
import os, glob, json, shutil, subprocess, datetime
AQUI = os.path.dirname(os.path.abspath(__file__))
PY = os.path.join(AQUI, ".venv", "Scripts", "python.exe")
SRV = os.path.join(AQUI, "server.py")
entrada = {"command": PY, "args": [SRV]}
stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
cfgs = glob.glob(os.path.expandvars(r"%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json"))
cfgs += [p for p in [os.path.expandvars(r"%APPDATA%\Claude\claude_desktop_config.json")] if os.path.exists(p)]
if not cfgs:
    print("AVISO: no encontre claude_desktop_config.json de Claude Desktop")
for c in cfgs:
    shutil.copy2(c, os.path.join(AQUI, f"respaldo_claude_desktop_config_{stamp}.json"))
    d = json.load(open(c, encoding="utf-8"))
    d.setdefault("mcpServers", {})["unity"] = entrada
    tmp = c + ".tmp"
    json.dump(d, open(tmp, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    os.replace(tmp, c)
    d2 = json.load(open(c, encoding="utf-8"))
    print("Claude Desktop:", c)
    print("   unity ->", d2["mcpServers"].get("unity"))
    print("   otros servidores conservados:", [k for k in d2["mcpServers"] if k != "unity"])
cl = os.path.expandvars(r"%USERPROFILE%\.local\bin\claude.exe")
if os.path.exists(cl):
    subprocess.run([cl, "mcp", "remove", "--scope", "user", "unity"], capture_output=True, text=True)
    r = subprocess.run([cl, "mcp", "add", "--scope", "user", "unity", "--", PY, SRV], capture_output=True, text=True, timeout=120)
    print("Claude Code (mcp add):", r.returncode, r.stdout.strip(), r.stderr.strip())
else:
    print("Claude Code no esta instalado en", cl, "(se omite)")
