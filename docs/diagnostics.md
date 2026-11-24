# Developer diagnostics

Release builds disable F9, the TCP listener and screenshots. Build explicitly with `-p:EnableDiagnostics=true -p:OutputPath=bin/Diagnostics/` to enable them. F9 writes under `%LOCALAPPDATA%/RNSClipboardProbe/samples`.

## Local query protocol version 2

The listener binds only to `127.0.0.1:47731`. Send one UTF-8 line ending in `\n`;
read one line of JSON and close the connection. Legacy missing values return the
literal `missing`; JSON mode always returns an object with `ok`.
One request is served per connection. Use a 20-second client timeout and batch
sequential queries instead of opening many connections.

```text
help
global.itemCd[6][0].Length
global.itemCd[6][0].Type
global.squareMap.RawType
global.squareMap.Keys
global.squareMap.Keys[0].Type
global.squareMap[?15]
global.languageMap[?"itemset_daynight"]
obj_itemSystem[0].slots[2][0]
```

Array indexes and instance selectors start at zero. `.Type` returns a normalized
type such as `array`, `real`, `string`, `bool`, `struct`, `instance`, `ds_map`, or
`ref`. `.RawType` preserves the `RValueType` enum name. Unknown handles stay `ref`;
the probe never guesses that a numeric ID is a map. Typed maps are identified from
the engine's `string(handle)` representation, and structs from `typeof` / `is_struct`.
Methods are distinguished from structs when the engine identifies them as methods.
The query layer converts Unicode string keys through JSON escapes to avoid the
installed RNSReloaded library's ANSI string-construction path.

`.Length` returns array length, engine string length, map size, or the number of
own fields on a struct/instance. `.Keys` returns the map keys or own field names
as an array. Use `keys` JSON pagination for array indexes or large key lists.
Metadata names are reserved only in dotted syntax: `["Type"]`, `["Length"]`,
and `["Keys"]` read literal struct/instance fields. Map keys use `[?15]` for numeric
keys and `[?"15"]` for string keys; these are distinct. `["key"]` is also supported
on maps and structs. Quoted keys use JSON escaping, support Unicode, and may
contain dots and brackets. Paths are not GML expressions and cannot call game
functions or modify game variables.

### JSON operations

```json
{"op":"inspect","path":"global.squareMap"}
{"op":"read","path":"global.itemData[8][0][3]"}
{"op":"keys","path":"global.languageMap","offset":0,"limit":32}
{"op":"page","path":"global.itemData","offset":6,"limit":4,"depth":3}
{"op":"batch","queries":[{"path":"global.itemCd[6][0].Length"},{"path":"global.squareMap.Type"}]}
```

`read` is the default operation. `inspect` returns `type`, `rawType`, `engineType`
(for objects/references), the engine handle text, `isStruct`, and `length` when
applicable. `page` returns `total`, `offset`, `count`, `nextOffset`, and `items`
containing `{key,value}` entries. `keys` uses the same pagination fields with key
values in `items`. A JSON array of requests is shorthand for `batch`.
Each batch entry has its own success or error result; nested batches are rejected.
Map/struct order is not guaranteed. Do not change the screen while walking pages
if you need a consistent snapshot. A batch runs in one hook callback, but does
not promise that a screenshot represents exactly the same rendered frame.

Strings are no longer shortened to 77 characters in nested arrays. Strings above
65536 UTF-16 code units have an explicit truncation object. Arrays longer than
64 entries or below the requested depth return `{array:length,truncated:true}`;
use `page` to read their entries. Structs/instances above 64 fields and maps return
a typed summary; use `page` for contents. Export is bounded by depth and a node
budget, so recursive objects cannot cause unbounded traversal. Undefined is
`{"type":"undefined"}` in JSON reads, null is JSON null, and absent paths are
`{"ok":false,"error":{"code":"missing",...}}`.

Limits: 65536 UTF-8 request bytes including newline, 64 batch entries, 256 entries
per page, depth 0–6 (default 2), 4096 serialized nodes per read/page, and 4 MiB per
reply. Over-limit replies return `response_too_large`; reduce page size, depth,
or batch size. Do not use integers above JavaScript's exact-number range as
numeric path keys. Page entries preserve engine int64 values as JSON numbers;
clients that need exact int64 values must use a lossless JSON decoder.

### Screenshots

```text
screenshot("C:/Temp/rns-example.png")
```

Equivalent JSON:

```json
{"op":"screenshot","path":"C:/Temp/rns-example.png"}
```

The path must be absolute and end in `.png`. Missing parent directories are
created; existing files are not overwritten. Success returns `path`, `capturedAt`,
and `bytes`. Capture targets only the game process's window. It tries PrintWindow
first; the screen-copy fallback requires the game window to be foreground. Keep
the game visible and unobscured for reliable output. A failed/black capture
returns an error rather than reporting success. A timeout cancels a queued
request; an already executing screenshot can still finish, so inspect its path
before retrying.

### Client and verification

`query.py` is a standard-library Python client. In PowerShell:

```powershell
python query.py 'global.itemCd[6][0].Length'
'{"op":"inspect","path":"global.squareMap"}' | python query.py
```

Build a separate diagnostics output with:

```powershell
dotnet build RNSClipboardProbe.csproj -c Release -p:EnableDiagnostics=true -p:OutputPath=bin/Diagnostics/ -p:RNSReloadedInterfacesDll="C:/path/to/Mods/RNSReloaded/RNSReloaded.Interfaces.dll"
```

This enables F9 and the localhost listener. F9 dumps go to `%LOCALAPPDATA%/RNSClipboardProbe/samples`. Install a diagnostics build only while developing, and replace it with the normal release build afterwards. `query.py` is in this repository's root; run it as `python query.py ...`.
