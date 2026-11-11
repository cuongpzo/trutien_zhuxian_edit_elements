using System.Text;

namespace ElementsCore.Formats;

/// <summary>
/// dyn_tasks.data — "dynamic task" pack (PW engine TaskTempl, marshalled compactly; in TT3 these are
/// the gift / special-award tasks such as 诛仙的馈赠). All little-endian, no padding.
/// <code>
/// Header (12 B): u32 fileSize; u32 timestamp (Unix); u16 version (13); u16 topLevelTaskCount
/// Task (recursive):
///   u32 mask; u32 mask2; u8 dynType; [u32 specialAward if dynType == 1]; u32 id;
///   u8 nameChars; wchar name[nameChars];
///   u8 flags[17]; u8 levelA; u8 levelB;
///   [mask &amp; 0x1000: u8 n; n × { u8 type; i32 start[6]; i32 end[6] }]   // year,month,day,hour,min,wday
///   u8 method; [method == 5: u32 waitSeconds]
///   u8 finishType; u32 awardMask;
///   [awardMask bit0..3: u32 gold, exp, sp, reputation]                  // inferred, unseen in samples
///   [awardMask &amp; 0x20: u8 nChoices; n × { u8 random; u8 nItems; nItems × Item(31 B) }]
///   u32 descChars + wchar[]; u32 successChars + wchar[]; u32 failureChars + wchar[];
///   5 × Talk { u32 promptBytes + bytes; u8 nWin; nWin × { u8 id; u8 parent; u32 textChars (incl. NUL) + wchar[];
///              u8 nOpt; nOpt × { u32 id; u32 param; u32 textBytes + bytes } } }
///   u32 subtaskCount; subtaskCount × Task
/// Item: u32 itemId; u8 common; u32 count; f32 prob; u8 flag; i32 periodSeconds; u8 extra[13]
/// </code>
/// Tasks are shown flattened in depth-first order with a <c>depth</c> column (subtask counts are recomputed).
/// Anything the parser does not understand (unknown mask bits, methods, award bits) makes the whole
/// task area fall back to one raw byte block so the file still round-trips byte-exact.
/// </summary>
public sealed class DynTasksFile : DataFile
{
    public const int HeaderSize = 12;
    public const uint MaskTimetable = 0x1000;
    public const uint AwardChoicesBit = 0x20;
    public const int FlagCount = 17, ItemExtraSize = 13, TalkCount = 5;

    private static readonly Encoding Utf16 = Encoding.Unicode;

    // ---------------- column definitions ----------------

    public static readonly IReadOnlyDictionary<long, string> DynTypeEnum = new Dictionary<long, string>
    {
        [0] = "Không", [1] = "Thưởng đặc biệt", [2] = "Thường",
    };
    public static readonly IReadOnlyDictionary<long, string> MethodEnum = new Dictionary<long, string>
    {
        [0] = "Không", [1] = "Giết quái", [2] = "Thu thập vật phẩm", [3] = "Nói chuyện NPC", [4] = "Đến địa điểm", [5] = "Chờ thời gian",
    };
    public static readonly IReadOnlyDictionary<long, string> FinishEnum = new Dictionary<long, string>
    {
        [0] = "Tự hoàn thành", [1] = "Gặp NPC trả",
    };
    public static readonly IReadOnlyDictionary<long, string> TalkEnum = new Dictionary<long, string>
    {
        [0] = "Giao NV", [1] = "Chưa đủ điều kiện", [2] = "Giao vật phẩm", [3] = "Đang làm", [4] = "Trao thưởng",
    };
    public static readonly IReadOnlyDictionary<long, string> OptionEnum = new Dictionary<long, string>
    {
        [0x80000006] = "Nhận nhiệm vụ", [0x80000007] = "Hoàn thành / nhận thưởng", [0x80000011] = "Thoát / từ chối",
    };

    public const int TtType = 0, TtStart = 1, TtEnd = 7;
    private static readonly string[] TmParts = ["year", "month", "day", "hour", "minute", "wday"];
    private static readonly string[] TmNotes = ["Năm", "Tháng", "Ngày", "Giờ", "Phút", "Thứ (0 = CN)"];
    public static readonly IReadOnlyList<ColumnDef> TimetableColumns =
    [
        new() { Name = "type", Type = ColType.U8, Note = "Kiểu khung giờ (mẫu = 0)" },
        .. TmParts.Select((p, i) => new ColumnDef { Name = "start_" + p, Type = ColType.I32, Note = "Bắt đầu: " + TmNotes[i] }),
        .. TmParts.Select((p, i) => new ColumnDef { Name = "end_" + p, Type = ColType.I32, Note = "Kết thúc: " + TmNotes[i] }),
    ];

    public const int ChRandom = 0;
    public static readonly IReadOnlyList<ColumnDef> ChoiceColumns =
    [
        new() { Name = "random", Type = ColType.U8, Note = "Byte đầu nhóm (mẫu luôn 0; có lẽ = chọn ngẫu nhiên). Vật phẩm của nhóm ở tab items (cột choice = số thứ tự nhóm)" },
    ];

    public const int ItChoice = 0, ItId = 1, ItCommon = 2, ItCount = 3, ItProb = 4, ItFlag = 5, ItPeriod = 6, ItExtra = 7;
    public static readonly IReadOnlyList<ColumnDef> ItemColumns =
    [
        new() { Name = "choice", Type = ColType.U8, Note = "Thuộc nhóm lựa chọn số mấy (đếm từ 0, xem tab choices)" },
        new() { Name = "item_id", Type = ColType.U32, Note = "ID vật phẩm (elements.data)" },
        new() { Name = "common", Type = ColType.U8, Note = "Vật phẩm thường (0/1)" },
        new() { Name = "count", Type = ColType.U32, Note = "Số lượng" },
        new() { Name = "prob", Type = ColType.F32, Note = "Xác suất (1 = 100%)" },
        new() { Name = "flag", Type = ColType.U8, Note = "Cờ (0/1, có thể là khoá/bind – chưa chắc)" },
        new() { Name = "period", Type = ColType.I32, Note = "Thời hạn (giây, 0 = vĩnh viễn; 604800 = 7 ngày)" },
        new() { Name = "extra", Type = ColType.Bytes, Length = ItemExtraSize, HideInGrid = true, Note = "13 byte chưa rõ (giữ nguyên)" },
    ];

    public const int WTalk = 0, WId = 1, WParent = 2, WText = 3;
    public static readonly IReadOnlyList<ColumnDef> WindowColumns =
    [
        new() { Name = "talk", Type = ColType.U8, Enum = TalkEnum, Note = "Thuộc hội thoại nào (0–4)" },
        new() { Name = "id", Type = ColType.U8, Note = "ID cửa sổ" },
        new() { Name = "parent", Type = ColType.U8, Note = "Cửa sổ cha (255 = gốc)" },
        new() { Name = "text", Type = ColType.WStr, Note = "Lời thoại NPC" },
    ];

    public const int OTalk = 0, OWindow = 1, OId = 2, OParam = 3, OText = 4;
    public static readonly IReadOnlyList<ColumnDef> OptionColumns =
    [
        new() { Name = "talk", Type = ColType.U8, Enum = TalkEnum, Note = "Thuộc hội thoại nào (0–4)" },
        new() { Name = "window", Type = ColType.U8, Note = "Cửa sổ thứ mấy trong hội thoại đó (đếm từ 0)" },
        new() { Name = "id", Type = ColType.U32, Enum = OptionEnum, Note = "Lệnh của lựa chọn" },
        new() { Name = "param", Type = ColType.U32, Note = "Tham số (thường = ID nhiệm vụ)" },
        new() { Name = "text", Type = ColType.WStr, Note = "Chữ trên nút lựa chọn" },
    ];

    public const int CDepth = 0, CMask = 1, CMask2 = 2, CType = 3, CSpecial = 4, CId = 5, CName = 6, CFlags = 7,
        CLevelA = 8, CLevelB = 9, CTimetable = 10, CMethod = 11, CWait = 12, CFinish = 13, CAwardMask = 14,
        CGold = 15, CExp = 16, CSp = 17, CRep = 18, CChoices = 19, CItems = 20, CDesc = 21, CSuccess = 22, CFailure = 23,
        CPrompt0 = 24, CWindows = 29, COptions = 30, CSubtasks = 31;

    public static readonly IReadOnlyList<ColumnDef> TaskColumns =
    [
        new() { Name = "depth", Type = ColType.U8, HideInGrid = true, Note = "Cấp lồng: 0 = NV gốc, 1 = NV con của dòng cấp 0 phía trên…" },
        new() { Name = "mask", Type = ColType.U32, HideInGrid = true, Note = "Mặt nạ trường tuỳ chọn (0x1000 = có lịch thời gian, tự bật khi có dòng timetable)" },
        new() { Name = "mask2", Type = ColType.U32, HideInGrid = true, Note = "Mặt nạ thứ hai (mẫu luôn 0; khác 0 chưa hỗ trợ)" },
        new() { Name = "dyn_type", Type = ColType.U8, Enum = DynTypeEnum, Note = "Loại NV động" },
        new() { Name = "special_award", Type = ColType.U32, Note = "Số thưởng đặc biệt (chỉ ghi khi dyn_type = 1)" },
        new() { Name = "id", Type = ColType.U32, Note = "ID nhiệm vụ" },
        new() { Name = "name", Type = ColType.WStr, Note = "Tên nhiệm vụ (tối đa 255 ký tự)" },
        new() { Name = "flags", Type = ColType.Bytes, Length = FlagCount, HideInGrid = true, Note = "17 cờ 0/1 (chưa đặt tên)" },
        new() { Name = "level_a", Type = ColType.U8, Note = "Byte cấp độ A (có vẻ là giới hạn cấp – chưa chắc min hay max)" },
        new() { Name = "level_b", Type = ColType.U8, Note = "Byte cấp độ B (có vẻ là giới hạn cấp – chưa chắc min hay max)" },
        new() { Name = "timetable", Type = ColType.Table, SubColumns = TimetableColumns, HideInGrid = true, Note = "Khung thời gian mở NV" },
        new() { Name = "method", Type = ColType.U8, Enum = MethodEnum, Note = "Cách hoàn thành (hỗ trợ 0, 3, 5)" },
        new() { Name = "wait_time", Type = ColType.U32, HideInGrid = true, Note = "Thời gian chờ (giây), chỉ ghi khi method = 5" },
        new() { Name = "finish_type", Type = ColType.U8, Enum = FinishEnum, Note = "Kiểu trả nhiệm vụ" },
        new() { Name = "award_mask", Type = ColType.U32, HideInGrid = true, Note = "Bit thưởng: 1 vàng, 2 KN, 4 SP, 8 danh vọng (suy đoán), 0x20 vật phẩm (tự bật khi có nhóm)" },
        new() { Name = "award_gold", Type = ColType.U32, HideInGrid = true, Note = "Thưởng vàng (chỉ ghi khi award_mask có bit 1 – suy đoán)" },
        new() { Name = "award_exp", Type = ColType.U32, HideInGrid = true, Note = "Thưởng kinh nghiệm (bit 2 – suy đoán)" },
        new() { Name = "award_sp", Type = ColType.U32, HideInGrid = true, Note = "Thưởng SP (bit 4 – suy đoán)" },
        new() { Name = "award_reputation", Type = ColType.I32, HideInGrid = true, Note = "Thưởng danh vọng (bit 8 – suy đoán)" },
        new() { Name = "choices", Type = ColType.Table, SubColumns = ChoiceColumns, Note = "Các nhóm phần thưởng (người chơi chọn 1 nhóm)" },
        new() { Name = "items", Type = ColType.Table, SubColumns = ItemColumns, Note = "Vật phẩm thưởng, gắn vào nhóm bằng cột choice" },
        new() { Name = "description", Type = ColType.WStr, HideInGrid = true, Note = "Mô tả nhiệm vụ" },
        new() { Name = "success_text", Type = ColType.WStr, HideInGrid = true, Note = "Lời khi thành công" },
        new() { Name = "failure_text", Type = ColType.WStr, HideInGrid = true, Note = "Lời khi thất bại" },
        .. Enumerable.Range(0, TalkCount).Select(t => new ColumnDef
        {
            Name = $"prompt_{t}", Type = ColType.WStr, HideInGrid = true, Note = $"Tên hội thoại {t} ({TalkEnum[t]}), vd \"RootNode\"",
        }),
        new() { Name = "windows", Type = ColType.Table, SubColumns = WindowColumns, Note = "Cửa sổ thoại của 5 hội thoại" },
        new() { Name = "options", Type = ColType.Table, SubColumns = OptionColumns, Note = "Nút lựa chọn, gắn vào cửa sổ bằng talk + window" },
        new() { Name = "subtasks", Type = ColType.U32, ReadOnly = true, HideInGrid = true, Note = "Số NV con (tính lại khi lưu từ cột depth)" },
    ];

    public const int HSize = 0, HTimestamp = 1, HVersion = 2, HCount = 3;
    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "file_size", Type = ColType.U32, ReadOnly = true, Note = "Kích thước file (tính lại khi lưu)" },
        new() { Name = "timestamp", Type = ColType.U32, Note = "Thời điểm xuất file (Unix time)" },
        new() { Name = "version", Type = ColType.U16, Note = "Phiên bản định dạng (client TT3 = 13)" },
        new() { Name = "task_count", Type = ColType.U16, ReadOnly = true, Note = "Số NV gốc (tính lại khi lưu)" },
    ];

    public static readonly IReadOnlyList<ColumnDef> RawColumns =
    [
        new() { Name = "data", Type = ColType.Bytes, ReadOnly = true, Note = "Toàn bộ vùng nhiệm vụ (chưa giải mã được, giữ nguyên)" },
    ];

    // ---------------- model ----------------

    public bool IsRaw { get; private set; }
    public override string FormatName => IsRaw ? "dyn_tasks.data (thô – chưa giải mã)" : $"dyn_tasks.data (v{Header.Rows[0].Long(HVersion)}, {Tasks.Rows.Count} NV)";
    public RecordTable Tasks => Tables[0];
    public RecordTable Header => Tables[^1];

    public static bool Sniff(byte[] d)
    {
        if (d.Length < HeaderSize) return false;
        if (BitConverter.ToUInt32(d, 0) != (uint)d.Length) return false;
        ushort ver = BitConverter.ToUInt16(d, 8), count = BitConverter.ToUInt16(d, 10);
        if (ver == 0 || ver > 255) return false;
        if (count == 0) return d.Length == HeaderSize;
        // first task: two masks then a dynamic type 0..2
        return d.Length > HeaderSize + 9 && d[HeaderSize + 8] <= 2;
    }

    public static DynTasksFile Parse(byte[] data, string path)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("File quá ngắn so với header dyn_tasks.data (12 byte).");
        var r = new BinReader(data);
        var h = new Record(HeaderColumns.Count);
        h.Values[HSize] = r.U32();
        h.Values[HTimestamp] = r.U32();
        h.Values[HVersion] = r.U16();
        h.Values[HCount] = r.U16();
        var f = new DynTasksFile { Path = path };
        if ((uint)h.Values[HSize]! != (uint)data.Length)
            f.Warnings.Add($"Header ghi kích thước {h.Values[HSize]} nhưng file dài {data.Length} byte (sẽ sửa khi lưu).");
        if ((ushort)h.Values[HVersion]! != 13)
            f.Warnings.Add($"Phiên bản {h.Values[HVersion]} (client TT3 dùng 13) – có thể đọc sai.");
        var header = new RecordTable { Name = "Header", Columns = HeaderColumns, CanAddRemove = false, Note = "Giá trị chung của file." };
        header.Rows.Add(h);

        int count = (ushort)h.Values[HCount]!;
        var tasks = new List<Record>();
        string? error = null;
        try
        {
            for (int i = 0; i < count; i++) ReadTask(r, 0, tasks);
            if (!r.AtEnd) throw new InvalidDataException($"Còn {r.Remaining} byte sau nhiệm vụ cuối (offset {r.Pos}).");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            error = ex.Message;
        }

        if (error == null)
        {
            f.Tables.Add(new RecordTable
            {
                Name = "Tasks", Columns = TaskColumns, Rows = tasks, KeyColumn = CId, NameColumn = CName,
                Note = "Mỗi dòng một nhiệm vụ động (NV con nằm ngay sau NV cha, cột depth lớn hơn).",
            });
            var dup = tasks.GroupBy(x => x.Long(CId)).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
            if (dup.Count > 0) f.Warnings.Add("ID nhiệm vụ trùng: " + string.Join(", ", dup));
        }
        else
        {
            f.IsRaw = true;
            f.Warnings.Add("Không giải mã được vùng nhiệm vụ (" + error + ") – chỉ giữ nguyên dạng thô, vẫn lưu đúng từng byte.");
            var raw = new Record(1);
            raw.Values[0] = data.AsSpan(HeaderSize).ToArray();
            f.Tables.Add(new RecordTable { Name = "Raw", Columns = RawColumns, CanAddRemove = false, Note = "Dữ liệu thô." });
            f.Tables[0].Rows.Add(raw);
        }
        f.Tables.Add(header);
        return f;
    }

    // ---------------- reading ----------------

    private static void ReadTask(BinReader r, int depth, List<Record> output)
    {
        if (depth > 32) throw new InvalidDataException("Nhiệm vụ con lồng quá sâu.");
        int start = r.Pos;
        var t = Record.CreateDefault(TaskColumns);
        t.Values[CDepth] = (byte)depth;
        uint mask = r.U32(), mask2 = r.U32();
        t.Values[CMask] = mask;
        t.Values[CMask2] = mask2;
        if ((mask & ~MaskTimetable) != 0 || mask2 != 0)
            throw new InvalidDataException($"NV tại offset {start}: mask 0x{mask:X}/0x{mask2:X} có bit chưa hỗ trợ.");
        byte type = r.U8();
        t.Values[CType] = type;
        if (type == 1) t.Values[CSpecial] = r.U32();
        t.Values[CId] = r.U32();
        ReadText(r, t, CName, r.U8() * 2);
        t.Values[CFlags] = r.Bytes(FlagCount);
        t.Values[CLevelA] = r.U8();
        t.Values[CLevelB] = r.U8();

        if ((mask & MaskTimetable) != 0)
        {
            int n = r.U8();
            var tt = t.Table(CTimetable);
            for (int i = 0; i < n; i++) tt.Add(RecordIO.ReadFixed(r, TimetableColumns));
        }

        byte method = r.U8();
        t.Values[CMethod] = method;
        switch (method)
        {
            case 0: case 3: break;
            case 5: t.Values[CWait] = r.U32(); break;
            default: throw new InvalidDataException($"NV {t.Values[CId]}: method {method} chưa hỗ trợ.");
        }
        t.Values[CFinish] = r.U8();
        uint am = r.U32();
        t.Values[CAwardMask] = am;
        if ((am & ~(0xFu | AwardChoicesBit)) != 0) throw new InvalidDataException($"NV {t.Values[CId]}: award_mask 0x{am:X} có bit chưa hỗ trợ.");
        if ((am & 1) != 0) t.Values[CGold] = r.U32();
        if ((am & 2) != 0) t.Values[CExp] = r.U32();
        if ((am & 4) != 0) t.Values[CSp] = r.U32();
        if ((am & 8) != 0) t.Values[CRep] = r.I32();
        if ((am & AwardChoicesBit) != 0)
        {
            int nc = r.U8();
            var choices = t.Table(CChoices);
            var items = t.Table(CItems);
            for (int c = 0; c < nc; c++)
            {
                var ch = new Record(ChoiceColumns.Count);
                ch.Values[ChRandom] = r.U8();
                choices.Add(ch);
                int ni = r.U8();
                for (int i = 0; i < ni; i++)
                {
                    var it = new Record(ItemColumns.Count);
                    it.Values[ItChoice] = (byte)c;
                    for (int k = ItId; k < ItemColumns.Count; k++) RecordIO.ReadCell(r, ItemColumns[k], it, k);
                    items.Add(it);
                }
            }
        }

        ReadText(r, t, CDesc, CheckedLen(r, 2));
        ReadText(r, t, CSuccess, CheckedLen(r, 2));
        ReadText(r, t, CFailure, CheckedLen(r, 2));

        var windows = t.Table(CWindows);
        var options = t.Table(COptions);
        for (int talk = 0; talk < TalkCount; talk++)
        {
            ReadText(r, t, CPrompt0 + talk, CheckedLen(r, 1));
            int nw = r.U8();
            for (int w = 0; w < nw; w++)
            {
                var win = new Record(WindowColumns.Count);
                win.Values[WTalk] = (byte)talk;
                win.Values[WId] = r.U8();
                win.Values[WParent] = r.U8();
                ReadText(r, win, WText, CheckedLen(r, 2));
                windows.Add(win);
                int no = r.U8();
                for (int o = 0; o < no; o++)
                {
                    var opt = new Record(OptionColumns.Count);
                    opt.Values[OTalk] = (byte)talk;
                    opt.Values[OWindow] = (byte)w;
                    opt.Values[OId] = r.U32();
                    opt.Values[OParam] = r.U32();
                    ReadText(r, opt, OText, CheckedLen(r, 1));
                    options.Add(opt);
                }
            }
        }

        uint subs = r.U32();
        if (subs > 10000) throw new InvalidDataException($"NV {t.Values[CId]}: số NV con không hợp lệ ({subs}).");
        t.Values[CSubtasks] = subs;
        output.Add(t);
        for (int i = 0; i < subs; i++) ReadTask(r, depth + 1, output);
    }

    /// <summary>Reads a u32 length (in units of <paramref name="unit"/> bytes) and returns the byte count.</summary>
    private static int CheckedLen(BinReader r, int unit)
    {
        uint n = r.U32();
        if ((long)n * unit > r.Remaining) throw new InvalidDataException($"Độ dài chuỗi {n} vượt quá dữ liệu còn lại (offset {r.Pos - 4}).");
        return (int)n * unit;
    }

    /// <summary>Reads UTF-16 text; the shown value stops at the first NUL, the raw bytes are kept for byte-exact output.</summary>
    private static void ReadText(BinReader r, Record rec, int col, int byteCount)
    {
        var raw = r.Bytes(byteCount);
        var s = Utf16.GetString(raw);
        int z = s.IndexOf('\0');
        if (z >= 0) s = s[..z];
        rec.Values[col] = s;
        rec.Raw[col] = raw;
    }

    // ---------------- writing ----------------

    public override byte[] ToBytes()
    {
        var h = Header.Rows[0];
        var w = new BinWriter();
        w.U32(0); // size, patched below
        w.U32(Convert.ToUInt32(h.Values[HTimestamp]));
        w.U16(Convert.ToUInt16(h.Values[HVersion]));
        if (IsRaw)
        {
            w.U16(Convert.ToUInt16(h.Values[HCount]));
            w.Bytes((byte[]?)Tables[0].Rows[0].Values[0] ?? []);
        }
        else
        {
            var rows = Tasks.Rows;
            int top = rows.Count(x => x.Long(CDepth) == 0);
            if (top > ushort.MaxValue) throw new InvalidDataException($"Một gói chứa tối đa {ushort.MaxValue} NV gốc.");
            w.U16((ushort)top);
            h.Values[HCount] = (ushort)top;
            int i = 0;
            while (i < rows.Count)
            {
                if (rows[i].Long(CDepth) != 0) throw new InvalidDataException($"Dòng {i}: NV đầu tiên / sau NV gốc phải có depth = 0 hoặc lớn hơn dòng trước tối đa 1.");
                i = WriteTask(w, rows, i, 0);
            }
        }
        w.PatchI32(0, w.Length);
        h.Values[HSize] = (uint)w.Length;
        return w.ToArray();
    }

    /// <summary>Writes rows[i] and its subtasks; returns the index of the next row after the subtree.</summary>
    private static int WriteTask(BinWriter w, List<Record> rows, int i, int depth)
    {
        var t = rows[i];
        string who = $"NV {t.Values[CId]} (dòng {i})";
        uint mask = Convert.ToUInt32(t.Values[CMask]);
        uint mask2 = Convert.ToUInt32(t.Values[CMask2]);
        var timetable = t.Table(CTimetable);
        if (timetable.Count > 0) mask |= MaskTimetable;
        if ((mask & ~MaskTimetable) != 0 || mask2 != 0) throw new InvalidDataException($"{who}: mask có bit chưa hỗ trợ.");
        w.U32(mask);
        w.U32(mask2);
        byte type = Convert.ToByte(t.Values[CType]);
        w.U8(type);
        if (type == 1) w.U32(Convert.ToUInt32(t.Values[CSpecial]));
        w.U32(Convert.ToUInt32(t.Values[CId]));

        var name = TextBytes(t, CName);
        if (name.Length / 2 > 255) throw new InvalidDataException($"{who}: tên dài quá 255 ký tự.");
        w.U8((byte)(name.Length / 2));
        w.Bytes(name);
        RecordIO.WriteCell(w, TaskColumns[CFlags], t, CFlags);
        w.U8(Convert.ToByte(t.Values[CLevelA]));
        w.U8(Convert.ToByte(t.Values[CLevelB]));

        if ((mask & MaskTimetable) != 0)
        {
            if (timetable.Count > 255) throw new InvalidDataException($"{who}: tối đa 255 khung thời gian.");
            w.U8((byte)timetable.Count);
            foreach (var e in timetable) RecordIO.WriteFixed(w, TimetableColumns, e);
        }

        byte method = Convert.ToByte(t.Values[CMethod]);
        if (method is not (0 or 3 or 5)) throw new InvalidDataException($"{who}: method {method} chưa hỗ trợ (chỉ 0, 3, 5).");
        w.U8(method);
        if (method == 5) w.U32(Convert.ToUInt32(t.Values[CWait]));
        w.U8(Convert.ToByte(t.Values[CFinish]));

        var choices = t.Table(CChoices);
        var items = t.Table(CItems);
        uint am = Convert.ToUInt32(t.Values[CAwardMask]);
        if (choices.Count > 0) am |= AwardChoicesBit;
        if ((am & ~(0xFu | AwardChoicesBit)) != 0) throw new InvalidDataException($"{who}: award_mask có bit chưa hỗ trợ.");
        w.U32(am);
        if ((am & 1) != 0) w.U32(Convert.ToUInt32(t.Values[CGold]));
        if ((am & 2) != 0) w.U32(Convert.ToUInt32(t.Values[CExp]));
        if ((am & 4) != 0) w.U32(Convert.ToUInt32(t.Values[CSp]));
        if ((am & 8) != 0) w.I32(Convert.ToInt32(t.Values[CRep]));
        if ((am & AwardChoicesBit) != 0)
        {
            if (choices.Count > 255) throw new InvalidDataException($"{who}: tối đa 255 nhóm thưởng.");
            if (items.FirstOrDefault(x => x.Long(ItChoice) >= choices.Count) is { } bad)
                throw new InvalidDataException($"{who}: vật phẩm {bad.Values[ItId]} gắn vào nhóm {bad.Values[ItChoice]} không tồn tại (có {choices.Count} nhóm).");
            w.U8((byte)choices.Count);
            for (int c = 0; c < choices.Count; c++)
            {
                w.U8(Convert.ToByte(choices[c].Values[ChRandom]));
                var list = items.Where(x => x.Long(ItChoice) == c).ToList();
                if (list.Count > 255) throw new InvalidDataException($"{who}: nhóm {c} có quá 255 vật phẩm.");
                w.U8((byte)list.Count);
                foreach (var it in list)
                    for (int k = ItId; k < ItemColumns.Count; k++) RecordIO.WriteCell(w, ItemColumns[k], it, k);
            }
        }
        else if (items.Count > 0) throw new InvalidDataException($"{who}: có vật phẩm thưởng nhưng chưa có nhóm (choices).");

        foreach (int col in new[] { CDesc, CSuccess, CFailure })
        {
            var b = TextBytes(t, col);
            w.I32(b.Length / 2);
            w.Bytes(b);
        }

        var windows = t.Table(CWindows);
        var options = t.Table(COptions);
        if (windows.FirstOrDefault(x => x.Long(WTalk) >= TalkCount) is not null || options.FirstOrDefault(x => x.Long(OTalk) >= TalkCount) is not null)
            throw new InvalidDataException($"{who}: cột talk phải từ 0 đến 4.");
        for (int talk = 0; talk < TalkCount; talk++)
        {
            var prompt = TextBytes(t, CPrompt0 + talk);
            w.I32(prompt.Length);
            w.Bytes(prompt);
            var wins = windows.Where(x => x.Long(WTalk) == talk).ToList();
            if (wins.Count > 255) throw new InvalidDataException($"{who}: hội thoại {talk} có quá 255 cửa sổ.");
            var opts = options.Where(x => x.Long(OTalk) == talk).ToList();
            if (opts.FirstOrDefault(x => x.Long(OWindow) >= wins.Count) is { } badOpt)
                throw new InvalidDataException($"{who}: lựa chọn \"{badOpt.Text(OText)}\" trỏ tới cửa sổ {badOpt.Values[OWindow]} không có trong hội thoại {talk}.");
            w.U8((byte)wins.Count);
            for (int k = 0; k < wins.Count; k++)
            {
                var win = wins[k];
                w.U8(Convert.ToByte(win.Values[WId]));
                w.U8(Convert.ToByte(win.Values[WParent]));
                var text = TextBytes(win, WText, nulTerminated: true);
                w.I32(text.Length / 2);
                w.Bytes(text);
                var mine = opts.Where(x => x.Long(OWindow) == k).ToList();
                if (mine.Count > 255) throw new InvalidDataException($"{who}: cửa sổ có quá 255 lựa chọn.");
                w.U8((byte)mine.Count);
                foreach (var o in mine)
                {
                    w.U32(Convert.ToUInt32(o.Values[OId]));
                    w.U32(Convert.ToUInt32(o.Values[OParam]));
                    var ot = TextBytes(o, OText);
                    w.I32(ot.Length);
                    w.Bytes(ot);
                }
            }
        }

        // subtasks: following rows with depth + 1, until a row at this depth or shallower
        int next = i + 1;
        var children = new List<int>();
        while (next < rows.Count)
        {
            long d = rows[next].Long(CDepth);
            if (d <= depth) break;
            if (d != depth + 1) throw new InvalidDataException($"Dòng {next}: depth {d} nhảy cóc (dòng cha có depth {depth}).");
            children.Add(next);
            next = SkipSubtree(rows, next, depth + 1);
        }
        w.U32((uint)children.Count);
        t.Values[CSubtasks] = (uint)children.Count;
        foreach (int c in children) WriteTask(w, rows, c, depth + 1);
        return next;
    }

    private static int SkipSubtree(List<Record> rows, int i, int depth)
    {
        int j = i + 1;
        while (j < rows.Count && rows[j].Long(CDepth) > depth) j++;
        return j;
    }

    /// <summary>Raw bytes when unchanged, else UTF-16LE of the value (plus a NUL for window texts).</summary>
    private static byte[] TextBytes(Record rec, int col, bool nulTerminated = false)
    {
        if (rec.Raw[col] is { } raw) return raw;
        var s = rec.Text(col);
        if (nulTerminated) s += "\0";
        return Utf16.GetBytes(s);
    }
}
