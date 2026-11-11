# domain*.data — map domains (territory / guild war)

Implemented in `src/ElementsCore/Formats/DomainFile.cs`. Verified byte-exact on all 7 TT3 client samples.
Little-endian, no padding, no version field. All numbers are `i32`.

| File | Use (per JD IDE) | Domains | Battle times |
|---|---|---|---|
| domain.data | Guild map | 20 | 7 |
| domain1.data | Territory war | 50 | 7 |
| domain2.data | World map: cross-server | 2 | 7 |
| domain3.data | World map: overall | 1 | 7 |
| domain4.data | World map: ancient world | 6 | 0 (perSlot 0) |
| domain5.data | (not named by JD IDE) | 2 | 7 |
| domainlandwar.data | Land war | 24 | 7 |

## Layout

```
0x00 char[4]  "MOXB"
0x04 i32      domainCount
     domainCount × Domain (variable size)
     i32      battleTimeCount
     battleTimeCount × { i32 weekday; i32 hour; i32 minute }   // 12 B
     i32      battlesPerTimeSlot      // JD IDE: "timeMax" / "the battles per time slot"
     (EOF — no samples have trailing bytes)

Domain:
  +0   wchar[16] name        UTF-16LE, NUL-padded (15 chars max)
  +32  i32 id                server domain id (not sequential; e.g. 401, 455 in domain.data)
  +36  i32 type              1/2/3 — rank: capitals 3, sect cities 2, ordinary 1 (domain1/landwar)
  +40  i32 reward            0 in every sample
  +44  i32 centerX, centerY  label / click point, map-image pixels
  +52  i32 vertCount;      vertCount × { i32 x; i32 y }         outline polygon
       i32 faceCount;      faceCount × { i32 a; i32 b; i32 c }  triangle indices into verts
       i32 neighbourCount; neighbourCount × i32 domainId
```

Observed invariants (all samples): faceCount = vertCount − 2 (ear-clipped polygon), all face indices
< vertCount, neighbour lists symmetric, no self-neighbour, ids unique within a file.
Battle times in every non-empty sample: Sat/Fri/Sun 20:00, Sat/Sun 14:00, Sat/Sun 09:00; perSlot = 7.
`weekday` is assumed to be tm_wday (0 = Sunday, 6 = Saturday).

JD IDE names (strings): struct DomainData{magic, domains, times, timeMax}, Domain{id, name, type, reward,
center?, verts, faces, neighbours, …} ("9 elements" — one more than the 8 stored fields; likely an
in-memory extra), SevDomain (5 elements) = server-side domain.sev export, not this file.

## Editor model (DomainFile)
- `Domains`: id, name (wstr[16]), type (enum), reward, center_x, center_y, verts[x,y], faces[v0,v1,v2], neighbours[domain_id].
- `Battle times`: weekday (enum), hour, minute.
- `Header` (1 row): battles_per_slot, trailing (bytes after the time section; empty in samples).
- If a file ended right after the domains, the time section is omitted on save unless edited.
- `Validate()` reports: duplicate ids, < 3 verts or no faces, face index out of range, self / duplicate /
  unknown / one-way neighbours, invalid battle time.

## Open questions
- Meaning of `reward` (always 0) and exact semantics of `type` (rank 1–3 assumed).
- Whether the client needs faces regenerated after outline edits (editor does not re-triangulate).
- Meaning of `battlesPerTimeSlot` exactly (max simultaneous battles?) and whether domain4's empty
  section disables battles on that map.
- configs\land.txt / landwar.txt (server-side class/declaration items per domain) are separate files.
