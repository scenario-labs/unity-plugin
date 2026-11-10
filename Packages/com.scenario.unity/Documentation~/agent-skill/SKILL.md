---
name: scenario-unity
description: Use when an MCP server named scenario-unity (Scenario for Unity, http://127.0.0.1:8791/mcp) is connected, to generate images, video, 3D models, PBR materials, skyboxes or audio with Scenario models, to edit, retexture, remesh, rig, animate or split 3D models, and to place the results in an open Unity scene, or to inspect and edit that scene.
---

# Scenario for Unity: agent guide

The Unity Editor serves this MCP server while the user has **Agents can use Scenario and this project** on in the Scenario window. Every tool runs inside the editor: generated files land in the project's `Assets/`, scene edits are Undo steps, and paid calls go through the same price checks and approval rules as the window.

## Start

1. `scenario_status`: account, the Scenario project that is billed, whether spending is on, your per-call limit and the session budget left, render pipeline, 2D or 3D, the active scene. If it says signed out, ask the user to sign in from the Scenario window; nothing else works until then.
2. `scene_info`: hierarchy paths, components, selection. Use those paths as `target` and `path` everywhere.

## Money

Costs are in Creative Units (CU).

- `estimate` is free and returns the exact price of a request. Call it before `generate` or `workflow_run` when the user cares about cost or the request is large (video, 3D, many images).
- `generate`, `workflow_run` and `prompt_spark` are paid. Pass `max_cu` when the user gave a budget: above it nothing is sent.
- Above the per-call limit or the session budget, the call waits up to two minutes for the user's **Approve** in Unity. Tell the user you are waiting. "Not approved" means nothing was sent; do not retry unless the user asks.
- **Unknown outcome.** If a paid call returns "The outcome is unknown", the request may have run and been charged. Never call it again for the same request. Call `jobs` with its `local_id` a minute later.
- If spending is off (Settings or `SCENARIO_NO_SPEND=1`), paid tools refuse before sending. Pricing, browsing, imports, uploads and scene tools still work.
- `upload_reference`, `import_asset`, `models_find`, `motions_find`, `model_schema` and `estimate` are free.

## Pick a model

- `models_find` with `output_type` (image, video, model3d, motion, audio, material, skybox) lists the catalog, including the team's own trained models.
- `models_find` with `recommend_for_prompt` returns Scenario's ranked picks for a prompt, with cost and speed notes. Prefer it when the user did not name a model.
- `models_find` with `sort: "newest"` lists the most recently added models first, with the date each was added. Check it before settling on a model you already know: a newer one may do better.
- `model_schema` gives the inputs: names, options, ranges, defaults, which inputs are files. Send only parameters the schema lists; unknown names are refused before pricing.

## Generate and place

`generate` prices the request, sends it once, waits, imports the files and places the result:

| output_type | Without `target` | With `target` |
|---|---|---|
| image | texture (sprite in a 2D project) on a new quad or sprite | set on its SpriteRenderer, Image, RawImage or renderer |
| material | material for the active pipeline with its maps, on a new sphere | set on the target's renderers and its children's |
| skybox | `Skybox/Panoramic` material set as the scene skybox | same |
| model3d | GLB imported as a prefab, instance at the Scene view pivot | instance placed next to the target |
| motion | animated GLB with an Animator Controller (one state per clip), instance at the pivot; it plays in Play mode | instance placed next to the target |
| audio | AudioClip on a new AudioSource | on the target's AudioSource (added if missing) |
| video | VideoClip on a new screen quad | on the target's VideoPlayer (added if missing) |

The result has `files` (Unity asset paths), `placed` (hierarchy paths), `asset_ids` (Scenario ids, reusable as inputs), `job_id`, `quoted_cu` and `charged_cu`. `place: false` imports without touching the scene. No call blocks longer than 45 s: 3D, video and long audio usually answer with `next` (still running). The job keeps going, imports and places itself; call `jobs` with `local_id` and `wait_seconds` until it is done, and never generate it again. For a batch, send each request with `wait: false`, then follow them with `jobs`.

File inputs (reference images, a start frame, a 3D model, audio) take a Scenario asset id or a Unity asset path; Unity files are uploaded once, free.

3D results, what to expect:

- One model per result: the OBJ, MTL and PBR-map copies some models add are skipped. FBX, Blender and VOX results arrive as GLB.
- A rig that returns walk and run files (Meshy Rigging, Tripo with `includeRiggedModel`) is one model whose controller holds every clip. Rigs written in centimeters are placed at 1% scale.
- Gaussian splats (TripoSplat, HY World, Marble) are saved as `.ply` or `.spz` and not placed: tell the user Unity needs a Gaussian splat renderer package.
- Meshy 7.1 defaults to a 30,000-triangle remesh at the same price; send `shouldRemesh: false` for the full mesh (about a million triangles).

## Chain steps

Feed one result into the next model through its file input:

- Image to 3D: `generate` an image, then a model3d model (for example Meshy 7.1 Image to 3D) with the image's asset id.
- Image to material or skybox: PATINA Image to Maps, HY World Image to Skybox.
- Image to video: an img2video model with the image as start frame.
- Clean up: background removal, upscale, vectorize, pixelate.
- 3D texture: retexture (Meshy 7 Retexture, Tripo 3.0 Texturing, Tencent Texture Edit, Trellis 2, YVO3D), UV unwrap (Tripo Smart UV, Meshy, Tencent).
- 3D shape: edit with a prompt (Claude Opus 5.5 3D, Astra 3D; prompt only, no reference image), retopology (Tripo Retopology v3, Hunyuan Polygen), remesh to a polycount (Meshy Remesh), refine from an image (UltraShape).
- Rig and animate: rig a character (Uthana, Meshy, Cartwheel, Tripo), rig a creature (Tripo Rigging 2.5, `rigType`), a library motion (Meshy Animation: `actionId`, for example 30 Casual Walk, 106 Confident Walk, 4 Attack, 22 FunnyDancing 01; `motions_find` searches all 656 by name or category), a motion from a prompt (Cartwheel, Uthana) or from a video (Cartwheel, Uthana).
- Parts and style: split into parts (Tripo Segmentation v2, Hunyuan 3D Part, Hitem3D Split, Rodin Bang), stylize (Tripo Stylization: LEGO, voxel, Voronoi; Hitem3D Multicolor).
- Inputs Scenario needs although the schema says optional: Cartwheel motions need `characterModel` (a 3D model) or `characterFile` (an image, much dearer); YVO3D needs `referenceImage` unless `retexture` is false. The tool says so before pricing.
- Pass a GLB asset id even to models that read FBX only (Tencent Texture Edit): the plugin sends Scenario's FBX copy of it, free.
- Video: upscale, add sound, restyle. Audio: voice change, voice isolation.

`models_find` with a `capability` (img23d, img2video, 3d23d, video23d, video2audio, audio2audio) finds alternatives.

## Workflows

`workflows_find` (`scope`: team or featured) returns ids, descriptions and inputs. `workflow_run` takes `inputs` by name, with the same money rules as `generate`; outputs import by their kind.

## Scene tools

Off when the user turns off **Agents can edit the scene**; the read tools (`scene_info`, `object_get`, `assets_find`, `console_logs`, `capture_view`) keep working.

- `object_create` (empty, primitive or prefab), `object_set` (name, active, parent, transform, tag, layer, `height` in meters, `ground` to sit it on the ground below, `collider` mesh or box), `object_delete`.
- `component_add`, `component_set`: serialized paths (`m_Intensity`) or public properties (`intensity`); vectors as `[x,y,z]`, colors as `[r,g,b,a]` or `#RRGGBB`, references as asset or hierarchy paths. `object_get` with `properties: true` lists what can be set.
- `material_assign`, `skybox_set`, `select`, `scene_save` (an untitled scene needs a path).
- `assets_find` with `label: "Scenario"` lists every generated asset in the project.

Each edit is one Undo step, so the user can revert anything you did.

## Walkable worlds

From one prompt to a world a player can walk in. The order below is the one that worked in rehearsals.

1. **Plan, then send everything at once.** Decide the layout in meters first: spawn, route, landmarks, the goal. Then send every generation with `wait: false` (heightmap, ground materials, skybox, concept images for the character and the set pieces, sounds, music) and follow them with `jobs`. Send each 3D model as soon as its concept lands, and build with what lands while the 3D jobs run.
   - **3D from a concept image, not from text.** From text alone, 3D models often keep the shape but lose the colours asked for (an olive pine, a terracotta temple, a grey scarf instead of a red one). Generate one image per piece first (the object alone, three-quarter view, plain grey background), look at it, then send it to an image-to-3D model. In a comparison on 2026-10-19, Tripo P2 Image to 3D kept the most of the concept for props and buildings, and Meshy 7.1 Image to 3D for a character to rig with Meshy; check `sort: "newest"` for anything more recent.
   - **Character concept:** full body, front view, A-pose with the arms slightly away from the body, plain background, and loose cloth (scarf, cape) hanging down. A scarf flying out to the side comes back as a rigid sheet the rig drags along.
2. **Ground.** `terrain_create` from a generated heightmap (top-down grayscale, white is high) with `layers` from generated materials (the first is the base; others by `min_height` or `min_slope`). `edge_falloff` makes an island or plateau. Measure it with `ground_height`: `points` for single spots, `from` and `to` for a profile with its steepest slope and biggest step. A route needs slopes under 50 degrees and steps under about 0.4 m. Rebuild with `flat_areas` for the spawn, building sites and both ends of a bridge, at heights read from `ground_height`.
3. **Set pieces.** `object_set` with `height` (meters), `ground: true` and `collider: "mesh"` sizes each one, sits it on the terrain and makes it solid: placed models have no collision by default, so a player walks through them otherwise. Use `collider: "box"` for a piece the player only bumps into. Typical heights: a person 1.8, a door 2.2, a house 6 to 9, a pine 8 to 14, a rock 0.5 to 2. Look at each with `capture_view` and `around`. Regenerate a piece that reads wrong (a temple that comes back as a platform on stilts) with a clearer description rather than forcing it.
4. **Bridges, stairs, platforms.** Generated ones are rarely walkable as they come: holes, a lip mid-deck, parts with no collision. Place and stretch the bridge so each end rests on a rim, then `make_walkable`: an invisible walkway on the deck with holes bridged and bumps smoothed, side rails, and ramps to the ground within 1.5 m of each end. Read `start` and `end` in the answer: `joined: "no"` with a note means that end floats or sinks; move the bridge or level the ground there, then run it again.
5. **Dressing.** `scatter` trees and rocks (`on`: the terrain) with `keep_clear` zones over the route and the buildings, a little wider than the path. Sheer terrain walls stretch their ground texture: dress them with a second `scatter` of rocks using `min_slope` (35 to 45) and `tilt` (30 to 45). `place_along` lines the route with lanterns, posts or torches: `offset` 1.5 to 2.5 m keeps them off the path, `light: {}` adds a warm point light to each.
6. **Light and mood.** `atmosphere` with a preset that matches the sky (golden_hour, midday, overcast, misty_morning, sunset, night), then single values on top (`fog_density`, `sun_elevation`, `saturation`, `bloom`). In URP it also adds a post-processing volume. Generate a skybox that matches the light.
7. **Player.** Generate the character, rig it, animate idle, walk, run and jump (`motions_find` for library ids), then `make_playable` with those results. It builds the controller, sizes the character, matches speeds to the clips and sets up the follow camera.
8. **Playtest in legs.** `play` with `action: "walk"` and waypoints (`run`, `jump`, `wait`), one leg at a time: spawn to the bridge, across it, on to the goal. It reports the points reached, falls (a drop longer than 4 m counts) and jumps. A walk longer than the call keeps going in Unity: the answer says `running`, and `play` with `action: "status"` and `wait_seconds` returns the result when it ends. Fix what fails, then walk the whole route once with no falls:
   - stuck on an object: move it off the path, or widen `keep_clear` and scatter again;
   - a fall at a bridge: `make_walkable` again and check both ends;
   - stuck on open ground: `ground_height` around that point for a step or a steep slope, then level it with `flat_areas`.
   `play` with `action: "stop"` before editing again: edits made in Play mode are lost. `record: true` (or `action: "record"`) writes PNG frames of the main camera at a fixed frame rate.
9. **Sound.** Ambience and music as looping audio sources (`generate` with `output_type: "audio"`, then `component_set` `loop: true`), footsteps through `make_playable`'s `footstep`.
10. `scene_save`.

## Check your work

- `capture_view` renders the Scene view or a camera and returns the image. Look at it after placing results or editing the scene. With `upload: true` it also returns a Scenario asset id, so a capture can be the reference for the next generation.
- `capture_view` with `around` (an object path or `[x, y, z]`) returns four three-quarter views in one image: top row from the south-west and south-east, bottom row from the north-west and north-east (+z is north). One call shows a piece from every side.
- `capture_view` with `from` and `at` renders one view from any point: 1.7 m above the ground is a player's eye.
- `console_logs` with `level: "error"` after imports or component changes.

## Good habits

- One request per intent. Ask before batches or before anything above the user's stated budget.
- Reuse `asset_ids` from earlier results instead of uploading the same file again.
- `jobs` lists recent requests from this Unity project; check it before resending anything after an error.
- Report what you made with its Unity paths and the placed objects.
