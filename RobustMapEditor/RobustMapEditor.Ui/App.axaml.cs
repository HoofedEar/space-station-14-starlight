#nullable enable
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RobustMapEditor.Ui.ViewModels;
using RobustMapEditor.Ui.Views;

namespace RobustMapEditor.Ui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Kick off async engine bootstrap after the window is shown so the
            // splash is actually visible while the pool spins up.
            desktop.MainWindow.Opened += (_, _) => vm.BeginInitialization();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
