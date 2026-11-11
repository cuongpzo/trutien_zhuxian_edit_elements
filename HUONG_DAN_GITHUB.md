# Hướng dẫn GitHub — Tru Tien Zhuxian Elements Editor

Repository: <https://github.com/cuongpzo/trutien_zhuxian_edit_elements>

Các lệnh dưới đây dành cho **Git Bash** trên Windows. Mở Git Bash tại thư mục dự án (hoặc `cd` tới đó) trước khi chạy. Repo này đã dùng nhánh `main` và remote `origin` trỏ tới URL ở trên; không cần khởi tạo repo hay thêm remote lần nữa.

## 1. Kiểm tra repo trước khi thao tác

```bash
git status --short --branch
git remote -v
git branch --show-current
```

Xác nhận đang ở `main` và `origin` có URL đúng. Nếu GitHub yêu cầu đăng nhập khi push, hoàn tất xác thực trong trình duyệt.

## 2. Thêm code mới hoặc cập nhật code

Đảm bảo `.gitignore` bỏ qua file build và gói phát hành. Có thể xem nội dung bằng:

```bash
cat .gitignore
```

Trong repo này, `.build/`, `bin/`, `obj/`, `Release/`, file ZIP và một số ghi chú riêng được ignore. File đang bị ignore sẽ không xuất hiện khi dùng `git add -A`.

Xem thay đổi, rồi stage các thay đổi mong muốn:

```bash
git status --short
git add -A
git status --short
```

`git add -A` đưa file mới, cập nhật và các thao tác xóa vào vùng chuẩn bị commit. Đọc kỹ danh sách sau lệnh này. Nếu có file không muốn đưa lên, bỏ riêng file đó khỏi staging, ví dụ:

```bash
git restore --staged path/to/file
```

Khi danh sách đã đúng, commit và push:

```bash
git commit -m "Mô tả ngắn thay đổi"
git push origin main
```

Nếu Git báo không có gì để commit, kiểm tra `git status`; có thể chưa có thay đổi hoặc file đang bị `.gitignore` bỏ qua. Kiểm tra file bị ignore bằng:

```bash
git check-ignore -v path/to/file
```

Nếu cần đưa một file bị ignore lên repo, chỉ rõ file đó:

```bash
git add -f path/to/file
```

Chỉ dùng `-f` khi chắc chắn file phù hợp để công khai.

## 3. Xóa file khỏi GitHub nhưng giữ bản trên máy

Dùng `git rm --cached` cho file đã được commit trước đó. Ví dụ:

```bash
git rm --cached README.md
git status --short
git commit -m "Remove README from repository"
git push origin main
```

Sau khi push, file bị xóa khỏi phiên bản hiện tại trên GitHub nhưng vẫn còn trong thư mục local. Thay `README.md` bằng đường dẫn tương ứng. Xóa nhiều file trong cùng một commit:

```bash
git rm --cached file1.txt folder/file2.txt
```

## 4. Xóa thư mục khỏi GitHub nhưng giữ bản trên máy

```bash
git rm -r --cached path/to/folder
git status --short
git commit -m "Remove folder from repository"
git push origin main
```

`-r` áp dụng đệ quy cho toàn bộ file được Git theo dõi trong thư mục đó. Kiểm tra kỹ đường dẫn trước khi commit.

Nếu muốn xóa cả trên GitHub **và** máy local, bỏ `--cached`:

```bash
git rm -r path/to/folder
```

## 5. Ngăn file/thư mục bị thêm lại

Thêm quy tắc vào `.gitignore`, rồi commit quy tắc đó. Ví dụ:

```bash
printf '\n# Thư mục riêng không đưa lên GitHub\nprivate-folder/\n' >> .gitignore
git add .gitignore
git commit -m "Ignore private folder"
git push origin main
```

`.gitignore` không tự xóa nội dung đã commit. Muốn gỡ nội dung cũ khỏi phiên bản hiện tại trên GitHub, thực hiện bước 3 hoặc 4 rồi push.

## 6. Xác nhận kết quả

```bash
git status --short --branch
git log -5 --oneline
```

Sau khi push thành công, mở repo GitHub và tải lại trang để xác nhận commit/file. Nếu lỡ push mật khẩu, token hoặc khóa riêng tư, xóa khỏi phiên bản hiện tại là chưa đủ vì dữ liệu vẫn nằm trong lịch sử commit; hãy thu hồi/đổi thông tin bí mật ngay.
