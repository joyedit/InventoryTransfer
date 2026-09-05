# Changelog

All notable changes to Inventory Transfer are documented here. Newest first.

## 1.11.0 — 2026-09-05

- New: `/exportinv --radius N` sets how far the world scan reaches (default
  30, max 128) — e.g. `/exportinv --radius 100` to catch an outbuilding
  without dismantling it. Also accepts `--radius=N`, `-r N`, or a bare
  number. The scan is a cube and its cost is cubic, so large radii make the
  export take noticeably longer.
- New: the export summary now reports the exact area scanned, and warns when
  chunks inside that area were not loaded and therefore not scanned.
- Fix: placed tool molds and ingot molds are now exported along with their
  cast contents. They keep their metal outside any inventory, so they were
  invisible to the scan — and even with `--all` only the bare mold (or one
  side of an ingot mold) came through.
- Unknown `/exportinv` options are now rejected instead of ignored, so a
  typo'd `--al` can no longer run a plain export that looks like `--all`.

## 1.10.5 — 2026-08-22

- Compatibility release for Vintage Story 1.22.7. Rebuilt against the 1.22.7
  assemblies; no code changes.

## 1.10.4 — 2026-08-05

- Fix: exported lore bookshelves (the ruin-spawned "cluttered bookshelf"
  blocks) could crash the game client with "Object reference not set to an
  instance of an object" the moment a chest containing them was opened.
  Ruin-spawned shelves can carry no variant data, and vanilla's item
  renderer crashes on such a stack. Export now repairs the missing variant
  where possible; unrepairable shelves are dropped from the manifest and
  reported instead of crashing the game. Import applies the same repair to
  manifests written by older versions.

## 1.10.3 — 2026-08-05

- Fix: `/exportinv --all` no longer crashes with "Can't create itemstack
  without block!" near a pulverizer. The mechanical-power multiblock filler
  blocks (`mppulverizertop`, `mpmultiblockwood`) don't follow the
  `multiblock-*` naming the surrogate skip matched on, so the build-block
  sweep called the pulverizer surrogate's own OnPickBlock — which throws
  inside vanilla when it can't resolve its principal block. Surrogates are
  now recognized by their shared BEMPMultiblock block entity (naming-
  independent, also fixes potential double-export of large gears).
- Hardening: any block whose OnPickBlock throws is now logged and reported
  as skipped instead of aborting the entire export.

## 1.10.2 — 2026-08-05

- Fix: multi-piece tapestries (2- and 3-piece sets) no longer arrive as N
  copies of a single piece. The overflow-chest fill merged stacks by
  collectible code alone, but every tapestry piece is the same `tapestry`
  block differing only in its `type` attribute, so the pieces were merged
  into one. The merge is now attribute-aware, which also protects any other
  code-shared, attribute-differentiated stacks landing in overflow chests.

## 1.10.1 — 2026-07-29

- Fix: `--all` exports no longer scoop natural Stone-material terrain whose
  block codes slip past the rock/ore/sandstone prefix check — cracked rock
  (the 1.20+ unstable-rock blocks that cave walls and mined tunnels convert
  to, which filled export chests with "cracked granite" etc.), loose
  stones/boulders/rubble/flints/ores, stalagmites/stalactites, and
  meteorites. These stay in the ground (and are no longer removed by
  `--purge`).

## 1.10.0 — 2026-07-25

- New `/exportinv --all --purge`: turns the export into a CUT. After the
  manifest is safely written, everything exported is removed from the world —
  whatever remains is exactly what was NOT exported, so you can see at a
  glance what the export covered. Everything removed stays recoverable via
  `/importinv` (into the same world or another). Caution: liquids are never
  exported, so purge discards them.
- Decor-layer blocks (rugs, carpets, rush mats) now export: they live in the
  chunk's decor layer, which the world scan never visited before.
- Under `--all`, stored bulk materials (logs, planks, stone, soil) in
  containers are now exported instead of being filtered out.
- `--all` now logs a skipped-block report to server-main.log listing every
  block code it declined to export.
- Known incompatibility: PlayerInventoryLib replaces the character/backpack
  inventories that Inventory Transfer reads, so transfers silently miss or
  duplicate items with no errors in the logs (thanks to AshOfTheLostVeil for
  tracking this down). `/exportinv` and `/importinv` now print a chat warning
  when PlayerInventoryLib is detected, and the mod description notes the
  incompatibility.

## 1.9.1 — 2026-07-23

- Compatibility release for Vintage Story 1.22.5. Rebuilt against the 1.22.5
  assemblies; no code changes.

## 1.9.0

- Import anti-duplication lock, dup fixes, toolrack/anvil export, crock
  sealant refund.
