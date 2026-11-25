using System.IO;

namespace SecureVideo.Player;

public sealed record PlaylistEntry(string FullPath, ZipVideoEntry? Archived = null)
{
    public string FileName { get; } = Path.GetFileName(Archived?.Name ?? FullPath);
    public string Location => Archived is null ? FullPath : $"{FullPath} → {Archived.Name}";
}

internal static class PlaylistFiles
{
    public static string[] Scan(string folder, CancellationToken cancellation)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var paths = new List<string>();
        foreach (string path in Directory.EnumerateFiles(folder, "*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            if (Path.GetExtension(path).ToUpperInvariant() is ".MP4" or ".MKV" or ".SVIDEO")
                paths.Add(path);
        }
        return paths.ToArray();
    }

    public static int Compare(PlaylistEntry left, PlaylistEntry right)
    {
        var a = NumericPrefix(left.FileName);
        var b = NumericPrefix(right.FileName);
        if (a.IsEmpty != b.IsEmpty) return a.IsEmpty ? 1 : -1;
        if (!a.IsEmpty)
        {
            int result = a.Length.CompareTo(b.Length);
            if (result == 0) result = a.SequenceCompareTo(b);
            if (result != 0) return result;
        }

        int nameOrder = StringComparer.CurrentCultureIgnoreCase.Compare(left.FileName, right.FileName);
        return nameOrder != 0 ? nameOrder : StringComparer.OrdinalIgnoreCase.Compare(left.Location, right.Location);
    }

    private static ReadOnlySpan<char> NumericPrefix(string fileName)
    {
        var name = fileName.AsSpan().TrimStart();
        int length = 0;
        while (length < name.Length && char.IsAsciiDigit(name[length])) length++;
        var digits = name[..length];
        // Keep one zero so a name beginning with zero sorts before names without a prefix.
        int start = 0;
        while (start + 1 < digits.Length && digits[start] == '0') start++;
        return digits[start..];
    }
}
