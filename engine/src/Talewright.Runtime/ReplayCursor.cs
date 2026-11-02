namespace Talewright.Runtime;

internal readonly record struct ReplayCursor
{
    public ReplayCursor(int position, int length)
    {
        if (position < 0 || length < 0 || position > length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "The replay cursor must be within its timeline.");
        }

        Position = position;
        Length = length;
    }

    public int Position { get; }
    public int Length { get; }
    public bool CanBack => Position > 0;
    public bool CanForward => Position < Length;

    public ReplayCursor Back() => At(Position - 1);
    public ReplayCursor Forward() => At(Position + 1);
    public ReplayCursor AfterAppend() => new(Position + 1, Position + 1);
    public ReplayCursor At(int position) => new(position, Length);
}
