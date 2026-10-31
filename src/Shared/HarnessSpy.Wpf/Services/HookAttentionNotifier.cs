using System.Media;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Wpf.Services;

public interface IAttentionSoundPlayer
{
    void Play();
}

public sealed class WindowsAttentionSoundPlayer : IAttentionSoundPlayer
{
    public void Play() => SystemSounds.Exclamation.Play();
}

public sealed class HookAttentionNotifier(IAttentionSoundPlayer soundPlayer)
{
    private readonly IAttentionSoundPlayer _soundPlayer = soundPlayer;
    private readonly HashSet<Guid> _notifiedEventIds = [];

    public void NotifyIfNeeded(HookObservation observation)
    {
        if (observation.HarnessOutcome?.RequiresUserAction != true ||
            !_notifiedEventIds.Add(observation.EventId))
        {
            return;
        }

        _soundPlayer.Play();
    }
}
