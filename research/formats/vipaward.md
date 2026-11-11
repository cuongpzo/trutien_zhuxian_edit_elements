# vipaward.data

Client file `element\data\vipaward.data` (the server reads it too): VIP rewards, daily rewards and the VIP shop.
Reader/writer: `src\ElementsCore\Formats\VipAwardFile.cs`. Little-endian.

## Layout

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0 | u32 | timeMark | Unix time of the export (sample: 2018-03-07). |
| 4 | u32 | count | Number of awards. JD IDE: at most 65,535. Recomputed on save. |
| 8 | award[count] | | Fixed size = (fileLength − 8) / count. Current builds use 172 B. |

Award (172 B):

| +0 | u32 | id | > 0 and unique. Otherwise the server refuses to start. |
| +4 | wchar_t[64] | name | UTF-16, NUL-terminated (display name, usually the item name). |
| +132 | u32 | itemId | elements.data item id (≠ 0). |
| +136 | u32 | count | ≥ 1. Must be 1 for an item that does not stack. |
| +140 | u32 | awardType | 0 normal, 1 VIP, 2 VIP shop. |
| +144 | u32 | level | 1–8: VIP level, or level bracket for normal awards. JD IDE says older server source accepted 1–6, and 172-byte builds use 7 and 8. |
| +148 | u32 | obtainType | 0 daily, 1 special (one-time), 2 shop. In official files, shop items have awardType = obtainType = 2. |
| +152 | i32 | expireTime | Item lifetime in seconds (0 = permanent). Sample values: 3600, 7200, 604800, 2592000. |
| +156 | f32 | price | VIP shop price (100.0 … 99900.0). 0 for other awards. |
| +160 | u32 | shopParam | Only VIP shop rows: 1, 2 or 3. **Meaning unknown** (possibly a buy limit or currency). |
| +164 | u8[8] | reserved | Always 0. |

JD IDE: "the source has … ; newer builds 172". An older/source layout exists. It is most likely the 156-byte prefix, without price, shopParam and reserved. The editor reads 156-byte records with the 8 base columns. Any other size ≥ 156 is read as the base columns plus a raw `extra` byte column.

## Sample (Client0619)
34,580 B, 201 awards × 172 B. awardType: 91 normal / 74 VIP / 36 shop. Levels 1–8. Bytes 164–171 are zero everywhere.
