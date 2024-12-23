using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MoveEpicGamesGames.Utils;
using MoveEpicGamesGames.ViewModels;
using MoveEpicGamesGames.Views;

namespace MoveEpicGamesGames;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
            AppHelper.MainWindow = desktop.MainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}