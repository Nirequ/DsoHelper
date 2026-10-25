using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DsoHelper.Infrastructure;

/// <summary>Итог задуманной проверки: есть ли новая версия.</summary>
internal enum CheckOutcome
{
    /// <summary>На GitHub нет версии новее установленной.</summary>
    UpToDate,

    /// <summary>Новая версия найдена, список данных готов к скачиванию.</summary>
    Available,

    /// <summary>Что-то не вышло (сеть, GitHub) — актуально только для ручной проверки.</summary>
    Error
}

/// <summary>Итог выполнения обновления.</summary>
internal enum UpdateOutcome
{
    /// <summary>Обновление скачано, подменено, запущен новый процесс.</summary>
    Updated,

    /// <summary>Ошибка очередего шага, актуально только для ручной проверки.</summary>
    Error
}

/// <summary>Результат ручной «проверить обновления».</summary>
internal sealed record UpdateCheckResult(
    CheckOutcome Outcome,
    string Message,
    Version? Latest,
    string DisplayTag,
    string? DownloadUrl,
    string? Sha256 = null);

/// <summary>Результат шага выполнения (скачать → подменить → перезапустить).</summary>
internal readonly record struct UpdateResult(UpdateOutcome Outcome, string Message);

/// <summary>
/// Самообновление single-file приложения через GitHub Releases.
/// Схема: скачали новый exe рядом как «{exe}.new» → переименовали работающий exe в
/// «{exe}.old» (запущенный файл Windows переименовывать позволяет) → «.new» занял
/// имя оригинала → запустили новый процесс и вышли. Прерванная замена убирается
/// на следующем старте («.old» — удаляется, недокачанный «.new» — удаляется).
/// Перед подменой SHA-256 скачанного файла сверяется с digest из GitHub API —
/// так подменённый или битый файл не попадёт в установку.
/// </summary>
internal static class GitHubUpdater
{
    private const string Owner = "Nirequ";
    private const string Repo = "DsoHelper";

    private const string ApiLatestUrl =
        "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

    private const string UserAgent = "DsoHelper-Updater";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
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
    /// Фоновая проверка на старте приложения: молча смотрит на GitHub и просто
    /// возвращает результат. Ничего не скачивает и не устанавливает — решение
    /// об обновлении принимает пользователь. Возвращает и «версия последняя»,
    /// чтобы окно настроек могло это показать; при ошибке — null.
    /// </summary>
    public static async Task<UpdateCheckResult?> CheckVersionSilentlyAsync()
    {
        try
        {
            var check = await TryCheck().ConfigureAwait(false);
            return check.Outcome switch
            {
                CheckOutcome.Available => new(
                    CheckOutcome.Available,
                    $"Доступна версия v{check.DisplayTag}.",
                    check.Latest,
                    check.DisplayTag,
                    check.DownloadUrl,
                    check.Sha256),
                CheckOutcome.UpToDate => new(
                    CheckOutcome.UpToDate,
                    check.Message,
                    check.Latest,
                    check.DisplayTag,
                    null),
                _ => null
            };
        }
        catch (Exception)
        {
            // Нет интернета / GitHub недоступен: работаем дальше, без уведомления.
            CleanupDownloadArtifacts();
            return null;
        }
    }

    /// <summary>
    /// Ручная проверка (шестерёнка): только смотрит на GitHub, ничего не скачивает.
    /// Возвращает предложение обновиться, статус и описание ошибки.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(Action<string>? report = null)
    {
        var check = await TryCheck(report).ConfigureAwait(false);
        return check.Outcome switch
        {
            CheckOutcome.UpToDate => new(
                CheckOutcome.UpToDate, check.Message, null, string.Empty, null),
            CheckOutcome.Error => new(
                CheckOutcome.Error, check.Message, null, string.Empty, null),
            _ => new(
                CheckOutcome.Available,
                $"Доступна версия v{check.DisplayTag}.",
                check.Latest,
                check.DisplayTag,
                check.DownloadUrl,
                check.Sha256)
        };
    }

    /// <summary>
    /// Подтвержденная установка: скачивает указанный exe, сверяет его SHA-256 с
    /// <paramref name="sha256"/> (digest из GitHub API, если есть), подменяет запущенный
    /// файл и запускает новый процесс. Позвал — значит обновляемся.
    /// <paramref name="downloadProgress"/> получает процент скачивания (0..100).
    /// </summary>
    public static async Task<UpdateResult> PerformUpdateAsync(
        string downloadUrl,
        string displayTag,
        string? sha256 = null,
        Action<string>? report = null,
        Action<double>? downloadProgress = null)
    {
        try
        {
            await TryInstall(downloadUrl, displayTag, sha256, report, downloadProgress).ConfigureAwait(false);
            return new(UpdateOutcome.Updated, $"Обновлено до v{displayTag}.");
        }
        catch (Exception exception)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return new(UpdateOutcome.Error, "Обновление не удалось: " + exception.Message);
            }

            await TryRollbackContentAsync(exe, exception).ConfigureAwait(false);
            return new(UpdateOutcome.Error, "Обновление не удалось: " + DescribeError(exception));
        }
    }

    private record struct QuickCheckResult(
        CheckOutcome Outcome, string Message, Version? Latest, string DisplayTag, string? DownloadUrl,
        string? Sha256 = null);

    /// <summary>Просмотр одного release: метаданные + URL exe-файла.</summary>
    private static async Task<QuickCheckResult> TryCheck(Action<string>? report = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return new(CheckOutcome.Error, "Не удалось определить путь до exe.", null, string.Empty, null);
        }

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
        if (currentVersion is null)
        {
            return new(CheckOutcome.Error, "Не удалось определить версию приложения.", null, string.Empty, null);
        }

        using var api = CreateApiClient();
        using var response = await api.GetAsync(ApiLatestUrl).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new(
                CheckOutcome.Error,
                $"GitHub ответил {(int)response.StatusCode} — проверьте позже.",
                null, string.Empty, null);
        }

        report?.Invoke("Проверяю обновления…");

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // tag_name → Version (без префикса v) — новее ли release.
        var (latest, tagDisplay) = ParseTagVersion(root);
        if (latest is null || string.IsNullOrEmpty(tagDisplay))
        {
            return new(CheckOutcome.Error, "GitHub не вернул корректный тег версии.", null, string.Empty, null);
        }

        report?.Invoke($"Последний release: v{tagDisplay}…");

        if (latest <= currentVersion)
        {
            return new(
                CheckOutcome.UpToDate,
                $"У вас последняя версия: {BuildVersionDisplay(currentVersion)}.",
                latest, tagDisplay, null);
        }

        // В assets ищем первый exe — это и есть новая сборка.
        var asset = FindExecutableAsset(root);
        if (asset is null)
        {
            return new(
                CheckOutcome.Error,
                $"В release v{tagDisplay} нет exe-файла.",
                latest, tagDisplay, null);
        }

        return new(
            CheckOutcome.Available,
            string.Empty,
            latest,
            tagDisplay,
            asset.Value.Url,
            asset.Value.Sha256);
    }

    /// <summary>Скачивание + замена + запуск нового процесса.</summary>
    private static async Task TryInstall(
        string downloadUrl,
        string displayTag,
        string? sha256,
        Action<string>? report = null,
        Action<double>? downloadProgress = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            throw new InvalidOperationException("Нет доступа к запущенному exe.");
        }

        report?.Invoke($"Найдена версия v{displayTag} — скачиваю…");

        // Скачиваем сначала в .tmp, чтобы осиротевший файл половинного размера
        // не выглядел готовым к установке.
        var stagingNew = exe + ".new";
        var stagingTmp = exe + ".new.tmp";

        using (var client = CreateDownloadClient())
        {
            await DownloadToFileAsync(client, downloadUrl, stagingTmp, downloadProgress).ConfigureAwait(false);
        }

        // Digest приходит из GitHub API вместе с ассетом: если файл подменили или он
        // побился по дороге, до подмены exe дело не дойдёт.
        await VerifySha256Async(stagingTmp, sha256).ConfigureAwait(false);

        if (File.Exists(stagingNew))
        {
            File.Delete(stagingNew);
        }

        File.Move(stagingTmp, stagingNew);

        // Переносим работающий exe в .old и подставляем на его место новую версию.
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

        downloadProgress?.Invoke(100);
        report?.Invoke("Загрузка завершена (100%). Перезапуск приложения…");

        // Даём UI отрисовать 100% на ProgressBar перед закрытием окна
        await Task.Delay(750).ConfigureAwait(false);

        // Запускаем новую версию и завершаем текущий процесс.
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true
        });
        Environment.Exit(0);
    }

    private static Task TryRollbackContentAsync(string exe, Exception exception)
    {
        // Недокачанный «.new» не должен оставаться рядом с exe.
        try
        {
            TryDelete(exe + ".new.tmp");
            TryDelete(exe + ".new");
        }
        catch (Exception)
        {
            // Игнорируем: следующий старт подберётся.
        }

        return Task.CompletedTask;
    }

    private static void CleanupDownloadArtifacts()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            TryDelete(exe + ".new.tmp");
            TryDelete(exe + ".new");
        }
        catch (Exception)
        {
            // Игнорируем.
        }
    }

    /// <summary>Человекочитаемый текст сетевых сбоев вместо сырых английских сообщений.</summary>
    internal static string DescribeError(Exception exception) => exception switch
    {
        TaskCanceledException when exception.InnerException is TimeoutException =>
            "GitHub не ответил за 30 секунд — проверьте интернет и попробуйте ещё раз.",
        TaskCanceledException =>
            "Запрос отменён — похоже, нет соединения с интернетом.",
        HttpRequestException =>
            "Не удалось соединиться с GitHub — проверьте интернет и попробуйте ещё раз.",
        _ => exception.Message
    };

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient { Timeout = ApiTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// Клиент для скачивания exe: таймаутом заведует CancellationToken
    /// в DownloadToFileAsync, поэтому HttpClient.Timeout — бесконечный.
    /// Важно: короткий 10-секундный клиент обрывал загрузку 21 МБ файла.
    /// </summary>
    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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

    /// <summary>Ассет релиза: ссылка на скачивание и ожидаемая контрольная сумма (может отсутствовать).</summary>
    private readonly record struct ReleaseAsset(string Url, string? Sha256);

    /// <summary>Первый exe в assets релиза: ссылка и digest (если GitHub его отдал).</summary>
    private static ReleaseAsset? FindExecutableAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String ||
                !(name.GetString() ?? string.Empty).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!asset.TryGetProperty("browser_download_url", out var url) ||
                url.ValueKind != JsonValueKind.String ||
                url.GetString() is not { Length: > 0 } downloadUrl)
            {
                continue;
            }

            return new ReleaseAsset(downloadUrl, ReadSha256Digest(asset));
        }

        return null;
    }

    /// <summary>
    /// У ассетов GitHub есть поле digest вида «sha256:{hex}». Старые релизы его не отдают —
    /// тогда проверка контрольной суммы просто пропускается.
    /// </summary>
    private static string? ReadSha256Digest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digest) ||
            digest.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        const string prefix = "sha256:";
        var raw = (digest.GetString() ?? string.Empty).Trim();
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = raw[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static async Task DownloadToFileAsync(
        HttpClient client, string url, string targetFile, Action<double>? progress = null)
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

                // Читаем кусками и докладываем прогресс: полоска в настройках
                // обновляется по каждому целому проценту (без спама в UI).
                var buffer = new byte[81920];
                var total = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                var lastPercent = -1;

                int bytesRead;
                while ((bytesRead = await stream
                           .ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)
                           .ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, bytesRead), timeout.Token).ConfigureAwait(false);

                    if (progress is not null && total > 0)
                    {
                        downloaded += bytesRead;
                        var percent = (int)(downloaded * 100 / Math.Max(total, 1));
                        if (percent > lastPercent && percent <= 100)
                        {
                            lastPercent = percent;
                            progress(percent);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Сверяет скачанный файл с digest из GitHub API. Если digest нет (старый релиз),
    /// проверка пропускается — остаётся только защита HTTPS.
    /// </summary>
    private static async Task VerifySha256Async(string path, string? expectedHex)
    {
        if (string.IsNullOrEmpty(expectedHex))
        {
            return;
        }

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
        if (string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Контрольная сумма скачанного файла не совпала с ожидаемой ({expectedHex[..8]}…). " +
            "Обновление отменено — попробуйте позже.");
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