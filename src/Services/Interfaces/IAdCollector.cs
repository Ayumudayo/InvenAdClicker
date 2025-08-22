using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    public interface IAdCollector<TPage>
    {
        Task<List<string>> CollectLinksAsync(TPage page, string url, CancellationToken cancellationToken);
    }
}
