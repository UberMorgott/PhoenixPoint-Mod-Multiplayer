# Boundary law — what rides the rail, what refuses, and how that may change

Cited from code as `boundary-law L-X` (tools/RailCheck/Program.cs:20/:244/:268, src/Rail/RailMeta.cs:1228).
One page: the rail's ride/refuse rules and the discipline for changing them.

## L-A — ride = the game's own opt-in, four gates

A member rides only because the GAME's save serializer would ship it. The four native gates:
1. `[SerializeType]` on the type (incl. `SerializeMembersByDefault`) — type opt-in.
2. `[SerializeMember]` / member discovery (`Serializer.ShouldSerializeMember`, Serializer.cs:248) — member opt-in.
3. `ShouldSerializeMemberCallback` (`SerializationCallback.ShouldSerializeMember`, per-type custom data) — runtime veto.
4. Fresh-graph read: the game's load path always builds NEW graphs (`SerializationReader.ReadObjects`
   → `Activator.CreateInstance`) — it never writes into a live graph. We do (L-B).

## L-B — our delta: in-place write ⇒ ownership law

The rail applies by writing IN PLACE into the live client graph — the one thing the native
serializer never does. Everything below exists to pay for that delta:
- **DefOwnership (runtime law):** an instance reachable from BOTH a mirrored entity AND the def graph
  (e.g. `ItemDef.GetDisplayName` returns `ViewElementDef` state BY REFERENCE) never ships and never
  applies — shipping it exports shared def state as writable, applying clobbers client defs. No static
  signal carries this (`IsWritable`/`IsComplexTypeSerializeable` both falsified) → reference identity at
  walk time: one lazily-built reference-hash set of all non-def instances reachable from
  `DefRepository.GetAllDefs`. Hooks: DiffEngine.cs:493 (entity entry) + :516 (non-Leaf field arm),
  GenericApplier.cs:219/:225 (apply backstop), `DefOwnership.Warm()` at SendLoadComplete
  (SaveTransferCoordinator.cs:1472 — every peer, once per load boundary, curtain still up). Build
  failure latches 30 s cooldown, law fails OPEN between retries. Invalidated at reload boundary.
- **IsPresentation belt:** classify-time refusal of presentation types (RailMeta.cs:438) — kept even
  where DefOwnership would also catch the instance (belt + suspenders; RailCheck L11 = static belt:
  no `LocalizedTextBind` field/element may ride covered).
- **Husk-gate:** a type may be blob-reconstructed only if its husk (reference members the blob does NOT
  carry) is empty or argued in review — the baseline's per-type `husk=` lists exist for exactly that.

## L-C — Unresolved sentinel: skip the write, never clobber

A decoded ref that cannot resolve (unknown def GUID, unspawned entity, Composite with a missing part)
returns the `Unresolved` sentinel (RailMeta.cs:829), NEVER null — a genuine null is `LeafKind.Null`,
so an unresolved decode means the host wrote a REAL ref this client cannot see yet. Every applier arm
(leaf, dict, blob member, list element, create arg) SKIPS the write / drops the element instead of
stomping a valid live ref the host would never re-ship.

## L-D — classification is decided ONCE

`RailMeta.BuildField` is the single place a field's class (Leaf/Descend/…/Excluded) is decided.
Downstream code (encoders, appliers, harness) READS the table; it never re-decides. Known violation,
kept deliberately: the blob encoder's Excluded-arm salvage (RailMeta.cs:1225 — init-only leaves +
local back-refs), ponytail-marked; move those into BuildField before adding a third case.

## L-E — refuse by classification, not by exception

A refusal must be a classify-time decision, visible in the coverage report and the baseline — never an
encode-time abort. An abort at encode (e.g. abstract element type with a declared-type-only codec) is
an exclusion by EXCEPTION: invisible until it fires. RailCheck prints these and L5 arms when the codec
turns polymorphic.

## L-F — classification change = law change (reviewable, apply-side first)

`docs/rail-baseline.txt` is the committed classifier snapshot; ANY drift is harness-RED. A field or
type moving Excluded↔covered — especially a ship-side WIDENING (more types reaching the codec, as in
the `7ef0a30` husk NOTEXT shape) — is a change to this law: it lands only with the apply-side able to
receive it and a harness law covering it, in the SAME commit as the regenerated baseline
(`dotnet run -c Debug -- --update`), so review sees the coverage delta next to the code that caused it.
