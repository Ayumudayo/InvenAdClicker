using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    internal static class ExceptionCleanup
    {
        private const string ErrorsKey = "InvenAdClicker.CleanupErrors";

        internal static async Task CaptureAsync(Exception primary, Func<Task> cleanup)
        {
            try { await cleanup(); }
            catch (Exception error)
            {
                lock (primary)
                {
                    if (primary.Data[ErrorsKey] is not List<Exception> errors)
                        primary.Data[ErrorsKey] = errors = new List<Exception>();
                    errors.Add(error);
                }
            }
        }

        internal static Exception[] Read(Exception primary)
        {
            lock (primary)
                return primary.Data[ErrorsKey] is List<Exception> errors ? errors.ToArray() : Array.Empty<Exception>();
        }
    }
}
