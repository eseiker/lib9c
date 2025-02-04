namespace Lib9c.Tests.Action
{
    using System;
    using System.Linq;
    using Bencodex.Types;
    using Libplanet.Action.State;
    using Libplanet.Crypto;
    using Libplanet.Types.Assets;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.Helper;
    using Nekoyume.Model.Rune;
    using Nekoyume.Model.State;
    using Nekoyume.Module;
    using Nekoyume.TableData;
    using Xunit;

    public class RuneEnhancement0Test
    {
        private readonly Currency _goldCurrency;

        public RuneEnhancement0Test()
        {
            _goldCurrency = Currency.Legacy("NCG", 2, null);
        }

        [Theory]
        [InlineData(10000)]
        [InlineData(1)]
        public void Execute(int seed)
        {
            var agentAddress = new PrivateKey().Address;
            var avatarAddress = new PrivateKey().Address;
            var sheets = TableSheetsImporter.ImportSheets();
            var tableSheets = new TableSheets(sheets);
            var blockIndex = tableSheets.WorldBossListSheet.Values
                .OrderBy(x => x.StartedBlockIndex)
                .First()
                .StartedBlockIndex;

            var goldCurrencyState = new GoldCurrencyState(_goldCurrency);
            var context = new ActionContext();
            var state = new World(new MockWorldState())
                .SetLegacyState(goldCurrencyState.address, goldCurrencyState.Serialize())
                .SetAgentState(agentAddress, new AgentState(agentAddress));

            foreach (var (key, value) in sheets)
            {
                state = state.SetLegacyState(Addresses.TableSheet.Derive(key), value.Serialize());
            }

            var avatarState = new AvatarState(
                avatarAddress,
                agentAddress,
                0,
                tableSheets.GetAvatarSheets(),
                new GameConfigState(),
                default
            );

            var runeListSheet = state.GetSheet<RuneListSheet>();
            var runeId = runeListSheet.First().Value.Id;
            var runeStateAddress = RuneState.DeriveAddress(avatarState.address, runeId);
            var runeState = new RuneState(runeId);
            state = state.SetLegacyState(runeStateAddress, runeState.Serialize());

            var costSheet = state.GetSheet<RuneCostSheet>();
            if (!costSheet.TryGetValue(runeId, out var costRow))
            {
                throw new RuneCostNotFoundException($"[{nameof(Execute)}] ");
            }

            if (!costRow.TryGetCost(runeState.Level + 1, out var cost))
            {
                throw new RuneCostDataNotFoundException($"[{nameof(Execute)}] ");
            }

            var runeSheet = state.GetSheet<RuneSheet>();
            if (!runeSheet.TryGetValue(runeId, out var runeRow))
            {
                throw new RuneNotFoundException($"[{nameof(Execute)}] ");
            }

            var ncgCurrency = state.GetGoldCurrency();
            var crystalCurrency = CrystalCalculator.CRYSTAL;
            var runeCurrency = Currency.Legacy(runeRow.Ticker, 0, minters: null);

            var ncgBal = cost.NcgQuantity * ncgCurrency * 10000;
            var crystalBal = cost.CrystalQuantity * crystalCurrency * 10000;
            var runeBal = cost.RuneStoneQuantity * runeCurrency * 10000;

            var rand = new TestRandom(seed);
            if (!RuneHelper.TryEnhancement(ncgBal, crystalBal, runeBal, ncgCurrency, crystalCurrency, runeCurrency, cost, rand, 99, out var tryCount))
            {
                throw new RuneNotFoundException($"[{nameof(Execute)}] ");
            }

            if (ncgBal.Sign > 0)
            {
                state = state.MintAsset(context, agentAddress, ncgBal);
            }

            if (crystalBal.Sign > 0)
            {
                state = state.MintAsset(context, agentAddress, crystalBal);
            }

            if (runeBal.Sign > 0)
            {
                state = state.MintAsset(context, avatarState.address, runeBal);
            }

            var action = new RuneEnhancement0()
            {
                AvatarAddress = avatarState.address,
                RuneId = runeId,
                TryCount = tryCount,
            };
            var ctx = new ActionContext
            {
                BlockIndex = blockIndex,
                PreviousState = state,
                RandomSeed = rand.Seed,
                Signer = agentAddress,
            };

            var nextState = action.Execute(ctx);
            if (!nextState.TryGetLegacyState(runeStateAddress, out List nextRuneRawState))
            {
                throw new Exception();
            }

            var nextRunState = new RuneState(nextRuneRawState);
            var nextNcgBal = nextState.GetBalance(agentAddress, ncgCurrency);
            var nextCrystalBal = nextState.GetBalance(agentAddress, crystalCurrency);
            var nextRuneBal = nextState.GetBalance(agentAddress, runeCurrency);

            if (cost.NcgQuantity != 0)
            {
                Assert.NotEqual(ncgBal, nextNcgBal);
            }

            if (cost.CrystalQuantity != 0)
            {
                Assert.NotEqual(crystalBal, nextCrystalBal);
            }

            if (cost.RuneStoneQuantity != 0)
            {
                Assert.NotEqual(runeBal, nextRuneBal);
            }

            var costNcg = tryCount * cost.NcgQuantity * ncgCurrency;
            var costCrystal = tryCount * cost.CrystalQuantity * crystalCurrency;
            var costRune = tryCount * cost.RuneStoneQuantity * runeCurrency;

            if (costNcg.Sign > 0)
            {
                nextState = nextState.MintAsset(context, agentAddress, costNcg);
            }

            if (costCrystal.Sign > 0)
            {
                nextState = nextState.MintAsset(context, agentAddress, costCrystal);
            }

            if (costRune.Sign > 0)
            {
                nextState = nextState.MintAsset(context, avatarState.address, costRune);
            }

            var finalNcgBal = nextState.GetBalance(agentAddress, ncgCurrency);
            var finalCrystalBal = nextState.GetBalance(agentAddress, crystalCurrency);
            var finalRuneBal = nextState.GetBalance(avatarState.address, runeCurrency);
            Assert.Equal(ncgBal, finalNcgBal);
            Assert.Equal(crystalBal, finalCrystalBal);
            Assert.Equal(runeBal, finalRuneBal);
            Assert.Equal(runeState.Level + 1, nextRunState.Level);
        }

        [Fact]
        public void Execute_RuneCostNotFoundException()
        {
            var agentAddress = new PrivateKey().Address;
            var avatarAddress = new PrivateKey().Address;
            var sheets = TableSheetsImporter.ImportSheets();
            var tableSheets = new TableSheets(sheets);
            var blockIndex = tableSheets.WorldBossListSheet.Values
                .OrderBy(x => x.StartedBlockIndex)
                .First()
                .StartedBlockIndex;

            var goldCurrencyState = new GoldCurrencyState(_goldCurrency);
            var state = new World(new MockWorldState())
                .SetLegacyState(goldCurrencyState.address, goldCurrencyState.Serialize())
                .SetAgentState(agentAddress, new AgentState(agentAddress));

            foreach (var (key, value) in sheets)
            {
                state = state.SetLegacyState(Addresses.TableSheet.Derive(key), value.Serialize());
            }

            var avatarState = new AvatarState(
                avatarAddress,
                agentAddress,
                0,
                tableSheets.GetAvatarSheets(),
                new GameConfigState(),
                default
            );

            var runeListSheet = state.GetSheet<RuneListSheet>();
            var runeId = runeListSheet.First().Value.Id;
            var runeStateAddress = RuneState.DeriveAddress(avatarState.address, runeId);
            var runeState = new RuneState(128381293);
            state = state.SetLegacyState(runeStateAddress, runeState.Serialize());
            var action = new RuneEnhancement0()
            {
                AvatarAddress = avatarState.address,
                RuneId = runeId,
                TryCount = 1,
            };

            Assert.Throws<RuneCostNotFoundException>(() =>
                action.Execute(new ActionContext()
                {
                    PreviousState = state,
                    Signer = agentAddress,
                    RandomSeed = 0,
                    BlockIndex = blockIndex,
                }));
        }

        [Fact]
        public void Execute_RuneCostDataNotFoundException()
        {
            var agentAddress = new PrivateKey().Address;
            var avatarAddress = new PrivateKey().Address;
            var sheets = TableSheetsImporter.ImportSheets();
            var tableSheets = new TableSheets(sheets);
            var blockIndex = tableSheets.WorldBossListSheet.Values
                .OrderBy(x => x.StartedBlockIndex)
                .First()
                .StartedBlockIndex;

            var goldCurrencyState = new GoldCurrencyState(_goldCurrency);
            var state = new World(new MockWorldState())
                .SetLegacyState(goldCurrencyState.address, goldCurrencyState.Serialize())
                .SetAgentState(agentAddress, new AgentState(agentAddress));

            foreach (var (key, value) in sheets)
            {
                state = state.SetLegacyState(Addresses.TableSheet.Derive(key), value.Serialize());
            }

            var avatarState = new AvatarState(
                avatarAddress,
                agentAddress,
                0,
                tableSheets.GetAvatarSheets(),
                new GameConfigState(),
                default
            );

            var runeListSheet = state.GetSheet<RuneListSheet>();
            var runeId = runeListSheet.First().Value.Id;
            var runeStateAddress = RuneState.DeriveAddress(avatarState.address, runeId);
            var runeState = new RuneState(runeId);
            var costSheet = state.GetSheet<RuneCostSheet>();
            if (!costSheet.TryGetValue(runeId, out var costRow))
            {
                throw new RuneCostNotFoundException($"[{nameof(Execute)}] ");
            }

            for (var i = 0; i < costRow.Cost.Count + 1; i++)
            {
                runeState.LevelUp();
            }

            state = state.SetLegacyState(runeStateAddress, runeState.Serialize());

            var action = new RuneEnhancement0()
            {
                AvatarAddress = avatarState.address,
                RuneId = runeId,
                TryCount = 1,
            };

            Assert.Throws<RuneCostDataNotFoundException>(() =>
                action.Execute(new ActionContext()
                {
                    PreviousState = state,
                    Signer = agentAddress,
                    RandomSeed = 0,
                    BlockIndex = blockIndex,
                }));
        }

        [Theory]
        [InlineData(false, true, true)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        public void Execute_NotEnoughFungibleAssetValueException(bool ncg, bool crystal, bool rune)
        {
            var agentAddress = new PrivateKey().Address;
            var avatarAddress = new PrivateKey().Address;
            var sheets = TableSheetsImporter.ImportSheets();
            var tableSheets = new TableSheets(sheets);
            var blockIndex = tableSheets.WorldBossListSheet.Values
                .OrderBy(x => x.StartedBlockIndex)
                .First()
                .StartedBlockIndex;

            var goldCurrencyState = new GoldCurrencyState(_goldCurrency);
            var context = new ActionContext();
            var state = new World(new MockWorldState())
                .SetLegacyState(goldCurrencyState.address, goldCurrencyState.Serialize())
                .SetAgentState(agentAddress, new AgentState(agentAddress));

            foreach (var (key, value) in sheets)
            {
                state = state.SetLegacyState(Addresses.TableSheet.Derive(key), value.Serialize());
            }

            var avatarState = new AvatarState(
                avatarAddress,
                agentAddress,
                0,
                tableSheets.GetAvatarSheets(),
                new GameConfigState(),
                default
            );

            var runeListSheet = state.GetSheet<RuneListSheet>();
            var runeId = runeListSheet.First().Value.Id;
            var runeStateAddress = RuneState.DeriveAddress(avatarState.address, runeId);
            var runeState = new RuneState(runeId);
            state = state.SetLegacyState(runeStateAddress, runeState.Serialize());

            var costSheet = state.GetSheet<RuneCostSheet>();
            if (!costSheet.TryGetValue(runeId, out var costRow))
            {
                throw new RuneCostNotFoundException($"[{nameof(Execute)}] ");
            }

            if (!costRow.TryGetCost(runeState.Level + 1, out var cost))
            {
                throw new RuneCostDataNotFoundException($"[{nameof(Execute)}] ");
            }

            var runeSheet = state.GetSheet<RuneSheet>();
            if (!runeSheet.TryGetValue(runeId, out var runeRow))
            {
                throw new RuneNotFoundException($"[{nameof(Execute)}] ");
            }

            var ncgCurrency = state.GetGoldCurrency();
            var crystalCurrency = CrystalCalculator.CRYSTAL;
            var runeCurrency = Currency.Legacy(runeRow.Ticker, 0, minters: null);

            if (ncg && cost.NcgQuantity > 0)
            {
                state = state.MintAsset(context, agentAddress, cost.NcgQuantity * ncgCurrency);
            }

            if (crystal && cost.CrystalQuantity > 0)
            {
                state = state.MintAsset(context, agentAddress, cost.CrystalQuantity * crystalCurrency);
            }

            if (rune)
            {
                state = state.MintAsset(context, avatarState.address, cost.RuneStoneQuantity * runeCurrency);
            }

            var action = new RuneEnhancement0()
            {
                AvatarAddress = avatarState.address,
                RuneId = runeId,
                TryCount = 1,
            };
            var ctx = new ActionContext
            {
                BlockIndex = blockIndex,
                PreviousState = state,
                RandomSeed = 0,
                Signer = agentAddress,
            };

            if (!ncg && cost.NcgQuantity == 0)
            {
                return;
            }

            if (!crystal && cost.CrystalQuantity == 0)
            {
                return;
            }

            if (!rune && cost.RuneStoneQuantity == 0)
            {
                return;
            }

            Assert.Throws<NotEnoughFungibleAssetValueException>(() =>
                action.Execute(new ActionContext()
                {
                    PreviousState = state,
                    Signer = agentAddress,
                    RandomSeed = 0,
                    BlockIndex = blockIndex,
                }));
        }

        [Fact]
        public void Execute_TryCountIsZeroException()
        {
            var agentAddress = new PrivateKey().Address;
            var avatarAddress = new PrivateKey().Address;
            var sheets = TableSheetsImporter.ImportSheets();
            var tableSheets = new TableSheets(sheets);
            var blockIndex = tableSheets.WorldBossListSheet.Values
                .OrderBy(x => x.StartedBlockIndex)
                .First()
                .StartedBlockIndex;

            var goldCurrencyState = new GoldCurrencyState(_goldCurrency);
            var state = new World(new MockWorldState())
                .SetLegacyState(goldCurrencyState.address, goldCurrencyState.Serialize())
                .SetAgentState(agentAddress, new AgentState(agentAddress));

            foreach (var (key, value) in sheets)
            {
                state = state.SetLegacyState(Addresses.TableSheet.Derive(key), value.Serialize());
            }

            var avatarState = new AvatarState(
                avatarAddress,
                agentAddress,
                0,
                tableSheets.GetAvatarSheets(),
                new GameConfigState(),
                default
            );

            var runeListSheet = state.GetSheet<RuneListSheet>();
            var runeId = runeListSheet.First().Value.Id;
            var runeStateAddress = RuneState.DeriveAddress(avatarState.address, runeId);
            var runeState = new RuneState(runeId);
            state = state.SetLegacyState(runeStateAddress, runeState.Serialize());

            var action = new RuneEnhancement0()
            {
                AvatarAddress = avatarState.address,
                RuneId = runeId,
                TryCount = 0,
            };

            Assert.Throws<TryCountIsZeroException>(() =>
                action.Execute(new ActionContext()
                {
                    PreviousState = state,
                    Signer = agentAddress,
                    RandomSeed = 0,
                    BlockIndex = blockIndex,
                }));
        }
    }
}
