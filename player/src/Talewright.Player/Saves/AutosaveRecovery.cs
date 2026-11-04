using System.Globalization;
using Talewright.Runtime.Saves;

namespace Talewright.Player.Saves;

internal sealed class AutosaveRecovery(ISaveStore store, string storyId, byte[]? rejectedAutosave)
{
    private byte[]? _rejectedAutosave = rejectedAutosave;

    public bool NeedsPreservation { get; private set; } = true;
    public string? RecoveredSlot { get; private set; }

    public bool UsesStore(ISaveStore candidate) => ReferenceEquals(store, candidate);

    public async Task<bool> WriteAsync(byte[] data, Func<bool> isCurrent)
    {
        if (!isCurrent()) return false;

        if (NeedsPreservation)
        {
            try
            {
                _rejectedAutosave ??= await store.ReadAutosaveAsync(storyId);
            }
            catch (FileNotFoundException)
            {
                // The rejected autosave is already gone, so there is nothing left to overwrite.
                NeedsPreservation = false;
            }
        }

        if (NeedsPreservation)
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var slot = $"Recovered autosave {timestamp}-{suffix}";
            await store.WriteAsync(storyId, slot, _rejectedAutosave!, overwrite: false);
            RecoveredSlot = slot;
            NeedsPreservation = false;
            _rejectedAutosave = null;
        }

        if (!isCurrent()) return false;
        await store.WriteAutosaveAsync(storyId, data);
        return true;
    }
}
