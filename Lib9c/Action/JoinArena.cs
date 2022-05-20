using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Nekoyume.Arena;
using Nekoyume.Extensions;
using Nekoyume.Helper;
using Nekoyume.Model.Arena;
using Nekoyume.Model.EnumType;
using Nekoyume.Model.State;
using Nekoyume.TableData;

namespace Nekoyume.Action
{
    /// <summary>
    /// Introduced at https://github.com/planetarium/lib9c/pull/
    /// </summary>
    [Serializable]
    [ActionType("join_arena")]
    public class JoinArena : GameAction
    {
        public Address avatarAddress;
        public int championshipId;
        public int round;
        public List<Guid> costumes;
        public List<Guid> equipments;

        protected override IImmutableDictionary<string, IValue> PlainValueInternal =>
            new Dictionary<string, IValue>()
            {
                ["avatarAddress"] = avatarAddress.Serialize(),
                ["championshipId"] = championshipId.Serialize(),
                ["round"] = round.Serialize(),
                ["costumes"] = new List(costumes
                    .OrderBy(element => element).Select(e => e.Serialize())),
                ["equipments"] = new List(equipments
                    .OrderBy(element => element).Select(e => e.Serialize())),
            }.ToImmutableDictionary();

        protected override void LoadPlainValueInternal(
            IImmutableDictionary<string, IValue> plainValue)
        {
            avatarAddress = plainValue["avatarAddress"].ToAddress();
            championshipId = plainValue["championshipId"].ToInteger();
            round = plainValue["round"].ToInteger();
            costumes = ((List)plainValue["costumes"]).Select(e => e.ToGuid()).ToList();
            equipments = ((List)plainValue["equipments"]).Select(e => e.ToGuid()).ToList();
        }

        public override IAccountStateDelta Execute(IActionContext context)
        {
            var states = context.PreviousStates;
            if (context.Rehearsal)
            {
                return states;
            }

            var addressesHex = GetSignerAndOtherAddressesHex(context, avatarAddress);

            if (!states.TryGetAgentAvatarStatesV2(context.Signer, avatarAddress,
                    out var agentState, out var avatarState, out _))
            {
                throw new FailedLoadStateException(
                    $"[{nameof(JoinArena)}] Aborted as the avatar state of the signer failed to load.");
            }

            var sheets = states.GetSheets(
                sheetTypes: new[]
                {
                    typeof(ItemRequirementSheet),
                    typeof(EquipmentItemRecipeSheet),
                    typeof(EquipmentItemSubRecipeSheetV2),
                    typeof(EquipmentItemOptionSheet),
                    typeof(ArenaSheet),
                });

            avatarState.ValidEquipmentAndCostume(costumes, equipments,
                sheets.GetSheet<ItemRequirementSheet>(),
                sheets.GetSheet<EquipmentItemRecipeSheet>(),
                sheets.GetSheet<EquipmentItemSubRecipeSheetV2>(),
                sheets.GetSheet<EquipmentItemOptionSheet>(),
                context.BlockIndex, addressesHex);

            var sheet = sheets.GetSheet<ArenaSheet>();
            if (!sheet.TryGetValue(championshipId, out var row))
            {
                throw new SheetRowNotFoundException(
                    nameof(ArenaSheet), $"championship Id : {championshipId}");
            }

            if (!row.TryGetRound(round, out var roundData))
            {
                throw new RoundNotFoundException(
                    $"[{nameof(JoinArena)}] ChampionshipId({row.Id}) - round({round})");
            }

            // check fee
            var costCrystal = ArenaHelper.GetEntranceFee(roundData, context.BlockIndex);
            if (costCrystal > 0 * CrystalCalculator.CRYSTAL)
            {
                var crystalBalance = states.GetBalance(context.Signer, CrystalCalculator.CRYSTAL);
                if (costCrystal > crystalBalance)
                {
                    throw new NotEnoughFungibleAssetValueException(
                        $"required {costCrystal}, but balance is {crystalBalance}");
                }

                var arenaAdr = ArenaHelper.DeriveArenaAddress(roundData.Id, roundData.Round);
                states = states.TransferAsset(context.Signer, arenaAdr, costCrystal);
            }

            // check medal
            if (roundData.ArenaType.Equals(ArenaType.Championship))
            {
                var medalCount = ArenaHelper.GetMedalTotalCount(row, avatarState);
                if (medalCount < roundData.RequiredMedalCount)
                {
                    throw new NotEnoughMedalException(
                        $"[{nameof(JoinArena)}] have({medalCount}) < Required Medal Count({roundData.RequiredMedalCount}) ");
                }
            }

            // create ArenaScore
            var arenaScoreAdr =
                ArenaScore.DeriveAddress(avatarAddress, roundData.Id, roundData.Round);
            if (states.TryGetState(arenaScoreAdr, out List _))
            {
                throw new ArenaScoreAlreadyContainsException(
                    $"[{nameof(JoinArena)}] id({roundData.Id}) / round({roundData.Round})");
            }

            var arenaScore = new ArenaScore(avatarAddress, roundData.Id, roundData.Round);

            // create ArenaInformation
            var arenaInformationAdr =
                ArenaInformation.DeriveAddress(avatarAddress, roundData.Id, roundData.Round);
            if (states.TryGetState(arenaInformationAdr, out List _))
            {
                throw new ArenaInformationAlreadyContainsException(
                    $"[{nameof(JoinArena)}] id({roundData.Id}) / round({roundData.Round})");
            }

            var arenaInformation =
                new ArenaInformation(avatarAddress, roundData.Id, roundData.Round);

            // update ArenaParticipants
            var arenaParticipantsAdr = ArenaParticipants.DeriveAddress(roundData.Id, roundData.Round);
            var arenaParticipants = states.GetArenaParticipants(arenaParticipantsAdr, roundData.Id, roundData.Round);
            arenaParticipants.Add(avatarAddress);

            // update ArenaAvatarState
            var arenaAvatarStateAdr = ArenaAvatarState.DeriveAddress(avatarAddress);
            var arenaAvatarState = states.GetArenaAvatarState(arenaAvatarStateAdr, avatarState);
            arenaAvatarState.UpdateCostumes(costumes);
            arenaAvatarState.UpdateEquipment(equipments);
            arenaAvatarState.UpdateLevel(avatarState.level);

            return states
                .SetState(arenaScoreAdr, arenaScore.Serialize())
                .SetState(arenaInformationAdr, arenaInformation.Serialize())
                .SetState(arenaParticipantsAdr, arenaParticipants.Serialize())
                .SetState(arenaAvatarStateAdr, arenaAvatarState.Serialize())
                .SetState(context.Signer, agentState.Serialize());
        }
    }
}
