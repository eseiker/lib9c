using System;
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
using Serilog;
using static Lib9c.SerializeKeys;

namespace Nekoyume.Action
{
    [ActionType("unlock_equipment_recipe")]
    [ActionObsolete(ActionObsoleteConfig.V200030ObsoleteIndex)]
    public class UnlockEquipmentRecipe1: GameAction, IUnlockEquipmentRecipeV1
    {
        public List<int> RecipeIds = new List<int>();
        public Address AvatarAddress;

        IEnumerable<int> IUnlockEquipmentRecipeV1.RecipeIds => RecipeIds;
        Address IUnlockEquipmentRecipeV1.AvatarAddress => AvatarAddress;

        public override IWorld Execute(IActionContext context)
        {
            context.UseGas(1);
            var states = context.PreviousState;
            var unlockedRecipeIdsAddress = AvatarAddress.Derive("recipe_ids");

            CheckObsolete(ActionObsoleteConfig.V200030ObsoleteIndex, context);
            var addressesHex = GetSignerAndOtherAddressesHex(context, AvatarAddress);
            var started = DateTimeOffset.UtcNow;
            Log.Debug("{AddressesHex}UnlockEquipmentRecipe exec started", addressesHex);
            if (!RecipeIds.Any() || RecipeIds.Any(i => i < 2))
            {
                throw new InvalidRecipeIdException();
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
                    states.SetAvatarState(AvatarAddress, avatarState);
                }
                else
                {
                    // Invalid Address.
                    throw new FailedLoadStateException($"Can't find AvatarState {AvatarAddress}");
                }
            }

            var equipmentRecipeSheet = states.GetSheet<EquipmentItemRecipeSheet>();

            var unlockedIds = UnlockedIds(states, unlockedRecipeIdsAddress, equipmentRecipeSheet, worldInformation, RecipeIds);

            FungibleAssetValue cost = CrystalCalculator.CalculateRecipeUnlockCost(RecipeIds, equipmentRecipeSheet);
            FungibleAssetValue balance = states.GetBalance(context.Signer, cost.Currency);

            if (balance < cost)
            {
                throw new NotEnoughFungibleAssetValueException($"required {cost}, but balance is {balance}");
            }

            states = states.SetLegacyState(unlockedRecipeIdsAddress,
                    unlockedIds.Aggregate(List.Empty,
                        (current, address) => current.Add(address.Serialize())));
            var ended = DateTimeOffset.UtcNow;
            Log.Debug("{AddressesHex}UnlockEquipmentRecipe Total Executed Time: {Elapsed}", addressesHex, ended - started);
            return states.TransferAsset(context, context.Signer, Addresses.UnlockEquipmentRecipe,  cost);
        }

        public static List<int> UnlockedIds(
            IWorld states,
            Address unlockedRecipeIdsAddress,
            EquipmentItemRecipeSheet equipmentRecipeSheet,
            WorldInformation worldInformation,
            List<int> recipeIds
        )
        {
            List<int> unlockedIds = states.TryGetLegacyState(unlockedRecipeIdsAddress, out List rawIds)
                ? rawIds.ToList(StateExtensions.ToInteger)
                : new List<int>
                {
                    1
                };

            // Sort recipe by ItemSubType & UnlockStage.
            // 999 is not opened recipe.
            var sortedRecipeRows = equipmentRecipeSheet.Values
                .Where(r => r.UnlockStage != 999)
                .OrderBy(r => r.ItemSubType)
                .ThenBy(r => r.UnlockStage)
                .ToList();

            var unlockRecipeRows = sortedRecipeRows
                .Where(r => recipeIds.Contains(r.Id))
                .ToList();

            foreach (var recipeRow in unlockRecipeRows)
            {
                var recipeId = recipeRow.Id;
                if (unlockedIds.Contains(recipeId))
                {
                    // Already Unlocked
                    throw new AlreadyRecipeUnlockedException(
                        $"recipe: {recipeId} already unlocked.");
                }

                if (!worldInformation.IsStageCleared(recipeRow.UnlockStage))
                {
                    throw new NotEnoughClearedStageLevelException(
                        $"clear {recipeRow.UnlockStage} first.");
                }

                var index = sortedRecipeRows.IndexOf(recipeRow);
                if (index > 0)
                {
                    var prevRow = sortedRecipeRows[index - 1];
                    if (prevRow.ItemSubType == recipeRow.ItemSubType && !unlockedIds.Contains(prevRow.Id))
                    {
                        // Can't skip previous recipe unlock.
                        throw new InvalidRecipeIdException($"unlock {prevRow.Id} first.");
                    }
                }

                unlockedIds.Add(recipeId);
            }

            return unlockedIds;
        }

        protected override IImmutableDictionary<string, IValue> PlainValueInternal =>
            new Dictionary<string, IValue>
            {
                ["r"] = new List(RecipeIds.Select(i => i.Serialize())),
                ["a"] = AvatarAddress.Serialize(),
            }.ToImmutableDictionary();
        protected override void LoadPlainValueInternal(IImmutableDictionary<string, IValue> plainValue)
        {
            RecipeIds = plainValue["r"].ToList(StateExtensions.ToInteger);
            AvatarAddress = plainValue["a"].ToAddress();
        }
    }
}
