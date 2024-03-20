namespace InvenAdClicker.@struct;

public class Job
{
    public string Url { get; private set; }
    public int Iteration { get; private set; }

    public Job(string url, int itCnt)
    {
        Url = url;
        Iteration = itCnt;
    }
}