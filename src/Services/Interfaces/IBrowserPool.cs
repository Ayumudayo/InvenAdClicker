
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Interfaces
{
    public interface IBrowserPool<TBrowser> : IDisposable, IAsyncDisposable where TBrowser : class
    {
        Task InitializePoolAsync(CancellationToken cancellationToken = default);
        Task<TBrowser> AcquireAsync(CancellationToken cancellationToken = default);
        void Release(TBrowser browser);
        Task<TBrowser> RenewAsync(TBrowser oldBrowser, CancellationToken cancellationToken = default);
    }
}
