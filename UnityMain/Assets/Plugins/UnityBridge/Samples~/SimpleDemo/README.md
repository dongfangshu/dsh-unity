# Simple Demo — DSH Unity Bridge

Minimal example that drives the bridge **directly from the editor**, without
any agent or DSH involved. It shows the file-queue protocol in action: menu
items write a JSON command into `<project>/UnityBridge/in/` and log the
response from `out/`.

## What it adds

Three menu items under **Tools > Unity Bridge > Samples**:

| Menu item | op | What it does |
|---|---|---|
| `Ping` | `ping` | round-trip check; logs `pong` |
| `Print Status` | `status` | logs play mode, open scene, Unity version |
| `Create Cube (Roslyn cs)` | `cs` | compiles + runs a C# script in memory that creates `sample-cube` in the scene |

## How to use

1. Make sure the bridge is active: the editor is open with the package
   installed (a `<project>/UnityBridge/status/heartbeat.json` exists).
2. Run a menu item. Each command logs its full JSON response in the Console.
3. Watch the Scene view after `Create Cube` — a cube named `sample-cube`
   appears; delete it or leave it.

## Install this sample

- **UPM install** (git URL): Package Manager → com.dsh.unitybridge →
  **Samples → Simple Demo → Import**.
- **Manual copy**: copy the `SimpleDemo/` folder into your project's
  `Assets/` (the `Samples~` root stays out of the build).

## Files

- `UnityBridgeSample.cs` — the demo (editor-only, `[MenuItem]` actions +
  a small poll helper over `EditorApplication.update`).
- `README.md` — this file.
