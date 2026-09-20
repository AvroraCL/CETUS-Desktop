using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cetus.Configuration;
using Microsoft.Web.WebView2.Wpf;

namespace Cetus.Updates;

/// <summary>
/// Hosts the GitHub Pages announcement inside CETUS (its own WebView2 on
/// the shared user-data directory) instead of bouncing the user to the
/// system browser. Falls back to the system browser when WebView2 cannot
/// initialize.
/// </summary>
internal sealed class AnnouncementWindow : Window
{
    private readonly string _url;
    private readonly WebView2 _browser;

    public AnnouncementWindow(string url, bool isDark)
    {
        _url = url;
        Title = "CETUS · 更新公告";
        Width = 1020;
        Height = 740;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = CreateBrush(isDark ? "#0C111B" : "#F8FAFC");

        _browser = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(
                255,
                isDark ? (byte)12 : (byte)248,
                isDark ? (byte)17 : (byte)250,
                isDark ? (byte)27 : (byte)252),
        };
        Content = _browser;
        Loaded += OnLoaded;
        Closed += (_, _) => _browser.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Microsoft.Web.WebView2.Core.CoreWebView2Environment environment =
                await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: CetusPaths.WebView2UserDataDirectory);
            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Outbound links (GitHub releases, etc.) belong in the system
            // browser; the announcement window stays on the announcement page.
            _browser.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            };
            _browser.CoreWebView2.Navigate(_url);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException
            or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(_url) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Both in-app and system browser failed; nothing more to do.
            }

            Close();
        }
    }

    private static SolidColorBrush CreateBrush(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
