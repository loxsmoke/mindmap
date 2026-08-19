using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MindMap.Services;

public static class UpdateInstaller
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{94CF3F77-A245-411A-AE74-A2830FFEE797}_is1";

    private const string UpdateDirName = "update";

    private static readonly string[] AllowedHosts = ["github.com", "githubusercontent.com"];
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static bool IsInstalledBySetup()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") as string is not { Length: > 0 } location) return false;
            return SamePath(location, AppContext.BaseDirectory);
        }
        catch
        {
            return false;
        }
    }

    public static string DownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var path) == 0
                    && !string.IsNullOrEmpty(path))
                    return path;
            }
            catch
            {
                // Fall through to the default location.
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static string UpdateStagingDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Brand.AppName,
            UpdateDirName);

    public static async Task<string?> DownloadAsync(
        ReleaseInfo release,
        string destDir,
        IProgress<double>? progress = null,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        if (release.SetupUrl is not { } url || !IsTrustedUrl(url))
            return null;

        var client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            Directory.CreateDirectory(destDir);
            var dst = UniquePath(destDir, UpdateCheck.SetupAssetName(release.Version));
            var tmp = dst + ".part";
            TryDelete(tmp);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await FetchToFileAsync(client, url, tmp, release.SetupSize, progress, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 2 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), ct).ConfigureAwait(false);
                }
            }

            File.Move(tmp, dst, overwrite: true);
            return dst;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (http is null) client.Dispose();
        }
    }

    public static void Launch(string setupPath)
    {
        var startInfo = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/RELAUNCH");
        Process.Start(startInfo);
    }

    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort only.
        }
    }

    public static bool IsTrustedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        foreach (var allowed in AllowedHosts)
        {
            if (uri.Host.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.Host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static string UniquePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(dir, fileName);
    }

    private static async Task FetchToFileAsync(
        HttpClient client,
        string url,
        string tmp,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = expectedSize > 0 ? expectedSize : response.Content.Headers.ContentLength ?? 0;
        var written = 0L;

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(tmp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            if (expectedSize > 0 && written != expectedSize)
                throw new IOException($"size mismatch: expected {expectedSize} bytes, got {written}");
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint flags,
        IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
