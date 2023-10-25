using Libplanet.Crypto;

namespace Nekoyume.Blockchain
{
    public interface IAccessControlService
    {
        public bool IsAccessDenied(Address address);

        public int GetAccessLevel(Address address);
    }
}
