"""Drives the Unity plugin's agent bridge with Claude Code in print mode. Waits for the editor's
endpoint file (ScenarioDev.E2E.Agent), writes a temporary MCP config (the user's own Claude config is
not touched), asks for a short free task, saves the transcript, then writes the stop file.

python3 e2e_agent.py <project Library/ScenarioDevLogs/e2e dir> <out dir>
"""
import json, pathlib, subprocess, sys, time, tempfile

e2e, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)
endpoint = e2e / "agent-endpoint.json"
t0 = time.time()
while not endpoint.exists() and time.time() - t0 < 300:
    time.sleep(1)
info = json.loads(endpoint.read_text())
if not info.get("running"):
    sys.exit("bridge not running: " + info.get("error", ""))

config = {"mcpServers": {"scenario-unity": {"type": "http", "url": info["url"], "headers": {"Authorization": info["authorization"]}}}}
prompt = """You are connected to a Unity editor through the scenario-unity MCP server. Do these steps with its tools only, no paid tool (never call generate, workflow_run or prompt_spark):
1. scenario_status: report who is signed in and the render pipeline.
2. motions_find for "sword attack": list the first three motions with their ids.
3. models_find with output_type model3d: name three models.
4. estimate the price of model_meshy-7-1-txt23d with the prompt "a stylized wooden barrel, game prop".
5. object_create a cube named "Agent Test Cube" at position (0, 0.5, 0), then scene_info to confirm it is there.
6. capture_view of the Scene view.
Finish with a five-line summary of what each step returned."""
try:
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
        json.dump(config, f)
        cfg = f.name
    cmd = ["claude", "-p", prompt, "--mcp-config", cfg, "--strict-mcp-config", "--model", "sonnet",
           "--allowedTools", "mcp__scenario-unity", "--output-format", "stream-json", "--verbose"]
    t1 = time.time()
    run = subprocess.run(cmd, capture_output=True, text=True, timeout=600, cwd=tempfile.gettempdir())
    (out / "agent-transcript.jsonl").write_text(run.stdout)
    (out / "agent-stderr.txt").write_text(run.stderr)
    calls, result = [], ""
    for line in run.stdout.splitlines():
        try:
            m = json.loads(line)
        except ValueError:
            continue
        if m.get("type") == "assistant":
            for c in m["message"].get("content", []):
                if c.get("type") == "tool_use":
                    calls.append(c["name"].replace("mcp__scenario-unity__", ""))
        if m.get("type") == "user":
            # The Scene view capture comes back as an image in a tool result: keep it for the report.
            for c in m.get("message", {}).get("content", []):
                parts = c.get("content", []) if isinstance(c, dict) and c.get("type") == "tool_result" else []
                for part in parts if isinstance(parts, list) else []:
                    data = part.get("source", {}).get("data") if part.get("type") == "image" else None
                    if data:
                        import base64
                        (out / "capture.png").write_bytes(base64.b64decode(data))
        if m.get("type") == "result":
            result = m.get("result", "")
    summary = f"exit {run.returncode} in {round(time.time() - t1)} s\ntools called: {', '.join(calls)}\n\n{result}"
    (out / "agent-summary.txt").write_text(summary)
    print(summary)
finally:
    (e2e / "agent-stop").write_text("stop")
