# dyn_tasks.data — dynamic task pack

Implemented in `src/ElementsCore/Formats/DynTasksFile.cs`. Verified byte-exact on the TT3 client sample
(`element\data\dyn_tasks.data`, 186 538 B; `element\userdata\dyn_tasks.data` is an identical copy),
plus an edit test (rename, numbers, texts, added item/window/option, subtask nesting → re-parse OK).

It is the PW engine "dynamic task" export (`TaskTempl` marshalled compactly, JD IDE `src\dyn_tasks\format.rs`,
`struct DynTask` with 30 fields). In TT3 it holds 301 gift / special-award tasks (诛仙的馈赠, 新手礼品 …):
talk to an NPC, accept, receive the item reward.
Little-endian, no padding, no alignment. Every length is explicit; there are no offsets.

## Header (12 B)

```
u32 fileSize        = file length (recomputed on save)
u32 timestamp       Unix time of export (sample 0x5AD83F12 = 2018-04-19)
u16 version         13 (JD IDE refuses other versions: "dyn_tasks.data version X is not supported")
u16 taskCount       top-level tasks (301); subtasks are nested inside their parent
```

## Task (recursive)

```
u32 mask                 bit 0x1000 = timetable present; no other bit seen (others unsupported → raw mode)
u32 mask2                always 0
u8  dynType              0 none, 1 special award, 2 normal (enum task_dynamic_type); sample: all 1
[u32 specialAward]       only when dynType == 1
u32 id
u8  nameChars; wchar name[nameChars]            UTF-16LE, no NUL
u8  flags[17]            0/1 bools, unnamed (pattern 00 00 00 01 00 00 00 01 00 01 00 01 00 01 00 00 01)
u8  levelA, levelB       probably level limits; values (200,60),(200,150),(15,150),(30,0)… — min/max order unclear
[mask & 0x1000]  u8 n; n × { u8 type; i32 start[6]; i32 end[6] }   // task_tm: year,month,day,hour,min,wday
u8  method               3 = talk to NPC (300 tasks), 5 = wait time (1 task) → followed by u32 seconds
                         (other methods carry monsters/items/gold/site data, layout unknown → raw mode)
u8  finishType           1 = return to NPC, 0 = direct
u32 awardMask            sample: always 0x20
  [bit0 u32 gold][bit1 u32 exp][bit2 u32 sp][bit3 i32 reputation]   INFERRED from JD IDE field order, unseen
  [bit 0x10]  unknown size (JD IDE: "the award uses a field of unknown size (bit 4)") → raw mode
  [bit 0x20]  u8 nChoices; nChoices × { u8 random(=0); u8 nItems; nItems × Item }
u32 descChars;    wchar[]      description (no NUL)
u32 successChars; wchar[]      (empty in sample)
u32 failureChars; wchar[]      (empty in sample)
5 × Talk                       0 deliver, 1 unqualified, 2 deliver item, 3 executing, 4 award
u32 subtaskCount; subtaskCount × Task
```

Item (31 B):

```
u32 itemId; u8 common(0/1); u32 count; f32 prob(1.0);
u8 flag(0/1, maybe bind); i32 periodSeconds (0, 259200, 604800); u8 extra[13]
   extra[4] is 0x30 / 0xFF / 0x00, all other extra bytes 0 in the sample
```

Talk (PW talk_proc, compact):

```
u32 promptBytes; bytes          talk name, UTF-16 (only "RootNode" or empty in sample)
u8  nWindows
  u8 id; u8 parent(0xFF = root)
  u32 textChars (INCLUDING the terminating NUL); wchar text[textChars]
  u8 nOptions
    u32 id      0x80000006 accept task, 0x80000007 complete/award, 0x80000011 exit/reject
    u32 param   usually the task id (0 for exit)
    u32 textBytes; bytes        UTF-16, no NUL
```

Note the mixed length units: name = u8 chars, desc/success/failure/window = u32 chars, prompt/option = u32 bytes.

## Editor model (DynTasksFile)

* `Tasks`: one row per task in depth-first order, `depth` column gives nesting (subtask counts and the header
  count are recomputed on save). Columns: depth, mask, mask2, dyn_type, special_award, id, name, flags, level_a,
  level_b, timetable (list), method, wait_time, finish_type, award_mask, award_gold/exp/sp/reputation,
  choices (list), items (list, `choice` = group index), description, success_text, failure_text, prompt_0..4,
  windows (list: talk, id, parent, text), options (list: talk, window index within talk, id, param, text), subtasks (RO).
  Nested lists are flattened one level (items→choices, options→windows) because the UI cannot edit lists inside lists.
* `Header`: file_size (RO), timestamp, version, task_count (RO).
* Texts keep their raw bytes until edited (byte-exact). Bits 0x1000 (mask) / 0x20 (award mask) are set
  automatically when timetable rows / choice groups exist.
* Fallback: if anything unsupported is met (unknown mask bits, method ∉ {0,3,5}, award bits 0x10/0x40+, bad
  lengths, trailing bytes) the task area becomes one read-only `Raw` blob — still saves byte-exact.

## Uncertain

levelA/levelB meaning, the 17 flag names, item `flag`/`extra`, choice `random`, award bits 0–3 (never seen),
whether `taskCount` counts only top-level tasks (no subtasks in the sample; assumed top-level).

---

## tasks.data (996 B) — not implemented

Index of the real task packs `tasks.data1 … tasks.data62` (2–9 MB each, client `element\data`).
`u32 magic 0x69340304; u32 184 (version?); u32 1; u32 18276 (task count?); u32 61` then 976 bytes of
near-random data (entropy 7.8 ≈ maximum for that length; no zlib/raw-deflate/LZMA stream found) → encrypted
or compressed. JD IDE calls it "a tasks.data index (magic 0x…)". The packs themselves start
`u32 0x06934554 ("TE\x93\x06"); u32 count; u32 offsets[count]` (PW TASK_PACK) — task bodies are the full
`ATaskTempl` layout (JD IDE has a large versioned layout system for them). Out of scope here.

## xxtree.data (1 088 848 B) — not implemented

Plain (unencrypted, entropy ≈ 6.2) binary record table for the 奇遇 / story-event tree: UTF-16 texts with u32
length prefixes ("张小二的故事", "流波山奇遇5", long narrative paragraphs, choice texts), u32 ids, 0xFFFFFFFF
links. Header ints `0, 260, 0, 1, 13, …`. The tail contains leaked memory (an environment block
"TEMP=C:\Users\Administrator\…"), i.e. the exporter wrote uninitialised buffer bytes. Needs its own RE.
