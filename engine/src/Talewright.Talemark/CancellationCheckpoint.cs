namespace Talewright.Talemark;

internal sealed class CancellationCheckpoint(CancellationToken token)
{
    private int _remaining = 64;

    public CancellationToken Token => token;

    public void Check()
    {
        if (--_remaining != 0) return;
        token.ThrowIfCancellationRequested();
        _remaining = 64;
    }
}
