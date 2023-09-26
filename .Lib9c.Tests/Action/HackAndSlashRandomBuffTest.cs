namespace Lib9c.Tests.Action
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Bencodex.Types;
    using Libplanet.Action;
    using Libplanet.Action.State;
    using Libplanet.Crypto;
    using Libplanet.Types.Assets;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.Action.Extensions;
    using Nekoyume.Helper;
    using Nekoyume.Model;
    using Nekoyume.Model.State;
    using Nekoyume.Module;
    using Nekoyume.TableData;
    using Nekoyume.TableData.Crystal;
    using Xunit;
    using static Lib9c.SerializeKeys;

    public class HackAndSlashRandomBuffTest
    {
        private readonly Dictionary<string, string> _sheets;
        private readonly TableSheets _tableSheets;

        private readonly Address _agentAddress;

        private readonly Address _avatarAddress;
        private readonly AvatarState _avatarState;

        private readonly Address _inventoryAddress;
        private readonly Address _worldInformationAddress;
        private readonly Address _questListAddress;

        private readonly Address _rankingMapAddress;

        private readonly WeeklyArenaState _weeklyArenaState;
        private readonly IWorld _initialWorld;
        private readonly IRandom _random;
        private readonly Currency _currency;

        public HackAndSlashRandomBuffTest()
        {
            _random = new TestRandom();
            _sheets = TableSheetsImporter.ImportSheets();
            _tableSheets = new TableSheets(_sheets);

            var privateKey = new PrivateKey();
            _agentAddress = privateKey.PublicKey.ToAddress();
            var agentState = new AgentState(_agentAddress);

            _avatarAddress = _agentAddress.Derive("avatar");
            var gameConfigState = new GameConfigState(_sheets[nameof(GameConfigSheet)]);
            _rankingMapAddress = _avatarAddress.Derive("ranking_map");
            _currency = CrystalCalculator.CRYSTAL;
            _avatarState = new AvatarState(
                _avatarAddress,
                _agentAddress,
                0,
                _tableSheets.GetAvatarSheets(),
                gameConfigState,
                _rankingMapAddress
            )
            {
                level = 100,
            };
            _inventoryAddress = _avatarAddress.Derive(LegacyInventoryKey);
            _worldInformationAddress = _avatarAddress.Derive(LegacyWorldInformationKey);
            _questListAddress = _avatarAddress.Derive(LegacyQuestListKey);
            agentState.avatarAddresses.Add(0, _avatarAddress);

            _weeklyArenaState = new WeeklyArenaState(0);

            _initialWorld = LegacyModule.SetState(
                new MockWorld(),
                _weeklyArenaState.address,
                _weeklyArenaState.Serialize());
            _initialWorld = AgentModule.SetAgentStateV2(_initialWorld, _agentAddress, agentState);
            _initialWorld = AvatarModule.SetAvatarStateV2(
                _initialWorld,
                _avatarAddress,
                _avatarState);
            _initialWorld = LegacyModule.SetState(
                _initialWorld,
                _inventoryAddress,
                _avatarState.inventory.Serialize());
            _initialWorld = LegacyModule.SetState(
                _initialWorld,
                _worldInformationAddress,
                _avatarState.worldInformation.Serialize());
            _initialWorld = LegacyModule.SetState(
                _initialWorld,
                _questListAddress,
                _avatarState.questList.Serialize());
            _initialWorld = LegacyModule.SetState(
                _initialWorld,
                gameConfigState.address,
                gameConfigState.Serialize());

            foreach (var (key, value) in _sheets)
            {
                _initialWorld = LegacyModule.SetState(_initialWorld, Addresses.TableSheet.Derive(key), value.Serialize());
            }

            foreach (var address in _avatarState.combinationSlotAddresses)
            {
                var slotState = new CombinationSlotState(
                    address,
                    GameConfig.RequireClearedStageLevel.CombinationEquipmentAction);
                _initialWorld = LegacyModule.SetState(_initialWorld, address, slotState.Serialize());
            }
        }

        [Theory]
        [InlineData(10, false, 10_000, 10_000, null)]
        [InlineData(20, true, 10_000, 10_000, null)]
        [InlineData(20, true, 10_000, 0, typeof(NotEnoughStarException))]
        [InlineData(20, false, 1, 10_000, typeof(NotEnoughFungibleAssetValueException))]
        public void Execute(int stageId, bool advancedGacha, int balance, int gatheredStar, Type excType)
        {
            var context = new ActionContext();
            var states = LegacyModule.MintAsset(_initialWorld, context, _agentAddress, balance * _currency);
            var gameConfigState = LegacyModule.GetGameConfigState(_initialWorld);
            var avatarState = new AvatarState(
                _avatarAddress,
                _agentAddress,
                0,
                LegacyModule.GetAvatarSheets(_initialWorld),
                gameConfigState,
                _rankingMapAddress)
            {
                worldInformation =
                    new WorldInformation(0, LegacyModule.GetSheet<WorldSheet>(_initialWorld), stageId),
                level = 400,
            };
            var gachaStateAddress = Addresses.GetSkillStateAddressFromAvatarAddress(_avatarAddress);
            var gachaState = new CrystalRandomSkillState(gachaStateAddress, stageId);
            states = AvatarModule.SetAvatarStateV2(states, _avatarAddress, avatarState);
            states = LegacyModule.SetState(
                states,
                _avatarAddress.Derive(LegacyInventoryKey),
                avatarState.inventory.Serialize());
            states = LegacyModule.SetState(
                states,
                _avatarAddress.Derive(LegacyWorldInformationKey),
                avatarState.worldInformation.Serialize());
            states = LegacyModule.SetState(
                states,
                _avatarAddress.Derive(LegacyQuestListKey),
                avatarState.questList.Serialize());
            var crystalStageSheet = _tableSheets.CrystalStageBuffGachaSheet;
            gachaState.Update(gatheredStar, crystalStageSheet);
            states = LegacyModule.SetState(states, gachaStateAddress, gachaState.Serialize());
            var cost =
                CrystalCalculator.CalculateBuffGachaCost(stageId, advancedGacha, crystalStageSheet);

            var action = new HackAndSlashRandomBuff
            {
                AvatarAddress = _avatarAddress,
                AdvancedGacha = advancedGacha,
            };

            if (excType is null)
            {
                var nextState = action.Execute(new ActionContext
                {
                    PreviousState = states,
                    Signer = _agentAddress,
                    Random = _random,
                }).GetAccount(ReservedAddresses.LegacyAccount);

                Assert.Equal(
                    nextState.GetBalance(_agentAddress, CrystalCalculator.CRYSTAL),
                    LegacyModule.GetBalance(states, _agentAddress, CrystalCalculator.CRYSTAL) - cost);
            }
            else
            {
                Assert.Throws(excType, () =>
                {
                    action.Execute(new ActionContext
                    {
                        PreviousState = states,
                        Signer = _agentAddress,
                        Random = _random,
                    });
                });
            }
        }

        [Theory]
        [InlineData(false, CrystalRandomBuffSheet.Row.BuffRank.A)]
        [InlineData(true, CrystalRandomBuffSheet.Row.BuffRank.S)]
        public void ContainMinimumBuffRank(bool advancedGacha, CrystalRandomBuffSheet.Row.BuffRank minimumRank)
        {
            var context = new ActionContext();
            var states = LegacyModule.MintAsset(
                _initialWorld,
                context,
                _agentAddress,
                100_000_000 * _currency);
            var gameConfigState = LegacyModule.GetGameConfigState(_initialWorld);
            var avatarState = new AvatarState(
                _avatarAddress,
                _agentAddress,
                0,
                LegacyModule.GetAvatarSheets(_initialWorld),
                gameConfigState,
                _rankingMapAddress)
            {
                worldInformation =
                    new WorldInformation(0, LegacyModule.GetSheet<WorldSheet>(_initialWorld), 1),
                level = 400,
            };
            var gachaStateAddress = Addresses.GetSkillStateAddressFromAvatarAddress(_avatarAddress);
            var gachaState = new CrystalRandomSkillState(gachaStateAddress, 1);
            states = AvatarModule.SetAvatarStateV2(states, _avatarAddress, avatarState);
            states = LegacyModule.SetState(
                states,
                _avatarAddress.Derive(LegacyInventoryKey),
                avatarState.inventory.Serialize());
            states = LegacyModule.SetState(
                states,
                _avatarAddress.Derive(LegacyWorldInformationKey),
                avatarState.worldInformation.Serialize());
            states = LegacyModule.SetState(
                states,
                _avatarAddress.Derive(LegacyQuestListKey),
                avatarState.questList.Serialize());
            var crystalStageSheet = _tableSheets.CrystalStageBuffGachaSheet;
            var randomBuffSheet = _tableSheets.CrystalRandomBuffSheet;
            gachaState.Update(100_000_000, crystalStageSheet);
            states = LegacyModule.SetState(states, gachaStateAddress, gachaState.Serialize());
            var checkCount = 100;
            while (checkCount-- > 0)
            {
                var action = new HackAndSlashRandomBuff
                {
                    AvatarAddress = _avatarAddress,
                    AdvancedGacha = advancedGacha,
                };
                var nextState = action.Execute(new ActionContext
                {
                    PreviousState = states,
                    Signer = _agentAddress,
                    Random = _random,
                }).GetAccount(ReservedAddresses.LegacyAccount);
                var newGachaState = new CrystalRandomSkillState(
                    gachaStateAddress,
                    (List)nextState.GetState(gachaStateAddress));
                Assert.Contains(
                    newGachaState.SkillIds.Select(id => randomBuffSheet[id].Rank),
                    rank => rank <= minimumRank);
            }
        }
    }
}
