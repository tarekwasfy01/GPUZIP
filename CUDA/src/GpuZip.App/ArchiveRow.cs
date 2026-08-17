using GpuZip.Core;

namespace GpuZip.App;

public sealed class ArchiveRow
{
    public ArchiveRow(ArchiveEntryInfo entry)
    {
        Name = entry.Path;
        Type = entry.Kind == ArchiveEntryKind.Directory ? "Folder" : "File";
        Size = entry.Kind == ArchiveEntryKind.Directory ? string.Empty : FormatSize(entry.OriginalSize);
        Packed = entry.Kind == ArchiveEntryKind.Directory ? string.Empty : FormatSize(entry.PackedSize);
        Modified = entry.LastWriteTimeUtc == DateTime.MinValue ? string.Empty : entry.LastWriteTimeUtc.ToLocalTime().ToString("g");
        Method = entry.Method;
    }

    public string Name { get; }
    public string Type { get; }
    public string Size { get; }
    public string Packed { get; }
    public string Modified { get; }
    public string Method { get; }

    public static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < suffixes.Length - 1) { value /= 1024; index++; }
        return $"{value:N1} {suffixes[index]}";
    }
}
