namespace Nekoyume.BlockChain
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using Bencodex.Types;
    using Libplanet;
    using Libplanet.Action;
    using Libplanet.Action.Loader;
    using Libplanet.Blockchain;
    using Libplanet.Blockchain.Policies;
    using Libplanet.Tx;
    using Nekoyume.Action;
    using NCAction = Libplanet.Action.PolymorphicAction<Action.ActionBase>;
    using Serilog;

    public class StagePolicy : IStagePolicy<NCAction>
    {
        private static Dictionary<Address, DateTimeOffset> _bannedAccountTracker = new();
        private readonly VolatileStagePolicy<NCAction> _impl;
        private readonly ConcurrentDictionary<Address, SortedList<Transaction, TxId>> _txs;
        private readonly int _quotaPerSigner;

        private ImmutableHashSet<Address> _bannedAccounts = new[]
        {
            new Address("de96aa7702a7a1fd18ee0f84a5a0c7a2c28ec840"),
            new Address("153281c93274bEB9726A03C33d3F19a8D78ad805"),
            new Address("7035AA8B7F9fB5db026fb843CbB21C03dd278502"),
            new Address("52393Ea89DF0E58152cbFE673d415159aa7B9dBd"),
            new Address("2D1Db6dBF1a013D648Efd16d85B4079dCF88B4CC"),
            new Address("dE30E00917B583305f14aD21Eafc70f1b183b779"),
            new Address("B892052f1E10bf700143dd9bEcd81E31CD7f7095"),

            new Address("C0a90FC489738A1153F793A3272A91913aF3956b"),
            new Address("b8D7bD4394980dcc2579019C39bA6b41cb6424E1"),
            new Address("555221D1CEA826C55929b8A559CA929574f7C6B3"),
            new Address("B892052f1E10bf700143dd9bEcd81E31CD7f7095"),
            // v100351
            new Address("0xd7e1b90dea34108fb2d3a6ac7dbf3f33bae2c77d"),
        }.ToImmutableHashSet();

        private static readonly IActionLoader _actionLoader =
            new SingleActionLoader(typeof(PolymorphicAction<ActionBase>));

        public StagePolicy(
            TimeSpan txLifeTime,
            int quotaPerSigner,
            IActionLoader? actionLoader = null)
        {
            if (quotaPerSigner < 1)
            {
                throw new ArgumentOutOfRangeException(
                    $"{nameof(quotaPerSigner)} must be positive: ${quotaPerSigner}");
            }

            _txs = new ConcurrentDictionary<Address, SortedList<Transaction, TxId>>();
            _quotaPerSigner = quotaPerSigner;
            _impl = (txLifeTime == default)
                ? new VolatileStagePolicy<NCAction>()
                : new VolatileStagePolicy<NCAction>(txLifeTime);
        }

        public Transaction Get(BlockChain<NCAction> blockChain, TxId id, bool filtered = true)
            => _impl.Get(blockChain, id, filtered);

        public long GetNextTxNonce(BlockChain<NCAction> blockChain, Address address)
            => _impl.GetNextTxNonce(blockChain, address);

        public void Ignore(BlockChain<NCAction> blockChain, TxId id)
            => _impl.Ignore(blockChain, id);

        public bool Ignores(BlockChain<NCAction> blockChain, TxId id)
            => _impl.Ignores(blockChain, id);

        public IEnumerable<Transaction> Iterate(BlockChain<NCAction> blockChain, bool filtered = true)
        {
            if (filtered)
            {
                var txsPerSigner = new Dictionary<Address, SortedSet<Transaction>>();
                foreach (Transaction tx in _impl.Iterate(blockChain, filtered))
                {
                    if (!txsPerSigner.TryGetValue(tx.Signer, out var s))
                    {
                        txsPerSigner[tx.Signer] = s = new SortedSet<Transaction>(new TxComparer());
                    }

                    s.Add(tx);
                    if (s.Count > _quotaPerSigner)
                    {
                        s.Remove(s.Max);
                    }

                    if (s.Count - _quotaPerSigner > -1)
                    {
                        if (!_bannedAccountTracker.ContainsKey(tx.Signer))
                        {
                            Log.Debug("Adding {signer} to banned accounts for 15 minutes", tx.Signer);
                            _bannedAccountTracker.Add(tx.Signer, DateTimeOffset.Now);
                            _bannedAccounts = _bannedAccounts.Add(tx.Signer);
                        }
                    }
                }

#pragma warning disable LAA1002 // DictionariesOrSetsShouldBeOrderedToEnumerate
                return txsPerSigner.Values.SelectMany(i => i);
#pragma warning restore LAA1002 // DictionariesOrSetsShouldBeOrderedToEnumerate
            }
            else
            {
                return _impl.Iterate(blockChain, filtered);
            }
        }

        public bool Stage(BlockChain<NCAction> blockChain, Transaction transaction)
        {
            if (_bannedAccounts.Contains(transaction.Signer))
            {
                if (_bannedAccountTracker.ContainsKey(transaction.Signer))
                {
                    if ((DateTimeOffset.Now - _bannedAccountTracker[transaction.Signer]).Minutes >= 15)
                    {
                        Log.Debug("Removing {signer} from banned accounts after 15 minutes", transaction.Signer);
                        _bannedAccountTracker.Remove(transaction.Signer);
                        _bannedAccounts = _bannedAccounts.Remove(transaction.Signer);
                    }
                }

                return false;
            }

            foreach (IValue rawAction in transaction.Actions)
            {
                try
                {
                    long index = blockChain.Count > 0 ? blockChain.Tip.Index + 1: 0;
                    var action = _actionLoader.LoadAction(blockChain.Count + 1, rawAction);
                    if (!(action is PolymorphicAction<ActionBase> polymorphicAction))
                    {
                        return false;
                    }

                    if (polymorphicAction.InnerAction.GetType()
                        .GetCustomAttribute<ActionObsoleteAttribute>(false) is { } attribute &&
                            attribute.ObsoleteIndex + 2 <= index)
                    {
                        return false;
                    }
                }
                catch (InvalidActionException)
                {
                    return false;
                }
            }

            return _impl.Stage(blockChain, transaction);
        }

        public bool Unstage(BlockChain<NCAction> blockChain, TxId id)
            => _impl.Unstage(blockChain, id);

        private class TxComparer : IComparer<Transaction>
        {
            public int Compare(Transaction x, Transaction y)
            {
                if (x.Nonce < y.Nonce)
                {
                    return -1;
                }
                else if (x.Nonce > y.Nonce)
                {
                    return 1;
                }
                else if (x.Timestamp < y.Timestamp)
                {
                    return -1;
                }
                else if (x.Timestamp > y.Timestamp)
                {
                    return 1;
                }
                else
                {
                    return 0;
                }
            }
        }
    }
}
