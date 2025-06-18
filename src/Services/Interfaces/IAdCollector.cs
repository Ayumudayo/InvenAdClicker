using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    public interface IAdCollector
    {
        Task<Dictionary<string, IEnumerable<string>>> CollectAsync(
            string[] urls,
            CancellationToken cancellationToken = default);
    }
}