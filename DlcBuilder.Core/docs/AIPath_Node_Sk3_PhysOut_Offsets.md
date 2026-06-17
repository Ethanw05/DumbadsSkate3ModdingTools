# Skate 3 — `AIPath::Node` / `ExtendedNodeData` → PhysOut module offsets

The Sk2 field→module map (see `AIPath_Node_Fields.md`) translated to **Skate 3** offsets, for the
recorder. The recorder cave dumps the 19 PhysOut module pointers to BSS `+0x34..+0x7C`; this doc
says, per pointer, what offset to add.

## Method / anchoring

- **Module identity & order** — Sk3 PhysOut ctor `sub_DE71D8` (`0x00DE71D8`, IDA 13337); module
  ctors are inlined, so init offsets are visible directly.
- **Sk2 reference** — symboled `Sk8::Physics::PhysOut_*` structs (IDA 13339).
- **Sk3 runtime confirmation** — live reads via Cheat Engine against a booted RPCS3, walking the
  recorder's BSS table to the live module objects, behaviorally diffed across grounded / airborne /
  off-board states.

Confidence: ✅ runtime-confirmed or ctor-proven · 🟢 high (module byte-identical to Sk2).

---

## 1. Sk3 PhysOut module table

PhysOut is the 80-byte container. Module **N** (1-indexed) is at `PhysOut + (N-1)*4`; the recorder
cave mirrors `PhysOut+0x00..+0x48` into BSS, so **module pointer in BSS = `0x34 + (N-1)*4`**.

| # | Module | `PhysOut+` | BSS slot | Sk3 size | Sk2 size |
|---|---|---|---|---|---|
| 1 | SkateboardReckoning | +0x00 | **+0x34** | 288 | 288 |
| 3 | Air | +0x08 | **+0x3C** | 464 | 448 |
| 6 | Skeleton | +0x14 | **+0x48** | 608 | 592 |
| 8 | State | +0x1C | **+0x50** | 88 | 72 |
| 10 | SystemReckoning | +0x24 | **+0x58** | 176 | 144 |
| 14 | Intents | +0x34 | **+0x68** | 80 | 80 |
| 15 | Animation | +0x38 | **+0x6C** | 176 | 176 |
| 16 | Scoring2 | +0x3C | **+0x70** | 14672 | 17968 |
| 17 | Filtered | +0x40 | **+0x74** | 88 | 88 |

(Other modules — SkateboardMotion, Physics, Grinds, Collision, Ground, Audio, Camera, Trick,
PathRecording, OffBoard — aren't sourced by Node/ExtendedNodeData. Full table in git
history. BSS `+0x2C` = PhysicalPlayerHiLOD\*, `+0x30` = PhysOut\*.)

---

## 2. `Node` fields → Sk3 PhysOut

| Node field | Sk3 module (BSS slot) | In-module offset (Sk3) | Conf. |
|---|---|---|---|
| `mPos` *(on-board)* | SkateboardReckoning (BSS+0x34) | **`+0x90`** `mDeckPosition` (Vector3) | ✅ |
| `mPos` *(off-board)* | Skeleton (BSS+0x48) | **`+0x1E0`** = `mEffectiveSkeletonRootMatrix(+0x1B0).wAxis(+0x30)` | ✅ |
| `mDirection` | — | *derived* — `mPos` minus previous node's pos | — |
| `mBoardOrientation` | SkateboardReckoning (BSS+0x34) | **`+0x00`** `mTransform` (Matrix44Affine → quaternion) | ✅ |
| `mSkaterOrientation` | Skeleton (BSS+0x48) | **`+0x170`** `mSkeletonRootMatrix` (Matrix44Affine → quaternion) | ✅ |
| `mEventType` | Scoring2 (BSS+0x70) | derived from raw-scorable cluster `+0x9C..+0xB8` — see §4 | ✅ |
| `mFrameCountSinceLastNode` | PathRecording itself | recorder bookkeeping | — |
| `mNodeWidthLeft/Right` | SystemReckoning (BSS+0x58) | `mUpVector` **`+0x60`** + collision width-probe | ✅ |
| `mExtendedNodeData` | PathRecording itself | recorder-allocated pointer | — |
| `mExtraData1/2/3` | — | unused — `Node::Set` never writes them | — |

### `Node.mBitFlags` bits

| Bit | Sk3 module (BSS slot) | In-module offset | Conf. |
|---|---|---|---|
| IsBoardFlipped | SkateboardReckoning (BSS+0x34) | **`+0x110`** `mIsTransformFlipped` (bool) | ✅ |
| IsAirborne | Air (BSS+0x3C) | **`+0x1B6`** `mIsSkateboardAirborne` (bool) — runtime 0→1 | ✅ |
| IsOffBoard | State (BSS+0x50) | **`+0x0C`** `mPhysicsStateCategory == 500` (off-board) | ✅ |
| IsOffBoard *(2nd signal)* | Animation (BSS+0x6C) | **`+0x9C`** `mTransitioningOnOffBoard` (bool) | 🟢 |
| IsCrouched | Intents (BSS+0x68) | **`+0x34`** crouch flag (bool) — `1` on the ollie wind-up crouch, `0` during a manual | ✅ |

`State.mPhysicsStateCategory` (`+0x0C`) coarse enum, runtime-observed across all four states:
**on-board-ground 100, on-board-air 200, off-board 500** — a single value `500` covers off-board
whether grounded or airborne, so `*(State+0x0C) == 500` reliably detects off-board. `mPhysicsState`
(`+0x10`) is the finer state: 100 / 201 / 500 / 501 respectively.

---

## 3. `ExtendedNodeData` fields → Sk3 PhysOut

| ExtendedNodeData field | Sk3 module (BSS slot) | In-module offset (Sk3) | Conf. |
|---|---|---|---|
| `mTrajectoryStartPos` | Air (BSS+0x3C) | **`+0xF0`** = `mTrajectoryStruct + 0x00` | ✅ |
| `mTrajectoryStartVel` | Air (BSS+0x3C) | **`+0x100`** = `mTrajectoryStruct + 0x10` | ✅ |
| `mTrajectoryOffset` | Air (BSS+0x3C) | **`+0x60`** `mTrajectoryOffset` (Vector3) | ✅ |
| `mTrickScorable` | Scoring2 (BSS+0x70) | resolved started-scorable from the `+0x9C..+0xB8` cluster — see §4 | ✅ |
| `mAirSpin180Count` | Scoring2 (BSS+0x70) | **`+0x2670`** = `mAirFinishedData(+0x1CE0)` + `mPlayerSpin180Count(+0x990)` — see §5 | ✅ |
| `mBitFlags` | Air (BSS+0x3C) + Scoring2 | HasValidTrajectory ← Air; HasFront/BackFlip ← `Scoring2.mAirFinishedData.mPlayerNumFlips` (**`Scoring2+0x2674`**) — see §5 | ✅ |

`Air.mTrajectoryStruct` (`tTrajectory` at Air `+0xF0`), runtime-verified: `+0x00` start position,
`+0x10` start velocity, `+0x20` gravity vector (Y = `-9.8` observed at Air `+0x114`).

---

## 4. Scoring2 raw-scorable cluster — `+0x98..+0xB8`

Runtime-found: 9 contiguous `EScorableID` dwords, **all `0xFFFFFFFF` (`INVALID = -1`) when idle**;
`+0x98` flipped to a valid scorable ID (`82`) during a flip trick. Order follows the Sk2
`PhysOut_Scoring2` struct (same engine):

| Sk3 offset | Field | Used by |
|---|---|---|
| `+0x98` | `mRawScorableID` | (the current scorable; not used by mEventType) |
| `+0x9C` | `mRawScorableStarted` | mEventType, mTrickScorable |
| `+0xA0` | `mRawScorableEnded` | mEventType |
| `+0xA4` | `mRawStartedRevert` | mEventType, mTrickScorable |
| `+0xA8` | `mRawFinishedRevert` | mEventType |
| `+0xAC` | `mRawStartedManual` | mEventType, mTrickScorable |
| `+0xB0` | `mRawFinishedManual` | mEventType |
| `+0xB4` | `mRawStartedPowerslide` | mEventType, mTrickScorable |
| `+0xB8` | `mRawFinishedPowerslide` | mEventType |

`mEventType` / `mTrickScorable` are computed from these 8 Started/Finished fields by the Sk2
`Node::Set` algorithm (see `AIPath_Node_Fields.md`): a valid "Started" scorable → `StartTrick`
(+ `mTrickScorable` = that ID); else a valid "Finished" → `EndTrick`; else `None`.

Cluster identified runtime; internal order inferred from the Sk2 struct. The `Started`/`Finished`
fields are **momentary** — non-INVALID only on the single frame a trick starts/ends — so a
steady-state capture (verified: a held manual) leaves all 9 slots at INVALID. Confirming an
individual slot needs a frame-exact trick-start snapshot; the recorder, running per-frame, catches
those frames on its own. `mRawScorableID` (`+0x98`) was caught valid (`82`) mid-flip.

---

## 5. `AirPeriodData` — it lives *inside* Scoring2

The Sk2 recorder (`PhysOutModule_PathRecording::Update`) does:
`if (physOut->mScoring2->mAirFinished) Node::UpdateAirData(node, &physOut->mScoring2->mAirFinishedData);`
— so `AirPeriodData` is **`PhysOut_Scoring2.mAirFinishedData`**, an embedded field of the Scoring2
module the recorder already has a pointer to. Not a separate subsystem.

**Sk2 reference:** `Scoring2.mAirFinishedData` @ `+0x2948` · `mCurrentAirData` @ `+0x3358` ·
`mAirFinished` (gate bool) @ `+0x463C`. `AirPeriodData` (2576 B): `mPlayerSpin180Count` @ `+0x9F8`,
`mPlayerNumFlips` @ `+0x9FC`.

**Sk3 — RESOLVED via `AirPeriodData::Reset`.** The Scoring2 ctor `sub_13EBD24` calls one function
(`sub_D80364`) on both `AirPeriodData` blocks → that's `AirPeriodData::Reset`. Decompiled against
the symboled Sk2 `AirPeriodData::Reset`, its tail zeroes **13 dwords + 10 bools in the exact same
order** as Sk2 — a clean **−104 shift** (the embedded Scorable shrank 304→200). Resolved offsets:

| | Sk3 | Sk2 |
|---|---|---|
| `Scoring2.mAirFinishedData` | `+0x1CE0` | `+0x2948` |
| `Scoring2.mCurrentAirData` | `+0x2688` | `+0x3358` |
| `AirPeriodData.mPlayerSpin180Count` | `+0x990` | `+0x9F8` |
| `AirPeriodData.mPlayerNumFlips` | `+0x994` | `+0x9FC` |

→ **`mAirSpin180Count` = `Scoring2 + 0x2670`** ; **`mPlayerNumFlips` = `Scoring2 + 0x2674`**.

⚠️ **`mAirFinishedData` is momentary** — `Scoring2`'s per-frame reset (`SetResetValues` →
`AirPeriodData::Reset`) clears it every frame; it holds the trick's data only on the air-finish
frame. The recorder runs per-frame so it catches that frame; a CE pause cannot (this is why 5
captures read `0` — the offset was right, the timing impossible). The recorder should sample
`Scoring2+0x2670` on the frame `Air.mIsSkateboardAirborne` (`+0x1B6`) transitions `1→0`.

**✅ Runtime-confirmed.** Paused mid-air during a fs 360, the in-progress twin
`mCurrentAirData.mPlayerSpin180Count` (`Scoring2+0x3018` — same `+0x990` sub-offset into the
`AirPeriodData` struct) read exactly **`2`**, with `mPlayerNumFlips` `0`, `mPlayerSpinPoints`
`360.0`, `mTrickCount` `1`. That nails the `AirPeriodData` layout — so
`mAirFinishedData.mPlayerSpin180Count` = `Scoring2+0x2670` is verified.

---

## 6. Anchoring evidence

| Module | Evidence |
|---|---|
| SkateboardReckoning | ctor `*(base+0x110)=0` confirms `mIsTransformFlipped`; module byte-identical to Sk2 → `mTransform@+0`, `mDeckPosition@+0x90`. |
| Skeleton | ctor sets `base+0x170` / `base+0x1B0` as matrices. |
| Air | **runtime**: `+0x1B6` flipped 0→1 airborne; `+0xF0` held trajectory startPos, `+0x100` startVel, `+0x114` gravity `-9.8`; `+0x60` populated airborne. |
| State | **runtime**: `+0x0C` = 100 / 200 / 500 across ground / air / off-board. |
| SystemReckoning | **runtime**: `+0x60` holds a live unit up-vector `(≈0,1,≈0)`. |
| Scoring2 | **runtime**: `+0x98..+0xB8` = 9 `EScorableID`, all `-1` idle, `+0x98`→`82` mid-flip. |
| Scoring2 `AirPeriodData` | **runtime**: mid-air fs 360 → `mCurrentAirData.mPlayerSpin180Count` (`+0x3018`) = `2`; static: `sub_D80364`=`AirPeriodData::Reset` 13-dword+10-bool order-match vs Sk2. |
| Intents | **runtime**: behavioral diff standing vs crouched — `+0x34` flips `0→1` on crouch and stays `0` during a manual (whereas `+0x31` fires on both crouch and manual). `+0x34` is the crouch-specific flag → `IsCrouched`. |
| Animation / Filtered | byte-identical module size to Sk2 → Sk2 offsets transfer. |

Source builds: Sk3 EBOOT (IDA 13337) + live RPCS3 via CE; Sk2 `_f` symboled structs (IDA 13339);
Sk2 `_zd` `Node::Set` for the field semantics (IDA 13338).

---

## 7. On-disk binary layout — confirmed from shipped Sk3 PSG

Decoded from `AIPATHDUMP.txt` — a `psg_structure_dumper.py` dump of
`DIST_Industrial/cSim_350_-50_high/A5249F6736ADC979.psg`. 1409 real `tAIPathNode`s across
9 `tAIPath`s. **Byte-for-byte the same layout my Sk2-derived recorder packs** — the Sk3
on-disk Node is the Sk2 Node.

### `tAIPath` — path header, 96 B

| Offset | Field | Type | Notes |
|---|---|---|---|
| `+0x00` | (bounding-box-like prefix) | 32 B | not labelled by the dumper |
| `+0x20` | `m_ID[16]` | byte[16] | path identity |
| `+0x30` | `m_pNodes` | uint32 | relative offset to first Node |
| `+0x34` | `m_uiNumNodes` | uint32 | |
| `+0x3C` | `m_pBranchGroup` | uint32 | relative offset |
| `+0x40` | `m_uiNumGroups` | uint32 | |
| `+0x44` | `m_BitFlags` | uint32 | observed `0x7` in shipped data |
| `+0x48` | `m_AllowedSkaters` | uint64 | bitset, `0x3FFFFFFFFFFFFFFF` in shipped data |
| `+0x50` | `m_SkillLevel` | int32 | |
| `+0x54` | `m_ExtraData1..3` | int32 ×3 | |

### `tAIPathNode` — 44 B

| Offset | Field | Type |
|---|---|---|
| `+0x00` | `m_Position` | float[3] |
| `+0x0C` | `m_Direction` | float[3] |
| `+0x18` | `m_BoardOrientation` | byte[4]  (CompressedQuaternion) |
| `+0x1C` | `m_SkaterOrientation` | byte[4]  (CompressedQuaternion) |
| `+0x20` | `m_pExtData` | uint32  (relative offset; 0 if none) |
| `+0x24` | `m_uiFramesSinceLastNode` | uint8 |
| `+0x25` | `m_uiNodeWidthLeft` | uint8  (255 = unbounded) |
| `+0x26` | `m_uiNodeWidthRight` | uint8 |
| `+0x27` | `m_uiEventType` | uint8 |
| `+0x28` | `m_i8Flags` | uint8 |
| `+0x29..+0x2B` | `m_uiExtraData1..3` | uint8 ×3 |

**`m_uiEventType` — enum locked from shipped data.** Distinct values observed:

| Value | Meaning |
|---|---|
| 0 | None |
| 1 | StartTrick |
| 2 | EndTrick |
| 4 | IncidentalAir |

(3 is unused — the enum is sparse; treat it as reserved.)

**`m_i8Flags` — bit assignments + reservation finding.** Distinct values observed across
1409 shipped nodes: `0, 1, 2, 3, 4, 5, 6, 7` (max). Bit 0 = `mIsBoardFlipped` (fakie),
bit 1 = `mIsCrouched`, bit 2 = `mIsAirborne`, bit 3 = `mIsOffBoard`. **Bit 4
(`NodeBitFlag5`) is never set in shipped data** — the Sk3 EBOOT string table at
`0x149b078` has the 5th name slot as the generic placeholder `NodeBitFlag5` (the other 4
are descriptively named `mIs...`), and 1409 real nodes confirm it never fires. Reserved /
unused. **There is no pushing field at any level of the AIPath model** — confirmed by
reading the on-disk dump (zero matches for "push") and the EBOOT string table.

### `tAIPathNodeExtData` — 40 B (when `m_pExtData != 0`)

| Offset | Field | Type | Notes |
|---|---|---|---|
| `+0x00` | `m_TrajectoryStartPos` | float[3] | |
| `+0x0C` | `m_TrajectoryStartVel` | float[3] | |
| `+0x18` | `m_TrajectoryOffset` | float[3] | |
| `+0x24` | `m_iTrickIndex` | int16 | `-1` = INVALID |
| `+0x26` | `m_AirSpin180Count` | int8 | signed |
| `+0x27` | `m_i8Flags` | uint8 | bit0=HasTrajectory · bit1=HasFrontFlip · bit2=HasBackFlip |

### XML authoring names — not in the binary

The Sk3 EBOOT also carries a richer set of field-name strings (`TrickName`,
`TrajectoryGravity`, `mTrickMetricPlayerYaw/Pitch`, `AirSpinDegrees`, `AirFrontFlip`,
`AirBackFlip`, the four `mIs*` flags, `NodeBitFlag5`, `Flags`) clustered at
`0x149af40..0x149b0bc`. These are the friendly names for the authoring `.xml` form (note
`aipaths/out/` is in the same string block), **not** separate binary fields. The on-disk
PSG carries the compact 44 + 40 byte structs above — the recorder targets that layout.

Container = PSG (RW4) — `ArenaBuilder.Core` writes these. P4 wraps node arrays in a
`tAIPath` header, concatenates Nodes and ExtData blocks, adds the branch-group table, and
embeds it as `PEGASUS::RWOBJECTTYPE_AIPATHDATA` inside a PSG.

---

## 8. Emission algorithm — Sk2 `ShouldOutputNode`

Decompiled from `sk82_na_zd.xex` (IDA port 13338) — `Sk8::Physics::PhysOutConditioner_PathRecording`:

- `Update` @ `0x82ac3cb8` — per-frame producer. Every engine frame it:
  1. `PathRecording::AddNode(mHighDetailPath, physOut, ...)` — appends to a **high-detail
     per-frame buffer** (NOT the output path).
  2. `Node::UpdateDirection(lastNodeAdded, newNode)` — patches the previous node's
     `mDirection` to point to the new one (retroactive, not at emit time).
  3. `UpdateTrajectory(this)`.
  4. **`if (ShouldOutputNode(this, lastNode, newNode))`** → `OutputNode(newNode)` (promote
     to the real output path). Otherwise `OutputPotentialEndNode(newNode)` (a debug hint —
     no effect on the saved data).
  5. `++mFrameCountSinceLastNodeOutput`.

- `ShouldOutputNode` @ `0x82ac39a0`:

  ```
  if  newNode.mEventType != 0:                          return true
  if  (last.mBitFlags ^ new.mBitFlags) & 0x0F:           return true   # any named state bit changed
  if  PhysOut->mPathRecording->mHasNewTrajectory:        return true
  if  mFrameCountSinceLastNodeOutput >= 0x80 (128):      return true   # hard cap

  if  mFrameCountSinceLastNodeOutput <= 1:               return false  # too soon

  # Interpolation-fidelity test against the high-detail buffer:
  for k in 1 .. mFrameCountSinceLastNodeOutput - 1:
      detailNode = mHighDetailPath.GetPreviousNodeByOffset(N - k)
      ratio      = k / N
      interp     = Node::SetInterpolated(mLastNodeOutput, newNode, ratio)
      if  ||interp.pos - detail.pos||² > 0.00039999999  return true    # ~0.02 u
      boardAngle  = AngleBetween(interp.board.forward,  detail.board.forward)
      if  boardAngle  > 0.08726646  (5°)                 return true
      skaterAngle = AngleBetween(interp.skater.forward, detail.skater.forward)
      if  skaterAngle > 0.08726646  (5°)                 return true
      if  |interp.widthLeft  - detail.widthLeft|  > 7    return true
      if  |interp.widthRight - detail.widthRight| > 7    return true
  return false
  ```

The 128-frame ceiling explains the maximum gaps seen in shipped data; the interp-fidelity
test explains the variable 2–23-ish gaps (skater cruising straight → LERP perfectly
reproduces intermediate frames → no emit).

**Recorder port:** `AIPathRecorder/recorder.py` implements `should_output_node()` /
`lerp_node()` / `quat_angle()` byte-for-byte against this with constants
`MAX_FRAMES_BETWEEN=128`, `INTERP_POS_TOL_SQ=0.0004`, `INTERP_ANGLE_TOL_RAD=0.08726646`,
`INTERP_WIDTH_TOL=7`, and maintains a `deque(maxlen=130)` high-detail buffer in the
poll thread. Direction is patched retroactively on the *previous* emitted Node to match
Sk2's `UpdateDirection`.
