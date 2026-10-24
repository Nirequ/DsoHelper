using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DsoHelper.Infrastructure;

/// <summary>
/// Самообновление single-file приложения через GitHub Releases.
/// Схема: скачали новый exe рядом как «{exe}.new» → переименовали работающий exe в
/// «{exe}.old» (запущенный файл Windows переименовывать позволяет) → «.new» занял
/// имя оригинала → запустили новый процесс и вышли. Прерванная замена убирается
/// на следующем старте («.old» — удаляется, недокачанный «.new» — удаляется).
/// </summary>
internal static class GitHubUpdater
{
    private const string Owner = "Nirequ";
    private const string Repo = "DsoHelper";

    private const string ApiLatestUrl =
        "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

    private const string UserAgent = "DsoHelper-Updater";
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Чистит следы прошлой замены: {exe}.old и недокачанный {exe}.new.</summary>
    public static void CleanupPreviousUpdate()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            TryDelete(exe + ".old");
            TryDelete(exe + ".new");
        }
        catch (Exception)
        {
            // Чистка не критична: следующая версия всё равно попробует снова.
        }
    }

    /// <summary>
    /// Проверяет последний release и при более новой версии обновляет себя.
    /// Любая ошибка (нет интернета, GitHub недоступен, нет прав на файлы)
    /// глушится: приложение продолжает обычную работу со старой версией.
    /// </summary>
    public static async Task CheckAndUpdateAsync()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion is null)
            {
                return;
            }

            // 1. Метаданные последнего release.
            using (var api = CreateApiClient())
            {
                using var response = await api.GetAsync(ApiLatestUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                // 2. tag_name → Version (без префикса v) — новее ли release.
                var latest = ParseTagVersion(root);
                if (latest is null || latest <= currentVersion)
                {
                    return;
                }

                // 3. В assets ищем первый exe — это и есть новая сборка.
                var downloadUrl = FindExecutableAsset(root);
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    return;
                }

                // 4. Скачиваем «.new» рядом. Сначала в .tmp, чтобы осиротевший
                //    файл половинного размера не выглядел готовым к установке.
                var stagingNew = exe + ".new";
                var stagingTmp = exe + ".new.tmp";
                await DownloadToFileAsync(api, downloadUrl, stagingTmp).ConfigureAwait(false);

                // 5. Обмен в три хода, при любой неудаче откатываем .
                if (File.Exists(stagingNew))
                {
                    File.Delete(stagingNew);
                }

                File.Move(stagingTmp, stagingNew);

                var backup = exe + ".old";
                TryDelete(backup);
                File.Move(exe, backup);

                try
                {
                    File.Move(stagingNew, exe);
                }
                catch
                {
                    // Новый файл не занял место старого — возвращаем всё как было.
                    TryMove(backup, exe);
                    TryDelete(stagingNew);
                    throw;
                }

                // 6. Запускаем новую версию и завершаем текущий процесс.
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });
                Environment.Exit(0);
            }
        }
        catch (Exception)
        {
            // Недокачанный «.new» не должен оставаться рядом с exe.
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    TryDelete(exe + ".new.tmp");
                    TryDelete(exe + ".new");
                }
            }
            catch (Exception)
            {
                // Игнорируем, приложение продолжает работу.
            }
        }
    }

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient { Timeout = ApiTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static Version? ParseTagVersion(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = tag.GetString() ?? string.Empty;
        var text = raw.TrimStart('v', 'V');

        return System.Version.TryParse(text, out var version) ? version : null;
    }

    private static string? FindExecutableAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return assets.EnumerateArray()
            .Where(asset => asset.TryGetProperty("name", out var name) &&
                            name.ValueKind == JsonValueKind.String &&
                            (name.GetString() ?? string.Empty)
                                .EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(asset => asset.TryGetProperty("browser_download_url", out var url) &&
                             url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null)
            .FirstOrDefault(url => !string.IsNullOrEmpty(url));
    }

    private static async Task DownloadToFileAsync(
        HttpClient client, string url, string targetFile)
    {
        using (var timeout = new CancellationTokenSource(DownloadTimeout))
        {
            using (var response = await client
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content
                    .ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);

                await using var file = File.Create(targetFile);
                await stream.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Пусть остаётся: следующая версия попробует ещё раз.
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Сценарий почти безнадёжен (нет прав на файл); следующий старт разберётся.
        }
    }
}
