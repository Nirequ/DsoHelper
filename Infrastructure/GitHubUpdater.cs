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

/// <summary>Итог ручной проверки обновлений.</summary>
internal enum UpdateOutcome
{
    /// <summary>На GitHub нет версии новее установленной.</summary>
    UpToDate,

    /// <summary>Обновление скачано, подменено, запущен новый процесс.</summary>
    Updated,

    /// <summary>Что-то не вышло (сеть, GitHub, диск) — актуально только для ручной проверки.</summary>
    Error
}

/// <summary>Сообщение для статусной строки в окне настроек.</summary>
internal readonly record struct UpdateResult(UpdateOutcome Outcome, string Message);

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
    /// Без <paramref name="report"/> (старт приложения) работает молча;
    /// ручная проверка получает статус в <paramref name="report"/> и результат.
    /// Любая ошибка не выбрасывается наружу: возвращается <see cref="UpdateOutcome.Error"/>.
    /// </summary>
    public static async Task<UpdateResult> CheckAndUpdateAsync(Action<string>? report = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                return new(UpdateOutcome.Error, "Не удалось определить путь до exe.");
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion is null)
            {
                return new(UpdateOutcome.Error, "Не удалось определить версию приложения.");
            }

            // 1. Метаданные последнего release.
            using (var api = CreateApiClient())
            {
                using var response = await api.GetAsync(ApiLatestUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new(UpdateOutcome.Error,
                        $"GitHub ответил {(int)response.StatusCode} — проверьте позже.");
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                // 2. tag_name → Version (без префикса v) — новее ли release.
                var (latest, tagDisplay) = ParseTagVersion(root);
                if (latest is null ||
                    string.IsNullOrEmpty(tagDisplay))
                {
                    return new(UpdateOutcome.Error, "GitHub не вернул корректную версию тега.");
                }

                report?.Invoke($"Последний release: v{tagDisplay}…");

                if (latest <= currentVersion)
                {
                    return new(UpdateOutcome.UpToDate,
                        $"У вас последняя версия: {BuildVersionDisplay(currentVersion)} (на GitHub v{tagDisplay}).");
                }

                // 3. В assets ищем первый exe — это и есть новая сборка.
                var downloadUrl = FindExecutableAsset(root);
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    return new(UpdateOutcome.Error,
                        $"В release v{tagDisplay} нет exe-файла.");
                }

                report?.Invoke($"Найдена версия v{tagDisplay} — скачиваю…");

                // 4. Скачиваем «.new» рядом. Сначала в .tmp, чтобы осиротевший
                //    файл половинного размера не выглядел готовым к установке.
                var stagingNew = exe + ".new";
                var stagingTmp = exe + ".new.tmp";
                await DownloadToFileAsync(api, downloadUrl, stagingTmp).ConfigureAwait(false);

                // 5. Обмен в три хода, при неудаче откатываем.
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
                return new(UpdateOutcome.Updated, $"Обновлено до v{tagDisplay}.");  // не исполнится: Exit внутри.
            }
        }
        catch (Exception exception)
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

            return new(UpdateOutcome.Error, "Обновление не удалось: " + exception.Message);
        }
    }

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient { Timeout = ApiTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static (Version? Version, string Display) ParseTagVersion(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
        {
            return (null, string.Empty);
        }

        var raw = tag.GetString() ?? string.Empty;
        var text = raw.TrimStart('v', 'V');

        return System.Version.TryParse(text, out var version)
            ? (version, text)
            : (null, string.Empty);
    }

    /// <summary>1.2.0.0 → "1.2.0": хвостовые нули в статусной строке не нужны.</summary>
    internal static string BuildVersionDisplay(Version version)
    {
        if (version.Revision > 0)
        {
            return version.ToString(4);
        }

        return version.Build > 0 ? version.ToString(3) : version.ToString(2);
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
