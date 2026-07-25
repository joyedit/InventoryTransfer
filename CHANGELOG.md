# Changelog

All notable changes to Inventory Transfer are documented here. Newest first.

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
