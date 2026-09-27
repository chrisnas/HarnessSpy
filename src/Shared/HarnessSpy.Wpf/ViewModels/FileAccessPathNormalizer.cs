using System.IO;

namespace HarnessSpy.Wpf.ViewModels;

internal sealed class FileAccessPathNormalizer
{
    public string Normalize(string path)
    {
        string candidate = path.Trim();
        if (!Path.IsPathRooted(candidate))
        {
            return candidate;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return candidate;
        }
    }
}
