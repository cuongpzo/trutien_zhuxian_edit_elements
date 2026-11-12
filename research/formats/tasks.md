# tasks.data + tasks.data1…N — task templates

Implemented in `src/ElementsCore/Formats/TasksFile.cs` (Tasks tab). Verified on the TT3 client
(`element\data`, Client0619): 61 packs, 18 276 top-level tasks, ~232 MB. Read in ≈1 s; every pack is
rebuilt byte-exact through the editing path (`ElementsCli data` / `tasks-test`).

**Nothing is encrypted.** The 996-byte `tasks.data` only *looked* encrypted in the hex view: after its
20-byte header it holds the MD5 of each pack file.

## tasks.data (index, 20 + 16·N bytes)

```
u32 magic      0x69340304
u32 version    184            (JD IDE also knows v165, v172)
u32 unknown    1
u32 taskCount  18276          top-level tasks in all packs
u32 packCount  61             N
N × md5[16]    MD5 of the whole file tasks.data{k}, k = 1…N
```

If a pack is edited, its MD5 here must be updated (the editor does it on save, also when a single pack
is opened on its own). `tasks.data62` in the sample is an old leftover (2025): not listed in the index.

## tasks.data{k} (pack)

```
u32 magic   0x06934554  ("TE\x93\x06")
u32 count   ≤ 300        top-level tasks in this pack
count × u32 offset       from the start of the file; first = 8 + 4·count
task blobs back to back  (blob i = offset[i] … offset[i+1] / end of file)
```

## Task blob

```
u32  id
wchar name[30]            UTF-16LE, NUL-padded (max 29 chars)
...  fixed + variable task data (premises, monsters/items wanted, awards, timetables…)
     the fixed part alone is ~5 000 bytes; array data in between shifts later fields
...  texts, talk procs (5 per task), 20 bytes, u32 subCount, subCount × child task (same layout, recursive)
```

The full field layout (JD IDE "verified built-in layout" for v184) is **not decoded yet**; the editor keeps
the task body as raw bytes and only touches the texts it can locate by structure:

### Dialogue window (inside a talk_proc)

```
talk_proc: wchar name[64] ("RootNode" or empty); u32 nWindows; nWindows × window
window:    u32 id; u32 parentId (0xFFFFFFFF = root); u32 len; wchar text[len] (incl. the NUL);
           u32; u32; u32 nOptions; nOptions × option
option:    u32 id (0x80000000+ = built-in action, e.g. 0x80000007 = finish); wchar text[64]; u32 param (often the task id)
```

Detection: id ≤ 0xFFFF or ≥ 0x80000000, parent likewise, 2 ≤ len ≤ 4000, NUL-terminated text of
"text characters" (CJK, ASCII, Latin-1, Vietnamese letters, punctuation, kana, full-width), ≤ 20 options
whose fixed 64-char buffers are text + zero padding. ~37 300 windows and ~45 700 option texts.

### Other texts (description, …)

`u32 len; wchar text[len]` (usually no NUL), found outside the windows: ≥ 2 text characters with at least
2 Chinese characters (or 1 in a ≥ 4-char text, or a Latin/Vietnamese text of ≥ 6 chars / with diacritics), and
not directly followed by another text character. ~32 700 texts (task descriptions, award notes…).

Editing a length-prefixed text changes its length field and shifts the rest of the blob; there are no
offsets inside a blob, only the pack offset table (rebuilt) and the MD5 in the index (rebuilt).
On save every rebuilt pack is read back and every text checked at its new offset before anything is written.

## Not done yet

- Field-level layout of the task body (type, premises, awards, timers, subtask tree editing).
- Adding / deleting tasks (packs hold at most 300 top-level tasks each).
