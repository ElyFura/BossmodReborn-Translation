namespace ProbeTarget;

// mirrors the shapes Milestone 2 has to patch in BossMod
public class BossModule
{
    public class TextHints : List<(string, bool)>
    {
        public void Add(string text, bool isRisk = true) => Add((text, isRisk));
    }

    public class GlobalHints : List<string>;

    public virtual string[] PrePullHints => [];

    // public, non-trivial, returns the list -> postfix target
    public TextHints CalculateHintsForRaidMember(int slot, string actor)
    {
        TextHints hints = [];
        for (var i = 0; i < 2; ++i)
        {
            hints.Add(i == 0 ? "Stay together!" : $"Stack with {actor}", true);
        }
        return hints;
    }

    public GlobalHints CalculateGlobalHints(string actor)
    {
        GlobalHints hints = ["Prepare for raidwide"];
        return hints;
    }
}

// a derived module overriding the virtual property, like the 59 real ones
public sealed class DerivedModule : BossModule
{
    public override string[] PrePullHints => ["Assign towers before pull"];
}

public class ZoneModule
{
    public virtual List<string> CalculateGlobalHints() => [];
}

public sealed class DerivedZone : ZoneModule
{
    public override List<string> CalculateGlobalHints() => ["Head to the next objective"];
}

// mirrors a UI method: several literals, some of them ImGui ids that must NOT be touched
public sealed class FakeWindow
{
    public List<string> Draw()
    {
        var log = new List<string>();
        log.Add(Emit("Enable radar"));
        log.Add(Emit("##ConfigSearch"));       // an id, never translated
        log.Add(Emit("Supported fights"));
        log.Add(Emit(Format("Boss: {0}", "Zoraal Ja")));
        return log;
    }

    private static string Emit(string s) => s;
    private static string Format(string f, object a) => string.Format(f, a);
}

// An abstract generic base whose concrete subclasses close it differently - BossMod's DuelFarm<Duel> and
// EurekaZone<NM> shape. The translatable literal lives on the base, so the method the UI actually runs
// belongs to the *closed* type, and the open definition has no code to patch at all.
public abstract class Zone<T>
{
    public virtual List<string> DrawExtra() => ["Max mobs to pull"];
}

public sealed class BozjaZone : Zone<int>;

public sealed class EurekaZone : Zone<string>;

public sealed class ZadnorZone : Zone<int>; // deliberately the same instantiation as BozjaZone
