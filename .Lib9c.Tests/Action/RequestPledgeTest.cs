namespace Lib9c.Tests.Action
{
    using Bencodex.Types;
    using Libplanet.Action.State;
    using Libplanet.Crypto;
    using Libplanet.Types.Assets;
    using Nekoyume.Action;
    using Nekoyume.Model.State;
    using Nekoyume.Module;
    using Xunit;

    public class RequestPledgeTest
    {
        [Theory]
        [InlineData(RequestPledge.DefaultRefillMead)]
        [InlineData(100)]
        public void Execute(int contractedMead)
        {
            Currency mead = Currencies.Mead;
            Address patron = new PrivateKey().Address;
            var context = new ActionContext();
            IWorld states = new World(new MockWorldState()).MintAsset(context, patron, 2 * mead);
            var address = new PrivateKey().Address;
            var action = new RequestPledge
            {
                AgentAddress = address,
                RefillMead = contractedMead,
            };

            Assert.Equal(0 * mead, states.GetBalance(address, mead));
            Assert.Equal(2 * mead, states.GetBalance(patron, mead));

            var nextState = action.Execute(new ActionContext
            {
                Signer = patron,
                PreviousState = states,
            });
            var contract = Assert.IsType<List>(nextState.GetLegacyState(address.GetPledgeAddress()));

            Assert.Equal(patron, contract[0].ToAddress());
            Assert.False(contract[1].ToBoolean());
            Assert.Equal(contractedMead, contract[2].ToInteger());
            Assert.Equal(1 * mead, nextState.GetBalance(address, mead));
            Assert.Equal(1 * mead, nextState.GetBalance(patron, mead));
        }

        [Fact]
        public void Execute_Throw_AlreadyContractedException()
        {
            Address patron = new PrivateKey().Address;
            var address = new PrivateKey().Address;
            Address contractAddress = address.GetPledgeAddress();
            IWorld states = new World(new MockWorldState()).SetLegacyState(contractAddress, List.Empty);
            var action = new RequestPledge
            {
                AgentAddress = address,
                RefillMead = 1,
            };

            Assert.Throws<AlreadyContractedException>(() => action.Execute(new ActionContext
            {
                Signer = patron,
                PreviousState = states,
            }));
        }
    }
}
