using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;

namespace InventoryTransfer
{
    public class TransferableItem
    {
        public string ItemCode;
        public string ItemType;
        public int Quantity;
        public string AttributesBase64;
        public string Source; // "player" | "bag" | "container" | "decor"; null for pre-1.5 manifests
    }

    public class SavedInventoryData
    {
        // Unique id stamped at export time. The import records it in the destination world's
        // savegame so the same manifest can't be imported twice into one world (duplication).
        public string ManifestId;
        public List<TransferableItem> Items = new List<TransferableItem>();
    }

    public class TransferSystem : ModSystem
    {
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockEntityClass("ImportChest", typeof(BlockEntityImportChest));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.ChatCommands.Create("exportinv")
                .WithDescription("Exports inventory, nearby containers (as furniture), decor, windmill/mechanical parts; excludes liquids (Radius 30). Add '--all' to also scan every placed crafted build block (walls/floors/doors/etc., skipping natural terrain). '--all --purge' turns the export into a CUT: after the manifest is safely written, everything exported is removed from the world — whatever remains is exactly what was NOT exported. Removed items stay recoverable via /importinv. CAUTION: liquids are never exported and are lost on purge.")
                .RequiresPrivilege(Privilege.chat)
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("mode"), api.ChatCommands.Parsers.OptionalWord("flag"))
                .HandleWith(OnCmdExport);

            api.ChatCommands.Create("importinv")
                .WithDescription("Imports inventory with Audit Report. Each manifest imports only ONCE per world (anti-duplication); add 'force' to deliberately import it again.")
                .RequiresPrivilege(Privilege.chat)
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("mode"))
                .HandleWith(OnCmdImport);
        }

        // PlayerInventoryLib (modid playerinventorylib) REPLACES the character and backpack
        // inventories with its own implementations, but export reads the vanilla inventories
        // by id ("character"/"backpack" via GetOwnInventory) and skips worn-bag content slots
        // by concrete type (ItemSlotBagContent) — with the lib installed a transfer runs
        // without any exception yet silently misses or mangles items. Warn, don't abort:
        // container-only transfers may still be fine and the player may know what they're doing.
        private static void WarnIfPlayerInventoryLib(ICoreAPI api, IServerPlayer player)
        {
            if (!api.ModLoader.IsModEnabled("playerinventorylib")) return;
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                "WARNING: PlayerInventoryLib is installed. It replaces the character and backpack inventories, which Inventory Transfer reads directly — transfers can silently miss or duplicate items with NO errors in the logs. Remove PlayerInventoryLib before exporting or importing.",
                EnumChatType.Notification);
        }

        private TextCommandResult OnCmdExport(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            // '--all' (also accepts 'all'/'-all') scans every placed crafted build block in range.
            // '--purge' (requires --all, either arg order) removes everything the export
            // captured from the world AFTER the manifest is safely written — the export becomes
            // a cut instead of a copy, and whatever is left standing/in inventories is exactly
            // what the exporter did not pick up.
            string mode = args.Parsers.Count > 0 ? args[0] as string : null;
            string flag = args.Parsers.Count > 1 ? args[1] as string : null;
            bool HasWord(string w) =>
                (!string.IsNullOrEmpty(mode) && mode.TrimStart('-').Equals(w, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(flag) && flag.TrimStart('-').Equals(w, StringComparison.OrdinalIgnoreCase));
            bool allBlocks = HasWord("all");
            bool purge = HasWord("purge");
            if (purge && !allBlocks)
            {
                return TextCommandResult.Error("--purge only works together with --all: /exportinv --all --purge");
            }

            if (player.WorldData.CurrentGameMode == EnumGameMode.Creative)
            {
                return TextCommandResult.Error("Export aborted: you are in Creative mode. Creative-mode inventory will be scooped into the manifest and on import TryGiveItemstack always succeeds, so the overflow import chests will never spawn and items will be lost into creative storage. Switch to Survival, then run /exportinv again.");
            }

            WarnIfPlayerInventoryLib(player.Entity.Api, player);

            SavedInventoryData saveData = new SavedInventoryData();
            
            int savedCount = 0;
            int filteredCount = 0;
            int containersScanned = 0;

            List<(ItemSlot slot, string source)> allSlots = new List<(ItemSlot, string)>();

            // --- PART 1: THE PLAYER ---
            string[] targetInvIds = { "hotbar", "character", "mouse" };
            foreach (string id in targetInvIds)
            {
                IInventory inv = player.InventoryManager.GetOwnInventory(id);
                if (inv != null) foreach (var slot in inv) allSlots.Add((slot, "player"));
            }

            IInventory bagHolder = player.InventoryManager.GetOwnInventory("backpack");
            if (bagHolder != null)
            {
                foreach (var slot in bagHolder)
                {
                    if (slot.Empty) continue;
                    // Skip bag CONTENT slots — vanilla worn-bag contents surface here as virtual
                    // ItemSlotBagContent slots, but we extract every bag's contents uniformly via
                    // IHeldBag in PART 2.5 below. Capturing them here too would double-count them.
                    if (slot is ItemSlotBagContent) continue;
                    allSlots.Add((slot, "player"));
                }
            }

            // --- PART 2: THE ROOM (World Scan) ---
            int radius = 30;
            BlockPos center = player.Entity.Pos.AsBlockPos;
            BlockPos minPos = center.AddCopy(-radius, -radius, -radius);
            BlockPos maxPos = center.AddCopy(radius, radius, radius);

            int decorPicked = 0;
            int containerBlocksPicked = 0;
            int mechanicalPicked = 0;
            int buildBlocksPicked = 0;
            int furniturePicked = 0;

            // Multiblock structures (trunks, large gears, pulverizers) fill their extra
            // positions with surrogate "multiblock-*" blocks whose OnPickBlock DELEGATES to the
            // principal block — so walking a 2-block trunk yielded the trunk twice (the player's
            // "3 trunks became 6"). Skip surrogates outright (the principal position exports the
            // real thing) and, belt-and-braces, never scan the same block entity twice.
            HashSet<BlockEntity> seenBlockEntities = new HashSet<BlockEntity>();

            // --purge: world positions to clear once the manifest has been written.
            List<BlockPos> purgePositions = purge ? new List<BlockPos>() : null;
            // Decor-layer entries to clear: (host position, faceAndSubposition decor index).
            List<(BlockPos pos, int decorIndex)> purgeDecors = purge ? new List<(BlockPos, int)>() : null;

            // --all diagnostic: block codes the walk saw but did NOT export — either a path
            // matched but OnPickBlock returned nothing (clutter behaves this way), or the block
            // has a block entity yet fell through every path. Dumped to the server log so missed
            // codes can be identified by name, not just by screenshot.
            Dictionary<string, int> skippedReport = allBlocks ? new Dictionary<string, int>() : null;
            void ReportSkip(string reason, string code)
            {
                if (skippedReport == null) return;
                string key = reason + " " + code;
                skippedReport[key] = skippedReport.GetValueOrDefault(key, 0) + 1;
            }

            // Belt-and-braces: a block's OnPickBlock implementation can itself throw (vanilla
            // BlockMPMultiblockPulverizer does when orphaned from its principal). One bad block
            // must not abort the entire export — log it, count it as skipped, keep walking.
            // A successful pick is additionally checked for render-safety (see
            // EnsureBookshelfRenderSafe): skipping HERE, before the stack enters allSlots,
            // matters because it also keeps the position off the purge list — purging a block
            // whose stack never made the manifest would be plain loss.
            ItemStack SafePick(Block b, BlockPos p)
            {
                ItemStack st;
                try { st = b.OnPickBlock(player.Entity.World, p); }
                catch (Exception e)
                {
                    player.Entity.Api.Logger.Warning(
                        "[inventorytransfer] OnPickBlock threw for {0} at {1}: {2}", b.Code, p, e.Message);
                    ReportSkip("pick-threw:", b.Code?.Path ?? "?");
                    return null;
                }
                if (st != null && !EnsureBookshelfRenderSafe(st))
                {
                    player.Entity.Api.Logger.Warning(
                        "[inventorytransfer] {0} at {1} picked a bookshelf stack with unrecoverable type/variant attributes — skipped (rendering it would crash the client)", b.Code, p);
                    ReportSkip("render-unsafe bookshelf:", b.Code?.Path ?? "?");
                    return null;
                }
                return st;
            }

            player.Entity.World.BlockAccessor.WalkBlocks(minPos, maxPos, (block, x, y, z) =>
            {
                BlockPos pos = new BlockPos(x, y, z);
                string blockCode = block.Code?.Path ?? "";

                // PATH 0: THE DECOR LAYER. Blocks with the "Decor" behavior — vanilla rugs,
                // small/medium carpets, rush mats, and modded equivalents — are NOT placed
                // blocks: they live in the chunk's separate decor layer, which WalkBlocks never
                // visits. Enumerate them per position or they are completely invisible to the
                // export (found via --purge testing: rugs were the only thing left standing).
                // The full variant (color/pattern/piece) is encoded in the block code, so a
                // plain ItemStack of the decor block round-trips faithfully.
                Dictionary<int, Block> subDecors = player.Entity.World.BlockAccessor.GetSubDecors(pos);
                if (subDecors != null)
                {
                    foreach (var kv in subDecors)
                    {
                        Block decorBlock = kv.Value;
                        if (decorBlock == null || decorBlock.Id == 0) continue;
                        allSlots.Add((new DummySlot(new ItemStack(decorBlock)), "decor"));
                        decorPicked++;
                        purgeDecors?.Add((pos, kv.Key));
                    }
                }

                BlockEntity be = player.Entity.World.BlockAccessor.GetBlockEntity(pos);

                // Surrogate positions render as part of their principal structure; when the
                // principal is purged the surrogates must go too or they linger as ghosts.
                // Mechanical-power surrogates (pulverizer top "mppulverizertop", large gear
                // "mpmultiblockwood") don't follow the "multiblock-*" naming — and vanilla
                // BlockMPMultiblockPulverizer.OnPickBlock THROWS when its principal can't be
                // resolved, killing the whole export — so ALSO match their shared BEMPMultiblock
                // block entity, which is naming-independent.
                if (blockCode.StartsWith("multiblock") || be is BEMPMultiblock)
                {
                    purgePositions?.Add(pos);
                    return;
                }

                // PATH 1: any BE that exposes an inventory — chests, crates, barrels, baskets,
                // toolracks, ground storage, displaycase, shelf, bookshelf, moldrack, scrollrack,
                // armor stands, etc. Broad-iterate now that we have an import chest to absorb
                // anything. NB: vanilla toolracks hold their inventory WITHOUT implementing
                // IBlockEntityContainer, so GetBlockEntityInventory falls back to reflection.
                IInventory beInventory = GetBlockEntityInventory(be);
                if (beInventory != null)
                {
                    if (!seenBlockEntities.Add(be)) return;
                    containersScanned++;
                    foreach (var slot in beInventory)
                    {
                        if (!slot.Empty) allSlots.Add((slot, "container"));
                    }
                    // Pick the container block itself regardless of contents — empty chests,
                    // crates, barrels, planters, flowerpots, etc. travel as furniture so the
                    // new base arrives stocked with storage to receive the imported items.
                    // GUARD: only when the pick actually IS this block (chest → chest item).
                    // Ground storage returns a clone of its CONTENTS from OnPickBlock — those
                    // stacks were already collected from the slots above, and blindly adding
                    // the pick duplicated them (the player's 19 copper ingots became ~38).
                    ItemStack pickedContainer = SafePick(block, pos);
                    if (pickedContainer != null && pickedContainer.StackSize > 0 &&
                        pickedContainer.Class == EnumItemClass.Block &&
                        pickedContainer.Block?.FirstCodePart() == block.FirstCodePart())
                    {
                        allSlots.Add((new DummySlot(pickedContainer), "container_block"));
                        containerBlocksPicked++;
                        purgePositions?.Add(pos);
                    }
                    else if (blockCode.StartsWith("groundstorage"))
                    {
                        // Ground storage IS its contents (captured from the slots above); the
                        // block has no exportable identity of its own, so purge clears it rather
                        // than leaving an emptied ghost pile that would read as "missed".
                        purgePositions?.Add(pos);
                    }
                    return;
                }

                // PATH 2: decor blocks (banners, tapestries, rugs, etc.). Lift as their item form
                // — OnPickBlock preserves painted/dyed/type attributes — and drop into the import
                // chest so the user can re-hang them manually in the new world.
                if (blockCode.Contains("banner") ||
                    blockCode.Contains("tapestry") ||
                    blockCode.Contains("painting") ||
                    blockCode.Contains("rug") ||
                    blockCode.Contains("carpet") ||
                    blockCode.Contains("lantern") ||   // metal lantern-up/down + paperlantern
                    blockCode.Contains("glass"))        // window glass-* and leaded glasspane-*
                {
                    ItemStack picked = SafePick(block, pos);
                    if (picked != null && picked.StackSize > 0)
                    {
                        allSlots.Add((new DummySlot(picked), "decor"));
                        decorPicked++;
                        purgePositions?.Add(pos);
                    }
                    else ReportSkip("pick-failed (decor):", blockCode);
                }
                // PATH 3: windmill / mechanical-power parts. The rotor (with its sails), wooden
                // axles, angled + large gears, transmission and the clutch/brake/toggle controls
                // are block entities but NOT containers, so neither path above catches them. Lift
                // each as its item form so the whole drivetrain can be rebuilt in the new world.
                else if (blockCode.Contains("windmill") ||
                         blockCode.Contains("rotor") ||
                         blockCode.Contains("axle") ||
                         blockCode.Contains("gear") ||
                         blockCode.Contains("transmission") ||
                         blockCode.Contains("clutch") ||
                         blockCode.Contains("brake") ||
                         blockCode.Contains("toggle"))
                {
                    ItemStack picked = SafePick(block, pos);
                    if (picked != null && picked.StackSize > 0)
                    {
                        allSlots.Add((new DummySlot(picked), "mechanical"));
                        mechanicalPicked++;
                        purgePositions?.Add(pos);
                    }
                    else ReportSkip("pick-failed (mechanical):", blockCode);
                }
                // PATH 3.5: standalone FUNCTIONAL FURNITURE that isn't a container (so PATH 1
                // misses it) and isn't decor/mechanical, but is clearly placed by the player and
                // worth carrying without forcing --all (which would also scoop every wall/floor).
                // Lifted as its item form — OnPickBlock keeps orientation/type attributes — and
                // re-placed manually in the new world. Keyword-matched so it stays mod-agnostic;
                // extend this list as more functional furniture turns up. Currently: the Bookkeeper
                // mod's lectern + manifest board, plus placed anvils (incl. helve anvil parts),
                // torch holders, and hanging oil lamps — none of which are containers, decor, or
                // mechanical, so nothing else catches them. Anvil work-in-progress smithing state
                // does not survive OnPickBlock; the anvil itself travels fine.
                else if (blockCode.Contains("lectern") ||
                         blockCode.Contains("bookkeeper") ||
                         blockCode.Contains("manifestboard") ||
                         blockCode.Contains("anvil") ||
                         blockCode.Contains("torchholder") ||
                         blockCode.Contains("oillamp"))
                {
                    ItemStack picked = SafePick(block, pos);
                    if (picked != null && picked.StackSize > 0)
                    {
                        allSlots.Add((new DummySlot(picked), "furniture"));
                        furniturePicked++;
                        purgePositions?.Add(pos);
                    }
                    else ReportSkip("pick-failed (furniture):", blockCode);
                }
                // PATH 4 (--all only): every other placed crafted build block — planks, stone
                // bricks/cobble, doors, fences, ladders, slabs, stairs, glass, metal, ceramic,
                // cloth, etc. Skip natural terrain so we don't scoop the mountain: raw rock/ore/
                // sandstone/crystal (Stone), grown logs/trees/saplings (Wood), and all Soil/Sand/
                // Gravel/Leaves/Plant/Liquid/Ice/Snow. Material-based so it stays mod-agnostic.
                else if (allBlocks && IsCraftedBuildBlock(block, player.Entity.World.BlockAccessor, pos))
                {
                    ItemStack picked = SafePick(block, pos);
                    if (picked != null && picked.StackSize > 0)
                    {
                        allSlots.Add((new DummySlot(picked), "block"));
                        buildBlocksPicked++;
                        purgePositions?.Add(pos);
                    }
                    else ReportSkip("pick-failed (build block):", blockCode);
                }
                // FALL-THROUGH DIAGNOSTIC (--all): a block entity we matched no path for is the
                // prime suspect for missed content (clutter, exotic modded furniture, ...).
                else if (be != null)
                {
                    ReportSkip("unhandled block entity:", blockCode);
                }
            });

            // --- PART 2.5: BAG CONTENTS (universal, via IHeldBag) ---
            // Held bags — vanilla backpacks/baskets and modded bags (backpackpackplus, etc.) — store
            // their contents in the bag itemstack's own attributes, NOT as separate slots. Worn bags
            // in containers, in hand, or nested inside other bags are all invisible to the scans
            // above. On import the bag's "backpack" attribute is stripped (stale world-local IDs), so
            // without this the contents would be lost entirely. IHeldBag.GetContents resolves them in
            // the live world (valid IDs) and we re-emit each as a portable "bag" line item.
            // Skip container_block picks — those blocks' inventories were already scanned as
            // "container" slots, so extracting again would duplicate them.
            int bagContentsExtracted = 0;
            Queue<ItemStack> bagQueue = new Queue<ItemStack>();
            foreach (var (slot, source) in allSlots)
            {
                if (!slot.Empty && source != "container_block") bagQueue.Enqueue(slot.Itemstack);
            }
            int bagSafety = 20000;
            while (bagQueue.Count > 0 && bagSafety-- > 0)
            {
                ItemStack bagStack = bagQueue.Dequeue();
                // IHeldBag is provided by a collectible BEHAVIOR (CollectibleBehaviorHeldBag), not
                // the collectible itself — so a direct cast misses every bag. GetCollectibleInterface
                // walks the collectible and its behaviors.
                IHeldBag heldBag = bagStack?.Collectible?.GetCollectibleInterface<IHeldBag>();
                if (heldBag != null && !heldBag.IsEmpty(bagStack))
                {
                    ItemStack[] contents;
                    try { contents = heldBag.GetContents(bagStack, player.Entity.World); }
                    catch { continue; }
                    if (contents == null) continue;
                    foreach (ItemStack contentStack in contents)
                    {
                        if (contentStack == null) continue;
                        contentStack.ResolveBlockOrItem(player.Entity.World);
                        if (contentStack.Collectible == null) continue;
                        allSlots.Add((new DummySlot(contentStack), "bag"));
                        bagContentsExtracted++;
                        bagQueue.Enqueue(contentStack); // recurse into bags nested inside bags
                    }
                }
            }

            // --- PART 3: PROCESS EVERYTHING ---
            Dictionary<string, int> savedBySource = new Dictionary<string, int>();
            int renderUnsafeDropped = 0;
            foreach (var (slot, source) in allSlots)
            {
                if (slot.Empty) continue;
                ItemStack stack = slot.Itemstack;

                // LIQUID FILTER — VS represents liquids two ways: as "*portion" stacks measured
                // in litres (inside vessels), and as placed liquid *blocks* (water/lava source
                // blocks, material Water/Lava) which can end up in a container's inventory and
                // display as e.g. "Still Water 1000 L". Neither can sit in inventory or be
                // placed in the new world, so drop both. The containing vessel (barrel, bucket,
                // watering can) is a separate line item and is preserved; only the liquid is
                // discarded. Ice/Snow are intentionally NOT filtered here — they transfer as
                // ordinary build material. (This API has no EnumBlockMaterial.Liquid; Water +
                // Lava cover the liquid block materials.)
                // VS 1.22 renamed the water liquid-block material to EnumBlockMaterial.Water;
                // VS 1.21.6 still calls it EnumBlockMaterial.Liquid. Lava is the same in both.
                bool isLiquidBlock = stack.Class == EnumItemClass.Block &&
                    (
#if VS1_21
                     stack.Block.BlockMaterial == EnumBlockMaterial.Liquid ||
#else
                     stack.Block.BlockMaterial == EnumBlockMaterial.Water ||
#endif
                     stack.Block.BlockMaterial == EnumBlockMaterial.Lava);
                if (stack.Collectible.Code.Path.Contains("portion") || isLiquidBlock)
                {
                    filteredCount += stack.StackSize;
                    continue;
                }

                // FILTER — exempt items explicitly lifted for transfer (decor blocks, container
                // blocks, bag contents). Without this, a wooden crate or log-coded barrel would be
                // culled as bulk material despite the user opting it in by storing items in it.
                // Bag contents are exempt too: the bag travels empty, so its packed bulk (chalk,
                // coal, etc.) must survive as loose stacks or it would be silently lost.
                // In --all mode the whole bulk cull is OFF: --all means "take everything", so
                // logs, planks, stone and soil sitting in trunks/chests/player inventory are
                // stored property and must travel (they were being silently dropped before).
                if (!allBlocks && stack.Class == EnumItemClass.Block && source != "container_block" && source != "decor" && source != "bag" && source != "mechanical" && source != "block" && source != "furniture")
                {
                    Block block = stack.Block;
                    if (!block.Code.Path.Contains("chest") &&
                        !block.Code.Path.Contains("torch") &&
                        !block.Code.Path.Contains("mold"))
                    {
                        if (block.BlockMaterial == EnumBlockMaterial.Stone || 
                            block.BlockMaterial == EnumBlockMaterial.Soil ||
                            block.BlockMaterial == EnumBlockMaterial.Gravel ||
                            block.BlockMaterial == EnumBlockMaterial.Sand ||
                            block.Code.Path.Contains("cobblestone") || 
                            block.Code.Path.Contains("plaster") ||
                            block.Code.Path.Contains("planks") ||
                            block.Code.Path.Contains("log"))
                        {
                            filteredCount += stack.StackSize;
                            continue; 
                        }
                    }
                }

                // RENDER-SAFETY GUARD — SafePick already covers world-picked blocks, but a
                // malformed clutter bookshelf stack can also arrive as CONTENTS (chest/bag/
                // player inventory — e.g. re-exporting a world that received one from a
                // pre-1.10.4 import). Repair it here or drop it from the manifest; importing
                // it would crash the destination client on every GUI open.
                if (!EnsureBookshelfRenderSafe(stack))
                {
                    player.Entity.Api.Logger.Warning(
                        "[inventorytransfer] dropped render-unsafe bookshelf stack {0}x{1} (source {2}) — unrecoverable type/variant attributes", stack.StackSize, stack.Collectible.Code, source);
                    renderUnsafeDropped += stack.StackSize;
                    continue;
                }

                TransferableItem tItem = new TransferableItem();
                tItem.ItemCode = stack.Collectible.Code.ToString();
                tItem.ItemType = (stack.Class == EnumItemClass.Block) ? "Block" : "Item";
                tItem.Quantity = stack.StackSize;
                tItem.Source = source;

                if (stack.Attributes != null)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (BinaryWriter writer = new BinaryWriter(ms))
                        {
                            stack.Attributes.ToBytes(writer);
                            tItem.AttributesBase64 = Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }

                saveData.Items.Add(tItem);
                savedCount++;
                savedBySource[source] = savedBySource.GetValueOrDefault(source, 0) + 1;
            }

            // Group the manifest by item type (then by attributes) so that on import the same-type
            // stacks are processed consecutively. The overflow-chest fill tops up matching slots
            // before walking east, so a sorted manifest lands each type in contiguous chests and
            // merges partial stacks into full ones — e.g. all chalk stone ends up together across
            // adjacent chests instead of scattered wherever the world scan happened to find it.
            // Primary key pushes decor (banners, tapestries, paintings, lanterns, glass) into its
            // own section at the very end, after all the materials/items.
            saveData.Items = saveData.Items
                .OrderBy(it => it.Source == "decor" ? 1 : 0)
                .ThenBy(it => it.ItemCode, StringComparer.Ordinal)
                .ThenBy(it => it.AttributesBase64 ?? "", StringComparer.Ordinal)
                .ToList();

            // Every export gets a fresh id, so a NEW export can always be imported — only
            // re-importing the SAME manifest into the same world is blocked (anti-duplication).
            saveData.ManifestId = Guid.NewGuid().ToString("N");

            args.Caller.Player.Entity.Api.StoreModConfig(saveData, "feyd_inventory_transfer.json");

            // --- PURGE --- runs strictly AFTER the manifest write above, so every removed item
            // is recoverable via import (same world or another — the once-per-world guard keys
            // on manifest id, and this manifest is freshly stamped). Empties every real slot the
            // scan captured (player + container contents; DummySlot picks aren't world-backed)
            // and clears every picked block position. Whatever remains in the world afterwards
            // is precisely what the export did not capture. NB: slots holding by-design-excluded
            // stacks (liquids) are emptied too — that content is NOT in the manifest.
            int purgedSlots = 0, purgedBlocks = 0;
            if (purge)
            {
                foreach (var (slot, source) in allSlots)
                {
                    if (source != "player" && source != "container") continue;
                    if (slot.Empty) continue;
                    slot.Itemstack = null;
                    slot.MarkDirty();
                    purgedSlots++;
                }
                IBlockAccessor accessor = player.Entity.World.BlockAccessor;
                foreach (BlockPos pos in purgePositions)
                {
                    accessor.SetBlock(0, pos);
                    purgedBlocks++;
                }
                // Decor layer: SetDecor with the air block removes cleanly WITHOUT dropping the
                // decor as an item (BreakDecor would drop it — with Better Ruins' rugs-drop-
                // itself patch that means dup: the rug in the manifest AND on the ground).
                Block airBlock = accessor.GetBlock(0);
                foreach (var (dpos, decorIndex) in purgeDecors)
                {
                    accessor.SetDecor(airBlock, dpos, decorIndex);
                    purgedBlocks++;
                }
            }

            // Skipped-block report (--all): full list to server-main.log, count in chat.
            if (skippedReport != null && skippedReport.Count > 0)
            {
                var logger = player.Entity.Api.Logger;
                logger.Notification("[inventorytransfer] exportinv --all skipped-block report, {0} distinct codes:", skippedReport.Count);
                foreach (var kv in skippedReport.OrderByDescending(kv => kv.Value))
                {
                    logger.Notification("[inventorytransfer]   {0} x{1}", kv.Key, kv.Value);
                }
            }

            string sourceBreakdown = string.Join(", ",
                savedBySource.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

            return TextCommandResult.Success(
                $"Export Complete.\n" +
                $"Scanned {containersScanned} containers.\n" +
                $"Picked {containerBlocksPicked} container blocks (incl. empty furniture).\n" +
                $"Picked {decorPicked} decor blocks.\n" +
                $"Picked {mechanicalPicked} windmill/mechanical parts.\n" +
                $"Picked {furniturePicked} furniture blocks.\n" +
                (allBlocks ? $"Picked {buildBlocksPicked} build blocks (--all).\n" : "") +
                $"Extracted {bagContentsExtracted} items from held bags.\n" +
                $"By source — {sourceBreakdown}\n" +
                $"Saved {savedCount} items.\n" +
                $"Filtered {filteredCount} blocks." +
                (renderUnsafeDropped > 0
                    ? $"\nDropped {renderUnsafeDropped} broken clutter bookshelf items (missing shape data — displaying them crashes the game; details in server-main.log)."
                    : "") +
                (skippedReport != null && skippedReport.Count > 0
                    ? $"\nSkipped {skippedReport.Count} block codes NOT exported — full list in server-main.log."
                    : "") +
                (purge
                    ? $"\nPURGED: emptied {purgedSlots} slots, removed {purgedBlocks} blocks. Anything still visible was NOT exported. Everything removed is in the manifest and restorable via /importinv — except liquids, which are never exported."
                    : "")
            );
        }

        // Vanilla clutter bookshelves (the Reso Archives lore shelves that Better Ruins etc.
        // scatter in ruins) render their item form from "type" + "variant" stack attributes.
        // A ruin-spawned shelf's BE behavior can hold a null Variant, and vanilla OnPickBlock
        // then emits a stack with a type but NO variant — and BlockClutterBookshelfWithLore's
        // genCombinedMesh does not null-check GetTypeProps, so merely DISPLAYING that stack in
        // any inventory GUI hard-crashes the client with an NRE in
        // BlockClutterBookshelf.GetOrCreateMesh (reported by Feyd 2026-08-05).
        // Returns true if the stack is safe (repairing it in place when possible: the owning
        // variant group is recoverable by searching the block's variant groups for the stack's
        // type). Returns false if unrecoverable — the caller must drop the stack; a missing
        // bookshelf beats a client that crashes on every chest open.
        private static bool EnsureBookshelfRenderSafe(ItemStack stack)
        {
            if (!(stack?.Block is BlockClutterBookshelf shelf)) return true;
            string type = stack.Attributes.GetString("type");
            // NB GetTypeProps intentionally mutates doublesided-group stacks (fills in random
            // type1/type2 when absent) — same thing vanilla does on first render; doing it at
            // export just pins the result into the manifest.
            if (shelf.GetTypeProps(type, stack, null) != null) return true;
            if (type != null)
            {
                foreach (var group in shelf.variantGroupsByCode)
                {
                    if (group.Value.typesByCode.ContainsKey(type))
                    {
                        stack.Attributes.SetString("variant", group.Key);
                        return shelf.GetTypeProps(type, stack, null) != null;
                    }
                }
            }
            return false;
        }

        // Decides whether a plain placed block is a player-built/crafted block worth exporting in
        // --all mode (as opposed to natural terrain we must not scoop). Material-based so it works
        // for modded blocks too: take Wood/Metal/Glass/Ceramic/Cloth/Brick, plus crafted Stone
        // (cobble/bricks/polished/paths), while excluding the natural variants that share those
        // materials — raw rock/ore/sandstone/crystal (Stone) and grown trees/logs/saplings (Wood).
        // Everything Soil/Sand/Gravel/Leaves/Plant/Liquid/Ice/Snow/Air is left in the ground.
        // Material is read position-aware via GetBlockMaterial: for plain blocks this equals the
        // BlockMaterial property, but for chiseled/microblocks it returns the actual contained
        // material (e.g. snow/plant grass) rather than the default Stone, so natural-material
        // chisel decor is excluded while genuine stone/wood chisel decor still travels.
        private static bool IsCraftedBuildBlock(Block block, IBlockAccessor accessor, BlockPos pos)
        {
            if (block == null || block.Id == 0) return false;
            string cp = block.Code?.Path ?? "";
            switch (block.GetBlockMaterial(accessor, pos))
            {
                case EnumBlockMaterial.Metal:
                case EnumBlockMaterial.Glass:
                case EnumBlockMaterial.Ceramic:
                case EnumBlockMaterial.Cloth:
                case EnumBlockMaterial.Brick:
                    return true;
                case EnumBlockMaterial.Wood:
                    // exclude natural trees/logs/branches/saplings; keep planks, doors, fences, etc.
                    return !cp.StartsWith("log") && !cp.StartsWith("fruittree") &&
                           !cp.StartsWith("sapling") && !cp.Contains("tree");
                case EnumBlockMaterial.Stone:
                    // exclude natural rock/ore/sandstone/crystal, plus the Stone-material terrain
                    // whose codes don't start with "rock": crackedrock (1.20+ unstable-rock system
                    // — cave walls / mined tunnels convert to it, so bases are full of them),
                    // loose surface stones/boulders/rubble/flints/ores, stalagmites/stalactites,
                    // and meteorites; keep cobble/bricks/polished/paths
                    return !cp.StartsWith("rock") && !cp.StartsWith("ore") &&
                           !cp.StartsWith("sandstone") && !cp.Contains("crystal") &&
                           !cp.StartsWith("crackedrock") && !cp.StartsWith("loose") &&
                           !cp.StartsWith("stalag") && !cp.StartsWith("meteorite");
                default:
                    return false;
            }
        }

        // Returns the BE's inventory whether it implements IBlockEntityContainer or — like
        // vanilla toolracks (BlockEntityToolrack is a bare BlockEntity with a public
        // InventoryGeneric field) — just holds an IInventory member without the interface.
        // Reflection keeps it mod-agnostic for other rack-style block entities too.
        private static IInventory GetBlockEntityInventory(BlockEntity be)
        {
            if (be == null) return null;
            if (be is IBlockEntityContainer container) return container.Inventory;

            foreach (var field in be.GetType().GetFields())
            {
                if (typeof(IInventory).IsAssignableFrom(field.FieldType))
                {
                    return field.GetValue(be) as IInventory;
                }
            }
            foreach (var prop in be.GetType().GetProperties())
            {
                if (typeof(IInventory).IsAssignableFrom(prop.PropertyType) && prop.GetIndexParameters().Length == 0)
                {
                    try { return prop.GetValue(be) as IInventory; } catch { return null; }
                }
            }
            return null;
        }

        // Pre-1.9 manifests carry no ManifestId; derive a stable one from the content so the
        // once-per-world import guard applies to them too.
        private static string ComputeLegacyManifestId(SavedInventoryData data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                string fingerprint = string.Join("|",
                    data.Items.Select(it => $"{it.ItemCode}:{it.Quantity}:{it.AttributesBase64}"));
                return "legacy-" + Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprint)));
            }
        }

        private TextCommandResult OnCmdImport(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            if (player.WorldData.CurrentGameMode == EnumGameMode.Creative)
            {
                return TextCommandResult.Error("Import aborted: you are in Creative mode. TryGiveItemstack always succeeds in Creative, so the overflow import chests will never spawn and your exported items will vanish into the creative inventory. Switch to Survival, then run /importinv.");
            }

            ICoreServerAPI api = args.Caller.Player.Entity.Api as ICoreServerAPI;

            WarnIfPlayerInventoryLib(api, player);

            SavedInventoryData loadedData = api.LoadModConfig<SavedInventoryData>("feyd_inventory_transfer.json");

            if (loadedData == null || loadedData.Items.Count == 0) return TextCommandResult.Error("No exported data found.");

            // ONCE-PER-WORLD IMPORT GUARD — importing the same manifest twice would duplicate
            // every item in it. Imported manifest ids are recorded in this world's savegame, so
            // the block is per-world: the same manifest can still seed a DIFFERENT world, and a
            // fresh /exportinv (new id) can always be imported. 'force' deliberately overrides.
            string mode = args.Parsers.Count > 0 ? args[0] as string : null;
            bool force = !string.IsNullOrEmpty(mode) && mode.TrimStart('-').Equals("force", StringComparison.OrdinalIgnoreCase);

            string manifestId = string.IsNullOrEmpty(loadedData.ManifestId)
                ? ComputeLegacyManifestId(loadedData)
                : loadedData.ManifestId;
            const string importedDataKey = "inventorytransfer_importedmanifests";
            byte[] importedRaw = api.WorldManager.SaveGame.GetData(importedDataKey);
            HashSet<string> importedIds = importedRaw == null
                ? new HashSet<string>()
                : new HashSet<string>(Encoding.UTF8.GetString(importedRaw).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            if (importedIds.Contains(manifestId) && !force)
            {
                return TextCommandResult.Error(
                    "Import blocked: this manifest was already imported into this world — importing it again would duplicate every item. " +
                    "Run /exportinv in the source world to create a fresh manifest, or use '/importinv force' if you really want a second copy.");
            }

            // AUDIT COUNTERS
            int totalManifest = loadedData.Items.Count;
            int successfulItems = 0;
            int overflowCount = 0;
            int liquidVesselsEmptied = 0;
            int sealedCrocksReclaimed = 0;
            List<string> missingItems = new List<string>();
            Dictionary<string, int> manifestBySource = new Dictionary<string, int>();
            Dictionary<string, int> landedBySource = new Dictionary<string, int>();

            BlockPos overflowPos = player.Entity.Pos.AsBlockPos.Copy();

            foreach (var tItem in loadedData.Items)
            {
                string src = tItem.Source ?? "unknown";
                manifestBySource[src] = manifestBySource.GetValueOrDefault(src, 0) + 1;

                bool itemSuccess = false;
                try
                {
                    CollectibleObject collectible = null;
                    AssetLocation code = new AssetLocation(tItem.ItemCode);
                    if (tItem.ItemType == "Block") collectible = api.World.GetBlock(code);
                    else collectible = api.World.GetItem(code);
                    
                    if (collectible != null)
                    {
                        ItemStack newStack = new ItemStack(collectible, tItem.Quantity);
                        // Held bags carry a recipeCode/contents too — exclude them from the food
                        // vessel logic below so a backpack isn't mistaken for a meal and emptied.
                        bool isHeldBag = collectible.GetCollectibleInterface<IHeldBag>() != null;
                        // For held bags, import a PRISTINE empty bag: skip the source attributes
                        // entirely so VS rebuilds a valid empty slot structure from the item type.
                        // Loading the old attributes left the bag malformed/zero-slot (it couldn't
                        // hold items); its real contents already arrive as separate "bag" lines.
                        if (!isHeldBag && !string.IsNullOrEmpty(tItem.AttributesBase64))
                        {
                            byte[] data = Convert.FromBase64String(tItem.AttributesBase64);
                            using (MemoryStream ms = new MemoryStream(data))
                            {
                                using (BinaryReader reader = new BinaryReader(ms))
                                {
                                    TreeAttribute tree = new TreeAttribute();
                                    tree.FromBytes(reader);
                                    newStack.Attributes = tree;
                                }
                            }
                        }

                        // RENDER-SAFETY GUARD — manifests written before 1.10.4 can carry
                        // clutter bookshelf stacks with a "type" but no "variant" attribute;
                        // merely displaying such a stack in any inventory GUI crashes the
                        // client (see EnsureBookshelfRenderSafe). Repair it, or refuse to
                        // deliver it into this world and list it in the audit instead.
                        if (!EnsureBookshelfRenderSafe(newStack))
                        {
                            missingItems.Add($"{tItem.ItemCode} x{tItem.Quantity} (broken bookshelf shape data — displaying it would crash the game; not imported)");
                            continue;
                        }

                        // Strip cooked food contents from clay/iron pots — nested ItemStack IDs
                        // inside "contents" are world-specific and won't resolve after transfer,
                        // producing ghost food (eating animation plays but no satiety). Pot
                        // arrives empty; cook fresh in the new world.
                        if (newStack.Attributes != null)
                        {
                            string itemPath = tItem.ItemCode;
                            bool isCookingVessel = itemPath.Contains("claypot") || itemPath.Contains("ironpot");
                            if (isCookingVessel && newStack.Attributes.GetTreeAttribute("contents") != null)
                            {
                                newStack.Attributes.RemoveAttribute("contents");
                                newStack.Attributes.RemoveAttribute("sealed");
                                missingItems.Add($"{tItem.ItemCode} (crockpot food stripped — stale item IDs; pot imported empty)");
                            }
                        }

                        // LIQUID-IN-VESSEL STRIP — buckets, jugs, barrels, and watering cans hold
                        // their liquid as item attributes (not a separate *portion slot), so the
                        // export-side liquid filter can't see it. Strip it so these vessels import
                        // empty (no-liquids policy). The liquid is serialized by world-specific
                        // numeric ID anyway and wouldn't resolve correctly across worlds. Stripped
                        // silently (counted, not listed) so the 46+ vessels don't bury real misses.
                        if (newStack.Attributes != null)
                        {
                            string vp = tItem.ItemCode;
                            bool isLiquidVessel = vp.Contains("bucket") || vp.Contains("wateringcan")
                                || vp.Contains("jug") || vp.Contains("barrel");
                            if (isLiquidVessel)
                            {
                                bool hadLiquid = newStack.Attributes.GetTreeAttribute("contents") != null
                                    || newStack.Attributes.HasAttribute("wateringSeconds");
                                if (hadLiquid)
                                {
                                    newStack.Attributes.RemoveAttribute("contents");
                                    newStack.Attributes.RemoveAttribute("wateringSeconds");
                                    newStack.Attributes.RemoveAttribute("sealed");
                                    liquidVesselsEmptied++;
                                }
                            }
                        }

                        // CROCK — crocks carry preserved food ("contents", plus a "recipeCode"
                        // when it's a cooked meal) and a "sealed" flag meaning a beeswax/fat
                        // seal was spent on them. The food can't cross worlds (stale numeric
                        // item IDs, same as pots/bowls), but the seal is a real material cost —
                        // so strip the food and refund one sealant per sealed crock after the
                        // loop. The crock only records THAT it was sealed, not with what, so
                        // the refund is paid in beeswax.
                        if (newStack.Attributes != null && tItem.ItemCode.Contains("crock"))
                        {
                            bool wasSealed = newStack.Attributes.GetBool("sealed", false);
                            bool hadFood = newStack.Attributes.GetTreeAttribute("contents") != null
                                || newStack.Attributes["recipeCode"] != null;
                            if (hadFood)
                            {
                                foreach (string key in new[] { "recipeCode", "quantityServings", "servings",
                                    "contents", "sealed", "cookedMealProps" })
                                {
                                    newStack.Attributes.RemoveAttribute(key);
                                }
                                missingItems.Add($"{tItem.ItemCode} (crock food stripped — stale item IDs; crock imported empty)");
                            }
                            else if (wasSealed)
                            {
                                newStack.Attributes.RemoveAttribute("sealed");
                            }
                            if (wasSealed) sealedCrocksReclaimed += tItem.Quantity;
                        }

                        // Any item with a recipeCode attribute is a filled food vessel —
                        // bowls, ceramic pots, or any mod container. Food ingredient stacks
                        // embed world-specific numeric IDs that resolve to wrong items after
                        // transfer, giving ghost food: eating animation plays, no satiety.
                        // For known bowl types swap to an empty vessel; otherwise strip all
                        // food attributes so the container arrives empty and functional.
                        if (!isHeldBag && newStack.Attributes != null && newStack.Attributes["recipeCode"] != null)
                        {
                            bool swapped = false;
                            if (tItem.ItemCode.Contains("bowl"))
                            {
                                CollectibleObject emptyBowl = api.World.GetItem(new AssetLocation("game:bowl-empty"));
                                if (emptyBowl != null)
                                {
                                    newStack = new ItemStack(emptyBowl, 1);
                                    swapped = true;
                                }
                            }
                            if (!swapped)
                            {
                                foreach (string key in new[] { "recipeCode", "servings", "contents",
                                    "ingr0", "ingr1", "ingr2", "ingr3", "cookedMealProps" })
                                {
                                    newStack.Attributes.RemoveAttribute(key);
                                }
                            }
                            missingItems.Add($"{tItem.ItemCode} (imported as empty — food content not transferable across worlds)");
                        }

                        try
                        {
                            if (player.InventoryManager.TryGiveItemstack(newStack))
                            {
                                successfulItems++;
                                itemSuccess = true;
                            }
                            else
                            {
                                StoreInOverflowChest(api, newStack, ref overflowPos);
                                overflowCount++;
                                successfulItems++;
                                itemSuccess = true;
                            }
                        }
                        catch (Exception)
                        {
                            // If placing into inventory still crashes (e.g. resolve failure),
                            // try the overflow chest as a fallback
                            try
                            {
                                StoreInOverflowChest(api, newStack, ref overflowPos);
                                overflowCount++;
                                successfulItems++;
                                itemSuccess = true;
                            }
                            catch (Exception)
                            {
                                missingItems.Add($"{tItem.ItemCode} (failed to resolve in new world)");
                            }
                        }
                    }
                }
                catch (Exception) { }

                if (itemSuccess)
                {
                    landedBySource[src] = landedBySource.GetValueOrDefault(src, 0) + 1;
                }
                else
                {
                    missingItems.Add(tItem.ItemCode);
                }
            }

            // SEALANT REFUND — one beeswax per sealed crock stripped above (wax and rendered
            // fat both seal crocks, but the crock doesn't record which was used).
            int sealantRefunded = 0;
            if (sealedCrocksReclaimed > 0)
            {
                CollectibleObject beeswax = api.World.GetItem(new AssetLocation("game:beeswax"));
                if (beeswax != null)
                {
                    int remaining = sealedCrocksReclaimed;
                    while (remaining > 0)
                    {
                        ItemStack waxStack = new ItemStack(beeswax, Math.Min(remaining, beeswax.MaxStackSize));
                        remaining -= waxStack.StackSize;
                        sealantRefunded += waxStack.StackSize;
                        if (!player.InventoryManager.TryGiveItemstack(waxStack))
                        {
                            StoreInOverflowChest(api, waxStack, ref overflowPos);
                        }
                    }
                }
            }

            // Mark this manifest as imported into this world (anti-duplication guard above).
            importedIds.Add(manifestId);
            api.WorldManager.SaveGame.StoreData(importedDataKey, Encoding.UTF8.GetBytes(string.Join("\n", importedIds)));

            // BUILD THE REPORT
            StringBuilder report = new StringBuilder();
            report.AppendLine("--- Import Audit (Feyd) ---");
            report.AppendLine($"Manifest Total: {totalManifest}");

            report.AppendLine("By source (landed / manifest):");
            foreach (string src in manifestBySource.Keys.Union(landedBySource.Keys).OrderBy(k => k))
            {
                int mfst = manifestBySource.GetValueOrDefault(src, 0);
                int lnd = landedBySource.GetValueOrDefault(src, 0);
                string marker = (lnd == mfst) ? "OK" : $"MISSING {mfst - lnd}";
                report.AppendLine($"  {src}: {lnd} / {mfst}  [{marker}]");
            }

            report.AppendLine($"Successfully Landed: {successfulItems}");
            report.AppendLine($" - In Pockets: {successfulItems - overflowCount}");
            report.AppendLine($" - In Chests: {overflowCount}");
            if (liquidVesselsEmptied > 0)
                report.AppendLine($"Liquid vessels emptied: {liquidVesselsEmptied} (buckets/jugs/barrels/cans — no-liquids policy)");
            if (sealantRefunded > 0)
                report.AppendLine($"Crock sealant reclaimed: {sealantRefunded}x beeswax (crocks don't record wax vs. fat — refunded as wax)");
            report.AppendLine("This manifest is now locked for this world — re-running /importinv will NOT duplicate items ('/importinv force' overrides).");
            
            if (missingItems.Count > 0)
            {
                report.AppendLine("--- MISSING ITEMS ---");
                int showCount = Math.Min(missingItems.Count, 5);
                for (int i = 0; i < showCount; i++) report.AppendLine($"[!] {missingItems[i]}");
                if (missingItems.Count > 5) report.AppendLine($"...and {missingItems.Count - 5} more.");
            }
            else
            {
                report.AppendLine("Result: 100% PERFECT TRANSFER.");
            }

            return TextCommandResult.Success(report.ToString());
        }

        private void StoreInOverflowChest(ICoreServerAPI api, ItemStack stack, ref BlockPos pos)
        {
            int safetyLimit = 256;
            while (stack.StackSize > 0 && safetyLimit-- > 0)
            {
                BlockEntity be = api.World.BlockAccessor.GetBlockEntity(pos);
                IBlockEntityContainer container = be as IBlockEntityContainer;

                if (container == null)
                {
                    Block chestBlock = api.World.GetBlock(new AssetLocation("inventorytransfer:importchest-east"));
                    if (chestBlock == null) chestBlock = api.World.GetBlock(new AssetLocation("game:chest-east"));
                    if (chestBlock == null) chestBlock = api.World.GetBlock(new AssetLocation("game:trunk-east"));

                    if (chestBlock != null)
                    {
                        api.World.BlockAccessor.SetBlock(chestBlock.Id, pos);
                        be = api.World.BlockAccessor.GetBlockEntity(pos);
                        container = be as IBlockEntityContainer;
                    }
                }

                if (container != null && container.Inventory != null)
                {
                    foreach (var slot in container.Inventory)
                    {
                        if (stack.StackSize <= 0) break;
                        if (slot.Empty) continue;
                        // Attribute-aware match, not just code: tapestry pieces, painted/typed
                        // decor, and filled containers all share one collectible code and differ
                        // only in attributes. Merging on code alone collapsed multi-piece
                        // tapestries into N copies of one piece (the other pieces vanished).
                        if (slot.Itemstack.Equals(api.World, stack, GlobalConstants.IgnoredStackAttributes))
                        {
                            int maxStack = slot.Itemstack.Collectible.MaxStackSize;
                            int space = maxStack - slot.Itemstack.StackSize;
                            if (space > 0)
                            {
                                int move = Math.Min(space, stack.StackSize);
                                slot.Itemstack.StackSize += move;
                                stack.StackSize -= move;
                                slot.MarkDirty();
                            }
                        }
                    }
                    foreach (var slot in container.Inventory)
                    {
                        if (stack.StackSize <= 0) break;
                        if (slot.Empty)
                        {
                            int move = Math.Min(stack.StackSize, stack.Collectible.MaxStackSize);
                            ItemStack toPlace = stack.Clone();
                            toPlace.StackSize = move;
                            slot.Itemstack = toPlace;
                            stack.StackSize -= move;
                            slot.MarkDirty();
                        }
                    }
                }
                if (stack.StackSize > 0) pos.East();
            }
        }
    }
}