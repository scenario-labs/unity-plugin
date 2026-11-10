# Scenario for Unity: user guide

Version 0.1.0 <!-- x-release-please-version -->. Unity 6.0 or later.

## 1. Install and open

1. **Window > Package Management > Package Manager**, **+**, **Install package from git URL**:
   `https://github.com/scenario-labs/unity-plugin.git?path=/Packages/com.scenario.unity`
2. **Window > Scenario**, or Alt+Shift+S (rebind it in **Edit > Shortcuts**, under Scenario).

The window docks anywhere and works from 320 px wide. Above 560 px the navigation rail shows labels and some actions show their text.

Keyboard: Ctrl+K (Cmd+K on macOS) opens the command palette; Ctrl+Enter (Cmd+Enter) generates from the prompt box; Esc closes an overlay.

The command palette lists every action, model, recent job, and the steps and scene actions for the selection (the same as the Scene view panel, section 4). Type a sentence and pick **Ask Claude: …** to send it to Claude Code (section 9). With a 3D object selected, type a number ("height 2.5") for **Set its height to 2.5 m**.

## 2. Sign in

**Sign in with Scenario** opens your browser on Scenario's sign-in page. Approve, go back to Unity, then pick a team and a project in the window. Everything you generate is billed to that project. Switch it later from the header.

**Use an API key instead** shows two fields: the key and the secret from the Scenario web app. You can paste `key:secret` into the first field. An API key belongs to one project, so there is no picker.

Where credentials live:

- Your sign-in (refresh token) or API secret: the macOS Keychain or the Windows Credential Manager, entry "Scenario Unity Plugin". On other systems, `Scenario/Unity/account.json` in your user config folder, readable by you only.
- Never in `Assets/`, `ProjectSettings/` or `Packages/`, so nothing reaches git.
- `SCENARIO_API_KEY` and `SCENARIO_API_SECRET` in the environment take precedence and are not stored.

Sign out from the account menu in the header or in **Settings**.

## 3. Create

### Output type

Pick **Image, Video, 3D, Motion, Audio, Material, Skybox** or **All models**. A type sets the model filter, the quick picks and how the result lands in Unity. It never limits you: **All models** reaches the whole catalog.

**Motion** gathers the animation models: a motion from a prompt (Cartwheel, Uthana, Meshy), from a video (Cartwheel, Uthana), or from Meshy's motion library, applied to your character.

### Model

The model card shows the current model. Quick picks per type sit under it.

- **Suggest** sends your prompt to Scenario's recommender and shows its top models for the type. One click applies one.
- **Change** opens the model browser: search by name, filter chips (Favorites, Team models, Show legacy), a star to pin a model to your favorites. Your team's own trained models appear here.

### Prompt and inputs

Type a prompt. **Improve** (next to the Prompt label) runs Prompt Spark, which rewrites the prompt for the chosen model; the button shows its price first, and Undo on the notice brings your words back.

Models that take files show one slot per input (reference images, a start frame, a 3D model, a video, a voice). Fill a slot by dragging from the Project window or the Hierarchy, or from its **+** menu: **Selection**, **Capture the Scene view**, **Capture the main camera**, **Browse files…** or **Scenario asset…**. Unity files are uploaded once when needed (uploads are free).

A 3D input that a model only reads as FBX (Tencent Texture Edit) gets an FBX copy of your model, made by Scenario for free. Some inputs are optional in the schema but needed in practice; the button then says which, for example "Add a character: a 3D model, or an image of one" for Cartwheel's motions.

### Settings

The form comes from the model's live schema. The essentials (required fields, anything that changes the price, aspect ratio, count, duration, polycount, rig type) are shown first; **More settings** opens the rest. Fields that change the price are marked.

Meshy Animation's motion is picked by name from its library of about 650 motions (walks, runs, fights, dances, daily actions), with a search and category chips. Meshy 7.1 generates at 30,000 triangles by default, for the same price as its full detail of about a million; turn off **Should remesh** in **More settings** for the full mesh.

### Price and Generate

The footer button reads `Generate · 11 CU`: a free price check of exactly this request. It updates when you change anything. It is disabled while the price is being checked, when the form is incomplete, or when spending is off. Above your confirmation threshold (1,000 CU by default) Unity asks before sending.

**Place** decides whether the result is put in the scene or only imported. The line above the button says where it goes and what it will land on, for example "Places on Player (SpriteRenderer)".

### Results tray

Each generation shows as a card with its progress, then a thumbnail. Card actions:

- **Place in the scene** (pin icon).
- **Next** (arrow icon): the typed steps for this result, see section 4.
- **More**: Show in Project, Use as input, Use settings (puts the model and settings back in the form, sends nothing), Import again, Open on Scenario, Copy asset id, Cancel, Dismiss.

Drag the bar above the tray to resize it.

## 4. Next steps

Steps turn an asset into the next one. They open Create with the right model and the asset already attached, so you see the price before anything is sent.

| Asset | Steps (default model; the other models of the step are one click away in the model card) |
|---|---|
| Image | Edit with a prompt (GPT Image 2.5 Sunburst), Remove background (Birefnet), Upscale (Clarity Pro), Make a 3D model (Meshy 7.1 Image to 3D; also Hunyuan 3D 3.1, Tripo P2, Rodin 2.5, Hitem3D, Trellis 2, Claude Opus 5.5 3D, Astra 3D, Voxel Crafter), Make a Gaussian splat (TripoSplat; also HY World, Marble), Animate to video (Minimax H3), Make a PBR material (PATINA Image to Maps), Make a skybox (HY World Image to Skybox), Vectorize, Pixelate |
| 3D model, Texture | Retexture (Meshy 7 Retexture; also Tripo 3.0 Texturing, Tencent Texture Edit, Trellis 2, YVO3D), UV unwrap (Tripo Smart UV; also Meshy, Tencent) |
| 3D model, Shape | Edit with a prompt (Claude Opus 5.5 3D; also Astra 3D), Retopology (Tripo Retopology v3; also Hunyuan Polygen), Remesh to a polycount (Meshy Remesh; also Hunyuan Polygen), Refine the shape from an image (UltraShape) |
| 3D model, Rig and animate | Rig a character (Uthana; also Meshy, Cartwheel, Tripo), Rig a creature (Tripo Rigging 2.5), Animate from the motion library (Meshy Animation; also Tripo), Animate from a prompt (Cartwheel; also Uthana, Astra), Animate from a video (Cartwheel; also Uthana) |
| 3D model, Parts and style | Split into parts (Tripo Segmentation v2; also Hunyuan 3D Part, Hitem3D Split, Rodin Bang, Tripo Segmentation v1), Stylize: LEGO, voxel, Voronoi (Tripo Stylization; also Hitem3D Multicolor) |
| Video | Upscale (Kandinsky 6.0 VSR), Add sound (Mirelo SFX 2.0 Video to Audio), Restyle (Seedance 2.5; also Minimax H3 3D to video), Animate a character from this video (Cartwheel; also Uthana) |
| Audio | Change the voice (ElevenLabs Voice Changer), Isolate the voice (ElevenLabs Voice Isolator) |

The 3D steps are grouped in the **Next** menu (Texture, Shape, Rig and animate, Parts and style). Each step shows a one-line hint under the model card, for example the input size a model accepts.

Where to find them: the Scenario panel in the Scene view (below), the result card's **Next**, a right-click in the Project window or the Hierarchy (**Scenario > Next Step…**), the **Next** button in the Inspector header of a generated asset, the command palette and the Library.

### The Scenario panel in the Scene view

The Scene view has a **Scenario** overlay, docked on the right at first. Drag it to dock it elsewhere or float it, collapse it to an icon, or show and hide it from the Scene view's Overlays menu. The command palette's **Show the Scenario panel in the Scene view** brings it back. It follows the selection:

| Selected | The panel offers |
|---|---|
| A 3D object in the scene | Its steps, grouped as above (Texture, Shape, Rig and animate, Parts and style), then **Make playable**, **Put on the ground**, **Height (m)** with **Set**, and **Make walkable** |
| A terrain | **Scatter here**: a prefab or model, a count, and the side of the area in meters; **Light and mood** |
| An image, a texture or a sprite, in the Project window or the scene | The image steps |
| A 3D model, video or sound asset, or an AudioSource or VideoPlayer with a clip | Its steps |
| Nothing | **Generate**: Image, 3D model, Material, Skybox, Sound; **Light and mood** |

Steps open Create with the model and the asset attached; nothing is sent until you press **Generate**, which shows the price. A placed Scenario model is sent as its Scenario asset, with no upload. Selecting a part of a placed model, or a mesh inside a character, stands for the whole model. Any other object is exported as GLB when a step needs it.

Scene actions run at once, each one Undo step:

- **Make playable** turns a rigged character with a walk into a third-person player: a CharacterController, an Animator Controller blending idle, walk and run, a jump, and a follow camera. Press Play, then WASD to move, Shift to run, Space to jump. When the character is not ready, one line says what is missing, for example "No skeleton: rig it first (Rig and animate)" or "No walk yet: animate it first".
- **Put on the ground** sits the object on the collider below it.
- **Set** scales the object evenly to the height you type, in meters. Its base stays where it was.
- **Make walkable** is for a bridge, stairs or a platform: an invisible walkway on its deck (holes filled, bumps smoothed), side rails, and short ramps to the ground beyond each end. The model's own colliders are turned off so their bumps cannot trip the player. Run it again after moving the object.
- **Light and mood** applies a preset to the open scene (Golden hour, Midday, Overcast, Misty morning, Sunset, Night): the sun's angle, color and strength, fog, ambient light and, in URP, a post-processing volume (tone mapping, exposure, contrast, saturation, warmth, bloom, vignette). The command palette lists the presets too.
- **Scatter** places copies around the Scene view's center (the terrain's center when you look elsewhere), sat on the ground, with colliders. Each run gives a new arrangement; one Undo removes it.

The **Ask** field at the top of the panel sends a request to Claude Code (section 9).

## 5. Where results go

- Files: `Assets/Scenario/<Type>/<yyyy-MM-dd>/<prompt>-<id>[-<map>].<ext>`. Change the root folder and the daily subfolders in **Settings > Output**.
- Images import as textures, or as sprites in 2D projects (or with **Settings > Import > Images as sprites**). Normal maps import as normal maps, roughness, metallic, occlusion and height maps as linear data.
- 3D models are GLB files imported by glTFast as prefabs. Placed instances sit on the ground at the Scene view pivot, or next to the selected object, with a MeshCollider if **Add a collider** is on.
- A 3D result is imported as one model. The copies some models add (OBJ, MTL, PBR maps already inside the GLB) are not downloaded. Results Scenario stores as FBX, Blender or VOX files are converted to GLB on download, skins and animations included.
- Animated results (rigging with a walk, motions, library animations) get an Animator Controller next to the file, one state per clip, a real motion as the default state. The placed model plays it in Play mode. A rig that comes with walk and run files is one model with all the clips; the bare-skeleton copies are skipped.
- A rig written in centimeters (a character tens of meters tall) is placed at 1% scale, and the notification says so. Meshes that come without a material get glTFast's default one, which keeps vertex colors (Hitem3D Multicolor, Hunyuan 3D Part).
- Gaussian splats (TripoSplat, HY World, Marble) are saved as `.ply` or `.spz` files and not placed: Unity needs a Gaussian splat renderer package to show them.
- Materials are built for the active render pipeline: base color, normal, height, occlusion, and metallic with smoothness packed into the pipeline's map.
- Skyboxes are `Skybox/Panoramic` materials set in the scene's lighting. HDRP draws its sky from a Volume: there the material is created and the window says what to set.
- Audio lands on the selected AudioSource or a new one (spatial in 3D scenes). Video lands on the selected VideoPlayer or a new screen quad.

Every placement is one Undo step.

Each generated asset records its model, prompt, job, settings and cost in its `.meta` file, so the record follows renames and moves and is versioned with the asset. It carries the `Scenario` label, and its Inspector shows a Scenario header with **Use settings**, **Next** and **Open on Scenario**.

Right-click entries:

- Project window: **Scenario > Generate Here…** on a folder (results go there), **Next Step…**, **Use as Input**, **Copy Scenario Asset Id**; **Create > Scenario > Image, 3D Model, Material, Skybox, Sound, Video**.
- Hierarchy: **Scenario > Next Step…** (the steps for the object: 3D steps for a model, image steps for a sprite), **Generate Material for This, Generate Sprite for This, Generate Sound for This, Retexture, Use as Reference**, **Make Playable** on a rigged character with a walk, and **Make Walkable** on a bridge or platform.

## 6. Library

**Scenario** lists the assets of your Scenario project, newest first, filtered by kind (images, videos, 3D, audio) or by a search. Select one for **Import**, **Import and place**, **Use as input**, **Next** or **Open on Scenario**.

**In this project** lists every asset of this Unity project that came from Scenario, so a lead can audit what was generated.

## 7. Jobs

The job list is kept in `Library/Scenario/jobs.json`, on your machine, and survives restarts and recompiles.

| State | Meaning |
|---|---|
| Sending | written to the list, being sent |
| Queued, Running | accepted by Scenario |
| Done | finished and imported (To import: finished, import pending) |
| Failed | Scenario reported an error; the message is on the row |
| Blocked | stopped by content moderation; never resent |
| Canceling, Canceled | canceled by you |
| Not sent | refused before sending (for example, no fresh price) |
| Unknown: check Jobs | the connection dropped while sending, so it may or may not have started |

An **Unknown** job blocks the same request until it is matched against Scenario's job list (automatic, or **Look for it on Scenario**) or dismissed. This is what keeps a network error from charging you twice.

Row actions: Cancel, Import, Dismiss, and under **More**: Show in Project, Use settings, Import again, Open on Scenario, Copy job id, Copy asset id, Hide from the list.

## 8. Workflows

**Your team** lists your team's Scenario workflows, **Featured** the public ones. Open one to see its description and inputs as a form, with its price. **Run** sends it like any generation; its outputs import by their kind. **Open on Scenario** shows it in the web app.

## 9. Agent

Turn on **Agents can use Scenario and this project**. Unity then serves an MCP server on `http://127.0.0.1:8791/mcp` (change the port under **Permissions** if another program uses it). It answers only on this machine and only with the bearer token shown in the view. **Regenerate** replaces the token; connected clients then need the new setup.

For each client, **Copy setup** copies what to paste:

- Claude Code: a `claude mcp add` command (**Configure** runs it for this project).
- Codex: a `~/.codex/config.toml` block.
- Cursor: `.cursor/mcp.json`. VS Code: `.vscode/mcp.json`.
- Any other client: the URL and the header.

Permissions:

- **Approve paid calls up to (CU)**: 80 by default, per call. 0 asks every time.
- **Session budget (CU)**: 1,200 by default, since Unity started.
- Anything above either waits for **Approve** or **Deny** in the Agent view, and is refused after two minutes without an answer.
- **Agents can edit the scene** on or off.

The activity log lists every call: time, client, tool, a short summary, approvals and errors.

The agent can also build walkable worlds: a terrain from a generated heightmap with painted ground, ground measurements along a route, models scattered and grounded or lined up along a path (with lights), generated bridges and platforms made walkable (an invisible walkway, rails and ramps to the ground), the light and mood from a preset (sun, fog, ambient light and, in URP, a post-processing volume), four-angle captures to check the result, a rigged character turned into a player you control with WASD, Shift, Space and the mouse, and playtests in Play mode. The player uses components you can add by hand too, under **Add Component > Scenario**: Scenario Player, Scenario Follow Camera, Scenario Autopilot and Scenario Frame Recorder.

The agent sees the same catalog, prices and import rules as you. A skill file for agents ships with the package in `Documentation~/agent-skill/SKILL.md`.

### Ask Claude from Unity

Type a request in the **Ask** field, at the top of the Agent view or of the Scenario panel in the Scene view, and press Enter: "make the bridge rustier", "add 20 pines around the temple", "rig and animate the selected character". The plugin runs your own Claude Code on this project through the bridge:

- It turns the bridge on if it is off, and says so.
- The request goes with what is selected: hierarchy paths for scene objects, asset paths for assets.
- Claude Code runs in print mode from a temporary folder, with only this editor's tools (no shell, no file edits), allowed without a prompt. Its MCP config, with the bridge token, is a file in your temporary folder, rewritten at each request.
- Each tool call shows as one line (the tool and what it is about), then Claude's text and the result. **Stop** ends the request; calls already sent finish.
- Paid calls follow the permissions above. A call above the limits waits for **Approve** or **Deny**, shown under the request in the panel and in the Agent view.
- The log stays until you **Clear** it, across script reloads. A script reload stops a running request.

Claude Code must be installed and signed in on this machine (`npm install -g @anthropic-ai/claude-code`, or the native installer from claude.com, then run `claude` once in a terminal to sign in). The plugin finds `claude` on your PATH and in the usual install folders (`~/.local/bin`, `~/.claude/local`, `/opt/homebrew/bin`, `/usr/local/bin`), and gives it those folders on its PATH, since editors started from the Dock or the Hub get a minimal one. To use another copy, set **Settings > Agent > Claude Code path**. Without Claude Code, every button in the panel and the window works as before.

## 10. Settings

- **Output**: the root folder (inside Assets) and daily subfolders.
- **Import**: images as sprites, sprite pixels per unit, image filter, colliders on placed 3D models.
- **Placement**: frame placed objects in the Scene view.
- **Spending**: ask before requests above N CU; turn spending off for this editor. `SCENARIO_NO_SPEND=1` in the environment does the same for every project.
- **Agent**: a shortcut to the Agent view, where the port and permissions are, and **Claude Code path**, the `claude` command Ask runs (empty: found automatically; the line under it says which one is used).
- **Diagnostics**: a free check of sign-in, project access, the model catalog and one price check. **Copy the report** for a support request.

Project settings (output, import, placement, spending threshold) are saved in `ProjectSettings/` and shared with your team. Your account, workspace, favorites, spending switch and agent settings stay on your machine.

## 11. Editor scripts and CI

```csharp
using Newtonsoft.Json.Linq;
using Scenario;

if (!ScenarioApi.IsReady) return; // signed in, or SCENARIO_API_KEY and SCENARIO_API_SECRET set
var parameters = new JObject { ["prompt"] = "A rune stone icon, flat colors" };
var cu = await ScenarioApi.Estimate("model_openai-gpt-image-2-5-flare", parameters);
var result = await ScenarioApi.Generate("model_openai-gpt-image-2-5-flare", parameters, maxCu: 20);
// result.Files: Unity asset paths. result.JobId, result.ChargedCu, result.State.
```

- `Estimate` is free. `Generate` prices first, refuses anything above `maxCu` without sending, sends once, waits, imports and returns the files.
- `Upload` returns a Scenario asset id for a project asset (free). `Import` brings Scenario assets into the project.
- Errors throw `ScenarioException`; `NotSent` says nothing reached Scenario, `Ambiguous` says the outcome is unknown (check Jobs, do not resend).
- In batch mode, set `SCENARIO_UNITY_SERVICE=1` so jobs are polled and imported.

## 12. Troubleshooting

- **"Price unavailable"**: the form has a value the model refuses, or Scenario did not answer. The message under the button says which. Run **Settings > Diagnostics**.
- **Generate stays disabled**: the price is being checked, a required input is empty, an upload is in progress, or spending is off. The button says which.
- **"Needs an input" on a motion model**: Cartwheel animates a character you give it. Add a 3D model (rigged or not), or an image it turns into a character first (dearer).
- **An animated model does not move**: animations play in Play mode. Select the placed model: its Animator has the controller made at import, with one state per clip.
- **A job is Unknown**: wait a minute, then **Look for it on Scenario** on its card. Do not generate the same request again until it is resolved.
- **The model did not land on my object**: the line above Generate says where results go. Select the object before generating, or use **Place** on the card afterwards.
- **The agent cannot connect**: check that the Agent toggle is on and the dot is green, that the client uses the current token (after **Regenerate**, copy the setup again), and that the port is free (change it in Settings if another program uses it). The bridge restarts for a few seconds after each script compile.
- **Nothing shows in the window after a compile**: close and reopen the window. Jobs and results are kept in the job list.
- **Ask says Claude Code was not found**: install it, or set its path in **Settings > Agent**. "Not signed in": run `claude` once in a terminal and sign in.
- **Ask says Claude could not reach the agent bridge**: check that the Agent toggle is on and that the port is free, then ask again.
- **The Scenario panel is gone from the Scene view**: open the Scene view's Overlays menu and tick **Scenario**, or use **Show the Scenario panel in the Scene view** in the command palette.
