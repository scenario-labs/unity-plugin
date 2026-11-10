# Roadmap

What comes after 0.1.0, roughly in order. Issues track the details.

- **Windows and Linux.** The keychain code paths (Windows Credential Manager, the user-only file elsewhere) are written but only macOS is tested.
- **HDRP and Built-in render pipelines.** Material and skybox code paths exist for both; 0.1.0 is tested on URP.
- **Video from a camera.** Record a camera clip (MediaEncoder) and send it to video-to-video models such as Seedance restyle.
- **Humanoid retargeting.** Animations import as Generic clips on the character they were made for. Setting up a Humanoid avatar would let a motion made for one character play on another.
- **Gaussian splat display.** Splats are saved as files; showing them needs a splat renderer package.
- **Last results in the Scene view panel.** The panel has the selection's steps, scene actions and Ask; a quick prompt that generates from it, and the latest results, would follow.
- **Generation history per asset**, beyond the Inspector header.
- **Balance in the header** once the Scenario MCP exposes it to every credential type. Today the header shows the plan and what this editor spent today.
- **Asset Store packaging.**
