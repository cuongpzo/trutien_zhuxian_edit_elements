# NHẬT KÝ KIẾN TRÚC & XỬ LÝ - TOOL EDIT ELEMENTS.DATA (TRU TIÊN 3)

## Tổng quan dự án `Tool_Edit_ElementsData_Claude`
- **Mục tiêu:** Đọc, kiểm tra toàn vẹn, Việt hóa (xuất/nhập CSV, tìm & thay thế hàng loạt, sửa từng field) và chỉnh sửa dữ liệu nhị phân `elements.data` (Tru Tiên 3 - version 165, signature `0x1000`).
- **Cấu trúc mã nguồn:**
  - `src\ElementsCore\`: Thư viện lõi (`ElementsFile.cs`, `FieldCodec.cs`, `TalkProc.cs`, `TextExchange.cs`, `ElementsOps.cs`, `Csv.cs`, `Schema\SchemaRepository.cs`).
  - `src\ElementsCli\`: Công cụ dòng lệnh (`info`, `verify`, `search`, `export-json`, `export`, `import`).
  - `src\ElementsEditor\`: Ứng dụng WinForms hoàn chỉnh (`MainForm.cs`, `MainForm.TalkProc.cs`, `Dialogs.cs`, `HexFormatter.cs`, `AppSettings.cs`).
  - `schemas\`: Chứa toàn bộ `layouts/` (`v112, v156, v158, v160, v165 [269 file], v176 [211 file]`), `enums/`, `masks/`.

## Các lưu ý kỹ thuật & Audit đã xác minh
1. **Format nhị phân v165:**
   - 8-byte header (`u16 version=165`, `u16 sig=0x1000`, `u32 timestamp`).
   - 318 list tuần tự `[u32 itemSize][u32 count][count * itemSize]` với 7 marker trước các list `13, 23, 36, 56, 62, 103, 296`.
   - Phần đuôi `TALK_PROC` (204 dialog NPC) nằm ngay sau list 317.
2. **An toàn khi Việt hóa:**
   - Chuỗi `wstr[N]` là UTF-16LE độ dài cố định (`N * 2` bytes, gồm `\0` kết thúc -> tối đa `N - 1` ký tự). `FieldCodec.SetText` kiểm tra cả `text.Length` và `Encoding.Unicode.GetBytes(text).Length` để không làm tràn buffer bản ghi.
   - `ElementsFile.Save()` luôn ghi ra `.tmp`, tạo backup `.bak_yyyyMMdd_HHmmss`, rồi dùng `File.Replace` để tránh hỏng file nếu mất điện/crash giữa chừng.
3. **WinForms Layout:**
   - Dùng `Panel` + `Splitter` thay `SplitContainer` để tránh lỗi GDI+ `0x80004005` (`RepaintSplitterRect`) khi lồng trong `TabPage` hoặc panel ẩn.
   - Dùng `DataGridView` `VirtualMode = true` + `DoubleBuffered` để hiển thị 34.767+ dòng của `EQUIPMENT_ESSENCE` tức thì.
4. **Liên kết Client - Server:**
   - `Server0619` chạy trên VM CentOS 7. Khi chỉ Việt hóa text (`name`, `title`, `desc`, `TALK_PROC`), sửa trên Client thường đủ; khi sửa chỉ số/ID/giá bán/tỷ lệ rơi, cần đồng bộ `elements.data` giữa Client và Server.
