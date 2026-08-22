namespace Picollo.Profiler.IpResolution;

internal interface IMethodCounters
{
    long TotalCount { get; }
    long OwnCount { get; }

    /// <summary>
    /// Native, kernel or unknown frames directly under this method.
    /// </summary>
    long OwnPlusCount { get; }

    void IncrementTotal();
    void IncrementOwn();
    void IncrementOwnPlus();

    void ResetCounters();
}

// TODO IResolvedMethod : IMethodCounters, increment them from thread

internal class ThreadMethodCounters : IMethodCounters
{
    public IResolvedMethod Method { get; }

    public long OwnCount { get; private set; }
    public long TotalCount { get; private set; }

    /// <summary>
    /// Native, kernel or unknown frames directly under this method.
    /// </summary>
    public long OwnPlusCount { get; private set; }

    public ThreadMethodCounters(IResolvedMethod method)
    {
        Method = method;
    }

    public void IncrementTotal()
    {
        TotalCount++;
        Method.IncrementTotal();
    }

    public void IncrementOwn()
    {
        OwnCount++;
        Method.IncrementOwn();
    }

    public void IncrementOwnPlus()
    {
        OwnPlusCount++;
        Method.IncrementOwnPlus();
    }

    public void ResetCounters()
    {
        OwnCount = 0;
        TotalCount = 0;
        OwnPlusCount = 0;

        // It's multiple work, but light
        Method.ResetCounters();
    }
}