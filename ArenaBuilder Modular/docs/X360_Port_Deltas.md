# ArenaBuilder X360 Port Deltas

Live tracking document for Xbox 360 (`.rx2`) output support. Notes every byte-level difference between PS3 (`.psg`) and X360 (`.rx2`) discovered while RE'ing both formats.

**Sources of truth**
- Sk2 X360 debug symbols: `sk82_na_zd.xex` (IDA port 13337). Source path strings confirm `target\xbox2\` builds — this binary IS the X360 RenderWare runtime.
- Sk2 X360 retail symbols: `sk82_na_m.xex` (OptiMesh offset RE per GLBtoRX2 author notes).
- Sk2 X360 source path cited: `D:\sk82\main\packages\rw\renderengine_base\1.04.03\source\core\target\xbox2\rwgvertexformat.cpp`.
- Stock matched PSG pairs at `Skate 3 Modding\Tools\DotBig\PSG` (PS3) and `…\XBOX\…\DIST_BlackBoxPark` (X360).
- Dumps via `psg_structure_dumper.py dump`.
- **Reference implementation (third-party, prior X360 RE):** `C:\Users\Ethans Desktop 2.0\Downloads\GLBtoRX2-v1.0.py` — patches an existing RX2 template; documents byte offsets, fetch-constant encoding, VertexFormat hashes, and platform quirks. Credit: SunJay, Dumbad, RenderWareGavin, Tuukkas.

Note: textures intentionally deferred — done by separate work.

---

## 1. Arena header & magic

| Offset | PS3 | X360 |
|---|---|---|
| 0x00..0x03 | `89 52 57 34` (`\x89RW4`) | same |
| 0x04..0x07 | `70 73 33 00` (`ps3\0`) | `78 62 32 00` (`xb2\0`) |
| 0x08..0x0B | `0D 0A 1A 0A` PNG-style EOL guard | same |
| 0x0C..0x0F | `01 20 04 00` version flags | same |
| 0x10..0x17 | `34 35 34 00 30 30 30 00` "454/000" | same |
| 0x34 sections offset | `0xC0` | `0xAC` (20 B earlier) |
| sec-desc alignment hint (texture-PSG only) | `0x800` (2048) | `0x2000` (8192) |
| BaseResource type ID | `0x00010034` | `0x00010031` (X360+Wii) |
| `graphics_baseresource_size` field | `0x6C` | `0x54` |
| **`headerTypeIdAt0x70` (mesh 0x10 / sim 1 / tex 0x80)** | **at `+0x70`** | **DOES NOT EXIST — `+0xA8` is `target_resources[5]`, ALWAYS 0** |

**⚠️⚠️ 2026-06-11 TYPE REGISTRY bug (every X360 arena — the "void everywhere" render killer):**
Mesh/collision composers passed the **PS3 64-entry** type registry (`CollisionTypeRegistry64`), which
includes the PS3-only BaseResource `0x00010034`. Stock X360 registries are **63 entries** and OMIT it
(`[5]` is `0x00010010`, not `0x00010034`). With the extra entry, EVERY dictionary `typeIdx` is shifted
+1 and the registry embeds a type the X360 loader can't resolve → the engine dispatches every object to
the wrong loader → arenas register but nothing draws/collides ANYWHERE. Fixed in `GeneralArenaBuilder`:
for X360, filter `0x00010034` out of the type registry (makes registry + all type-indices byte-match
stock). Symptom was: F5 render-adds climb (arenas register), but freecam shows void everywhere.

**⚠️ 2026-06-11 dict `+0x0C` bug (fixed):** stock X360 uses dict-entry `+0x0C` = `0x04` for GPU/
render-engine resources (BaseResource 0x1003x, VertexBuffer 0x200EA, IndexBuffer 0x200EB,
VertexDescriptor 0x200E9, MeshHelper 0x020081) and `0x10` for Pegasus metadata (0x00EBxxxx). Our
builder wrote `0x10` for everything. The `Xbox2GetPhysicalResource() != NULL` assert in
VertexBuffer/IndexBuffer Initialize implies `+0x0C` gates physical(GPU)-vs-main allocation — `0x10`
likely placed vertex/index data in main memory, leaving `m_baseResources[2]` (physical) NULL → no GPU
geometry → void. Fixed in `DictionaryEntryFlags` for X360 (PS3 stays 0x10 uniformly, which works there).

**⚠️ 2026-06-11 RX2 header bug (every X360 arena):** `GeneralArenaBuilder` wrote the PS3-only
`headerTypeIdAt0x70` value into X360 header `+0xA8`. But on X360 that offset is `target_resources[5]`
and is **0 in all stock arenas** (mesh/collision/texture verified). Writing `0x10` (mesh) / `1` (sim)
there put a bogus target-resource into every arena. Fixed to write 0. The X360 arena needs NO type-id
field — the engine reads object types from the dictionary, not a header field.

Section descriptor table (between +0x40 and graphics_baseresource_size) — X360 has 3 fewer 8-B records than PS3 (~24 B shorter section); script's "one fewer resource descriptor" comment confirmed via stock byte diff (`mesh_x360.rx2` 0x40..0x53 vs `mesh_ps3.psg` 0x40..0x6B). Builder must emit fewer descriptor records on X360 path.

**Builder fix points (PS3 currently hard-coded):**
- `GenericArenaWriter.cs:459` — magic literal `p,s,3` → `x,b,2`
- `RwTypeIds.cs:29` — `BaseResource = 0x00010034` → `0x00010031`
- `GenericArenaWriter.cs:438` — dict entry flags `0x00010034 => 0x80` → branch on platform
- `GenericArenaWriter.cs:474` — sections offset `0xC0` → `0xAC` for X360
- `GenericArenaWriter.cs` — `graphics_baseresource_size` write site moves from 0x6C → 0x54
- `Ps3RenderWareConstants.CollisionTypeRegistry64` already lists all platform IDs — no change

---

## 2. VertexBuffer (RW type `0x000200EA`)

### Load-time `renderengine::VertexBuffer::Initialize` @ `0x830cafb8` — DECOMPILED

Engine **zeros all 40 bytes** at the runtime VertexBuffer location, then writes:

```c
Common         = 1                          // overwrites disk value
ReferenceCount = 1                          // overwrites disk value
Fence          = 0                          // overwrites disk value
ReadFence      = 0                          // overwrites disk value
Identifier     = 0                          // overwrites disk value
BaseFlush      = 0xFFFF0000                 // overwrites disk value
Format.dword[0] = 3                         // overwrites disk value (gets ORed with 3)
m_bufferSize   = params->bufferSize         // from Parameters
m_type         = params->type               // from Parameters
XGSetVertexBufferHeader(bufferSize, 0, 1, buffer, vb); // sets Format.dword[0/1] for Xenos
```

**KEY: leading 32 B of disk image (D3DResource + GPUVERTEX_FETCH_CONSTANT) are IGNORED at load.** Engine zeros + rewrites them.

### `renderengine::VertexBuffer::Parameters` (8 B) — Sk2 IDA verified

| Offset | Field | Type |
|---|---|---|
| 0x00 | `type` | uint32 |
| 0x04 | `bufferSize` | uint32 |

### Stock PS3 (16 B Pegasus compact)

PS3 file Vertexbuffer[4] @ 0x4EB8:
```
0x00  00 00 00 03   m_baseResourceIndex = 3
0x04  00 00 00 00   reserved
0x08  00 00 0B 98   m_bufferSize = 2968
0x0C  00 00 00 00   m_flags
```

### Stock X360 (40 B = D3DVertexBuffer + 8 B trailing)

```
0x00..0x17  D3DResource (24 B template — engine overwrites)
   0x00  Common = 1                       → engine writes 1 (always)
   0x04  ReferenceCount = 1               → engine writes 1
   0x08-0x13 zeros
   0x14  BaseFlush = 0xFFFF0000           → engine writes 0xFFFF0000

0x18..0x1F  GPUVERTEX_FETCH_CONSTANT (8 B — engine overwrites via XGSetVertexBufferHeader)
   0x18  dword[0] varies per VB (0x0F, 0x2B, 0x47, 0x63) ← Xenos VA template; clobbered at load
   0x1C  dword[1] = 0x10000000 | (size + 2)              ← clobbered at load

0x20  m_bufferSize   ← READ by loader as params.bufferSize
0x24  m_type         ← READ by loader as params.type
```

### ArenaBuilder X360 form (clean from scratch)

**Only +0x20 and +0x24 must carry valid data.** Engine zeros + rewrites everything else.

```csharp
var buf = new byte[40];
// 0x00..0x1F: leave zero (or write template constants for diff-friendliness against stock)
BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x20, 4), bufferSize);
BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x24, 4), 0);  // m_type
```

For diff parity against stock, optionally write:
- 0x00 Common = 0x00000001
- 0x04 RefCount = 0x00000001
- 0x14 BaseFlush = 0xFFFF0000
- 0x1C fetch[1] = `0x10000000 | (size + 2)` (formula from GLBtoRX2; matches stock on 4 verified samples)
- 0x18 fetch[0] = leave zero (varies per VB; engine recomputes)

---

## 3. IndexBuffer (RW type `0x000200EB`)

### Load-time `renderengine::IndexBuffer::Initialize` @ `0x830c967c` — DECOMPILED

Engine **zeros all 36 bytes** then writes:

```c
Common = (depth == 32 ? 0xC0000000 : 0x20000000) | 2   // overwrites disk
ReferenceCount = 1                                      // overwrites disk
BaseFlush = 0xFFFF0000                                  // overwrites disk
Address = resource->m_baseResources[2]                  // overwrites disk (Xenos VA from raw data)
Size = (numIndices * (depth==16 ? 2 : 4) + 15) & ~0xF   // overwrites disk (16-B aligned byte size)
m_numIndices = params->numIndices                       // from Parameters
```

**`Common = 0x20000002` for 16-bit indices** ✓ matches stock observation exactly.

### `renderengine::IndexBuffer::Parameters` (12 B) — Sk2 IDA verified

| Offset | Field | Type |
|---|---|---|
| 0x00 | `type` | uint32 |
| 0x04 | `depth` | uint32 (16 or 32) |
| 0x08 | `numIndices` | uint32 |

### Stock PS3 (20 B Pegasus form)

PS3 file Indexbuffer[6] @ 0x4EC8:
```
0x00  00 00 00 05   m_baseResourceIndex = 5
0x04  00 00 00 00   reserved
0x08  00 00 00 FC   m_numIndices = 252
0x0C  00 00 00 02   m_indexFormat = 2 (PS3-only)
0x10  01 00 00 00   m_trailingFlags
```

### Stock X360 (36 B)

```
0x00..0x17  D3DResource (24 B — engine overwrites)
   0x00  Common = 0x20000002              ← engine writes (depth16=0x20.. + IB type=2)
   0x04  ReferenceCount = 1               ← engine writes
   0x14  BaseFlush = 0xFFFF0000           ← engine writes

0x18..0x1F  D3DIndexBuffer trailing
   0x18  Address (Xenos VA template)      ← engine writes from raw data
   0x1C  Size = ceil(numIndices×2, 16)    ← engine writes

0x20  m_numIndices   ← READ by loader as params.numIndices
```

### ArenaBuilder X360 form (clean from scratch)

**Only +0x20 must carry valid data.** Engine zeros + rewrites everything else.

```csharp
var buf = new byte[36];
BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x20, 4), numIndices);
```

For diff parity against stock, optionally write:
- 0x00 Common = 0x20000002 (depth=16, IB type)
- 0x04 RefCount = 0x00000001
- 0x14 BaseFlush = 0xFFFF0000
- 0x18 Address = leave zero (engine recomputes)
- 0x1C Size = `(numIndices*2 + 15) & ~0xF`

---

## 4. VertexDescriptor (RW type `0x000200E9`)

### Load-time `renderengine::VertexDescriptor::Initialize` @ `0x830cb460` — DECOMPILED

Engine **zeros the 32 B header** then iterates `params->elements[0..15]` (Parameters has 16 inline elements). For each element where `format != -1`:
- Copy element from Parameters to runtime VDesc.
- `m_typesFlags |= (1 << element.type)` — accumulated bitmask of all element.type values
- Compute per-stream stride via `renderengine::VertexFormatGetStride(format)`
- Special usage values (0xFF) fall back to `elementTypeParamsTable` defaults

After loop:
- `m_numElements = count of valid elements`
- `m_refCount = 1`
- Calls `VertexDescriptor::CreateD3DObject(v2)` to build D3DVertexDeclaration

**m_typesFlags = OR(1 << element[i].type for each i)** — recomputed at load.

Verification on stock VDesc[8]: elements types = {1, 6, 7, 21} →  
`(1<<1) | (1<<6) | (1<<7) | (1<<21) = 0x002000C2` ✓ **EXACT** match to stock byte value.

### `renderengine::VertexDescriptor::Parameters` (272 B)

| Offset | Field | Type |
|---|---|---|
| 0x00 | `elements[16]` | `Element` (16 B each) = 256 B |
| 0x100 | `strides[16]` | u8 |

### Stock X360 VDesc[8] decoded per IDA layout (not dumper output)

Header (16 B):
```
0x00  m_d3dVertexDeclaration = NULL (engine creates at load)
0x04  m_typesFlags = 0x002000C2 (recomputed at load)
0x08  m_numElements = 4
0x0A  m_refCount = 1 (engine writes)
0x0C  m_instanceStreams (engine writes 0)
0x0E  m_pad0 = 0
```

Elements at +0x10 (4 × 16 B):
| Idx | stream | offset | format | usage | type |
|---|---|---|---|---|---|
| 0 | 0 | 0 | `0x002A23B9` (FLOAT3) | 0 (POSITION) | 1 |
| 1 | 0 | 12 | `0x002C235F` (NOT in script table — likely USHORT2/SHORT2 variant) | 5 (TEXCOORD) | 6 |
| 2 | 0 | 16 | `0x001A215A` (SHORT4) | 5 (TEXCOORD) | 7 |
| 3 | 0 | 24 | `0x002A2190` (DEC3N variant) | 6 (TANGENT) | 21 |

Strides at +0x50: `1C 1C 1C 1C` (stride 28 for streams 0-3; only stream 0 used). Stride count is variable up to 16; only first N visible in stock.

### `renderengine::VertexFormat` hash table

19 verified entries from GLBtoRX2 script (cross-referenced with `VertexFormatGetStride` decompile @ `0x830c81a4`):

| VertexFormat | Hash hex | Stride | PSG VT_ |
|---|---|---|---|
| FLOAT1 | `0x002C84E4` | 4 | 0x02 |
| FLOAT2 | `0x002C2525` | 8 | 0x02 |
| FLOAT3 | `0x002A23B9` | 12 | 0x02 |
| FLOAT4 | `0x001A2326` | 16 | 0x02 |
| D3DCOLOR | `0x00182106` | 4 | 0x04 |
| UBYTE4 | `0x001A2206` | 4 | 0x04 |
| SHORT2 | `0x002C24D9` | 4 | 0x05 |
| SHORT4 | `0x001A22DA` | 8 | 0x05 |
| UBYTE4N | `0x001A2006` | 4 | 0x07 |
| BYTE4N | `0x001A2106` | 4 | 0x07 |
| SHORT2N | `0x002C22D9` | 4 | 0x05 |
| SHORT4N | `0x001A20DA` | 8 | 0x05 |
| USHORT2N | `0x002C21D9` | 4 | 0x05 |
| USHORT4N | `0x001A1FDA` | 8 | 0x05 |
| UDEC3 | `0x002A2287` | 4 | 0x06 |
| DEC3N | `0x002A2187` | 4 | 0x06 |
| DEC3N (variant) | `0x002A2190` | 4 | 0x06 |
| FLOAT16_2 | `0x002C24DF` | 4 | 0x03 |
| FLOAT16_4 | `0x001A22E0` | 8 | 0x03 |

### Extended hash table from disassembly of `VertexFormatGetStride` @ 0x830c81a4

`0x002C235F` **resolved** — stride 4 (per disasm at 0x830c8120 → `li r3, 4`). Likely **USHORT2** (unsigned non-normalized 2-component; sits between SHORT2N=`0x002C22D9` and SHORT2=`0x002C24D9`).

Additional stride-4 hashes extracted from disasm (still need name labels — only stride confirmed):

`0x001A2086`, `0x001A2087`, `0x001A2186`, `0x001A2287`, `0x002A2087`, `0x002A2090`, `0x002A2091`, `0x002A2191`, `0x002A2290`, `0x002A2291`, `0x002A2390`, `0x002A2391`, `0x002C2059`, `0x002C20A2`, `0x002C2259`, `0x002C2359`, `0x002C23A5`, `0x002C82A1`, `0x00014C86`, `0x00182886`

Stride-8 hashes additional to script table: `0x001A205A`, `0x001A21A3`, `0x001A22DA` (=SHORT4 already known), `0x001A23A3`, `0x001A23A6`.
Stride-12: `0x002A23B9` (= FLOAT3 known).
Stride-16: `0x001A2326` (= FLOAT4 known), large switch table @ `jpt_830C7C38` covers INT4/UINT4/FLOAT4 family.

### `D3DDECLUSAGE` (Element.usage byte) → PSG element-type remap

| usage | D3DDECLUSAGE | PSG element type |
|---|---|---|
| 0 | POSITION | XYZ (0) |
| 1 | BLENDWEIGHT | WEIGHTS (1) |
| 2 | BLENDINDICES | BONEINDICES (7) |
| 3 | NORMAL | NORMAL (2) |
| 5 | TEXCOORD | TEX0 (8) |
| 6 | TANGENT | TANGENT (14) |
| 7 | BINORMAL | BINORMAL (15) |
| 10 | COLOR | VERTEXCOLOR (3) |

### ⚠️ CRITICAL (2026-06-11): descriptor MUST match our 32-byte packer, NOT stock's 28-byte layout

`BuildStaticMeshLayout` was originally copied from the **stock BlackBoxPark** cPres descriptor: TEX0
USHORT2 (4 B) @12, TEX1 @16, TANGENT @24, **stride 28**. But `MeshVertexPacker` (shared with PS3) emits
a **32-byte** vertex: Position FLOAT3 @0, **TEX0 FLOAT2 (8 B) @12**, TEX1 i16×4 lm_norm @20, TANGENT
DEC3N @28. A descriptor claiming stride 28 over 32-byte data makes the GPU read every vertex at the wrong
offset → the mesh renders as garbage / is invisible (registers via AddRenderInstance but never draws).
**Symptom:** custom X360 map loads, F5 render-mesh-adds climbs, but nothing renders.

Fixed: X360 descriptor now declares the real 32-byte layout (FLOAT2 TEX0 @12 = `0x002C2525`, lm_norm
@20 = `0x001A215A` stock's exact hash, DEC3N_V @28, **4 stride bytes of 32**), mirroring the working PS3
`VertexDescriptorBuilder`. Verified: our descriptor is now 84 B with offsets 0/12/20/28 and stride 32 —
format hashes match stock where the format is identical; only TEX0 differs (FLOAT2 full-precision UV vs
stock's quantized USHORT2, same choice the PS3 path makes). **The descriptor must always match whatever
`MeshVertexPacker` packs, never stock's layout.**

### ArenaBuilder X360 form

```csharp
// 16 B header
WriteU32(buf, 0x00, 0);                    // m_d3dVertexDeclaration NULL
WriteU32(buf, 0x04, 0);                    // m_typesFlags recomputed at load (or precompute for diff parity)
WriteU16(buf, 0x08, (ushort)numElements);
WriteU16(buf, 0x0A, 0);                    // m_refCount overwritten at load
WriteU16(buf, 0x0C, 0);                    // m_instanceStreams
WriteU16(buf, 0x0E, 0);                    // m_pad0

// 16 B per element at +0x10..
for each element:
    WriteU16(buf, off + 0, stream);
    WriteU16(buf, off + 2, offsetInVertex);
    WriteU32(buf, off + 4, vertexFormatHash); // from §4 table
    WriteU8 (buf, off + 8, method);            // usually 0
    WriteU8 (buf, off + 9, d3ddeclusage);      // from §4 remap
    WriteU8 (buf, off + 10, usageIndex);       // usually 0
    WriteU8 (buf, off + 11, pegasusElemType);  // 0=XYZ, 8=TEX0, 14=TANGENT, etc.
    WriteU32(buf, off + 12, elementClass);     // typically 1

// Strides u8[16] at +0x10 + 16*numElements
// Only stream 0 used: write stride at strides[0], rest 0 (engine ignores)
```

For diff parity:
- Precompute m_typesFlags = `OR(1 << e.type for e in elements)`.
- m_refCount: stock has 1; engine writes 1 anyway. Either works.

---

## 5. Vertex-data packing quirks (per-vertex bytes inside VB raw blob)

Non-struct deltas; ArenaBuilder.Mesh per-vertex packer must branch:

1. **XYZ width** (GLBtoRX2 line 1142): X360 packs **4 shorts** (x, y, z, w=0); PS3 packs 3. Both use signed-normalized SHORT4N.
2. **Blend indices/weights byte order** (GLBtoRX2 line 109): reversed on X360 vs PS3. X360 packs natural `>BBBB` (j[0..3]); PS3 packs reversed.
3. **Binormal sign** (GLBtoRX2 line 1308): X360 uses `cross(tangent, normal)`; PS3 uses `cross(normal, tangent)`.

---

## 6. tROptiMeshData inline padding (X360 116 B vs PS3 112 B)

Both runtime structs are 96 B (Sk2 IDA verified). Both inline an IslandDrawParams block (16 B) at offset +0x60 after the struct. Delta in trailing layout:

- **PS3 112 B**: `m_pRemapTable` points to +0x6C (inside last 4 B of IslandDrawParams trailing pad — overlapped). Remap data sits within the 16-B inline block.
- **X360 116 B**: `m_pRemapTable` points to +0x70 (own slot after the 16 B IslandDrawParams). 4 B alignment slot.

**ArenaBuilder X360**: emit struct + IslandDrawParams + 4-B RemapTable slot at +0x70 instead of PS3 overlap at +0x6C. Total 116 B.

---

## 7. Confirmed cross-platform clean (no per-platform builder change)

- `pegasus::tCollisionModelData` / cClusteredMesh: identical byte size + structure.
- `pegasus::tIrradianceData` + `tIrradianceProbeData`: identical.
- `pegasus::tAIPathData` + NavPowerData: identical.

These three families only need the global header + BaseResource type swap; their builders stay as-is.

---

## 8. Texture (IMPLEMENTED 2026-06-11)

X360 (.rx2) texture building is done and verified **byte-for-byte against stock** `DIST_BlackBoxPark`
cTex .rx2 (256×256 DXT1, 9 mips). Output is identical to stock except the three asset-identity fields
that must differ (arena_id, texture GUID + TOC name, one stock uninitialized pad byte).

**Pipeline.** Routed through the normal `PsgArenaSpec` → `GeneralArenaBuilder.Write(…, Xbox360)` path,
exactly like PS3 textures — no separate writer. `TextureRX2Composer.Compose` builds the spec;
`ArenaBuilder.Texture/Xbox/` holds the X360-only pieces.

**Texture-specific deltas vs PS3 (all in `ArenaBuilder.Texture/Xbox/`):**
- **Xenos tiling + fetch constant** (`XenosTextureTiler.cs`) — port of the compressed path of
  `dds_to_x360.py` (5C8/RW4-TextureArena-creator-in-python). Re-tiles linear DXT blocks into the
  Xenos 32×32 swizzle (`get_xbox360_tiled_offset` + `tile_level`, endian-mode-1 16-bit-pair swap) and
  builds the 24-byte `GPUTEXTURE_FETCH_CONSTANT` (`generate_header`: bit-reversed field packing +
  whole-DWORD bit reversal). All six fetch DWORDs verified byte-exact against the stock 256² file.
  Packed-mip (≤16px) offsets handled per the script's square/wider branches.
- **Texture-info struct** (`XboxTextureInfoBuilder.cs`, RW type 0x000200E8, **0x34 B**): seven BE u32
  `[3, 1, 0, 0, 0, 0xFFFF0000, 0xFFFF0000]` + 24-byte fetch constant. (PS3 is the 0x28-byte
  TextureInformationPS3.)
- **Type registry** (`XboxTextureConstants.cs`): **9 entries** (PS3 has 10 — X360 drops the PS3-only
  BaseResource 0x00010034). BaseResource type = **0x00010031**.

**Arena-framing deltas (fixed in `GeneralArenaBuilder`, compact-texture path only, guarded so PS3 +
mesh/collision bytes are unchanged):**
- Compact section offsets shift −4 vs PS3 because the 9-entry type list is 4 B shorter:
  ext@0x4C, subref@0x70, atoms@0x8C, total sections 0x98 → texture-info lands at 0xAC+0x98 = **0x144**.
- Header resource-descriptor table: rd[2].align = **0x1000** (base_align) at +0x58; resources_used[0]
  size constant **0x304** at +0x6C (PS3 sits at +0x74 with 0x390); trailing target-resource slot +0xA8 = 0.
- Dict-entry +0x0C for the BaseResource carries the virtual base_align (**0x1000** X360 / 0x80 PS3),
  not the 0x80 flag.
  These constants are size-invariant — confirmed across three differently-sized stock cTex .rx2.

**CLI.** `psg-build-textures <in> [out.rx2] --platform=xbox` (also `--xbox` / `--rx2`).

---

## 9. IDA reference addresses

| Symbol | Address | Purpose |
|---|---|---|
| `renderengine::VertexBuffer::Initialize` | `0x830cafb8` | Load-time fixup — zeros + writes template constants + reads bufferSize/type from Parameters |
| `renderengine::VertexBuffer::GetResourceDescriptor` | `0x830cc6d4` | Type registration callback: 40 B main + bufferSize raw, align 4 |
| `renderengine::VertexBuffer::Xbox2GetPhysicalMemorySize` | `0x830c77a8` | Helper for raw data size |
| `renderengine::IndexBuffer::Initialize` | `0x830c967c` | Same, IB version. Common = `(depth==32?0xC0000000:0x20000000) \| 2` |
| `renderengine::VertexDescriptor::Initialize` | `0x830cb460` | Iterates Parameters.elements[16], applies elementTypeParamsTable defaults if usage=0xFF, calls CreateD3DObject |
| `renderengine::VertexDescriptor::CreateD3DObject` | (called from Initialize@0x830cb704; runtime D3D state, no disk impact) | Builds D3DVertexDeclaration |
| `renderengine::VertexFormatGetStride` | `0x830c81a4` | Switch maps VertexFormat hash → stride bytes |
| `renderengine::VertexFormatGetNumComponents` | (string @ `0x8249cf10`) | Component-count |
| `elementTypeParamsTable` | `0x8249c628` | 28-entry (usage,usageIndex) fallback table indexed by element.type byte |
| `VertexFormats` global | `0x8268f578` | `XGRAPHICS::_SSM_VERTEXFORMAT_DATA[]` |
| `g_GPUVertexFormat` | `0x83631880` | D3DXShader FIELD_ENTRY array — small ordinal GPU register indices |
| `pegasus::tROptiMeshData::Fixup` | `0x82d18a14` | Identical on both platforms; per-island loop, pointer-or-offset decode via bit 0x800000 |
| Sk2 X360 source root | `D:\sk82\main\packages\rw\renderengine_base\1.04.03\source\core\target\xbox2\` | All renderengine code |

### elementTypeParamsTable layout (decoded bytes from 0x8249c628)

28 entries × 2 bytes = `{usage:u8, usageIndex:u8}` — used as fallback when builder writes `0xFF` sentinel.

| Index | usage / usageIndex | D3DDECLUSAGE name |
|---|---|---|
| 0  | 0xFF, 0x00 | (sentinel — XYZ default 0,0) |
| 1  | 0x00, 0x00 | POSITION(0) |
| 2  | 0x00, 0x03 | POSITION(3) |
| 3  | 0x00, 0x0A | POSITION(10) |
| 4  | 0x0A, 0x01 | COLOR(1) |
| 5  | 0x05, 0x00 | TEXCOORD(0) |
| 6  | 0x05, 0x01 | TEXCOORD(1) |
| 7  | 0x05, 0x02 | TEXCOORD(2) |
| 8  | 0x05, 0x03 | TEXCOORD(3) |
| 9  | 0x05, 0x04 | TEXCOORD(4) |
| 10 | 0x05, 0x05 | TEXCOORD(5) |
| 11 | 0x05, 0x06 | TEXCOORD(6) |
| 12 | 0x05, 0x07 | TEXCOORD(7) |
| 13 | 0x02, 0x00 | BLENDINDICES(0) |
| 14 | 0x01, 0x00 | BLENDWEIGHT(0) |
| 15 | 0x00, 0x01 | POSITION(1) |
| 16 | 0x03, 0x00 | NORMAL(0) |
| 17-19 | (zeros / filler) | |
| 20 | 0x06, 0x00 | TANGENT(0) |
| 21 | 0x07, 0x00 | BINORMAL(0) |
| 22 | 0x0A, 0x00 | COLOR(0) |
| 23 | 0x0B, 0x00 | FOG(0) |
| 24 | 0x04, 0x00 | PSIZE(0) |
| 25 | 0x02, 0x01 | BLENDINDICES(1) |
| 26 | 0x01, 0x01 | BLENDWEIGHT(1) |
| 27 | (truncated) | |

ArenaBuilder writes explicit `usage` + `usageIndex` per element, so this table is reference-only.

### VertexDescriptor element-end marker

`format == 0xFFFFFFFF` marks unused slots in Parameters when builder fills all 16. Stock files write compact form (only N elements) and rely on `header.m_numElements` count — no `0xFFFFFFFF` markers in observed stock. ArenaBuilder follows stock convention.

---

## 10. PSG dict-entry → Parameters extraction (RE conclusion)

On-disk VB/IB/VDesc images serve **dual purpose**: they are both the runtime-shape allocation AND the source for Parameters extraction. The loader reads specific offsets from the disk image to construct Parameters BEFORE Initialize zeros the runtime memory.

**VertexBuffer (40 B disk image):**
- Parameters.type ← disk +0x24 (m_type field)
- Parameters.bufferSize ← disk +0x20 (m_bufferSize field)
- All other bytes are clobbered by Initialize.

**IndexBuffer (36 B disk image):**
- Parameters.numIndices ← disk +0x20 (m_numIndices field)
- Parameters.depth ← inferred from disk +0x00 Common high bits (`0x20000000` → 16-bit, `0xC0000000` → 32-bit)
- Parameters.type ← disk +0x00 Common low bits (`& 2` → D3DRTYPE_INDEXBUFFER)
- All other bytes are clobbered by Initialize.

**VertexDescriptor (16 B header + N×16 B elements + ~4 B strides):**
- Parameters.elements ← disk +0x10 onwards (parsed as 16 B records per element, up to header.m_numElements)
- Parameters.strides ← disk trailing strides bytes (one byte per stream)
- Header field m_numElements ← disk +0x08 (used to bound element parsing)
- All other header bytes (m_typesFlags / m_instanceStreams / m_refCount) are recomputed by Initialize.

**ArenaBuilder write recipe** (minimum required fields):

| Type | Must write | Optional (template parity, engine overwrites) |
|---|---|---|
| VB | +0x20 bufferSize, +0x24 type | Common=1, RefCount=1, BaseFlush=0xFFFF0000, fetch[1]=`0x10000000\|(size+2)` |
| IB | +0x00 Common (encodes depth+type), +0x20 numIndices | RefCount=1, BaseFlush=0xFFFF0000, Size=`(numIdx*2+15)&~0xF` |
| VDesc | full header (m_numElements) + N elements (stream/offset/format/usage/usageIndex/type/elementClass) + strides[]| m_typesFlags=`OR(1<<e.type)`, m_refCount=1 |

---

## 10. Verification pass (2026-06-06)

All §1-§5 claims cross-checked against (a) IDA Sk2 symbols, (b) raw stock bytes (4 VBs / 3 IBs / 2 VDescs sampled), (c) `GLBtoRX2-v1.0.py` source code.

| Claim | Verified |
|---|---|
| Magic ps3/xb2 | ✓ bytes 0x04-0x07 |
| sections offset 0xC0/0xAC | ✓ file +0x34 |
| graphics_baseresource_size 0x6C/0x54 | ✓ PS3 `0x7508` at +0x6C, X360 `0x74DC` at +0x54 |
| BaseResource ID 0x10034/0x10031 | ✓ embedded type list + TOC bytes |
| sec-desc alignment 0x800/0x2000 | ⚠ texture-PSG only |
| VertexBuffer 40 B + fetch formula | ✓ 4 stock VBs byte-exact |
| VB::Initialize zeros leading 32 B | ✓ decomp confirms |
| IndexBuffer 36 B + Common=0x20000002 | ✓ 3 stock IBs, IB::Initialize confirms encoding |
| IB Size = ceil(numIndices×2, 16) | ✓ 252→512, 117→240, 18→48; IB::Initialize confirms formula |
| VDesc 16 B header + 16 B Element | ✓ IDA |
| VDesc m_typesFlags = OR(1<<type) | ✓ stock VDesc[8] elements {1,6,7,21} → 0x002000C2 |
| VertexFormat hash table 3/4 hit | ⚠ 1 unknown (`0x002C235F`) |
| XYZ 4 shorts, binormal cross, blend reversed | ✓ GLBtoRX2 source |
| tROptiMeshData +4 B | ✓ X360 RemapTable at +0x70, PS3 overlaps at +0x6C |

---

## 11. ArenaBuilder readiness for from-scratch X360 build

**Resolved (no longer blockers):**
- ~~VB fetch[0] address encoding~~ → engine zeros + recomputes at load. ArenaBuilder writes zero (or template constant for diff parity).
- ~~Section descriptor table~~ → X360 has 3 fewer 8-B descriptor records than PS3. Byte-exact stock template available; replicate.
- ~~m_typesFlags encoding~~ → `OR(1 << element.type)` bitmask. Recomputed at load, but precompute for cleanliness.
- ~~Index Common 0x20000002~~ → `(depth==16 ? 0x20000000 : 0xC0000000) | 2`. Engine recomputes at load.
- ~~tROptiMeshData +4 B~~ → RemapTable layout at +0x70 (own 4-B slot).

**Remaining caveats:**
- 1 unknown VertexFormat hash (`0x002C235F`, likely USHORT2). Stock mesh DLC will work if the same authoring tool produces this hash; from-scratch GLB→mesh would need to either (a) emit `SHORT2N` (verified hash) instead, or (b) RE the remaining ~40 enum hashes.
- ArenaBuilder.Mesh per-vertex packer needs platform branch for §5 quirks.

**Conclusion:** All structural questions resolved. ArenaBuilder X360 builder can be written from scratch with byte-exact stock parity. No remaining IDA RE blockers.

---

## 12. Full DIST pipeline X360 integration (2026-06-11)

The whole tile build pipeline can now emit a complete Xbox 360 DIST, not just individual assets.

- **`TileBuildOptions.TargetPlatform`** (`ArenaPlatform`, default Ps3) drives everything; `PsgExtension`
  yields `.psg` / `.rx2`.
- **Mesh** branches `MeshPsgComposer` (PS3) vs `MeshRX2Composer` (X360). `MeshRX2Composer.ComposeMulti`
  is now implemented — a verbatim port of the PS3 multi-part layout with the two X360 builder-signature
  swaps (VertexBuffer/IndexBuffer take size/count only) and BaseResource type 0x00010031. Vertex packing
  is platform-identical (32-byte stride), so per-part payloads are reused as-is. Regression test:
  `test/PsgBuilder.Tests/MeshRX2MultiPartTests.cs`.
- **Texture** flows through `TextureDeduplicationRegistry`, which now carries the platform and switches
  `TexturePsgComposer` ↔ `TextureRX2Composer` + `.psg`/`.rx2`.
- **Collision / AIPath / Irradiance / NavPower / WorldPainter** are cross-platform-clean (no BaseResource
  object); their builders gained an `ArenaPlatform platform = Ps3` parameter that just selects the header
  the writer emits.
- **Packing**: `DistPackRunner` threads the platform letter into `RunProcess`, so Stream File Tool is
  invoked with `--platform=x` on Xbox (was hardcoded `--platform=p`).
- **UI/CLI**: WinForms has a PS3/Xbox "Target" dropdown; `psg-build-batch` accepts `--platform=xbox`
  (and `psg-build-textures --platform=xbox`).

Verified end-to-end: a real GLB built with `--platform=xbox` produced mesh/texture/collision `.rx2`
files with correct `xb2` magic, 0xAC sections offset, and BaseResource 0x00010031.

---

_Last updated: 2026-06-06._
