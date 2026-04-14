# Image Clipboard Support — Design Spec

**Date:** 2026-04-14
**Status:** Approved

---

## Problem

ClipMaster captures only text from the clipboard. When a user copies an image (from a browser, image editor, or via a screenshot tool such as Win+Shift+S or Print Screen), the image bypasses ClipMaster entirely and lives only in the native Windows clipboard — lost on the next copy.

---

## Goals

- Capture images from the clipboard and display them in the ClipMaster stack alongside text clips.
- Persist images to disk so they survive restarts.
- Allow paste-back of image clips into any application that accepts image paste (Ctrl+V).
- Keep the implementation minimal and consistent with existing patterns.

---

## Out of Scope

- Duplicate image detection (pixel comparison is expensive; duplicate screenshots stack normally).
- Sensitive masking for images.
- Regex/transform rules for images.
- Text search matching image clips (no text content to match).
- Size/dimension filtering of captured images.

---

## Windows Clipboard — Image Formats

Windows clipboard is a multi-format store. When an image is placed on the clipboard it is typically available in several formats simultaneously:

| Format | CF ID | Source |
|---|---|---|
| `CF_DIB` | 8 | Universal fallback — all image sources |
| `CF_DIBV5` | 17 | Extended DIB with colour space info |
| `"PNG"` | custom | Chrome, Edge, Snipping Tool (Win+Shift+S) |

**Capture strategy:** Prefer the registered `"PNG"` format when present (lossless, alpha preserved). Fall back to `Clipboard.GetImage()` (CF_DIB) otherwise. This ensures Win+Shift+S screenshots and browser image copies are stored at full fidelity.

```
if Clipboard.ContainsData("PNG")
    → decode MemoryStream via PngBitmapDecoder
else if Clipboard.ContainsImage()
    → Clipboard.GetImage()
```

**Text priority:** When the clipboard contains both text and image data (e.g. copying a cell in Excel), text takes priority. ClipMaster checks `ContainsText()` first; the image branch runs only in the `else` path.

---

## Data Model

### `ClipEntry` — two new fields (`Models.cs`)

```csharp
public bool   IsImage   { get; set; }  // true when this is an image clip
public string? ImagePath { get; set; } // relative path: "images/<id>.png"
```

`ImagePath` is stored as a relative path so the data file remains portable if `~/.clipmaster/` is moved.

### `AppSettings` — one new field (`Models.cs`)

```csharp
public int MaxImageHistory { get; set; } = 50;
```

Image clips and text clips are pruned independently against their respective caps.

---

## Storage Layout

```
~/.clipmaster/
  data.json            unchanged — ImagePath stored as relative string
  images/
    <id>.png           full-resolution PNG, one file per image clip
```

**No separate thumbnail files.** WPF loads the full PNG with `DecodePixelWidth=420` at render time, which decodes only a downscaled version into memory without reading the whole file.

**Atomic write:** PNG files are written to `<id>.tmp` first, then renamed — consistent with the existing atomic-write pattern for `data.json`.

**Pruning with file cleanup:** When an image clip is removed (pruned or user-deleted), its `.png` file is deleted from disk alongside the JSON entry.

---

## Capture Pipeline

### `ClipboardMonitor.cs`

New event added alongside the existing `NewClipText`:

```csharp
public event Action<BitmapSource>? NewClipImage;
```

Detection order in `WndProc` on `WM_CLIPBOARDUPDATE`:

1. `Clipboard.ContainsText()` → fire `NewClipText` (unchanged)
2. `else` → try PNG format, fall back to `GetImage()`, fire `NewClipImage`

The existing retry loop (5 attempts, 30 ms delay) for clipboard locks applies to both branches.

### `DataService.cs` — `AddImageClip()`

New method:

```csharp
public ClipEntry? AddImageClip(BitmapSource image)
```

Steps:
1. Encode `BitmapSource` to PNG bytes via `PngBitmapEncoder`.
2. Generate clip ID; write bytes to `~/.clipmaster/images/<id>.tmp`, rename to `<id>.png`.
3. Create `ClipEntry { IsImage=true, ImagePath="images/<id>.png", CopiedAt=now }`.
4. Prepend to `Data.Clips`; prune unpinned image clips beyond `MaxImageHistory`; delete orphaned `.png` files.
5. Save `data.json`.
6. Return the new entry.

### `App.xaml.cs`

Wire up in `OnStartup()`:

```csharp
_monitor.NewClipImage += OnNewClipImage;
```

`OnNewClipImage()` mirrors `OnNewClip()`: checks `_suppressing`, calls `DataService.AddImageClip()`, refreshes the stack UI.

---

## UI — Image Clip Card

Built in `MainWindow.xaml.cs` as `BuildImageCard()`, called from `RenderStack()` when `clip.IsImage` is true.

### Layout (Option C — large thumbnail)

```
┌─────────────────────────────────────────────┐
│ [1] [image] [pinned?]          2 min ago    │  ← header row
│ ┌─────────────────────────────────────────┐ │
│ │                                         │ │
│ │           140px thumbnail               │ │  ← Image control, UniformToFill
│ │                                         │ │
│ └─────────────────────────────────────────┘ │
│ [Paste] [Copy] [Pin] [Tag] [✕]  1920×1080·PNG·2.1MB │  ← footer row
└─────────────────────────────────────────────┘
```

- **Header:** rank badge + `image` badge (teal, `#1a2e3a` / `#5bb8d4`) + optional `pinned` badge + timestamp
- **Thumbnail:** WPF `Image` control, `Stretch=UniformToFill`, loaded via `BitmapImage` with `DecodePixelWidth=420`
- **Footer:** action buttons on the left, `width × height · format · size` label on the right
- **Action buttons:** Paste, Copy, Pin/Unpin, Tag, ✕ — identical style to text clip buttons
- **No** transform buttons, no sensitive masking logic

### `RenderStack()` change

```csharp
foreach (var clip in filtered)
{
    var card = clip.IsImage ? BuildImageCard(clip, rank) : BuildClipCard(clip, rank);
    _stack.Children.Add(card);
    rank++;
}
```

---

## Paste-back

**Paste** (Ctrl+V simulation):
1. Load PNG from `~/.clipmaster/images/<id>.png` into `BitmapSource`.
2. `Clipboard.SetImage(bitmapSource)` — write to clipboard.
3. Hide window.
4. `PasteService.Paste()` — simulate Ctrl+V after 150 ms delay.

`TrySetClipboardAndHide()` is extended with a `BitmapSource` overload; the existing retry/COMException logic is reused unchanged.

**Copy** (clipboard only, window stays open):
Steps 1–2 only.

---

## Settings Tab

New "Images" section in the Settings tab:

```
Images
  Max image history    [ 50 ]
```

Same `NumberBox`-style input as the existing "Max history" field. When the user lowers the value and saves, unpinned image clips beyond the new cap are immediately pruned and their `.png` files deleted.

---

## Files Changed

| File | Nature of change |
|---|---|
| `Models.cs` | Add `IsImage`, `ImagePath` to `ClipEntry`; add `MaxImageHistory` to `AppSettings` |
| `ClipboardMonitor.cs` | Add `NewClipImage` event; PNG-preferring image detection in `WndProc` |
| `DataService.cs` | Add `AddImageClip()`; image-specific pruning with `.png` file cleanup |
| `MainWindow.xaml.cs` | Add `BuildImageCard()`; update `RenderStack()`; add Images section to Settings tab |
| `App.xaml.cs` | Wire `NewClipImage`; add `OnNewClipImage()`; extend `TrySetClipboardAndHide()` for images |
