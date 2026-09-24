using System.Windows;
using SshManager.Core.Inventory;
using SshManager.Core.Models;

namespace SshManager.Views;

/// <summary>Asks what to remove with a container: its compose project, volumes, image, project folder.</summary>
public partial class ContainerRemoveWindow : Window
{
    private readonly ContainerInfo _container;

    private ContainerRemoveWindow(ContainerInfo container, string serverName)
    {
        InitializeComponent();
        _container = container;
        Header.Text = L.F("Container.RemoveHeader", container.Name, serverName);
        Info.Text = container.ComposeProject == null
            ? L.F("Container.RemoveInfo", container.Image, container.Status)
            : L.F("Container.RemoveInfoCompose", container.Image, container.Status, container.ComposeProject, container.ComposeDir ?? "—");

        var compose = container.ComposeProject != null;
        ScopePanel.Visibility = compose ? Visibility.Visible : Visibility.Collapsed;
        OnlyContainer.Content = L.F("Container.RemoveOnly", container.Name);
        WholeProject.Content = L.F("Container.RemoveProject", container.ComposeProject);
        Folder.Content = L.F("Container.RemoveFolder", container.ComposeDir);
        if (compose) WholeProject.IsChecked = true;
        else UpdateOptions();
    }

    public static ContainerRemoval? Ask(Window? owner, ContainerInfo container, string serverName)
    {
        var dlg = new ContainerRemoveWindow(container, serverName) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        var whole = dlg.WholeProject.IsChecked == true && container.ComposeProject != null;
        return new ContainerRemoval(whole, dlg.Volumes.IsChecked == true, dlg.Image.IsChecked == true,
            whole && dlg.Folder.IsChecked == true);
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e) => UpdateOptions();

    private void UpdateOptions()
    {
        if (Folder == null) return; // during InitializeComponent
        var whole = WholeProject.IsChecked == true && _container.ComposeProject != null;
        Volumes.Content = L.Get(whole ? "Container.RemoveVolumesProject" : "Container.RemoveVolumes");
        var folderOk = whole && ContainerCommands.SafeFolder(_container.ComposeDir);
        Folder.Visibility = whole && _container.ComposeDir != null ? Visibility.Visible : Visibility.Collapsed;
        Folder.IsEnabled = folderOk;
        if (!folderOk) Folder.IsChecked = false;
    }

    private void OnRemove(object sender, RoutedEventArgs e) => DialogResult = true;
}
