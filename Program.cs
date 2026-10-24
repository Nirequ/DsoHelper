using Avalonia;
using System;
using System.Threading.Tasks;
using DsoHelper.Infrastructure;

namespace DsoHelper;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // След прошлой замены (если обновление сорвалось на середине) убираем сразу.
        GitHubUpdater.CleanupPreviousUpdate();

        // Проверка обновлений в фоне: до 10 секунд ожидания не блокируют UI,
        // при удачном обновлении процесс сам перезапустится.
        _ = Task.Run(() => GitHubUpdater.CheckAndUpdateAsync());

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
