# Developer Power-Paste — Design Spec

**Date:** 2026-05-26
**Status:** Approved

---

## Problem

ClipMaster's standout feature is its regex transform engine, but day-to-day developer copying needs more than regex find/replace. Developers constantly copy JSON, base64, URL-encoded values, SQL, and code in many languages, and:

- Common reshaping (pretty-print JSON, decode base64, change case) requires leaving the app or hand-writing a regex rule.
- Every clip renders as flat proportional text, so code and data are hard to read at a glance and there is no way to tell a SQL clip from a shell clip from a JSON blob.
- There is no way to view a long clip in full — the card preview truncates at ~72 px and the only "view" is whatever fits.

This makes ClipMaster weaker than it should be for its most likely power user: a polyglot developer living in terminals and editors.

---

## Goals

- Add a library of built-in, on-demand **transforms** (JSON, encoding, case, whitespace) that run on a clip without writing a regex.
- **Detect** a clip's language heuristically and show it as a badge.
- **Syntax-highlight** code/data clips — in the card preview and in a new full-clip detail view.
- Keep it dependency-free and consistent with existing patterns; never regress the responsiveness just achieved.

---

## Out of Scope (YAGNI)

- **Auto-applying** built-in transforms on copy — they are on-demand only (auto-minifying everything you copy would be destructive).
- **Real parsing.** Highlighting is heuristic, display-only token coloring, not a compiler front-end.
- **Paste-as-plain-text** — redundant: ClipMaster captures via `Clipboard.GetText()` and pastes via `SetText()`, so clips are already plain text.
- **User-defined custom transforms** beyond the existing regex rules.
- **Highlighting arbitrarily large clips.** Above a size cap the detail view renders plain monospaced text.
- **Languages beyond the listed set.** The registry/specs make adding more trivial later.

---

## Scope Summary (agreed decisions)

| Decision | Choice |
|---|---|
| Code-awareness depth | Full syntax highlighting |
| Where highlighting shows | Card preview **and** an on-open full detail view |
| Highlighting engine | Hand-rolled, zero-dependency tokenizer → themed WPF `Run`s |
| Transform invocation | On-demand only (context menu + detail view) |
| Transform result | Mutates `Text`, preserves `Raw` (same as existing manual regex rules) |
| Expand gesture | Explicit "Expand" button on the card + `Ctrl+Enter` on the top clip (plain `Enter` keeps pasting top, as documented) |

---

## Data Model

### `ClipEntry` — one new field (`Models.cs`)

```csharp
[JsonPropertyName("language")] public string Language { get; set; } = "";
```

- `""` means "not yet detected / plain". Set by detection in `AddClip` for text clips; lazily backfilled on render for clips loaded from older data files.
- Persisted so a future "filter by language" needs no recompute.
- Image clips leave it `""`.

### `AppSettings` — one new field (`Models.cs`)

```csharp
[JsonPropertyName("highlightCode")] public bool HighlightCode { get; set; } = true;
```

Master toggle for both detection and highlighting. When `false`, clips render as today (proportional, no badge, no highlight) and the detail view shows plain monospaced text.

### `Rule` — unchanged

Built-in transforms are **code**, not user data. They live in a static registry, not in `AppData.Rules`, so the persisted data file is unaffected.

---

## Component 1 — `LanguageDetector.cs` (new)

Static, pure: `Detect(string text) -> string` returning a language id (`"json"`, `"sql"`, `"yaml"`, `"xml"`, `"css"`, `"ts"`, `"go"`, `"rust"`, `"python"`, `"php"`, `"shell"`, `"dockerfile"`) or `""` for plain.

- Operates on a **capped prefix** (first ~4 KB) so it is cheap on the UI thread inside `AddClip`.
- Ordered heuristics, highest-confidence first; all regex use the existing 1 s timeout:
  - **json** — trimmed text starts with `{` or `[` and parses via `System.Text.Json` (`JsonDocument.Parse` in try/catch).
  - **xml/html** — starts with `<`, contains tag-like `<\w+`.
  - **dockerfile** — first non-comment line matches `^FROM\s`.
  - **yaml** — `^---` or multiple `^\s*[\w.-]+:\s` lines and not JSON.
  - **sql** — early match of `\b(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP)\b` (case-insensitive).
  - **go** — `\bpackage\s+\w+` or `\bfunc\s` with `:=`.
  - **rust** — `\bfn\s`, `\blet\s+mut\b`, or `::` with `impl`/`->`.
  - **python** — `^\s*(def|class|import|from)\s` or `print(`.
  - **php** — contains `<?php` or `\$\w+\s*=`.
  - **css** — selector blocks `[.#]?\w+\s*\{[^}]*:[^}]*;`.
  - **ts/js** — `\b(const|let|function)\b`, `=>`, or `import .* from`.
  - else `""`.
- Detection is best-effort; a wrong guess only affects coloring/badge, never clip content.

---

## Component 2 — `SyntaxHighlighter.cs` (new)

Display-only tokenizer plus renderer. Zero dependencies.

### Tokens

```csharp
enum TokenKind { Plain, Keyword, String, Number, Comment, Punctuation, Property, Literal }
readonly record struct Token(string Text, TokenKind Kind);
IReadOnlyList<Token> Tokenize(string text, string language);
```

### Tokenizers

- **Generic tokenizer** parameterized by a `LanguageSpec`:

  ```csharp
  record LanguageSpec(
      HashSet<string> Keywords,
      string[] LineComment,      // e.g. ["//"], ["#"], ["--"]
      (string Open, string Close)? BlockComment,  // e.g. ("/*","*/")
      char[] StringDelims);      // e.g. ['"','\'','`']
  ```

  Scans for strings, comments, numbers, identifiers (colored `Keyword` if in the set, else `Plain`), and punctuation. Covers the C-like and scripting languages.

- **Dedicated JSON tokenizer** — distinguishes object **keys** (`Property`) from string **values** (`String`), plus `Number`, `Literal` (`true`/`false`/`null`), and `Punctuation`. JSON is the most common dev clip and benefits most from key/value distinction.

`LanguageSpec` instances for each detected language are defined as static data in this file.

### Rendering

```csharp
void AppendTo(InlineCollection inlines, string text, string language);
```

Builds `Run`s with `Foreground` from theme brushes and appends to a `TextBlock`/host. The token→brush map reads named resources so colors live in the theme.

### Performance and safety

- **Card preview:** the card already truncates to ~72 px (~4 lines). The card tokenizes **only the preview slice it displays**, not the whole clip.
- **Detail view:** tokenizes the full clip, capped at **100 KB**; larger clips render as plain monospaced text (no tokenization) to protect responsiveness.
- All regex time-boxed at 1 s (existing project convention). Tokenizing a few lines for a virtualized card is cheap enough to run during card realization on the UI thread.

---

## Component 3 — `Transforms.cs` (new)

A static registry of on-demand transforms.

```csharp
record Transform(string Id, string Label, string Group, Func<string,string> Apply);
IReadOnlyList<Transform> All { get; }   // grouped, ordered
```

Initial set (grouped):

| Group | Transforms |
|---|---|
| JSON | Pretty-print, Minify, Escape (to JSON string), Unescape |
| Encode | Base64 encode, Base64 decode, URL encode, URL decode |
| Case | lowercase, UPPERCASE, Title Case |
| Whitespace | Trim lines, Collapse blank lines |

- JSON transforms use `System.Text.Json` (already referenced). Pretty/minify re-serialize a parsed `JsonDocument`; failures throw.
- A transform that cannot apply (e.g. Base64-decode on non-base64, JSON-pretty on invalid JSON) **throws**; the caller catches and shows a toast, leaving the clip unchanged.

---

## UI Changes (`MainWindow.xaml.cs`, `MainWindow.xaml`)

### Card preview (`BuildClipCard`)

- When `HighlightCode` is on and `clip.Language != ""`: render the preview in **monospaced** font with highlighted `Run`s (tokenize the displayed slice only), and add a small **language badge** in the header (reusing the existing `MakeBadge` style, neutral accent color). Sensitive clips keep their masked dots and are never tokenized.
- When off or language is `""`: unchanged from today.
- Add an **"Expand"** action button (`⤢`) to the card's button row → opens the detail view for that clip.

### Detail view (in-window overlay)

A new overlay panel in `MainWindow.xaml` (sibling of the toast overlay, `Grid.RowSpan` over the content area), hidden by default:

```
┌ DETAIL ───────────────────────────────────┐
│ [JSON]                              [Back] │
│ ┌────────────────────────────────────────┐│
│ │ {                                       ││  full clip,
│ │   "name": "clipmaster",                 ││  monospaced,
│ │   "version": "1.2.0"                    ││  highlighted,
│ │ }                                       ││  scrollable
│ └────────────────────────────────────────┘│
│ [Copy] [Paste] [Transform ▾]               │
└────────────────────────────────────────────┘
```

- Opened by the card's Expand button, or `Ctrl+Enter` when the stack is focused (acts on the top filtered clip). Plain `Enter` keeps pasting the top clip; double-click stays paste-and-promote; single-click is not overloaded.
- Shows: language badge, full syntax-highlighted scrollable monospaced text, and `[Copy] [Paste] [Transform ▾] [Back]`.
- `[Transform ▾]` lists the registry grouped; applying re-renders the detail view with the new text.
- A field `_detailClipId` tracks the open clip; `OpenDetail(clip)` / `CloseDetail()` toggle visibility and (re)render.

### Right-click context menu

Add a **"Transform ▸"** submenu to the existing text-clip context menu, listing the registry grouped. Applying mutates `Text` (preserving `Raw`), tags the clip "transformed", re-renders, and toasts. Image clips do not get the submenu.

### Keyboard / Esc

`Window_KeyDown`: if the detail view is open, `Esc` closes it first; otherwise `Esc` hides the window as today. Plain `Enter` (search box not focused) keeps pasting the top clip, unchanged. The detail view opens only via the card's Expand button or `Ctrl+Enter` on the top clip — so no existing shortcut changes.

### Settings

Add a **"Code"** section with one toggle: **"Highlight & detect code"** bound to `AppSettings.HighlightCode`. Toggling re-renders the stack.

### Theme (`Dark.xaml`)

Add token brushes in the existing palette: `CodeKeywordBrush`, `CodeStringBrush`, `CodeNumberBrush`, `CodeCommentBrush`, `CodePunctuationBrush`, `CodePropertyBrush`, `CodeLiteralBrush`.

---

## Capture Pipeline (`DataService.cs`)

`AddClip` sets `Language` for text clips:

```csharp
entry.Language = data.Settings.HighlightCode ? LanguageDetector.Detect(rawText) : "";
```

Detection runs on the capped prefix and is cheap. No change to the save queue or threading introduced by the perf work.

---

## Testing

Pure-logic units in the existing `ClipMaster.Tests` xUnit project (no UI):

- **`LanguageDetectorTests`** — representative samples map to the expected id; ambiguous/prose maps to `""`; oversized input is handled (prefix cap).
- **`TransformsTests`** — JSON pretty↔minify round-trip; base64 and URL enc/dec round-trip; case and whitespace transforms; invalid input throws (and is therefore toast-able, not crash-able).
- **`SyntaxHighlighterTests`** — token kinds for representative inputs: JSON key vs string value vs number vs literal; line/block comments; keyword vs identifier in one generic-spec language; the 100 KB cap returns a single `Plain` token.

Detail-view rendering, badges, and the Expand/Esc gestures are verified manually (consistent with existing UI verification in this project).

---

## Build Order (incremental, each independently shippable)

1. `LanguageDetector` + `ClipEntry.Language` + detection in `AddClip` (badge only, no color yet).
2. `SyntaxHighlighter` + token brushes + highlighted card previews + `HighlightCode` setting.
3. Detail view overlay (Expand button, `Ctrl+Enter`, Esc handling, Copy/Paste/Back).
4. `Transforms` registry + context-menu submenu + detail-view Transform menu.

---

## Files Changed

| File | Nature of change |
|---|---|
| `Models.cs` | Add `Language` to `ClipEntry`; add `HighlightCode` to `AppSettings` |
| `LanguageDetector.cs` | **New** — heuristic language detection |
| `SyntaxHighlighter.cs` | **New** — tokenizer, `LanguageSpec` data, JSON tokenizer, WPF run renderer |
| `Transforms.cs` | **New** — built-in transform registry |
| `DataService.cs` | Set `ClipEntry.Language` in `AddClip` (capped-prefix detection) |
| `MainWindow.xaml` | Add detail-view overlay; "Code" settings section |
| `MainWindow.xaml.cs` | Highlighted previews + badge in `BuildClipCard`; Expand button; detail view open/close/render; Transform context submenu; Esc/Ctrl+Enter handling |
| `Themes/Dark.xaml` | Add code-token brushes |
| `ClipMaster.Tests/` | New tests for detector, transforms, highlighter |
