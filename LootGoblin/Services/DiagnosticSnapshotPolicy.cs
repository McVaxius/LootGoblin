using System;

namespace LootGoblin.Services;

internal sealed class DiagnosticSnapshotPolicy
{
    private readonly TimeSpan interval;
    private DateTime nextPeriodicAtUtc = DateTime.MinValue;

    public DiagnosticSnapshotPolicy(TimeSpan? interval = null)
    {
        this.interval = interval ?? TimeSpan.FromSeconds(60);
    }

    public bool ShouldWritePeriodic(DateTime nowUtc, bool activelyRunning)
    {
        if (!activelyRunning)
        {
            nextPeriodicAtUtc = DateTime.MinValue;
            return false;
        }

        if (nextPeriodicAtUtc == DateTime.MinValue)
        {
            nextPeriodicAtUtc = nowUtc + interval;
            return false;
        }

        if (nowUtc < nextPeriodicAtUtc)
            return false;

        nextPeriodicAtUtc = nowUtc + interval;
        return true;
    }

    public void Reset()
        => nextPeriodicAtUtc = DateTime.MinValue;
}
