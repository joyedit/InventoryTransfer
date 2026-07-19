using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace InventoryTransfer
{
    public class BlockEntityImportChest : BlockEntityGenericTypedContainer
    {
        private bool vanishScheduled;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            if (Inventory != null)
            {
                Inventory.SlotModified += OnSlotModified;
            }
        }

        public override void OnBlockRemoved()
        {
            if (Inventory != null) Inventory.SlotModified -= OnSlotModified;
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            if (Inventory != null) Inventory.SlotModified -= OnSlotModified;
            base.OnBlockUnloaded();
        }

        private void OnSlotModified(int slotid)
        {
            if (Api?.Side != EnumAppSide.Server) return;
            if (vanishScheduled) return;
            if (Inventory == null) return;

            foreach (ItemSlot slot in Inventory)
            {
                if (!slot.Empty) return;
            }

            vanishScheduled = true;
            // Defer so we don't mutate the world inside the slot-change callback.
            Api.World.RegisterCallback(_ => Vanish(), 50);
        }

        private void Vanish()
        {
            if (Api == null) return;

            Block block = Api.World.BlockAccessor.GetBlock(Pos);
            if (block != null && block.Id != 0)
            {
                Vec3d center = Pos.ToVec3d().Add(0.5, 0.5, 0.5);
                Api.World.SpawnCubeParticles(center, new ItemStack(block), 0.6f, 24, 1.0f);
            }

            Api.World.PlaySoundAt(
                new AssetLocation("sounds/block/planks"),
                Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5,
                null, true, 16f, 1f
            );

            Api.World.BlockAccessor.SetBlock(0, Pos);
        }
    }
}
