# ClipMaster rich clipboard preview

This branch includes experimental composite text + HTML clipboard history support for Notes. It is not yet merged to `main`.

## Windows preview installer

GitHub Actions → **ClipMaster Rich Clipboard Preview** → choose a successful run → **Artifacts** → `ClipMaster-Rich-Clipboard-Preview-Installer`.

The installer is intentionally named **ClipMaster Rich Preview** and has a separate installer identity so it does not overwrite the stable ClipMaster installer. The preview build still reads/writes the same `%USERPROFILE%\.clipmaster` data directory, however. **Quit stable ClipMaster before launching the preview, and export a backup of your history before testing.** Never run both instances at once.

You can alternatively download `ClipMaster-Rich-Clipboard-Preview-Portable` from the same workflow run.

## Round-trip test

1. Copy a Notes document containing multiple screenshots with **Copy rich**.
2. Inspect the resulting entry in ClipMaster preview.
3. Paste it back into Notes, and compare image ordering and readable text.
4. Restart ClipMaster, repeat, and check that the history entry survives.

This is an early compatibility implementation. Standard HTML is captured only when it contains an `img` tag; not every HTML clipboard image reference is guaranteed durable or embedded. In particular, remote URLs and temporary WebView paths may expire. Support for fully self-contained image asset persistence is still in development.

No final GitHub Release is published by this workflow: artifacts become downloadable after successful CI execution.
