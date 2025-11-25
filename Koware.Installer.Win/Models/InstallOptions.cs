// Author: Ilgaz Mehmetoğlu 
// Options to control how the GUI installer publishes and deploys Koware.
using System;

namespace Koware.Installer.Win.Models;

public sealed class InstallOptions
{
    public string InstallDir { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "koware");

    public bool Publish { get; set; } = true;

    public bool IncludePlayer { get; set; } = true;

    public bool AddToPath { get; set; } = true;

    public bool CleanTarget { get; set; } = false;
}
