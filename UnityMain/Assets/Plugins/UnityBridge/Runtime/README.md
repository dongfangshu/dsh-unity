# Runtime

Reserved for runtime (player) code. This package is currently **editor-only**:
the bridge (`Editor/UnityBridge.cs`) runs inside the Unity editor via
`[InitializeOnLoad]` and needs no runtime components.

Future runtime features (e.g. an in-game agent hook, remote input relay) would
land here. Scripts in this folder compile into a player assembly and are
referenced by the editor code in `../Editor`.
