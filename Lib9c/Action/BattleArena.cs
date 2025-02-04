using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bencodex.Types;
using Lib9c.Abstractions;
using Libplanet.Action;
using Libplanet.Action.State;
using Libplanet.Crypto;
using Nekoyume.Arena;
using Nekoyume.Battle;
using Nekoyume.Extensions;
using Nekoyume.Helper;
using Nekoyume.Model;
using Nekoyume.Model.Arena;
using Nekoyume.Model.BattleStatus.Arena;
using Nekoyume.Model.EnumType;
using Nekoyume.Model.Item;
using Nekoyume.Model.Stat;
using Nekoyume.Model.State;
using Nekoyume.Module;
using Nekoyume.TableData;
using Serilog;
using static Lib9c.SerializeKeys;

namespace Nekoyume.Action
{
    /// <summary>
    /// Introduce at https://github.com/planetarium/lib9c/pull/2229
    /// Changed at https://github.com/planetarium/lib9c/pull/2242
    /// </summary>
    [Serializable]
    [ActionType("battle_arena15")]
    public class BattleArena : GameAction, IBattleArenaV1
    {
        public const string PurchasedCountKey = "purchased_count_during_interval";
        public const int HpIncreasingModifier = 5;
        public Address myAvatarAddress;
        public Address enemyAvatarAddress;
        public int championshipId;
        public int round;
        public int ticket;

        public List<Guid> costumes;
        public List<Guid> equipments;
        public List<RuneSlotInfo> runeInfos;

        Address IBattleArenaV1.MyAvatarAddress => myAvatarAddress;

        Address IBattleArenaV1.EnemyAvatarAddress => enemyAvatarAddress;

        int IBattleArenaV1.ChampionshipId => championshipId;

        int IBattleArenaV1.Round => round;

        int IBattleArenaV1.Ticket => ticket;

        IEnumerable<Guid> IBattleArenaV1.Costumes => costumes;

        IEnumerable<Guid> IBattleArenaV1.Equipments => equipments;

        IEnumerable<IValue> IBattleArenaV1.RuneSlotInfos => runeInfos
            .Select(x => x.Serialize());

        protected override IImmutableDictionary<string, IValue> PlainValueInternal =>
            new Dictionary<string, IValue>()
            {
                [MyAvatarAddressKey] = myAvatarAddress.Serialize(),
                [EnemyAvatarAddressKey] = enemyAvatarAddress.Serialize(),
                [ChampionshipIdKey] = championshipId.Serialize(),
                [RoundKey] = round.Serialize(),
                [TicketKey] = ticket.Serialize(),
                [CostumesKey] = new List(costumes
                    .OrderBy(element => element).Select(e => e.Serialize())),
                [EquipmentsKey] = new List(equipments
                    .OrderBy(element => element).Select(e => e.Serialize())),
                [RuneInfos] = runeInfos.OrderBy(x => x.SlotIndex).Select(x=> x.Serialize()).Serialize(),
            }.ToImmutableDictionary();

        protected override void LoadPlainValueInternal(
            IImmutableDictionary<string, IValue> plainValue)
        {
            myAvatarAddress = plainValue[MyAvatarAddressKey].ToAddress();
            enemyAvatarAddress = plainValue[EnemyAvatarAddressKey].ToAddress();
            championshipId = plainValue[ChampionshipIdKey].ToInteger();
            round = plainValue[RoundKey].ToInteger();
            ticket = plainValue[TicketKey].ToInteger();
            costumes = ((List) plainValue[CostumesKey]).Select(e => e.ToGuid()).ToList();
            equipments = ((List) plainValue[EquipmentsKey]).Select(e => e.ToGuid()).ToList();
            runeInfos = plainValue[RuneInfos].ToList(x => new RuneSlotInfo((List) x));
            ValidateTicket();
        }

        public override IWorld Execute(IActionContext context)
        {
            context.UseGas(1);
            ValidateTicket();
            var states = context.PreviousState;
            var addressesHex = GetSignerAndOtherAddressesHex(
                context,
                myAvatarAddress,
                enemyAvatarAddress);

            var started = DateTimeOffset.UtcNow;
            Log.Debug("{AddressesHex}BattleArena exec started", addressesHex);
            if (myAvatarAddress.Equals(enemyAvatarAddress))
            {
                throw new InvalidAddressException(
                    $"{addressesHex}Aborted as the signer tried to battle for themselves.");
            }

            if (!states.TryGetAvatarState(
                    context.Signer,
                    myAvatarAddress,
                    out var avatarState))
            {
                throw new FailedLoadStateException(
                    $"{addressesHex}Aborted as the avatar state of the signer was failed to load.");
            }

            var collectionStates =
                states.GetCollectionStates(new[]{ myAvatarAddress, enemyAvatarAddress });
            var collectionExist = collectionStates.Count > 0;
            var sheetTypes = new List<Type>
            {
                typeof(ArenaSheet),
                typeof(ItemRequirementSheet),
                typeof(EquipmentItemRecipeSheet),
                typeof(EquipmentItemSubRecipeSheetV2),
                typeof(EquipmentItemOptionSheet),
                typeof(MaterialItemSheet),
                typeof(RuneListSheet),
            };
            if (collectionExist)
            {
                sheetTypes.Add(typeof(CollectionSheet));
            }
            var sheets = states.GetSheets(
                containArenaSimulatorSheets: true,
                sheetTypes: sheetTypes);

            var gameConfigState = states.GetGameConfigState();
            avatarState.ValidEquipmentAndCostumeV2(costumes, equipments,
                sheets.GetSheet<ItemRequirementSheet>(),
                sheets.GetSheet<EquipmentItemRecipeSheet>(),
                sheets.GetSheet<EquipmentItemSubRecipeSheetV2>(),
                sheets.GetSheet<EquipmentItemOptionSheet>(),
                context.BlockIndex, addressesHex, gameConfigState);

            // update rune slot
            var runeSlotStateAddress = RuneSlotState.DeriveAddress(myAvatarAddress, BattleType.Arena);
            var runeSlotState = states.TryGetLegacyState(runeSlotStateAddress, out List rawRuneSlotState)
                ? new RuneSlotState(rawRuneSlotState)
                : new RuneSlotState(BattleType.Arena);
            var runeListSheet = sheets.GetSheet<RuneListSheet>();
            runeSlotState.UpdateSlot(runeInfos, runeListSheet);
            states = states.SetLegacyState(runeSlotStateAddress, runeSlotState.Serialize());

            // update item slot
            var itemSlotStateAddress = ItemSlotState.DeriveAddress(myAvatarAddress, BattleType.Arena);
            var itemSlotState = states.TryGetLegacyState(itemSlotStateAddress, out List rawItemSlotState)
                ? new ItemSlotState(rawItemSlotState)
                : new ItemSlotState(BattleType.Arena);
            itemSlotState.UpdateEquipment(equipments);
            itemSlotState.UpdateCostumes(costumes);
            states = states.SetLegacyState(itemSlotStateAddress, itemSlotState.Serialize());

            var arenaSheet = sheets.GetSheet<ArenaSheet>();
            if (!arenaSheet.TryGetValue(championshipId, out var arenaRow))
            {
                throw new SheetRowNotFoundException(nameof(ArenaSheet),
                    $"championship Id : {championshipId}");
            }

            if (!arenaRow.TryGetRound(round, out var roundData))
            {
                throw new RoundNotFoundException(
                    $"[{nameof(BattleArena)}] ChampionshipId({arenaRow.ChampionshipId}) - " +
                    $"round({round})");
            }

            if (!roundData.IsTheRoundOpened(context.BlockIndex))
            {
                throw new ThisArenaIsClosedException(
                    $"{nameof(BattleArena)} : block index({context.BlockIndex}) - " +
                    $"championshipId({roundData.ChampionshipId}) - round({roundData.Round})");
            }

            var arenaParticipantsAdr =
                ArenaParticipants.DeriveAddress(roundData.ChampionshipId, roundData.Round);
            if (!states.TryGetArenaParticipants(arenaParticipantsAdr, out var arenaParticipants))
            {
                throw new ArenaParticipantsNotFoundException(
                    $"[{nameof(BattleArena)}] ChampionshipId({roundData.ChampionshipId}) - " +
                    $"round({roundData.Round})");
            }

            if (!arenaParticipants.AvatarAddresses.Contains(myAvatarAddress))
            {
                throw new AddressNotFoundInArenaParticipantsException(
                    $"[{nameof(BattleArena)}] my avatar address : {myAvatarAddress}");
            }

            if (!arenaParticipants.AvatarAddresses.Contains(enemyAvatarAddress))
            {
                throw new AddressNotFoundInArenaParticipantsException(
                    $"[{nameof(BattleArena)}] enemy avatar address : {enemyAvatarAddress}");
            }

            var myArenaAvatarStateAdr = ArenaAvatarState.DeriveAddress(myAvatarAddress);
            if (!states.TryGetArenaAvatarState(myArenaAvatarStateAdr, out var myArenaAvatarState))
            {
                throw new ArenaAvatarStateNotFoundException(
                    $"[{nameof(BattleArena)}] my avatar address : {myAvatarAddress}");
            }

            var battleArenaInterval = roundData.ArenaType == ArenaType.OffSeason
                ? 1
                : gameConfigState.BattleArenaInterval;
            if (context.BlockIndex - myArenaAvatarState.LastBattleBlockIndex < battleArenaInterval)
            {
                throw new CoolDownBlockException(
                    $"[{nameof(BattleArena)}] LastBattleBlockIndex : " +
                    $"{myArenaAvatarState.LastBattleBlockIndex} " +
                    $"CurrentBlockIndex : {context.BlockIndex}");
            }

            var enemyArenaAvatarStateAdr = ArenaAvatarState.DeriveAddress(enemyAvatarAddress);
            if (!states.TryGetArenaAvatarState(
                    enemyArenaAvatarStateAdr,
                    out var enemyArenaAvatarState))
            {
                throw new ArenaAvatarStateNotFoundException(
                    $"[{nameof(BattleArena)}] enemy avatar address : {enemyAvatarAddress}");
            }

            var myArenaScoreAdr = ArenaScore.DeriveAddress(
                myAvatarAddress,
                roundData.ChampionshipId,
                roundData.Round);
            if (!states.TryGetArenaScore(myArenaScoreAdr, out var myArenaScore))
            {
                throw new ArenaScoreNotFoundException(
                    $"[{nameof(BattleArena)}] my avatar address : {myAvatarAddress}" +
                    $" - ChampionshipId({roundData.ChampionshipId}) - round({roundData.Round})");
            }

            var enemyArenaScoreAdr = ArenaScore.DeriveAddress(
                enemyAvatarAddress,
                roundData.ChampionshipId,
                roundData.Round);
            if (!states.TryGetArenaScore(enemyArenaScoreAdr, out var enemyArenaScore))
            {
                throw new ArenaScoreNotFoundException(
                    $"[{nameof(BattleArena)}] enemy avatar address : {enemyAvatarAddress}" +
                    $" - ChampionshipId({roundData.ChampionshipId}) - round({roundData.Round})");
            }

            var arenaInformationAdr = ArenaInformation.DeriveAddress(
                myAvatarAddress,
                roundData.ChampionshipId,
                roundData.Round);
            if (!states.TryGetArenaInformation(arenaInformationAdr, out var arenaInformation))
            {
                throw new ArenaInformationNotFoundException(
                    $"[{nameof(BattleArena)}] my avatar address : {myAvatarAddress}" +
                    $" - ChampionshipId({roundData.ChampionshipId}) - round({roundData.Round})");
            }

            if (!ArenaHelper.ValidateScoreDifference(
                    ArenaHelper.ScoreLimits,
                    roundData.ArenaType,
                    myArenaScore.Score,
                    enemyArenaScore.Score))
            {
                var scoreDiff = enemyArenaScore.Score - myArenaScore.Score;
                throw new ValidateScoreDifferenceException(
                    $"[{nameof(BattleArena)}] Arena Type({roundData.ArenaType}) : " +
                    $"enemyScore({enemyArenaScore.Score}) - myScore({myArenaScore.Score}) = " +
                    $"diff({scoreDiff})");
            }

            var dailyArenaInterval = gameConfigState.DailyArenaInterval;
            var currentTicketResetCount = ArenaHelper.GetCurrentTicketResetCount(
                context.BlockIndex, roundData.StartBlockIndex, dailyArenaInterval);
            var purchasedCountAddr = arenaInformation.Address.Derive(PurchasedCountKey);
            if (!states.TryGetLegacyState(purchasedCountAddr, out Integer purchasedCountDuringInterval))
            {
                purchasedCountDuringInterval = 0;
            }

            if (arenaInformation.TicketResetCount < currentTicketResetCount)
            {
                arenaInformation.ResetTicket(currentTicketResetCount);
                purchasedCountDuringInterval = 0;
                states = states.SetLegacyState(purchasedCountAddr, purchasedCountDuringInterval);
            }

            if (roundData.ArenaType != ArenaType.OffSeason && ticket > 1)
            {
                throw new ExceedPlayCountException($"[{nameof(BattleArena)}] " +
                                                   $"ticket : {ticket} / arenaType : " +
                                                   $"{roundData.ArenaType}");
            }

            if (arenaInformation.Ticket > 0)
            {
                arenaInformation.UseTicket(ticket);
            }
            else if (ticket > 1)
            {
                throw new TicketPurchaseLimitExceedException(
                    $"[{nameof(ArenaInformation)}] tickets to buy : {ticket}");
            }
            else
            {
                var arenaAdr =
                    ArenaHelper.DeriveArenaAddress(roundData.ChampionshipId, roundData.Round);
                var goldCurrency = states.GetGoldCurrency();
                var ticketBalance =
                    ArenaHelper.GetTicketPrice(roundData, arenaInformation, goldCurrency);
                arenaInformation.BuyTicket(roundData.MaxPurchaseCount);
                if (purchasedCountDuringInterval >= roundData.MaxPurchaseCountWithInterval)
                {
                    throw new ExceedTicketPurchaseLimitDuringIntervalException(
                        $"[{nameof(ArenaInformation)}] PurchasedTicketCount({purchasedCountDuringInterval}) >= MAX({{max}})");
                }

                purchasedCountDuringInterval++;
                states = states
                    .TransferAsset(context, context.Signer, arenaAdr, ticketBalance)
                    .SetLegacyState(purchasedCountAddr, purchasedCountDuringInterval);
            }

            // update arena avatar state
            myArenaAvatarState.UpdateEquipment(equipments);
            myArenaAvatarState.UpdateCostumes(costumes);
            myArenaAvatarState.LastBattleBlockIndex = context.BlockIndex;
            var runeStates = new List<RuneState>();
            foreach (var address in runeInfos.Select(info => RuneState.DeriveAddress(myAvatarAddress, info.RuneId)))
            {
                if (states.TryGetLegacyState(address, out List rawRuneState))
                {
                    runeStates.Add(new RuneState(rawRuneState));
                }
            }

            // get enemy equipped items
            var enemyItemSlotStateAddress = ItemSlotState.DeriveAddress(enemyAvatarAddress, BattleType.Arena);
            var enemyItemSlotState = states.TryGetLegacyState(enemyItemSlotStateAddress, out List rawEnemyItemSlotState)
                ? new ItemSlotState(rawEnemyItemSlotState)
                : new ItemSlotState(BattleType.Arena);
            var enemyRuneSlotStateAddress = RuneSlotState.DeriveAddress(enemyAvatarAddress, BattleType.Arena);
            var enemyRuneSlotState = states.TryGetLegacyState(enemyRuneSlotStateAddress, out List enemyRawRuneSlotState)
                ? new RuneSlotState(enemyRawRuneSlotState)
                : new RuneSlotState(BattleType.Arena);

            var enemyRuneStates = new List<RuneState>();
            var enemyRuneSlotInfos = enemyRuneSlotState.GetEquippedRuneSlotInfos();
            foreach (var address in enemyRuneSlotInfos.Select(info => RuneState.DeriveAddress(enemyAvatarAddress, info.RuneId)))
            {
                if (states.TryGetLegacyState(address, out List rawRuneState))
                {
                    enemyRuneStates.Add(new RuneState(rawRuneState));
                }
            }

            // simulate
            var enemyAvatarState = states.GetEnemyAvatarState(enemyAvatarAddress);
            var myArenaPlayerDigest = new ArenaPlayerDigest(
                avatarState,
                equipments,
                costumes,
                runeStates);
            var enemyArenaPlayerDigest = new ArenaPlayerDigest(
                enemyAvatarState,
                enemyItemSlotState.Equipments,
                enemyItemSlotState.Costumes,
                enemyRuneStates);
            var previousMyScore = myArenaScore.Score;
            var arenaSheets = sheets.GetArenaSimulatorSheets();
            var winCount = 0;
            var defeatCount = 0;
            var rewards = new List<ItemBase>();
            var random = context.GetRandom();
            var modifiers = new Dictionary<Address, List<StatModifier>>
            {
                [myAvatarAddress] = new(),
                [enemyAvatarAddress] = new(),
            };
            if (collectionExist)
            {
                var collectionSheet = sheets.GetSheet<CollectionSheet>();
#pragma warning disable LAA1002
                foreach (var (address, state) in collectionStates)
#pragma warning restore LAA1002
                {
                    var modifier = modifiers[address];
                    foreach (var collectionId in state.Ids)
                    {
                        modifier.AddRange(collectionSheet[collectionId].StatModifiers);
                    }
                }
            }
            for (var i = 0; i < ticket; i++)
            {
                var simulator = new ArenaSimulator(random, HpIncreasingModifier);
                var log = simulator.Simulate(
                    myArenaPlayerDigest,
                    enemyArenaPlayerDigest,
                    arenaSheets,
                    modifiers[myAvatarAddress],
                    modifiers[enemyAvatarAddress],
                    true);
                if (log.Result.Equals(ArenaLog.ArenaResult.Win))
                {
                    winCount++;
                }
                else
                {
                    defeatCount++;
                }

                var reward = RewardSelector.Select(
                    random,
                    sheets.GetSheet<WeeklyArenaRewardSheet>(),
                    sheets.GetSheet<MaterialItemSheet>(),
                    myArenaPlayerDigest.Level,
                    maxCount: ArenaHelper.GetRewardCount(previousMyScore));
                rewards.AddRange(reward);
            }

            // add reward
            foreach (var itemBase in rewards.OrderBy(x => x.Id))
            {
                avatarState.inventory.AddItem(itemBase);
            }

            // add medal
            if (roundData.ArenaType != ArenaType.OffSeason && winCount > 0)
            {
                var materialSheet = sheets.GetSheet<MaterialItemSheet>();
                var medal = ArenaHelper.GetMedal(
                    roundData.ChampionshipId,
                    roundData.Round,
                    materialSheet);
                avatarState.inventory.AddItem(medal, count: winCount);
            }

            // update record
            var (myWinScore, myDefeatScore, enemyWinScore) =
                ArenaHelper.GetScores(previousMyScore, enemyArenaScore.Score);
            var myScore = (myWinScore * winCount) + (myDefeatScore * defeatCount);
            myArenaScore.AddScore(myScore);
            enemyArenaScore.AddScore(enemyWinScore * winCount);
            arenaInformation.UpdateRecord(winCount, defeatCount);

            var ended = DateTimeOffset.UtcNow;
            Log.Debug("{AddressesHex}BattleArena Total Executed Time: {Elapsed}", addressesHex, ended - started);
            return states
                .SetLegacyState(myArenaAvatarStateAdr, myArenaAvatarState.Serialize())
                .SetLegacyState(myArenaScoreAdr, myArenaScore.Serialize())
                .SetLegacyState(enemyArenaScoreAdr, enemyArenaScore.Serialize())
                .SetLegacyState(arenaInformationAdr, arenaInformation.Serialize())
                .SetAvatarState(myAvatarAddress, avatarState);
        }

        private void ValidateTicket()
        {
            if (ticket <= 0)
            {
                throw new ArgumentException("ticket must be greater than 0");
            }
        }
    }
}
