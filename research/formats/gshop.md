# gshop*.data – item mall

Reader/writer: `src/ElementsCore/Formats/GShopFile.cs`. All integers little-endian; every
struct is **packed** (no alignment padding, wchar strings may start at odd offsets).

## File layout

```
u32 timestamp          // version stamp (client and server copy must match)
u32 item_count
item_count × ITEM      // fixed size, layout depends on the shop (see below)
u32 main_count         // >= 1 ("The shop needs at least one main category")
main_count × {
    i32   id           // 0,1,2… in every sample
    wchar name[64]     // 128 B
    u32   sub_count
    sub_count × wchar sub_name[64]
}
EOF
```

The record size is **not stored**. It is found by arithmetic: the only size `S` for which
the category list read at `8 + item_count*S` ends exactly at EOF. Known sizes are tried
first (2660, 2630, 1252, 357, 200); if none fits, the parser scans S = 8…65536 and exposes
the item as raw bytes. An empty shop (item_count 0) has the category list right at offset 8;
new items then use the 2660 layout.

`ITEM.main_type` / `sub_type` are indices into the category list (main row index, sub index
within it). Category names are UTF-16; a name of "-" is used for unused slots.

## Samples

| file | size | items | item size | layout | main cats |
|---|---|---|---|---|---|
| gshop.data  | 766340 | 286 | 2660 | TT3 shop | 8 (last = "-") |
| gshop1.data | 3780   | 0   | –    | (empty)  | 7 |
| gshop2.data | 111432 | 41  | 2660 | TT3 shop | 7 |
| gshop3.data | 587956 | 220 | 2660 | TT3 shop | 7 |
| gshop4.data | 2070   | 2   | 357  | lucky bag | 8 (3 named, 5 "-") |
| gshop5.data | 6012   | 28  | 200  | exchange | 2 (奇珍异宝, 下架物品) |

gshop4 also closes at S=1029 by coincidence; 357 is the known layout and its fields decode.
All six round-trip byte-exact; Sniff gives no false positive on the other sample files
(domain*, path, dynamicobjects, task_npc, tasks, vipaward, xxtree, dyn_tasks).

## Item layout "TT3 shop" – 2660 B (gshop, gshop2, gshop3)

= GSHOP_ITEM of the C++ source, packed (2630 B), + 30 extra bytes.

| off | type | name | notes |
|---|---|---|---|
| 0 | u32 | id | item sold |
| 4 | u32 | num | count per purchase |
| 8 | char[128] | icon | GBK path, `Surfaces\QShop\…dds` |
| 136 | u32 | price | e.g. 509900 (probably 1/100 currency unit) |
| 140 | u32 | time | duration in s (0 = permanent; 604800, 28800 seen) |
| 144 | i32 | discount | 100 (normal) or 50; 0 on 2 items in gshop3 |
| 148 | i32 | bonus | always 0 |
| 152 | u8 | props | 0, 1 = New, 2 = Recommended (low byte of Source `props`) |
| 153 | u8 | props_b1 | always 0 |
| 154 | u8 | props_b2 | always 0xFF (except 1 item: whole props = 0) |
| 155 | u8 | props_b3 | 0x80 exactly when discount = 50 |
| 156 | i32 | main_type | category index |
| 160 | i32 | sub_type | sub-category index |
| 164 | i32 | local_id | unique per item |
| 168 | wchar[512] | description | colour codes `^RRGGBB`, `\r\n` |
| 1192 | wchar[32] | name | |
| 1256 | bool | has_present | always 0, even when present fields are filled |
| 1257 | wchar[32] | present_name | (odd offset) filled on 50 items of gshop.data |
| 1321 | u32 | present_id | |
| 1325 | u32 | present_count | |
| 1329 | u32 | present_time | |
| 1333 | char[128] | present_icon | |
| 1461 | bool | present_bind | |
| 1462 | wchar[512] | present_description | |
| 2486 | i32 | valid_type | 0: window start..end; 1: (start, 28800, param 1) – repeating? |
| 2490 | i32 | valid_start | Unix time |
| 2494 | i32 | valid_end | Unix time (or 28800 with type 1) |
| 2498 | i32 | valid_param | |
| 2502 | wchar[64] | search_keys | comma separated |
| 2630 | u8 | x_flag1 | unknown; 1 together with x_flag2 on pets / masks |
| 2631 | u32 | x_price1 | unknown; often equals price (3900, 2900, 900) or 12800/9900/2500 on price-1 items |
| 2635 | u8 | x_flag2 | unknown |
| 2636 | u32 | x_price2 | unknown, 0 or same as x_price1 |
| 2640 | u32 | x_value | 1 on 4 items (sub "十年特卖") |
| 2644 | i32×4 | valid2_type/start/end/param | copy of the sale window on the 14 items that have one |

The editor splits Source's u32 `props` into 4 bytes (152–155) since the high bytes behave as
separate markers. Only gshop.data uses the extra 30 bytes; gshop2/3 have them all zero.

## "Source" – 2630 B (no sample)
Offsets 0–2629 of the table above.

## "Old client" – 1252 B (no sample, from JD IDE)
id, num, icon[128], price, time, i32 unknown_144 (0), u32 props, main_type, sub_type,
local_id, wchar description[512], wchar name[32].

## "Lucky bag" – 357 B (gshop4)

| off | type | name | notes |
|---|---|---|---|
| 0 | i32 | place | 17, 18 |
| 4 | i32 | main_type | |
| 8 | i32 | sub_type | |
| 12 | char[128] | name | GBK |
| 140 | u32 | id | the box sold |
| 144 | u32 | num | |
| 148 | u32 | price_item | token item paid (78075 = 福灵百华, also a present in gshop.data) |
| 152 | u32 | price_item_count | |
| 156 | 36 B | unknown_156 | zero |
| 192 | u8 | flag_192 | 1 on both items |
| 193 | char[128] | icon | GBK |
| 321 | i32×4 | valid_type/start/end/param | (1, ts, ts2, 2) and (1, ts, 28800, 1) – same scheme as TT3 |
| 337 | 20 B | unknown_337 | zero |

(JD IDE lists 321–356 as 9 unknown ints; the first four are clearly the sale window.)

## "Exchange" – 200 B (gshop5)

| off | type | name | notes |
|---|---|---|---|
| 0 | i32 | place | 5…32, increasing |
| 4 | i32 | main_type | |
| 8 | i32 | sub_type | |
| 12 | char[128] | name | GBK |
| 140 | u32 | id | |
| 144 | u32 | num | |
| 148 | u32 | price | 200…108800 |
| 152 | 20 B | unknown_152 | zero |
| 172 | i32 | value_172 | 1–20; maybe purchase limit |
| 176 | 16 B | unknown_176 | zero |
| 192 | i32 | local_id? | 32…714, unique |
| 196 | i32 | value_196 | always 1 |

## Open questions
- Meaning of the 30 extra bytes of the TT3 layout (2630–2659) beyond the duplicated sale window.
- props high bytes (0xFF at 154, 0x80 at 155), `discount` 0 vs 100.
- Price unit / currency per shop (gshop2 cross-server prices are small: 100–8800).
- gshop5 fields 172, 192, 196; gshop4 flag_192.
- Which JD IDE shop name ("Bonus shop", "Item mall") goes with gshop5 / gshop1.
