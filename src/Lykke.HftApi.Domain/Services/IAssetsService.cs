using System.Collections.Generic;
using System.Threading.Tasks;
using Lykke.HftApi.Domain.Entities;

namespace Lykke.HftApi.Domain.Services
{
    public interface IAssetsService
    {
        Task<IReadOnlyList<Asset>> GetAllAssetsAsync();
        Task<Asset> GetAssetByIdAsync(string assetId);

        Task<IReadOnlyList<AssetPair>> GetAllAssetPairsAsync();
        Task<AssetPair> GetAssetPairByIdAsync(string assetPairId);
    }
}
