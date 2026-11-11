# ClipMaster rich clipboard preview

This branch adds rich clips: text with embedded images copied from Notes is kept as one history
entry and pasted back as the same item. It is not merged to `main`. What exactly is and is not
implemented is in [`rich-clipboard-contract.md`](rich-clipboard-contract.md).

## Before you run a preview build

The preview has its own installer identity (**ClipMaster Rich Preview**) and does not overwrite
the stable ClipMaster program files. It does, however, read and write the **same data
directory**, `%USERPROFILE%\.clipmaster`.

- **Quit stable ClipMaster first.** Never run both at once.
- **Back up `%USERPROFILE%\.clipmaster`** (copy the whole folder) before the first launch. The
  tray menu's *Export backup* does not include rich clips' HTML.
- Going back to stable afterwards is safe for text and image clips. Rich clips will show as
  plain text in stable, and lose their rich content the first time stable saves.

## Getting a build

GitHub Actions → **ClipMaster Rich Clipboard Preview** → a successful run → **Artifacts**:
`ClipMaster-Rich-Clipboard-Preview-Installer` or `ClipMaster-Rich-Clipboard-Preview-Portable`.
No GitHub Release is published by this workflow.

## Automated tests

```
dotnet test ClipMaster.Tests
```

These include a real clipboard round trip that runs on a private window station, so it never
reads or replaces what you have copied and a running ClipMaster does not see the test data.
To see where it ran:

```
dotnet test ClipMaster.Tests --filter "FullyQualifiedName~RichClipboardRoundTripTests" --logger "console;verbosity=detailed"
```

Each test prints `clipboard tests are running on window station Service-…$` (anything but
`WinSta0`), or `SKIPPED: no private window station …` if isolation was not possible — in which
case the round trip did not run and the test reports as passed without having checked anything.

The 96 MB case (the largest note Notes will copy) needs about a gigabyte of memory and is
opt-in: set `CLIPMASTER_LARGE_TESTS=1`.

## Manual round trip (what the automated tests cannot cover)

The automated tests stop at the clipboard. Notes producing the item, the paste keystroke and
the target application accepting it need a person:

1. In Notes, write: heading → paragraph → screenshot A → paragraph with an em dash, an accent,
   an emoji and some CJK text → screenshot B. Copy it as rich text.
2. In ClipMaster: one new entry, showing the note's text and a `Rich · 2 images` label.
3. Copy something else, then paste the entry from ClipMaster back into an empty note. Check the
   order, both images, and every non-ASCII character.
4. Copy the same note again in Notes: the existing entry moves to the top with its count
   increased; no second entry appears. Pasting from ClipMaster must not add an entry either.
5. Quit and restart the ClipMaster preview, then repeat step 3.
6. Delete the entry and check that its file under `%USERPROFILE%\.clipmaster\rich\` is gone.
7. Copy ordinary text from a browser (HTML without images): it must appear as a normal text
   clip, with auto-rules applied as before.
8. *Copy image* in a browser: it must appear as an image clip, as before.

## Known limits

- Only HTML containing at least one `<img>` becomes a rich clip; other HTML is a text clip.
- Images referenced by URL or file path are stored as references, not as pixels. They may not
  survive until you paste. Notes embeds its images, so its notes are self-contained.
- HTML over 128 MB is captured as plain text only.
- Pasting into other applications depends on what they accept; a web composer may drop
  embedded images or the formatting.
