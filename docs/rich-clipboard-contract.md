# Rich clipboard interoperability — Notes ↔ ClipMaster

Status: design contract for the `feature/rich-content-clipboard` branch. No runtime changes yet.

## Current behavior
`ClipboardMonitor.TryReadClipboard()` calls `Clipboard.ContainsText()` first, and otherwise captures a bitmap. `ClipEntry` stores either text or one image. `App.OnNewClip` can rewrite clipboard contents using regex transforms, which must **never** be applied to composite clips.

## Clipboard formats
Capture all compatible representations of a **single clipboard event**, before deciding how to render it:
1. `HTML Format` / CF_HTML: Windows HTML Clipboard Format, with a valid StartHTML/EndHTML/StartFragment/EndFragment header.
2. `UnicodeText` (CF_UNICODETEXT): plain text, useful for fallback/search.
3. `PNG` and CF_DIB: optional bitmap fallback.
4. `application/x-simba-notes+json;version=1` (registered Windows clipboard format): future private lossless payload, with a document sequence and image metadata. This is **not** a standardized MIME type for external applications.

A `Rich` clip is a cohesive item, not multiple image and text entries. Store original clipboard HTML bytes/markup, plain text, and owned image assets in ClipMaster-managed storage; never rely on source process paths, transient blob URLs, or external network URLs. Sanitize HTML on preview; never execute scripts or load remote resources. Enforce image count, byte limits and overall payload limits.

## Capture/paste order
- Check for the private Notes payload, then HTML, then image-only, then text-only.
- For rich clips, preserve compatible formats when repopulating the clipboard using a `DataObject` with multiple formats in one `Clipboard.SetDataObject` call.
- Do not run regex auto-transforms on rich clips.
- Keep old `ClipEntry` JSON data readable with default nullable fields.
- Preserve DPAPI encryption for metadata; treat image files as sensitive persisted assets (the current image storage is not encrypted).
- Suppress self-recapture and avoid clipboard lock races, as the current text/image paths already do.

## Interoperability acceptance test
Write a note: heading → paragraph → screenshot A → paragraph → screenshot B. Copy all, let ClipMaster capture it, paste from ClipMaster back into Notes, then restart both applications and repeat. Validate ordering, image pixels, Unicode, no duplicate history entries, and no auto-transform.

External apps (including ChatGPT's web composer) may not accept mixed text+images in one paste. Offer an exported HTML/Markdown + image-assets fallback; do not claim universal one-step paste support.
