using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Core;
using STranslate.Plugin;
using STranslate.ViewModels.Pages;
using System.Diagnostics;
using System.Windows;

namespace STranslate.Views.Pages;

public partial class AboutPage
{
    public AboutPage(AboutViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;

        InitializeComponent();
    }

    public AboutViewModel ViewModel { get; }

    private void OnRepoCopy(object sender, RoutedEventArgs e)
    {
        Utilities.SetText(RepoTextBox.Text);

        Ioc.Default.GetRequiredService<ISnackbar>()
            .ShowSuccess(Ioc.Default.GetRequiredService<Internationalization>().GetTranslation("CopySuccess"));
    }

    private void OnWebsiteRequest(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Constant.Website) { UseShellExecute = true });

    private void OnSponsorRequest(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Constant.Sponsor) { UseShellExecute = true });

    private void OnJoinGroupRequest(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Constant.Group) { UseShellExecute = true });

    private void OnReportRequest(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Constant.Report) { UseShellExecute = true });
}