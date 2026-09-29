# Stable guard for the live Touch/Right graph

This is the small Flux-SDK module added to the user-authored Touch/Right graph.
It is not a change to the converter's generated Version 48 graph.

- `Stable`: per-user `StoredValue<int>` for the last sent gesture.
- `HasStable`: per-user `StoredValue<bool>`; false initially and after input stops.
  This lets the first Neutral (0) through without relying on an initial int value.
- `Check`: forwards only when `!HasStable || Candidate != Stable`.
- `Remember`: records Candidate and sets HasStable after the existing sender runs.
- `Reset`: sets Stable=-1 and HasStable=false after the existing Candidate reset.

The existing graph owns input authority, candidate changes, and its 0.05-second
positive stability interval. Its ready If remains before Check. Candidate writes
are unconditional whenever input is accepted, and OnWritten resets ElapsedTime.
This preserves A -> brief B -> A without another send, while A -> brief B -> C
can settle on C. Re-enabling input clears the previous send and starts a fresh wait.
A zero delay is outside this fixed-positive-delay graph's contract.

## Build and connection

```powershell
resoloop flux check flux/gesture-stable/GestureStable.pg --project flux/gesture-stable --json
resoloop flux build flux/gesture-stable/GestureStable.pg --project flux/gesture-stable --out .tmp_verify/GestureStable.brson --json
```

Deploy only below the verified Touch/Right ownership boundary. The CallInput
nodes preserve all three operations during compilation; they are not automatic
entrypoints. After deployment, the existing graph supplies the impulses.

Capture a fresh resoloop inspect response for the entire Right subtree with
members and sufficient depth. `scripts/connect-gesture-stable.ps1` resolves the
known node roles in this particular authored graph and writes a seven-link plan.
It stops on ambiguous/missing roles. Review that plan before using `-Apply -Url`.
Apply re-reads each component, checks its type and the expected previous target,
and skips links already at their desired targets. Re-capture after reconnecting;
never reuse a prior world's snapshot. IDs and state stay in `.tmp_verify/`.

Candidate's SDK input placeholder is bypassed at its two consumers when linking
to the existing Store. SDK redeployment replaces this module and requires fresh
inspection and relinking; it is not a transparent update of the external wires.

## Verified on 2026-09-30

Flux-SDK 1.9.0 check/build passed against Resonite 2026.9.18.82. Live Reflection
verified edited member types. Post-edit inspection verified all seven links and
all 32 Touch code mappings. Reapplying the links and the ownership-root manifest
made no writes. This is structural verification; physical controller operation
and multiplayer behavior were not tested.
