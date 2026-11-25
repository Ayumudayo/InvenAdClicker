
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    /// <summary>Pools leased page resources (IPage for Playwright); the browser lifetime belongs to the implementation.</summary>
    public interface IBrowserPool<TBrowser> : IDisposable, IAsyncDisposable where TBrowser : class
    {
        Task InitializePoolAsync(CancellationToken cancellationToken = default);
        Task<TBrowser> AcquireAsync(CancellationToken cancellationToken = default);
        void Release(TBrowser browser);
        Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<TBrowser> RenewAsync(TBrowser oldBrowser, CancellationToken cancellationToken = default);
    }
}
