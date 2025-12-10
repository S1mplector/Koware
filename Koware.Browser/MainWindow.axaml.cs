// Author: Ilgaz Mehmetoğlu
using Avalonia.Controls;
using Avalonia.Input;
using Koware.Browser.ViewModels;

namespace Koware.Browser;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            vm.SearchCommand.Execute(null);
        }
    }
}
