# task_npc.data

Client file `element\data\task_npc.data`: the map position of every task NPC (task auto-pathing and map marks).
Reader/writer: `src\ElementsCore\Formats\TaskNpcFile.cs`. Little-endian.

## Layout

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | u32 | fileSize | = total file length. JD IDE: a mismatch makes the client refuse the file. Recomputed on save. |
| 4 | u32 | timeMark | Unix time of the export (sample: 2018-04-26). |
| 8 | u16 | version | 2. JD IDE: the client reads only version 2. |
| 10 | u16 | count | Number of records, so the file holds at most 65,535. Recomputed on save. |
| 12 | record[count] | | 16 B each. |

Record (16 B):

| +0 | u32 | npcId | NPC_ESSENCE id in elements.data. Checks: 55372 = 小娜娜, 6208 = 尉缭子, 79959 = 宝箱. JD IDE: "A record has ID 0" is an error, and a repeated id keeps only the last record. |
| +4 | u32 | mapId | World/map id (401, 1, 2, 23, …). 0 means no position known. |
| +8 | i16 | x | Whole metres. |
| +10 | i16 | y | Height. |
| +12 | i16 | z | |
| +14 | u16 | pad | Always 0. |

## Sample (Client0619)
61,820 B, version 2, 3,863 records, no duplicate ids, no id 0. 1,717 records are `map 0, (0,0,0)`. Coordinates range from about -900 to 900. The records are not sorted.

## Uncertainties
- The y/z order is inferred from the value ranges: the middle value is always ≥ 0, so it looks like height.
- mapId is assumed to be the world tag/instance id. It was not checked against the map list.
