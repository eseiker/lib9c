using System;
using System.Collections.Generic;
using Bencodex.Types;
using Lib9c.Abstractions;
using Libplanet.Action;
using Libplanet.Action.State;
using Libplanet.Crypto;
using Nekoyume.Model.State;
using Nekoyume.Module;
using Serilog;

namespace Nekoyume.Action
{
    [Serializable]
    [ActionObsolete(ActionObsoleteConfig.V200020AccidentObsoleteIndex)]
    [ActionType("activate_account")]
    public class ActivateAccount0 : ActionBase, IActivateAccount
    {
        public Address PendingAddress { get; private set; }

        public byte[] Signature { get; private set; }

        Address IActivateAccount.PendingAddress => PendingAddress;
        byte[] IActivateAccount.Signature => Signature;

        public override IValue PlainValue => Dictionary.Empty
            .Add("type_id", "activate_account")
            .Add("values", new Dictionary(
                new[]
                {
                    new KeyValuePair<IKey, IValue>((Text)"pending_address", PendingAddress.Serialize()),
                    new KeyValuePair<IKey, IValue>((Text)"signature", (Binary) Signature),
                }
            ));

        public ActivateAccount0()
        {
        }

        public ActivateAccount0(Address pendingAddress, byte[] signature)
        {
            PendingAddress = pendingAddress;
            Signature = signature;
        }

        public override IWorld Execute(IActionContext context)
        {
            context.UseGas(1);
            IWorld state = context.PreviousState;

            CheckObsolete(ActionObsoleteConfig.V100080ObsoleteIndex, context);

            if (!state.TryGetLegacyState(ActivatedAccountsState.Address, out Dictionary accountsAsDict))
            {
                throw new ActivatedAccountsDoesNotExistsException();
            }
            if (!state.TryGetLegacyState(PendingAddress, out Dictionary pendingAsDict))
            {
                throw new PendingActivationDoesNotExistsException(PendingAddress);
            }

            var accounts = new ActivatedAccountsState(accountsAsDict);
            var pending = new PendingActivationState(pendingAsDict);

            if (pending.Verify(Signature))
            {
                // We left this log message to track activation history.
                // Please delete it if we have an API for evaluation results on the Libplanet side.
                Log.Information("{pendingAddress} is activated by {signer} now.", pending.address, context.Signer);
                return state.SetLegacyState(
                    ActivatedAccountsState.Address,
                    accounts.AddAccount(context.Signer).Serialize()
                ).SetLegacyState(
                    pending.address,
                    new Bencodex.Types.Null()
                );
            }
            else
            {
                throw new InvalidSignatureException(pending, Signature);
            }
        }

        public override void LoadPlainValue(IValue plainValue)
        {
            var asDict = (Dictionary)((Dictionary)plainValue)["values"];
            PendingAddress = asDict["pending_address"].ToAddress();
            Signature = ((Binary)asDict["signature"]).ToByteArray();
        }

        public Address GetPendingAddress() => PendingAddress;

        public byte[] GetSignature() => Signature;
    }
}
