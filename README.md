# Tru Tiên Elements Editor

Công cụ đọc/sửa/Việt hoá `elements.data` (Tru Tiên 3 / Jade Dynasty, engine Perfect World).
C# .NET 10 – WinForms. Đã kiểm chứng trên Client0619 (version 165): đọc rồi ghi lại **giống hệt từng byte**.

## Chạy
- App: `Release\ElementsEditor.exe` (cần .NET 10 Desktop Runtime – máy này đã có). Phiên bản hiện trên thanh tiêu đề, `Release\VERSION.txt`, `ElementsCli --version`.
- Build bản phát hành: `.\build-release.ps1 [-Note "ghi chú"]` – tự tăng phiên bản, build Editor + CLI vào `Release\`, ghi dòng vào `RELEASE_HISTORY.md`. `-NoBump` để build lại giữ nguyên số.
- Phiên bản `vMYY.DDMM.{A|P}N` (vd `v226.0610.A4`): M = năm − 2024, YY = năm, DDMM = ngày+tháng, A/P = sáng/chiều, N = lần thay đổi thứ N trong ngày (sang ngày mới về 1). Lưu trong `version.props`.
- Build thử (Debug): `dotnet build`.

## Tính năng
- 318 list + **NPC Dialogs** (204 hội thoại, dạng cây cửa sổ/lựa chọn, sửa được, độ dài tự do).
- Field theo schema JSON: enum/mask hiện ý nghĩa, field đổi kiểu theo điều kiện (vd addon param → float).
- Màu list: đen = schema đúng version · nâu = mượn version khác cùng kích thước · xám = tự dò.
- Khung BYTES tô sáng vùng byte của field đang chọn.
- Lọc list / item / field; tìm toàn bộ theo ID hoặc tên (Ctrl+G).
- Nhân bản / xoá item (chuột phải).
- Lưu an toàn: ghi file tạm rồi thay thế, tự backup `elements.data.bak_yyyyMMdd_HHmmss`.
- Giao diện tối kiểu JD IDE (mặc định); đổi sang sáng ở **Công cụ → Chuyển sang giao diện sáng** (lưu trong `%AppData%\ElementsEditor\settings.json`).
- Icon Tru Tiên (`src/ElementsEditor/Assets/app.ico`, 16–256 px) dùng cho file exe, mọi cửa sổ và mọi hộp thông báo (`Msg.Show` thay `MessageBox.Show`). Chọn list bằng nút xổ xuống phía trên cột item (**Ctrl+L**).

## Các file khác (thanh bên trái, kiểu JD IDE)
Mỗi loại file một tab: **Elements · Path · Objects · Domains · Drops · Quiz · Lua · Areas · Tasks · Dyn Tasks · Task NPC · NPC Gen · GShop · VIP · AI**.
- Tab chưa mở file hiện trang **Mở file**: Chọn file…, Mở lại file trước, danh sách *Gần đây*, danh sách file tìm thấy trong thư mục client; kéo thả file vào cửa sổ cũng được.
- Lịch sử lưu ở `Documents\TruTien Elements Editor\recent-files.json` (20 file gần nhất mỗi tab + file đang mở) và `open-history.log` (nhật ký mở/lưu/đóng). Mở chương trình là các tab tự mở lại file lần trước, và quay về tab đang dùng.
- Phím tắt trong tab: Ctrl+O mở, Ctrl+S lưu (tự backup `.bak_yyyyMMdd_HHmmss`), Ctrl+Shift+S lưu thành, Ctrl+W đóng, Ctrl+F tìm. Chấm xanh trên thanh bên = tab đang mở file, chấm vàng = chưa lưu.
- Bảng: sửa trực tiếp trong ô hoặc ở khung *Chi tiết*; danh sách con (điểm viền lãnh địa, danh mục con của shop…) là các tab bên phải, có Thêm/Chèn/Xoá. Cột **→ tên (elements)** tra tên vật phẩm/NPC từ elements.data đang mở.
- Trước khi ghi, dữ liệu được đọc thử lại; nếu không đọc lại được thì không ghi gì.

| Tab | File | Trạng thái |
|---|---|---|
| Path / Objects | `path.data`, `dynamicobjects.data` (ID → đường dẫn model) | Đọc/sửa, ghi lại giống từng byte |
| Domains | `domain*.data` (lãnh địa, đường viền, tam giác, lãnh địa kề, giờ chiến) | Đọc/sửa, giống từng byte (7/7 file) |
| GShop | `gshop*.data` (món bán + cây danh mục; 3 kiểu bản ghi 2660/357/200 B) | Đọc/sửa, giống từng byte (6/6 file) |
| Task NPC | `task_npc.data` (NPC nhiệm vụ + vị trí) | Đọc/sửa, giống từng byte |
| VIP | `vipaward.data` (quà VIP) | Đọc/sửa, giống từng byte |
| Areas / Lua | `precinct.clt`, `region.clt`, `*.lua`, `*.txt` | Trình sửa văn bản, giữ nguyên bảng mã (UTF-16/UTF-8/GBK) và kiểu xuống dòng |
| Tasks | `tasks.data` + `tasks.data1…62` | Chỉ xem hex (chưa giải mã) |
| Drops / Quiz / NPC Gen / AI | file phía server (`extra_drops.sev`, `npcgen.data`, `aipolicy.data`…) | Chỉ xem hex – cần chép file mẫu từ máy chủ để giải mã |

### Sửa, xuất/nhập, lịch sử sửa (mọi tab)
- **Khung soạn chữ**: chọn một field chữ (hoặc nhấp đúp ô chữ) → khung *Đang sửa …* bên dưới: đếm `n / tối đa`, thanh **màu** chèn mã `^ffffff` trắng, `^ffcb4a` vàng, `^ff0000` đỏ, `^00ff00` xanh lá, `^00b4ff` xanh dương, `^c297ff` tím, cam, hồng, xanh ngọc, vàng kim… (+ *Màu khác…*); bôi đen rồi bấm màu = tô đoạn đó rồi trả về trắng. *Xem trước* hiển thị đúng màu như trong game. Ctrl+Enter lưu · Esc huỷ. Tab *Việt hóa Text* cũng có thanh màu.
- **Xuất / Nhập** CSV · Excel (.xlsx) · JSON: Elements → *Xuất dữ liệu* (list đang chọn, mọi field) / *Nhập dữ liệu*; tab khác → nút *Xuất ▾* / *Nhập…* (Ctrl+E / Ctrl+I). Cột khoá: Elements `_list` + `ID` + `_index`; bảng khác `#` + cột ID. Khi nhập: tìm bản ghi theo khoá (đổi thứ tự dòng trong Excel vẫn đúng), ô trống = giữ nguyên, ID mới = thêm bản ghi, ô sai kiểu / quá dài bị bỏ qua và liệt kê. Xuất rồi nhập lại không sửa gì = 0 thay đổi, file giống từng byte.
- **Lịch sử sửa**: thanh trạng thái hiện `● N bản ghi đã sửa · chưa lưu` – bấm vào (hoặc nút *Lịch sử sửa*, Ctrl+H ở tab khác) để xem mọi thay đổi so với file đã mở, gom theo bảng/list → bản ghi → field `cũ → mới`; nhấp đúp để tới đúng chỗ, nút ↺ hoàn tác từng thay đổi, *Hoàn tác hết*.
- **Thoát khi chưa lưu**: một cảnh báo liệt kê mọi tab còn thay đổi (kể cả chữ đang gõ dở) – *Có* = lưu tất cả (có backup) rồi thoát, *Không* = bỏ, *Hủy* = ở lại.

### Việt hoá Hán Việt hàng loạt
- Dùng từ điển phiên âm `ChinesePhienAmWords.txt` (dạng `字=âm`, mặc định `E:\Quick Translator 2016 - Revo\Data\…`, đổi được trong hộp thoại): 积善衣（女） → **Tích Thiện Y (Nữ)**. Giữ nguyên mã màu, số, chữ Latin, `\n`; dấu câu toàn khổ → ASCII; chữ thiếu trong từ điển giữ nguyên (có báo).
- Mở: Elements → *Việt hoá Hán Việt…* (list đang chọn / tất cả list), tab khác → *Hán Việt…*; chọn field (vd `name`), kiểu viết hoa, **Xem trước** rồi **Áp dụng**.
- **Chuột phải** vào một field (khung Chi tiết hoặc ô của bảng): *Việt hoá field này của bản ghi đang chọn* · *Việt hoá field cùng tên cho cả list/bảng đang mở* (chỉ trong list đó, vd EQUIPMENT_ESSENCE).
- Dòng nào sau khi đổi **vượt độ dài field** (vd `wstr[32]` → tối đa 31 ký tự) thì **bỏ qua, giữ nguyên chữ gốc** và chuyển sang dòng khác; field GBK (đường dẫn icon/model) không được đổi.

Kiểm tra nhanh (chỉ đọc): `ElementsCli data <thư mục element\data>` – in định dạng, số dòng và OK/SAI cho từng file. Đặc tả định dạng: `research/formats/*.md`.

## Quy trình Việt hoá
1. **Công cụ → Xuất văn bản (Ctrl+E)**:
   - *Glossary*: gộp chuỗi trùng (≈115k chuỗi thay vì 176k) – dịch một lần áp dụng mọi nơi. Khuyên dùng.
   - *Chi tiết*: mỗi vị trí một dòng (Key, ID, Field) – để dịch riêng từng chỗ.
2. Điền cột **Translation** (Excel / Google Sheets / script máy dịch). Giữ nguyên mã màu `^ff6000`, `\n`.
   Không vượt **MaxLen** (tên item thường 31 ký tự; câu thoại NPC không giới hạn).
3. **Công cụ → Nhập bản dịch (Ctrl+I)** → xem báo cáo (chuỗi quá dài bị bỏ qua và liệt kê) → **Lưu (Ctrl+S)**.

> Excel: mở CSV bằng *Data → From Text/CSV*, chọn UTF-8 và kiểu *Text* cho mọi cột để Excel không đổi `-5`, `+10%` thành số/công thức.

## CLI (dùng cho script / dịch hàng loạt)
```
ElementsCli info    <elements.data>
ElementsCli verify  <elements.data>
ElementsCli export  <elements.data> out.csv [--glossary] [--all] [--lists 0,3,9]
ElementsCli import  <elements.data> in.csv [--out new.data]
ElementsCli data    <file|thư mục> [module]      # gshop, domain, path, task_npc, vipaward… (chỉ đọc)
```

## Cấu trúc
```
src/ElementsCore   đọc/ghi file, schema, codec field, talk_proc, CSV
src/ElementsEditor giao diện WinForms
src/ElementsCli    công cụ dòng lệnh
schemas/           layout JSON (trích từ JD IDE) – sửa/bổ sung để thêm field
research/          ghi chú định dạng + script Python khảo sát
```
Định dạng file: xem [research/ELEMENTS_FORMAT.md](research/ELEMENTS_FORMAT.md).

## Lưu ý
- Thư mục `schemas/` lấy từ dữ liệu nhúng trong JD IDE – chỉ dùng cá nhân.
- Client đọc `elements.data`; **server cũng có bản elements.data riêng** – sửa chỉ số (không chỉ chữ) thì phải đồng bộ cả hai.
- Font game phải hỗ trợ tiếng Việt có dấu, nếu không chữ sẽ hiện ô vuông.
- Nhiều văn bản khác không nằm trong elements.data – theo cách tổ chức thường thấy ở engine PW: mô tả item (`item_ext_desc.txt` trong configs), nhiệm vụ (`tasks.data`), giao diện (`interfaces.pck`). Chưa kiểm tra trên client này.
# trutien_zhuxian_edit_elements
