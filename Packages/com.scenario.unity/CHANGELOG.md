# Changelog

## [0.2.0](https://github.com/scenario-labs/unity-plugin/compare/unity-plugin-v0.1.0...unity-plugin-v0.2.0) (2026-10-19)


### Features

* **agent:** agents auto-approve up to 80 CU per call and 1,200 CU per session by default (was 20 and 300) ([981cece](https://github.com/scenario-labs/unity-plugin/commit/981cece1585e7a826531e18e35bd79a18460fa96))
* **agent:** ask the local Claude Code from Unity, through the agent bridge ([679f254](https://github.com/scenario-labs/unity-plugin/commit/679f2544f6da6a2406b89806ff50ea629da471c2))
* **agent:** footsteps timed to the player's steps ([de3c0c7](https://github.com/scenario-labs/unity-plugin/commit/de3c0c77c63a7f385ef52842a43bf913ac81b243))
* **agent:** object_set gives set pieces collision, scatter dresses cliff faces with min_slope ([0f749d6](https://github.com/scenario-labs/unity-plugin/commit/0f749d62235a3bf2aca209d97ed4cc0167e24e30))
* **agent:** walkable worlds: terrain, scatter, playable character, playtests ([a4a48e0](https://github.com/scenario-labs/unity-plugin/commit/a4a48e005cf742d9f7780bd7596acd72bef1fb72))
* **agent:** world tools for ground checks, walkable bridges, props along a path, atmosphere and four-angle views ([44989ab](https://github.com/scenario-labs/unity-plugin/commit/44989abb608f8abd3fb33c2c2bac09d07a1a5e26))
* **import:** scene actions for the selection, Hierarchy Next Step and Make Playable ([519744b](https://github.com/scenario-labs/unity-plugin/commit/519744b1ac6c80c5fedf1be32b1e688bf0fbac65))
* **ui:** ask before requests above 1,000 CU by default (was 100) ([250713a](https://github.com/scenario-labs/unity-plugin/commit/250713a3d3fa9a4bdcc6ab45e1634e131c413534))
* **ui:** Make walkable and Light and mood in the Scenario panel, palette and GameObject menu ([c9ccb6a](https://github.com/scenario-labs/unity-plugin/commit/c9ccb6a23d9d2e41a616cb2c6eb794e058ae9ad6))
* **ui:** Scenario panel in the Scene view, Ask box in Agent, palette actions, Claude Code path setting ([1527be9](https://github.com/scenario-labs/unity-plugin/commit/1527be94275272b74820e5069e33d61d4729b8b9))


### Bug Fixes

* **agent:** answer every call within 45 s, playtests that keep running, steps that move at any frame rate ([7ce9ac4](https://github.com/scenario-labs/unity-plugin/commit/7ce9ac449cbc9db9bb38050d7035903485207931))
* **auth:** browser sign-in opens the browser from the main thread, and Cancel always resets ([0931c92](https://github.com/scenario-labs/unity-plugin/commit/0931c92d40243f90195d0313a4191aa4ad60a423))
* **core:** settings load back after a restart ([c8227ca](https://github.com/scenario-labs/unity-plugin/commit/c8227caaa914d5d9284219a518f0211b862b81b9))
* **ui:** show Improve (Prompt Spark) for every model whose prompt allows it ([3f4f1b6](https://github.com/scenario-labs/unity-plugin/commit/3f4f1b623cef5d987d2bb0dee993229a287d5d9f))


### Documentation

* **agent:** skill builds 3D from a concept image, with the picks from the model shoot-out ([0162bb4](https://github.com/scenario-labs/unity-plugin/commit/0162bb40293cc753db61b4cbc261c964f351c9ac))
* **tests:** ledger for the 3D model shoot-out ([70777c5](https://github.com/scenario-labs/unity-plugin/commit/70777c53dab83f85e9150e5a6b1b91c9242c2f4d))


### Miscellaneous Chores

* **tools:** rehearsal agent limited to the Unity bridge, rehearsal 1 in the ledger ([aa36b51](https://github.com/scenario-labs/unity-plugin/commit/aa36b516789ee2a8fc7e9ad256d60fff558038ab))
* **tools:** rehearsal harness takes the agent skill and a bridge port, and stops the editor if the bridge fails ([d56d816](https://github.com/scenario-labs/unity-plugin/commit/d56d816a87d0589f31293e78e8719abe90b624a4))


### Continuous Integration

* **release:** changelog starts after 0.1.0, version badge follows each release ([f616cf0](https://github.com/scenario-labs/unity-plugin/commit/f616cf016a69ef269a5acf51f17d08810d03ab1e))
* **release:** one version per badge line, so release-please updates both ([74d43cc](https://github.com/scenario-labs/unity-plugin/commit/74d43ccc438ca2ae800e752beff0cf4d9f32228e))
* **release:** version badge in a URL form release-please updates whole ([2eff15f](https://github.com/scenario-labs/unity-plugin/commit/2eff15fc07821f3d03f8384b1a84e4bb40e21b9e))

## 0.1.0 (2026-10-19)

First release.

### Features

- Scenario window (Window > Scenario, Alt+Shift+S): Create, Library, Jobs, Workflows, Agent and Settings, a command palette (Ctrl/Cmd+K), toasts, light and dark editor themes, from 320 px wide.
- Sign in with Scenario in the browser (OAuth with PKCE) and a team and project picker, or an API key and secret, or `SCENARIO_API_KEY` and `SCENARIO_API_SECRET`. Secrets stay in the OS keychain.
- Every model in the catalog and the team's own models, with forms built from each model's live schema. Output types Image, Video, 3D, Motion, Audio, Material, Skybox and All models, each with quick picks, Suggest (Scenario's ranking for the prompt) and a model browser.
- Price before every paid call, single-use quotes, paid calls sent once, Unknown outcomes blocked until matched, a confirmation threshold and a spending switch (`SCENARIO_NO_SPEND=1`).
- Import and placement per type: sprites and textures, prefabs from GLB through glTFast, pipeline-correct PBR materials, panoramic skyboxes, audio sources, video players. One Undo step each. Provenance in the asset's `.meta`, the `Scenario` label and an Inspector header.
- Typed next steps on images, 3D models, videos and audio, from result cards, the Project window and the Inspector.
- Every 3D operation on Scenario as a step on a model, in four groups: Texture (retexture, UV unwrap), Shape (edit with a prompt, retopology, remesh to a polycount, refine from an image), Rig and animate (rig a character or a creature, animate from Meshy's motion library, from a prompt or from a video) and Parts and style (split into parts, stylize as LEGO, voxels or Voronoi).
- Motion output type: character animation from a prompt or a video (Cartwheel, Uthana, Meshy), with a searchable picker for Meshy's 656 library motions.
- 3D results import whole: a rig set (rigged mesh plus walk and run files) gets one Animator controller with every clip, played on the rigged mesh; models stored in another format (FBX, `.blend`, `.vox`) are converted to GLB on download; models without a material get the pipeline's default one; rigs written in centimeters are placed at meter scale; every model gets a plain scene root, so previews show it standing and the files of one rig share a hierarchy.
- Inputs a model needs beyond its schema are asked for before pricing (a character for Cartwheel motions, a reference image for YVO3D retexture), and GLB inputs are sent as an FBX copy to models that refuse GLB.
- Library of the Scenario project's assets, and the list of Scenario assets in the Unity project.
- Workflows: team and featured workflows, their inputs as a form, priced and run like any model.
- Prompt Spark to improve a prompt (priced first).
- Agent bridge: a local MCP server (`127.0.0.1`, bearer token) with Scenario tools (including `motions_find` for the motion library), scene tools and a capture of the Scene view, a camera or, when there is neither, an overview of the open scenes, one-click setup for Claude Code and setup lines for Codex, Cursor and VS Code, per-call and session spending limits with approvals in the window.
- `Scenario.ScenarioApi` for editor scripts and CI.
