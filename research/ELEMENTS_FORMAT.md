# Định dạng elements.data – Tru Tiên 3 (Client0619, version 165)

Đã kiểm chứng trên `Client0619/element/data/elements.data` (71.954.180 byte).

## Header (8 byte)
| Offset | Kiểu | Giá trị | Ý nghĩa |
|---|---|---|---|
| 0 | u16 | 0x00A5 = 165 | version → dùng layout `v165` |
| 2 | u16 | 0x1000 | signature |
| 4 | u32 | timestamp | |

## 318 list liên tiếp
Mỗi list:
```
u32 itemSize
u32 count
count * itemSize byte   (bản ghi kích thước cố định)
```
Trước một số list có thêm **marker** (lấy từ `layouts/v165layouts/v165/layout.json`):

| Trước list | Loại | Cấu trúc |
|---|---|---|
| 13, 36, 62, 103, 296 | checksum | 8 ký tự ASCII hex (vd `53f70b45`) |
| 23 | exporter | u32 head, u32 len, `len` byte, u32 tail |
| 56 | tag | u32 head, u32 len, `len` byte |

## Sau list 317: NPC Dialogs (talk_proc)
Bắt đầu bằng `u32 count` (= 204), cấu trúc cây có độ dài thay đổi (chuỗi `RootNode`…). Phần này cần parser riêng.

## Schema field
`schemas/layouts/v165/list_N.json` – **269 list** v165 + **211 list** v176 + **247 list** v160 (tổng hợp đủ từ bảng `.rdata` của `jdide.exe`). Khi kết hợp fallback giữa các version (kể cả dịch index `±1`), **313/318 list** của `Client0619` khớp schema chính xác:
- `size`: kích thước bản ghi; `fields[]`: `name`, `off` (offset), `t.k` (`u8/u16/u32/i32/f32/bool/wstr`), `t.n` (số ký tự wstr), `c` (chú thích TQ), `e` (enum), `when` (đổi kiểu theo điều kiện).
- Chuỗi tên là **UTF-16LE cố định độ dài** (vd `name` = 32 ký tự = 64 byte → tối đa 31 ký tự + `\0`) → Việt hoá phải giữ dưới giới hạn, bảo toàn mã màu `^ff6000`.
- 5 list `Auto` còn lại (`244`, `288` và 3 list rỗng `306, 315, 316`) được `SchemaRepository.AutoSchema()` tự dò chuỗi UTF-16LE chia hết cho 4 nên vẫn xuất/nhập Việt hoá đầy đủ.

Chạy thử: `python research/parse_elements.py <elements.data> research/jdide_schemas`
