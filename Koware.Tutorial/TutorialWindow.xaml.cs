// Author: Ilgaz Mehmetoğlu
// Main tutorial window with sidebar navigation and content pages.
using System.Windows;
using System.Windows.Controls;
using Koware.Tutorial.Pages;

namespace Koware.Tutorial;

/// <summary>
/// Tutorial window with sidebar navigation between different help pages.
/// </summary>
public partial class TutorialWindow : Window
{
    private Button? _activeNavButton;

    public TutorialWindow()
    {
        InitializeComponent();
        
        // Set initial page
        _activeNavButton = NavGettingStarted;
        ContentFrame.Navigate(new GettingStartedPage());
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // Update active state
        if (_activeNavButton != null)
        {
            _activeNavButton.Tag = null;
        }
        button.Tag = "Active";
        _activeNavButton = button;

        // Navigate to appropriate page
        Page? page = button.Name switch
        {
            "NavGettingStarted" => new GettingStartedPage(),
            "NavProviderSetup" => new ProviderSetupPage(),
            "NavWatchingAnime" => new WatchingAnimePage(),
            "NavReadingManga" => new ReadingMangaPage(),
            "NavManagingLists" => new ManagingListsPage(),
            "NavTipsShortcuts" => new TipsShortcutsPage(),
            _ => null
        };

        if (page != null)
        {
            ContentFrame.Navigate(page);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Static helper to show the tutorial window.
    /// </summary>
    public static void ShowTutorial(Window? owner = null)
    {
        var tutorial = new TutorialWindow();
        if (owner != null)
        {
            tutorial.Owner = owner;
            tutorial.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        tutorial.ShowDialog();
    }
}
