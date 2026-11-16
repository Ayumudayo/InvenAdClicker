
using InvenAdClicker.Models;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    public interface IPipelineRunner
    {
        Task<PipelineRunResult> RunAsync(string[] urls, CancellationToken cancellationToken = default);
    }
}
