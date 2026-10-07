using System.Security.Cryptography;
using System.Text;

namespace Runspace.Desktop;

internal sealed record ScriptFile(string Path, string Text, Encoding Encoding, byte[] Preamble, byte[] Hash);

internal static class ScriptFileStore
{
    internal static Encoding DefaultEncoding { get; } = new UTF8Encoding(false, true);

    public static Task<ScriptFile> ReadAsync(string path) => Task.Run(() =>
    {
        path = System.IO.Path.GetFullPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var (encoding, preamble) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        return new ScriptFile(path, text, encoding, preamble, SHA256.HashData(bytes));
    });

    private static (Encoding Encoding, byte[] Preamble) DetectEncoding(byte[] bytes)
    {
        Encoding[] encodings =
        [
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)
        ];
        foreach (var encoding in encodings)
        {
            var preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble)) return (encoding, preamble);
        }
        return (DefaultEncoding, []);
    }

    public static Task<ScriptFile> WriteAsync(string path, string text, Encoding encoding, byte[] preamble, byte[]? expectedHash) =>
        Task.Run(() =>
        {
            path = System.IO.Path.GetFullPath(path);
            var bytes = preamble.Concat(encoding.GetBytes(text)).ToArray();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var backup = path + "." + Guid.NewGuid().ToString("N") + ".save-backup";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var output = new FileStream(temporary, options))
                {
                    output.Write(bytes);
                    output.Flush(true);
                }
                if (expectedHash is null)
                    File.Move(temporary, path, false);
                else
                {
                    // Deny in-place writers until the atomic swap. A competing rename is detected in the displaced file.
                    using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (!SHA256.HashData(original).AsSpan().SequenceEqual(expectedHash))
                        throw new IOException("The script changed on disk. Reopen it or use Save As; no file was overwritten.");
                    if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("Atomic saving cannot replace a symbolic-link or reparse-point script. Use Save As to a regular file.");
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
                    File.Replace(temporary, path, backup);
                    var displacedHash = SHA256.HashData(File.ReadAllBytes(backup));
                    if (!displacedHash.AsSpan().SequenceEqual(expectedHash))
                        throw new IOException($"A concurrent file replacement occurred during saving. Your save is at {path}; " +
                            $"the displaced disk version is preserved at {backup}. Reopen and compare before saving again.");
                    File.Delete(backup);
                }
                return new ScriptFile(path, text, encoding, preamble, SHA256.HashData(bytes));
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        });
}
