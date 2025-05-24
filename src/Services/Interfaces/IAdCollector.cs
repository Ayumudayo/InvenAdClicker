namespace InvenAdClicker.Services.Interfaces
{
    public interface IAdCollector
    {
        Task<Dictionary<string, IEnumerable<string>>> CollectAsync(
            string[] urls,
            CancellationToken cancellationToken = default);
    }
}