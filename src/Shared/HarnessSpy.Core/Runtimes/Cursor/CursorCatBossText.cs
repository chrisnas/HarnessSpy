namespace HarnessSpy.Core.Runtimes.Cursor;

public sealed class CursorCatBossText
{
    public const string Prefix =
        "(o.o) Cat BOSS> ";
        //" /|_/|\\n" +
        //"( o.o )\n" +
        //" -----\n" +
        //"Chaircat Whiskers:";

    public string Say(string message)
    {
        string normalized = message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return $"{Prefix}\n{normalized}";
    }
}
