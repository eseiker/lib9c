using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bencodex.Types;
using Lib9c.Abstractions;
using Libplanet.Action;
using Libplanet.Action.State;
using Libplanet.Crypto;
using Libplanet.Types.Assets;
using Nekoyume.Helper;
using Nekoyume.Model;
using Nekoyume.Model.State;
using Nekoyume.Module;
using Nekoyume.TableData;
using static Lib9c.SerializeKeys;

namespace Nekoyume.Action
{
    [ActionType("unlock_world")]
    [ActionObsolete(ActionObsoleteConfig.V200030ObsoleteIndex)]
    public class UnlockWorld1: GameAction, IUnlockWorldV1
    {
        public List<int> WorldIds;
        public Address AvatarAddress;

        IEnumerable<int> IUnlockWorldV1.WorldIds => WorldIds;
        Address IUnlockWorldV1.AvatarAddress => AvatarAddress;

        public override IWorld Execute(IActionContext context)
        {
            context.UseGas(1);
            var states = context.PreviousState;
            var unlockedWorldIdsAddress = AvatarAddress.Derive("world_ids");

            CheckObsolete(ActionObsoleteConfig.V200030ObsoleteIndex, context);
            if (!WorldIds.Any() || WorldIds.Any(i => i < 2 || i == GameConfig.MimisbrunnrWorldId))
            {
                throw new InvalidWorldException();
            }

            WorldInformation worldInformation;
            if (states.GetWorldInformation(AvatarAddress) is { } worldInfo)
            {
                worldInformation = worldInfo;
            }
            else
            {
                // AvatarState migration required.
                if (states.TryGetAvatarState(context.Signer, AvatarAddress, out AvatarState avatarState))
                {
                    worldInformation = avatarState.worldInformation;
                    states = states.SetAvatarState(AvatarAddress, avatarState);
                }
                else
                {
                    // Invalid Address.
                    throw new FailedLoadStateException($"Can't find AvatarState {AvatarAddress}");
                }
            }

            List<int> unlockedIds = states.TryGetLegacyState(unlockedWorldIdsAddress, out List rawIds)
                ? rawIds.ToList(StateExtensions.ToInteger)
                : new List<int>
                {
                    1,
                    GameConfig.MimisbrunnrWorldId,
                };

            var sortedWorldIds = WorldIds.OrderBy(i => i).ToList();
            var worldUnlockSheet = states.GetSheet<WorldUnlockSheet>();
            foreach (var worldId in sortedWorldIds)
            {
                // Already Unlocked.
                if (unlockedIds.Contains(worldId))
                {
                    throw new AlreadyWorldUnlockedException($"World {worldId} Already unlocked.");
                }

                WorldUnlockSheet.Row row =
                    worldUnlockSheet.OrderedList.First(r => r.WorldIdToUnlock == worldId);
                // Check Previous world unlocked.
                if (!worldInformation.IsWorldUnlocked(row.WorldId) || !unlockedIds.Contains(row.WorldId))
                {
                    throw new FailedToUnlockWorldException($"unlock ${row.WorldId} first.");
                }

                // Check stage cleared in HackAndSlash.
                if (!worldInformation.IsWorldUnlocked(worldId) && !worldInformation.IsStageCleared(row.StageId))
                {
                    throw new FailedToUnlockWorldException($"{worldId} is locked.");
                }

                unlockedIds.Add(worldId);
            }

            FungibleAssetValue cost =
                CrystalCalculator.CalculateWorldUnlockCost(sortedWorldIds, worldUnlockSheet);
            FungibleAssetValue balance = states.GetBalance(context.Signer, cost.Currency);

            // Insufficient CRYSTAL.
            if (balance < cost)
            {
                throw new NotEnoughFungibleAssetValueException($"UnlockWorld required {cost}, but balance is {balance}");
            }

            return states
                .SetLegacyState(unlockedWorldIdsAddress, new List(unlockedIds.Select(i => i.Serialize())))
                .TransferAsset(context, context.Signer, Addresses.UnlockWorld, cost);
        }

        protected override IImmutableDictionary<string, IValue> PlainValueInternal
            => new Dictionary<string, IValue>
            {
                ["w"] = new List(WorldIds.Select(i => i.Serialize())),
                ["a"] = AvatarAddress.Serialize(),
            }.ToImmutableDictionary();
        protected override void LoadPlainValueInternal(IImmutableDictionary<string, IValue> plainValue)
        {
            WorldIds = plainValue["w"].ToList(StateExtensions.ToInteger);
            AvatarAddress = plainValue["a"].ToAddress();
        }
    }
}
