
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    public interface IPipelineRunner
    {
        Task RunAsync(string[] urls, CancellationToken cancellationToken = default);
    }
}
