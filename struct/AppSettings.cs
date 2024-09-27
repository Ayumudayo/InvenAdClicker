// InvenAdClicker.@struct.AppSettings.cs

namespace InvenAdClicker.@struct
{
    public class AppSettings
    {
        public int MaxIter { get; set; }
        public int MaxWorker { get; set; }
        public int MaxSet { get; set; }
        public int IterationInterval { get; set; }
        public int ClickIframeInterval { get; set; }
        public int ClickAdInterval { get; set; } // New setting
    }

}