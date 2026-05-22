# Skate 2 — `AIPath::Node` & `ExtendedNodeData` field map

What data an AIPath node carries, and **which engine module each field is sourced from**.
This is the data the Sk3 recorder must reproduce per frame.

## Where this came from

- **Field names + types** — read from the symboled structs in the **Sk2 `_f` build**
  (`sk82_na_f.xex`, IDA port 13339). `Sk8::AIPath::Node` = 13 members, `Sk8::AIPath::ExtendedNodeData`
  = 6 members.
- **Field → module mapping** — the `_f` release build *inlined* `Node::Set`, so the per-field
  sources were read from the **Sk2 `_zd` debug build** (`sk82_na_zd.xex`, IDA port 13338), which
  keeps `Node::Set` and the trajectory/air writers as standalone symboled functions. Same Skate 2
  source — the module each field reads from is identical between builds.

---

## `Sk8::AIPath::Node` — 13 fields

The recorder fills a Node every frame in `Node::Set(node, physOut, isCrouching, lastNode, nodeType)`.

| Field | Type | Source module | What it is |
|---|---|---|---|
| `mPos` | CompressedPositionVector | **PhysOut_SkateboardReckoning** (on-board) / **PhysOut_Skeleton** (off-board) | Node world position. On-board → `SkateboardReckoning.mDeckPosition`. Off-board (state category == OFFBOARD) → translation of `Skeleton.mEffectiveSkeletonRootMatrix`. |
| `mDirection` | CompressedPositionVector | *derived* — `mPos` minus the previous node's position | Travel vector between this node and the last recorded node. Zero on the first node. |
| `mBoardOrientation` | CompressedQuaternion | **PhysOut_SkateboardReckoning** | Board orientation quaternion, from `SkateboardReckoning.mTransform`. |
| `mSkaterOrientation` | CompressedQuaternion | **PhysOut_Skeleton** | Skater orientation quaternion, from `Skeleton.mSkeletonRootMatrix`. |
| `mExtendedNodeData` | `ExtendedNodeData*` | **PathRecording (recorder)** | Optional pointer to an `ExtendedNodeData` block (trajectory / air / trick). Allocated by the recorder only when the node needs it; otherwise null. |
| `mFrameCountSinceLastNode` | u8 | **PathRecording (recorder)** | Recorder bookkeeping. `Node::Set` writes 1; the recorder increments it each frame the node stays "current". |
| `mNodeWidthLeft` | u8 | **`FindWidths()`** — PhysOut_SkateboardReckoning + PhysOut_SystemReckoning + collision probe | Rideable-surface half-width to the left of the node. Only filled when auto-width generation is on, the skater is not airborne, and not grinding. |
| `mNodeWidthRight` | u8 | same as `mNodeWidthLeft` | Rideable-surface half-width to the right. |
| `mEventType` | u8 | **PhysOut_Scoring2** | Trick start/end event. Derived from Scoring2 raw scorables (`mRawScorableStarted/Ended`, `mRawStarted/FinishedPowerslide/Manual/Revert`). Values: None / StartTrick / EndTrick / IncidentalAir. |
| `mBitFlags` | BitFlags&lt;u8&gt; | **multi-module** (see below) | Packed state bits. |
| `mExtraData1` | u8 | — *(reserved / unused)* | Not written by `Node::Set`; left zero. |
| `mExtraData2` | u8 | — *(reserved / unused)* | Not written by `Node::Set`; left zero. |
| `mExtraData3` | u8 | — *(reserved / unused)* | Not written by `Node::Set`; left zero. |

### `Node::mBitFlags` — individual bits and their module

| Bit | Setter | Source module |
|---|---|---|
| IsBoardFlipped | `SetIsBoardFlipped` | **PhysOut_SkateboardReckoning** — `mIsTransformFlipped` |
| IsCrouched | `SetIsCrouched` | *caller-supplied* — the `isCrouching` argument the recorder passes in |
| IsAirborne | `SetIsAirborne` | **PhysOut_Air** — `mIsSkateboardAirborne` |
| IsOffBoard | `SetIsOffBoard` | **PhysOut_State** (`mPhysicsStateCategory == OFFBOARD`) **OR PhysOut_Animation** (`mTransitioningOnOffBoard`) |

---

## `Sk8::AIPath::ExtendedNodeData` — 6 fields

Allocated and filled only when a node carries trajectory / air / trick data
(`ExtendedNodeData::UpdateTrajectoryData`, `Node::UpdateAirData`, `Node::SetTrickScorable`).

| Field | Type | Source module | What it is |
|---|---|---|---|
| `mTrajectoryStartPos` | CompressedPositionVector | **PhysOut_Air** | Projectile-trajectory start position, from `Air.mTrajectoryStruct`. |
| `mTrajectoryStartVel` | CompressedPositionVector | **PhysOut_Air** | Projectile-trajectory start velocity, from `Air.mTrajectoryStruct`. |
| `mTrajectoryOffset` | CompressedPositionVector | **PhysOut_Air** | Trajectory offset, from `Air.mTrajectoryOffset`. |
| `mTrickScorable` | __int16 | **PhysOut_Scoring2** | Scorable ID of the trick being started. Set by `Node::SetTrickScorable` from the Scoring2 raw-started scorable. |
| `mAirSpin180Count` | char | **Scoring — `Score::AirPeriodData`** | Number of 180° spins in this air period, from `AirPeriodData.mPlayerSpin180Count`. |
| `mBitFlags` | BitFlags&lt;u8&gt; | **PhysOut_Air + Scoring** | HasValidTrajectory ← PhysOut_Air (`UpdateTrajectoryData`); HasFrontFlip / HasBackFlip ← `Score::AirPeriodData.mPlayerNumFlips`. |

---

## Module legend

PhysOut sub-modules and other sources referenced above:

| Source | Role |
|---|---|
| **PhysOut_SkateboardReckoning** | Board/deck world position (`mDeckPosition`), board transform (`mTransform`), flipped flag (`mIsTransformFlipped`). |
| **PhysOut_Skeleton** | Skater skeleton root matrices (`mSkeletonRootMatrix`, `mEffectiveSkeletonRootMatrix`). |
| **PhysOut_Air** | Airborne flag (`mIsSkateboardAirborne`) and projectile trajectory (`mTrajectoryStruct`, `mTrajectoryOffset`). |
| **PhysOut_State** | Physics state category (on-board / off-board / grind). |
| **PhysOut_Animation** | On/off-board transition state (`mTransitioningOnOffBoard`). |
| **PhysOut_Scoring2** | Raw scorable trick events (started/ended powerslide/manual/revert). |
| **PhysOut_SystemReckoning** | World up-vector (`mUpVector`) — used by `FindWidths`. |
| **PhysOut_Filtered** | Filtered physics state — only used to *gate* `FindWidths` (skip when grinding). |
| **Score::AirPeriodData** | Air-period scoring summary (flip count, spin count). Produced by the Score system, not a PhysOut module. |
| **PathRecording (recorder)** | The recorder module itself — owns the node, allocates `ExtendedNodeData`, keeps `mFrameCountSinceLastNode`. |

---

## Provenance — functions decompiled

Sk2 `_zd` debug build (`sk82_na_zd.xex`):

| Function | Address | Fills |
|---|---|---|
| `Sk8::AIPath::Node::Set` | `0x8274E8F0` | mPos, mDirection, mBoardOrientation, mSkaterOrientation, mEventType, mBitFlags, mFrameCountSinceLastNode, widths (via FindWidths) |
| `Sk8::AIPath::Node::FindWidths` | `0x8274C580` | mNodeWidthLeft, mNodeWidthRight |
| `Sk8::AIPath::Node::SetTrickScorable` | `0x8271A8A8` | ExtendedNodeData.mTrickScorable |
| `Sk8::AIPath::ExtendedNodeData::UpdateTrajectoryData` | `0x8272B4E8` | ExtendedNodeData.mTrajectoryStartPos / StartVel / Offset, HasValidTrajectory |
| `Sk8::AIPath::Node::AddTrajectoryData` | `0x8273C2D0` | dispatch wrapper → UpdateTrajectoryData |
| `Sk8::AIPath::Node::UpdateAirData` | `0x82723708` | ExtendedNodeData.mAirSpin180Count, HasFrontFlip / HasBackFlip |

Struct definitions verified in the Sk2 `_f` build (`sk82_na_f.xex`): `Sk8::AIPath::Node` (44 B, 13 members),
`Sk8::AIPath::ExtendedNodeData` (40 B, 6 members), `Sk8::Physics::PhysOut_PathRecording` (140 B).
