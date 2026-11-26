using System.IO;
using System.Text.Json;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.WindowsFeatures;

internal sealed class WindowsTweakJournal(string root) : ITweakJournal
{
    private string Journal => Path.Combine(root, "last-change.json");

    public IDisposable Lock()
    {
        var pins = new WindowsStartupFileMove.DirectoryPins();
        try
        {
            pins.Pin(root, create: true);
            return new JournalLock(pins, WindowsStartupFileMove.OpenExclusiveLock(Path.Combine(root, "changes.lock")));
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    public TweakJournalState Read()
    {
        try { _ = File.GetAttributes(Journal); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return new(); }
        using var pins = new WindowsStartupFileMove.DirectoryPins();
        pins.Pin(root);
        if (!StartupPaths.IsSafeFile(Journal)) throw new InvalidDataException();
        using var stream = WindowsStartupFileMove.OpenVerifiedFileForRead(Journal, allowReplacement: true);
        if (stream.Length is < 2 or > 8192) throw new InvalidDataException();
        var state = JsonSerializer.Deserialize<TweakJournalState>(stream);
        if (state is null || !Valid(state.Last) || !Valid(state.Pending)) throw new InvalidDataException();
        return state;
    }

    public void Save(TweakJournalState state)
    {
        if (!Valid(state.Last) || !Valid(state.Pending)) throw new InvalidDataException();
        StartupJournalFile.Save(Journal, state, replace: true);
    }

    private static bool Valid(TweakChange? change) => change is null || (change.Address?.Valid == true
        && change.Before?.ValidFor(change.Address) == true && change.After?.ValidFor(change.Address) == true
        && change.Before != change.After);

    private sealed class JournalLock(IDisposable pins, IDisposable file) : IDisposable
    {
        public void Dispose()
        {
            file.Dispose();
            pins.Dispose();
        }
    }
}

