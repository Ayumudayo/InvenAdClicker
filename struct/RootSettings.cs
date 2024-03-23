namespace InvenAdClicker.@struct;

public class RootSettings
{
    public AppSettings AppSettings { get; set; } = new AppSettings();
    public List<string> URL { get; set; } = new List<string>();
    public List<string> GoodsURL { get; set; } = new List<string>();
    public List<string> MobileURL { get; set; } = new List<string>();
}