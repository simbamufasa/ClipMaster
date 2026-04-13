# ClipMaster — Encrypted Storage Design

**Date:** 2026-04-13
**Status:** Approved

## Problem

`~/.clipmaster/data.json` stores the full clipboard history in plaintext. Any process
running as the same Windows user, any other user account on the machine, or anyone with
physical access to the drive can read every copied item verbatim.

## Threat Model

All four vectors are in scope:

- Other Windows user accounts on the same machine
- Malware / other processes running as the same user
- Physical drive access or backup images
- Cloud sync leaking the file contents

## Approach: Windows DPAPI

Use `System.Security.Cryptography.ProtectedData` with `DataProtectionScope.CurrentUser`.
The OS derives the encryption key from the current user's Windows login credentials.
No prompts, no passwords, fully transparent to the user.

**NuGet dependency:** `System.Security.Cryptography.ProtectedData` (required on .NET 8,
not included in default Windows TFM imports).

## Storage Layer Changes (`DataService.cs`)

### Save

```
AppData → JsonSerializer.Serialize → UTF-8 bytes
       → ProtectedData.Protect(bytes, null, CurrentUser)
       → write to data.bin.tmp
       → File.Move(data.bin.tmp → data.bin, overwrite: true)
```

### Load

```
read data.bin
→ ProtectedData.Unprotect(bytes, null, CurrentUser)
→ UTF-8 string → JsonSerializer.Deserialize<AppData>
```

### File naming

| File | Purpose |
|---|---|
| `~/.clipmaster/data.bin` | Encrypted live store |
| `~/.clipmaster/data.bin.tmp` | In-progress write (cleaned up on load if orphaned) |
| `~/.clipmaster/data.json` | Legacy plaintext — deleted after one-time migration |
| `~/.clipmaster/debug.log` | Trace log — stays plaintext (no clipboard content) |

## One-Time Silent Migration

On `Load()`:

1. `data.bin` exists → decrypt and return. (Normal path.)
2. `data.bin` absent, `data.json` exists → deserialize JSON → call `Save()` → delete
   `data.json`. User sees nothing.
3. Both absent → return fresh `AppData()`.

## Interrupted-Write / Power-Cut Safety

Write sequence uses `.tmp` + atomic rename (NTFS same-volume move).

On `Load()`, stale-`.tmp` recovery:

| Files present on disk | Action |
|---|---|
| `data.bin` + `data.bin.tmp` | Delete `.tmp`; load `data.bin` (last good state) |
| `data.bin.tmp` only | Delete `.tmp`; return fresh `AppData()` |
| `data.bin` only | Normal load |
| Neither | Return fresh `AppData()` |

Worst case of a mid-save power cut: the last write is lost. No corruption, no
unreadable file.

## Error Handling

| Exception | Cause | Handling |
|---|---|---|
| `CryptographicException` on Unprotect | Different user/machine encrypted the file | Log to `debug.log`; return fresh `AppData()` |
| Any other exception on Load | Corrupt file, disk error | Log; return fresh `AppData()` |
| Any exception on Save | Disk full, locked | Swallowed silently (same as today) |

## Export Feature

A **"Export backup…"** tray menu item (added to `TrayMenu.xaml.cs`) allows the user
to write a portable plain-JSON backup:

- Opens `SaveFileDialog` defaulting to `clipmaster-backup-YYYY-MM-DD.json`
- Serializes the current in-memory `AppData` to indented JSON
- Writes to the user-chosen path
- The Save dialog is the confirmation — no additional prompt

No import feature in scope.

## Out of Scope

- `debug.log` encryption (contains no clipboard content)
- Cross-machine key sharing or cloud sync
- Import from backup file
