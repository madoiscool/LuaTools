using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameGuy.Services;
using GameGuy.ViewModels;
using Microsoft.Win32;

namespace GameGuy.Views;

/// <summary>
/// Reusable drag-and-drop install box. Set its DataContext to a <see cref="DropInstallViewModel"/>;
/// it forwards dropped file paths to the VM and reflects drag-over highlight.
/// </summary>
public partial class DropZone : UserControl
{
    public DropZone() => InitializeComponent();

    private DropInstallViewModel? Vm => DataContext as DropInstallViewModel;

    /// <summary>
    /// The link text for the drag in progress, resolved ONCE on DragEnter. DragOver fires continuously
    /// while the mouse moves, and pulling the payload out of the data object on every one of those just
    /// to re-run the same regexes is wasted work. Null when this drag carries no usable link.
    /// </summary>
    private string? _dragLink;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragLink = LinkTextFrom(e.Data);
        UpdateDrag(e, entering: true);
    }

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDrag(e, entering: true);

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragLink = null;
        if (Vm is not null) Vm.IsDragOver = false;
    }

    private void UpdateDrag(DragEventArgs e, bool entering)
    {
        bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        // Only highlight for a link we can actually act on. Leaving Effects at None for anything else
        // means Windows never delivers the drop, so there's no "that wasn't a Steam link" case to report.
        bool hasLink = !hasFiles && SteamLinkParser.AppIdFrom(_dragLink) is not null;

        e.Effects = hasFiles || hasLink ? DragDropEffects.Copy : DragDropEffects.None;
        if (Vm is not null) Vm.IsDragOver = (hasFiles || hasLink) && entering;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not null) Vm.IsDragOver = false;
        string? link = _dragLink;
        _dragLink = null;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            if (Vm is not null) await Vm.HandleDropAsync(paths);
            return;
        }

        // A dragged SteamDB / Steam link → install that appid, same as gameguy://install/<id>.
        if (Vm is not null) await Vm.TryHandleLinkAsync(link ?? LinkTextFrom(e.Data));
    }

    /// <summary>
    /// The URL out of a drag payload. Browsers differ: Chrome and Firefox put the link in the text
    /// formats, so those come first; the UniformResourceLocator pair is the fallback for stricter
    /// sources (an address bar, a .url shortcut).
    /// </summary>
    private static string? LinkTextFrom(IDataObject data)
    {
        foreach (string format in new[] { DataFormats.UnicodeText, DataFormats.Text })
            if (data.GetDataPresent(format) && data.GetData(format) is string s && s.Length > 0)
                return s;

        // These two arrive as a MemoryStream rather than a string, and are null-terminated. Decode and
        // cut at the terminator, or the appid regexes run against whatever trails it.
        foreach (var (format, encoding) in new[]
                 {
                     ("UniformResourceLocatorW", System.Text.Encoding.Unicode),
                     ("UniformResourceLocator", System.Text.Encoding.Default),
                 })
        {
            if (!data.GetDataPresent(format)) continue;
            switch (data.GetData(format))
            {
                case string s when s.Length > 0:
                    return s.Split('\0')[0];
                case MemoryStream ms:
                    string decoded = encoding.GetString(ms.ToArray()).Split('\0')[0];
                    if (decoded.Length > 0) return decoded;
                    break;
            }
        }
        return null;
    }

    // "Browse files…". A non-drag way to add files. Feeds the same VM install path as a drop.
    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = GameGuy.Resources.Strings.Drop_Picker_Title,
            Multiselect = true,
            Filter = GameGuy.Resources.Strings.Drop_Picker_Filter,
        };
        if (dialog.ShowDialog() == true && Vm is not null)
            await Vm.HandleDropAsync(dialog.FileNames);
    }
}
