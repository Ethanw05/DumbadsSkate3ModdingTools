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
| 15 | Animation | +0x38 | **+0x6C** | 176 | 176 |
| 16 | Scoring2 | +0x3C | **+0x70** | 14672 | 17968 |
| 17 | Filtered | +0x40 | **+0x74** | 88 | 88 |

(Other modules — SkateboardMotion, Physics, Grinds, Collision, Ground, Audio, Camera, Trick,
Intents, PathRecording, OffBoard — aren't sourced by Node/ExtendedNodeData. Full table in git
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
| IsCrouched | — | caller-supplied flag — not a module read | — |

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
| Animation / Filtered | byte-identical module size to Sk2 → Sk2 offsets transfer. |

Source builds: Sk3 EBOOT (IDA 13337) + live RPCS3 via CE; Sk2 `_f` symboled structs (IDA 13339);
Sk2 `_zd` `Node::Set` for the field semantics (IDA 13338).
