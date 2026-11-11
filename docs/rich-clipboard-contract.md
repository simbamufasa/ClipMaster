# Rich clipboard interoperability — Notes ↔ ClipMaster

Status: implemented on the `feature/rich-content-clipboard` branch (not merged to `main`).
This document describes what the code does today, and says plainly what it does not do.

## What a rich clip is

One clipboard item with two formats, captured and restored together:

| Format | Content |
| --- | --- |
| `HTML Format` (CF_HTML) | UTF-8 bytes, NUL-terminated: a header with 10-digit **byte** offsets (`StartHTML`, `EndHTML`, `StartFragment`, `EndFragment`), then the HTML with the fragment between `<!--StartFragment-->` and `<!--EndFragment-->`. |
| `CF_UNICODETEXT` | The plain-text fallback. May be empty (an image-only note) or absent. |

This is what Notes writes (`cf_html()` in its `src-tauri/src/clipboard.rs`): ordinary HTML
(headings, paragraphs, lists, `<pre><code>`, tables) with every image embedded as
`<img src="data:image/png;base64,…">`. Payloads of tens of megabytes are normal; Notes refuses
to copy more than 96 MB of HTML.

**The rule that matters:** because the header holds byte offsets, the `HTML Format` bytes
ClipMaster puts back must be identical to the bytes it captured. ClipMaster therefore treats the
HTML as opaque bytes. It never decodes it to a string, parses it, sanitizes it, rewrites it or
re-encodes it. The bytes up to the terminating NUL are stored and restored as they are.

## Capture

`ClipboardMonitor.Capture()` decides what a clipboard change is; `RichClipboard.TryRead()` reads
the pair with Win32 (`OpenClipboard` / `GetClipboardData`), not through WPF, so no 16-bit string
copy of the payload is ever made.

A clipboard change is captured as a rich clip when all of these hold:

1. `HTML Format` is present and contains at least one image tag (`<img` in any letter case,
   followed by the end of the tag name — the meaning of the regex `<img\b`).
2. The HTML is at most `RichClipboard.MaxHtmlBytes` = **128 MB** (Notes' 96 MB cap plus headroom).
3. It is not the "bitmap + HTML snippet, no text" shape that browsers produce for *Copy image*;
   that stays an image clip, as before.

Everything else goes down the legacy path unchanged — text first, then image:

- HTML without an image (ordinary text copied from a browser or editor) → text clip.
- HTML over the limit → its plain text is captured as a text clip.
- The clipboard stays locked by another program for the rich read → after the usual retries,
  its plain text is captured as a text clip.
- Reading or storing the HTML fails for any reason (out of memory, disk error) → its plain
  text is captured as a text clip.

So a copy is not lost because it was rich. The one case with nothing to fall back to is a rich
item that fails **and** has no plain text (for example an image-only note over 128 MB): there
is no text to keep, and a line is written to `debug.log`.

Rich clips are never run through regex auto-transform rules, and a text copy is never merged
into a rich clip that happens to have the same plain text. Manual rules and the Transform menu
are not offered on rich clips, because changing the text would make it disagree with the HTML.
Password masking is decided from the plain text, as for text clips.

## Storage

- The entry in the database (`data.bin`) holds only small fields: the plain text (`raw`/`text`,
  used for the list, search and masking), `isRich`, `richPath`, `richBytes`, `richImages`
  (image-tag count) and `richHash`.
- The HTML bytes live in one file per clip: `rich/<id>.bin` under the data directory. The
  database is no longer re-serialized and re-encrypted with megabytes of HTML in it.
- The file is protected for the current Windows user like `data.bin` is, as an envelope: DPAPI
  protects a random AES-256 key, and AES-GCM with that key encrypts and authenticates the HTML.
  Layout: `"CMR1" | int32 key length | DPAPI(key) | 12-byte nonce | 16-byte tag | ciphertext`.
  DPAPI directly on the payload was measured at about 15 MB/s (1.3 s for 20 MB, 19 s for
  128 MB, each way), which would freeze every paste of a large clip. A file that was altered,
  truncated or written for another user fails authentication and is treated as unreadable.
- The file is read only when the clip is pasted or copied.
- The file is deleted when its clip is deleted, trimmed from history, or removed by *Clear
  History*. Rich files that no entry refers to (a crash between writing the file and saving the
  database, or an older ClipMaster rewriting the database without the rich fields) are removed
  at the next start — but only after the database loaded successfully.
- Entries written by the first preview builds, with the HTML inline as `richHtml`, still load.
  On load the HTML is moved to a side file and the database is saved without it. If that write
  fails, the entry keeps its inline HTML and still pastes.

## History

- **De-duplication:** the identity of a rich clip is SHA-256 over its HTML bytes and its plain
  text. Copying the same content again moves the existing entry to the top and increments its
  copy count; nothing new is stored.
- **Limits:** pinned clips are always kept. Unpinned rich clips are limited by the general
  *Max History* (shared with text clips) and additionally by *Max rich history* (default 50,
  in Settings next to *Max image history*), because each one can be very large.

## Restore (paste / copy)

`RichClipboard.Restore()` reads the side file and writes one clipboard item with Win32:
`HTML Format` = the stored bytes plus a terminating NUL, then `CF_UNICODETEXT` = the plain text
captured with it. If the HTML can no longer be read (file missing or damaged), ClipMaster pastes
the clip's plain text instead; if there is no text either, nothing is pasted.

Self-recapture: the write returns the clipboard sequence number, and the monitor ignores the
update notification that carries that number, so pasting a rich clip does not capture it again
or bump its copy count.

## Not implemented — know this before relying on it

- **Images that are not embedded are not made durable.** HTML whose images are remote URLs
  (`https://…`), file paths, `blob:` URLs or `cid:` references is stored exactly as copied.
  ClipMaster does not download or inline them, so such images may be gone when the clip is
  pasted later. Only `data:` images (what Notes writes) are self-contained.
- **Only two formats are kept.** RTF, bitmap/PNG renderings, file lists and private formats
  that were on the clipboard next to the HTML are not captured or restored. A source that had
  no `CF_UNICODETEXT` at all gets an empty one on restore.
- **No rich preview.** The list shows the plain text and a "Rich · N images" label (or
  "(N images, no text · size)" when there is no text). The HTML is never rendered, so nothing
  in it is executed or fetched by ClipMaster.
- **No private Notes format.** The `application/x-simba-notes+json` payload from the original
  design is not read or written.
- **Backup export** (tray menu) writes the database as JSON: rich clips appear with their plain
  text only. The HTML side files are not exported.
- **Image-only items over the limit** are not captured at all (see Capture).
- **Target applications decide what they accept.** ClipMaster restores the same item Notes
  wrote; an application that ignores `HTML Format`, or that strips `data:` images on paste,
  will still do so.
- **Stable ClipMaster shares the data directory.** If the stable build opens a database that
  contains rich clips, it shows them as text clips and drops the rich fields when it saves;
  the side files are then cleaned up by the next preview start.

## How it is tested

`ClipMaster.Tests` covers the pure logic (image detection, hashing, labels), storage
(encryption, lifecycle, migration, de-duplication, trimming, rules, masking) and a real
round trip on a real Windows clipboard that is **not the user's**: the test process moves to
a private, non-interactive window station (`CreateWindowStation` / `SetProcessWindowStation` /
`CreateDesktop` / `SetThreadDesktop`), where another "program" writes a Notes-style item with
raw Win32, ClipMaster's capture code reads it, the clip is stored and reloaded with a new
`DataService` (a restart), ClipMaster's restore code writes it back, and the raw `HTML Format`
bytes are compared with the copied ones. If the private window station cannot be set up, those
tests do nothing rather than touch the real clipboard. See `IsolatedClipboard.cs`.

Not covered by automated tests: the WPF window, the paste keystroke into a target application,
and Notes itself producing and accepting the item — see `RICH_PREVIEW_TESTING.md` for the manual
check.
