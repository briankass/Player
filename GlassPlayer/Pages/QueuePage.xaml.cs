using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class QueuePage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;

    public QueuePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ps.QueueChanged += Refresh;
        _ps.TrackChanged += Refresh;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.QueueChanged -= Refresh;
        _ps.TrackChanged -= Refresh;
    }

    private void Refresh()
    {
        QueueList.ItemsSource = null;
        QueueList.ItemsSource = _ps.Queue;
        QueueList.SelectedIndex = _ps.Index;
        var left = Math.Max(0, _ps.Queue.Count - _ps.Index - 1);
        CountText.Text = _ps.Queue.Count == 0 ? "The queue is empty" : $"{_ps.Queue.Count} songs · {left} up next";
        if (_ps.Index >= 0 && _ps.Index < _ps.Queue.Count) QueueList.ScrollIntoView(_ps.Queue[_ps.Index]);
    }

    private void QueueList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track t)
        {
            var i = _ps.Queue.IndexOf(t);
            if (i >= 0) _ps.PlayAt(i);
        }
    }

    private void QueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track t) return;
        var i = _ps.Queue.IndexOf(t);
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "" } };
        play.Click += (_, _) => _ps.PlayAt(i);
        var remove = new MenuFlyoutItem { Text = "Remove from queue", Icon = new FontIcon { Glyph = "" }, IsEnabled = i != _ps.Index };
        remove.Click += (_, _) => _ps.RemoveFromQueue(i);
        menu.Items.Add(play);
        menu.Items.Add(remove);
        menu.ShowAt(QueueList, e.GetPosition(QueueList));
        e.Handled = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _ps.ClearQueue();
}
