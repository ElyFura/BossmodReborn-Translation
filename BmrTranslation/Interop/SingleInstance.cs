using System.Threading;

namespace BmrTranslation.Interop;

// Refuses to let a second copy of this plugin patch BossMod.
//
// Two copies can be loaded at once without anything looking wrong: a dev-plugin path and an install from
// the plugin repository are separate plugins to Dalamud, each in its own load context. Both then attach to
// the same BossMod objects, and the damage is not cosmetic:
//
//   * the second copy reads the first copy's German and records it as "BossMod's English", so its undo log
//     restores *German* on unload - the original English is then gone until the game restarts
//   * every key looks stale, because the stored English no longer matches what was read
//
// A named mutex is the right primitive because the copies share nothing else - not an ALC, not a static.
// It is never acquired, only created: the name exists for as long as any handle is open, so whoever
// creates it first is the owner and everyone else sees createdNew == false. Not taking ownership avoids
// the thread affinity a held mutex would impose, since a plugin is constructed and disposed on whichever
// thread Dalamud happens to use.
public sealed class SingleInstance : IDisposable
{
    private const string Name = @"Global\de.elyfura.bmrtranslation.single";

    private Mutex? _handle;
    private bool _failed;

    // Re-checked rather than decided once at construction: when the user removes the other copy, the name
    // is released, and the next attach attempt (every two seconds) picks it up on its own. Deciding once
    // would leave this copy idle until someone thought to reload it.
    public bool IsPrimary()
    {
        if (_handle != null || _failed)
        {
            return true;
        }
        try
        {
            var handle = new Mutex(initiallyOwned: false, Name, out var createdNew);
            if (createdNew)
            {
                _handle = handle;
                return true;
            }
            handle.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            // an unavailable mutex must not cost the user their translation; the doubled-instance case is
            // rare and self-inflicted, a broken plugin is not
            Service.Log.Warning(ex, "could not check for a second instance - continuing as primary");
            _failed = true;
            return true;
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
