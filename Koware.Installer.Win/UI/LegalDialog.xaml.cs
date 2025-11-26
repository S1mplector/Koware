// Author: Ilgaz Mehmetoğlu
// Code-behind for the Usage Notice acceptance dialog.
using System;
using System.IO;
using System.Windows;

namespace Koware.Installer.Win.UI;

public partial class LegalDialog : Window
{
    public bool Accepted { get; private set; }

    public LegalDialog(string noticeText)
    {
        InitializeComponent();
        // The dialog now shows a rich, static FlowDocument defined in XAML.
        // The noticeText parameter is intentionally ignored to avoid rendering raw markdown.
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
        Close();
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
        Close();
    }
}
