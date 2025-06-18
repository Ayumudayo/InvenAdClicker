using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    public interface IAdClicker
    {
        Task ClickAsync(
            Dictionary<string, IEnumerable<string>> pageToLinks,
            CancellationToken cancellationToken = default);
    }
}