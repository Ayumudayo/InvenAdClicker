using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    public interface IAdClicker<TPage>
    {
        Task<TPage> ClickAdAsync(TPage page, string link, CancellationToken cancellationToken);
    }
}
