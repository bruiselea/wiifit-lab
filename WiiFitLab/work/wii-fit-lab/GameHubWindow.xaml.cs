using System.Windows;
using System.Windows.Media;

namespace WiiFitLab;

public partial class GameHubWindow : Window
{
    public GameHubWindow() => InitializeComponent();

    public event EventHandler? DriveRequested;

    public event EventHandler? BreakoutRequested;

    public event EventHandler? HoverRaceRequested;

    public event EventHandler? TiltMazeRequested;

    public event EventHandler? SnowboardRequested;

    public void SetConnectionState(bool connected)
    {
        ConnectionText.Text = connected ? "● ボード接続済み" : "● キーボードでも遊べます";
        ConnectionText.Foreground = BrushFrom(connected ? "#48D7C8" : "#F0A84A");
    }

    private void DriveButton_Click(object sender, RoutedEventArgs e) => DriveRequested?.Invoke(this, EventArgs.Empty);

    private void BreakoutButton_Click(object sender, RoutedEventArgs e) => BreakoutRequested?.Invoke(this, EventArgs.Empty);

    private void HoverRaceButton_Click(object sender, RoutedEventArgs e) => HoverRaceRequested?.Invoke(this, EventArgs.Empty);

    private void TiltMazeButton_Click(object sender, RoutedEventArgs e) => TiltMazeRequested?.Invoke(this, EventArgs.Empty);

    private void SnowboardButton_Click(object sender, RoutedEventArgs e) => SnowboardRequested?.Invoke(this, EventArgs.Empty);

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
