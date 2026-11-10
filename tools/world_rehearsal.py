"""Has Claude Code build a walkable world in Unity from one prompt, through the plugin's agent bridge.

Waits for the editor's endpoint file (ScenarioDev.E2E.Agent, started with SCENARIO_E2E_SCENE and a
long SCENARIO_E2E_MINUTES), writes a temporary MCP config (the user's own Claude config is not
touched), sends the prompt, and records the run for review and for editing a film of it:
transcript.jsonl (raw stream), timeline.json (every tool call with its time, arguments and result
summary), captures/NN.png (the images the agent looked at), summary.txt. Writes the stop file at the end.

PAID: the agent generates with Scenario models on the signed-in project.

python3 world_rehearsal.py <Library/ScenarioDevLogs/e2e> <out dir> <prompt file> [model]
SCENARIO_REHEARSAL_SKILL=<path to SKILL.md> adds the agent skill to the system prompt, as a user who
installed it would have it.
"""
import base64
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import time

e2e, out, prompt_file = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3])
model = sys.argv[4] if len(sys.argv) > 4 else "opus"
(out / "captures").mkdir(parents=True, exist_ok=True)
endpoint = e2e / "agent-endpoint.json"
t0 = time.time()
# The editor starts just before this script: an endpoint file older than that is from an earlier run.
while not (endpoint.exists() and endpoint.stat().st_mtime > t0 - 60) and time.time() - t0 < 600:
    time.sleep(1)
info = json.loads(endpoint.read_text())
if not info.get("running"):
    (e2e / "agent-stop").write_text("stop")
    sys.exit("bridge not running: " + info.get("error", ""))

prompt = prompt_file.read_text().strip()
system = """You are connected to an open Unity project through the scenario-unity MCP server (Scenario for Unity). \
The user wants something they can play when you are done. Work end to end without asking questions. \
Start with scenario_status and scene_info. Pick models with models_find (recommend_for_prompt) and price each paid batch with estimate before generating. \
Size every model in meters and put it on the ground; look at your work with capture_view and fix what looks wrong. \
Make the character playable, playtest it with play (action walk) along a route that crosses the world, fix what fails, then stop Play mode. \
Save the scene with scene_save. The user approved spending on this build: generate what the world needs, one generation per asset unless a result is unusable. \
Finish with a short summary of what you built and how to play it."""
skill_path = os.environ.get("SCENARIO_REHEARSAL_SKILL", "")
if skill_path:
    skill = re.sub(r"\A---\n.*?\n---\n", "", pathlib.Path(skill_path).read_text(), flags=re.S).strip()
    system += "\n\nThe Scenario for Unity skill, installed by the user:\n\n" + skill

config = {"mcpServers": {"scenario-unity": {"type": "http", "url": info["url"], "headers": {"Authorization": info["authorization"]}}}}
timeline, calls, captures, result = [], {}, 0, ""
try:
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
        json.dump(config, f)
        cfg = f.name
    # The agent gets the Unity bridge and nothing else: no shell or file tools (it would read the project
    # instead of using the plugin), no user settings or hooks, and any other tool is refused, not asked.
    cmd = ["claude", "-p", prompt, "--append-system-prompt", system, "--mcp-config", cfg, "--strict-mcp-config",
           "--tools", "ToolSearch", "--allowedTools", "mcp__scenario-unity", "--permission-mode", "dontAsk",
           "--setting-sources", "local", "--model", model, "--output-format", "stream-json", "--verbose"]
    start = time.time()
    with open(out / "transcript.jsonl", "w") as raw, \
            subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, cwd=tempfile.gettempdir()) as run:
        for line in run.stdout:
            now = round(time.time() - start, 1)
            raw.write(json.dumps({"t": now, "line": line.rstrip("\n")}) + "\n")
            raw.flush()
            try:
                m = json.loads(line)
            except ValueError:
                continue
            if m.get("type") == "assistant":
                for c in m["message"].get("content", []):
                    if c.get("type") == "tool_use":
                        entry = {"t": now, "tool": c["name"].replace("mcp__scenario-unity__", ""), "input": c.get("input", {})}
                        calls[c["id"]] = entry
                        timeline.append(entry)
                        print(f"{now:8.1f}s  {entry['tool']}", flush=True)
                    elif c.get("type") == "text" and c.get("text", "").strip():
                        timeline.append({"t": now, "say": c["text"].strip()})
            elif m.get("type") == "user":
                for c in m.get("message", {}).get("content", []):
                    if not (isinstance(c, dict) and c.get("type") == "tool_result"):
                        continue
                    entry = calls.get(c.get("tool_use_id"), {})
                    entry["done"] = now
                    entry["error"] = bool(c.get("is_error"))
                    parts = c.get("content", [])
                    texts = [p.get("text", "") for p in parts if isinstance(p, dict) and p.get("type") == "text"] if isinstance(parts, list) else [str(parts)]
                    entry["result"] = "\n".join(texts)[:1500]
                    for p in parts if isinstance(parts, list) else []:
                        data = p.get("source", {}).get("data") if isinstance(p, dict) and p.get("type") == "image" else None
                        if data:
                            captures += 1
                            name = f"{captures:02d}.png"
                            (out / "captures" / name).write_bytes(base64.b64decode(data))
                            entry["capture"] = name
            elif m.get("type") == "result":
                result = m.get("result", "")
        (out / "stderr.txt").write_text(run.stderr.read())
    (out / "timeline.json").write_text(json.dumps(timeline, indent=1))
    tools = [e["tool"] for e in timeline if "tool" in e]
    errors = [e for e in timeline if e.get("error")]
    summary = (f"exit {run.returncode} in {round(time.time() - start)} s, {len(tools)} tool calls, {len(errors)} errors, {captures} captures\n"
               f"tools: {', '.join(sorted(set(tools)))}\n\n{result}")
    (out / "summary.txt").write_text(summary)
    print(summary)
finally:
    (e2e / "agent-stop").write_text("stop")
