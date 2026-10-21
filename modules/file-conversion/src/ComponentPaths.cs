namespace Nook.FileConversion;

internal static class ComponentPaths
{
    internal static string LocalRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")
            || path.IndexOfAny(['"', '\0']) >= 0 || path.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("组件目录必须是本机绝对路径。");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length < 5 || full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar)
            || new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.MyDocuments }
                .Any(folder => string.Equals(full, Environment.GetFolderPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("不能把系统或个人文件根目录用作组件目录。");
        NoLinks(full);
        return full;
    }

    internal static void NoLinks(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("组件路径不能包含链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string Child(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Contains(':'))
            throw new InvalidDataException("组件路径无效。");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("组件路径越界。");
        NoLinks(path);
        return path;
    }

    internal static long InspectTree(string root)
    {
        NoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("组件目录不存在。");
        long bytes = 0;
        var count = 0;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                if (++count > 100_000) throw new InvalidDataException("组件文件数量异常。");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("组件包含不安全的链接。");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                else
                {
                    bytes = checked(bytes + new FileInfo(entry).Length);
                    if (bytes > 5_000_000_000) throw new InvalidDataException("组件解包体积异常。");
                }
            }
        }
        return bytes;
    }

    internal static void DeleteTree(string parent, string exactPath)
    {
        var safe = Child(parent, Path.GetRelativePath(parent, exactPath));
        if (!Directory.Exists(safe)) return;
        InspectTree(safe);
        Directory.Delete(safe, recursive: true);
    }
}
