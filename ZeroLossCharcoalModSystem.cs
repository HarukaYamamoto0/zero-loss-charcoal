using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ZeroLossCharcoal;

[HarmonyPatch]
// ReSharper disable once UnusedType.Global
public class ZeroLossCharcoalSystem : ModSystem
{
    private Harmony? _harmony;

    private static ZeroLossCharcoalConfig _config = new ZeroLossCharcoalConfig();

    private const string ModLogPrefix = "[ZeroLossCharcoal]";

    public override bool ShouldLoad(EnumAppSide side)
    {
        return side == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        var loaded = api.LoadModConfig<ZeroLossCharcoalConfig>("ZeroLossCharcoalConfig.json");
        _config = loaded ?? new ZeroLossCharcoalConfig();
        api.StoreModConfig(_config, "ZeroLossCharcoalConfig.json");

        _harmony = new Harmony(Mod.Info.ModID);
        _harmony.PatchAll(typeof(ZeroLossCharcoalSystem).Assembly);

        api.Logger.Event(
            $"{ModLogPrefix} Loaded. MaxPileHeight={_config.MaxPileHeight}");
    }

    public override void Dispose()
    {
        _harmony?.UnpatchAll(Mod.Info.ModID);
    }

    /// <summary>
    /// After the pit converts, promote all connected charcoal piles
    /// to the configured maximum height to avoid random yield loss.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BlockEntityCharcoalPit), "ConvertPit")]
    // ReSharper disable once InconsistentNaming
    // ReSharper disable once UnusedMember.Local
    private static void AfterConvertPit(BlockEntityCharcoalPit __instance)
    {
        if (__instance.Api is not ICoreServerAPI api)
        {
            return;
        }

        var ba = api.World.BlockAccessor;
        var pitPos = __instance.Pos;

        // Clear the center block if it is still a charcoalpile*
        var centerBlock = ba.GetBlock(pitPos);
        if (IsCharcoalPile(centerBlock))
        {
            ba.SetBlock(0, pitPos); // air
        }

        // Build a map charcoalpile-1..MaxPileHeight -> BlockId
        var pileIds = BuildCharcoalPileIdMap(api, _config.MaxPileHeight);
        if (pileIds.Count == 0)
        {
            api.Logger.Warning($"{ModLogPrefix} No charcoalpile-* variants found. Skipping promotion.");
            return;
        }

        var targetHeight = _config.MaxPileHeight;
        if (!pileIds.TryGetValue(targetHeight, out var fullId))
        {
            // Fallback: use the highest available height
            targetHeight = 0;
            fullId = 0;

            foreach (var (height, blockId) in pileIds)
            {
                if (height <= targetHeight) continue;
                targetHeight = height;
                fullId = blockId;
            }

            if (targetHeight == 0 || fullId == 0)
            {
                api.Logger.Warning(
                    $"{ModLogPrefix} Failed to determine target charcoalpile height. Skipping promotion.");
                return;
            }

            api.Logger.Warning(
                $"{ModLogPrefix} Configured MaxPileHeight={_config.MaxPileHeight} not found. Using {targetHeight} as fallback.");
        }

        // Flood-fill all connected charcoalpile-* blocks around the pit
        var changed = PromoteConnectedCharcoalPiles(api, pitPos, fullId);

        if (changed > 0)
        {
            api.Logger.Debug(
                $"{ModLogPrefix} Pit at {pitPos} promoted to charcoalpile-{targetHeight}. Modified blocks: {changed}");
        }
    }

    private static bool IsCharcoalPile(Block? block)
    {
        return block?.Code != null &&
               block.Code.Path != null &&
               block.Code.Path.StartsWith("charcoalpile-", StringComparison.Ordinal);
    }

    // ReSharper disable once GrammarMistakeInComment
    /// <summary>
    /// Build a map of charcoalpile-1..MaxPileHeight -> BlockId available in the world.
    /// </summary>
    private static Dictionary<int, int> BuildCharcoalPileIdMap(ICoreServerAPI api, int maxHeight)
    {
        var map = new Dictionary<int, int>();

        // Small safety limit to avoid misconfiguration like 999
        var safeMax = Math.Clamp(maxHeight, 1, 16);
        for (var h = 1; h <= safeMax; h++)
        {
            // AssetLocation("charcoalpile-1") implies domain "game"
            var code = new AssetLocation("charcoalpile-" + h);
            var block = api.World.GetBlock(code);
            if (block != null && block.BlockId != 0)
            {
                map[h] = block.BlockId;
            }
        }

        return map;
    }

    /// <summary>
    /// Flood-fills all connected charcoalpile-* blocks starting from the pit
    /// and promotes them to the given fullId.
    /// </summary>
    private static int PromoteConnectedCharcoalPiles(ICoreServerAPI api, BlockPos pitPos, int fullId)
    {
        var ba = api.World.BlockAccessor;

        var visited = new HashSet<Vec3i>();
        var queue = new Queue<BlockPos>();
        var changed = 0;

        // Seed from all 6 neighbors around the pit position
        foreach (var face in BlockFacing.ALLFACES)
        {
            var startPos = pitPos.AddCopy(face);
            var startBlock = ba.GetBlock(startPos);
            if (!IsCharcoalPile(startBlock))
            {
                continue;
            }

            var key = new Vec3i(startPos.X, startPos.Y, startPos.Z);
            if (!visited.Add(key))
            {
                continue;
            }

            queue.Enqueue(startPos);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var block = ba.GetBlock(current);

            if (!IsCharcoalPile(block))
            {
                continue;
            }

            // Only touch vanilla charcoal pile blocks (domain "game")
            if (block.BlockId != fullId && block.Code.Domain == "game")
            {
                ba.SetBlock(fullId, current);
                changed++;
            }

            // Traverse neighbors
            foreach (var face in BlockFacing.ALLFACES)
            {
                var next = current.AddCopy(face);
                var key = new Vec3i(next.X, next.Y, next.Z);

                if (!visited.Add(key))
                {
                    continue;
                }

                var nextBlock = ba.GetBlock(next);
                if (IsCharcoalPile(nextBlock))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return changed;
    }
}

/// <summary>
/// Simple configuration for Zero Loss Charcoal.
/// </summary>
public class ZeroLossCharcoalConfig
{
    /// <summary>
    /// Target pile height (charcoalpile-N). Vanilla uses up to 8.
    /// </summary>
    public int MaxPileHeight { get; set; } = 8;
}