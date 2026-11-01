using System.IO;
using System.Media;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Wpf.Services;

public interface IAttentionSoundPlayer
{
    void Play();
}

public sealed class CatMeowAttentionSoundPlayer : IAttentionSoundPlayer, IDisposable
{
    private const string ResourceName = "HarnessSpy.Wpf.Assets.CatMeow.wav";

    private readonly Stream? _sound;
    private readonly SoundPlayer? _player;

    public CatMeowAttentionSoundPlayer()
    {
        _sound = typeof(CatMeowAttentionSoundPlayer)
            .Assembly
            .GetManifestResourceStream(ResourceName);
        if (_sound is null)
        {
            return;
        }

        _player = new SoundPlayer(_sound);
        _player.Load();
    }

    public void Play()
    {
        if (_player is null)
        {
            SystemSounds.Exclamation.Play();
            return;
        }

        _player.Stop();
        _sound!.Position = 0;
        _player.Play();
    }

    public void Dispose()
    {
        _player?.Dispose();
        _sound?.Dispose();
    }
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
