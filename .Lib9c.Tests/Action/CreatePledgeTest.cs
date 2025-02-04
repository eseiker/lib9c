namespace Lib9c.Tests.Action
{
    using System;
    using System.Collections.Generic;
    using Libplanet.Action.State;
    using Libplanet.Crypto;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.Model.State;
    using Nekoyume.Module;
    using Serilog;
    using Xunit;
    using Xunit.Abstractions;

    public class CreatePledgeTest
    {
        public CreatePledgeTest(ITestOutputHelper outputHelper)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.TestOutput(outputHelper)
                .CreateLogger();
        }

        [Theory]
        [InlineData(true, null)]
        [InlineData(false, typeof(PermissionDeniedException))]
        public void Execute(bool admin, Type exc)
        {
            var adminAddress = new PrivateKey().Address;
            var poolAddress = new PrivateKey().Address;
            var adminState = new AdminState(adminAddress, 150L);
            var patronAddress = new PrivateKey().Address;
            var mead = Currencies.Mead;
            var agentAddress = new PrivateKey().Address;
            var pledgeAddress = agentAddress.GetPledgeAddress();
            var context = new ActionContext();
            IWorld states = new World(new MockWorldState())
                .SetLegacyState(Addresses.Admin, adminState.Serialize())
                .MintAsset(context, patronAddress, 4 * 500 * mead);

            var agentAddresses = new List<(Address, Address)>
            {
                (agentAddress, pledgeAddress),
            };
            for (int i = 0; i < 499; i++)
            {
                var address = new PrivateKey().Address;
                agentAddresses.Add((address, address.GetPledgeAddress()));
            }

            var action = new CreatePledge
            {
                PatronAddress = patronAddress,
                Mead = RequestPledge.DefaultRefillMead,
                AgentAddresses = agentAddresses,
            };

            Address singer = admin ? adminAddress : poolAddress;
            var actionContext = new ActionContext
            {
                Signer = singer,
                PreviousState = states,
            };

            if (exc is null)
            {
                var nextState = action.Execute(actionContext);

                Assert.Equal(0 * mead, nextState.GetBalance(patronAddress, mead));
                Assert.Equal(4 * mead, nextState.GetBalance(agentAddress, mead));
            }
            else
            {
                Assert.Throws(exc, () => action.Execute(actionContext));
            }
        }
    }
}
