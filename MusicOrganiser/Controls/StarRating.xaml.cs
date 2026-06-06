using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicOrganiser.Controls;

/// <summary>
/// Compact 5-star rating control. Bound to a nullable int (1..5; null/0 = unrated).
/// Click a star to set the rating; click the current rating again to clear it.
/// </summary>
public partial class StarRating : UserControl
{
    private static readonly Brush FilledBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xB7, 0x00));
    private static readonly Brush EmptyBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));

    private readonly TextBlock[] _stars = new TextBlock[5];

    public static readonly DependencyProperty RatingProperty = DependencyProperty.Register(
        nameof(Rating), typeof(int?), typeof(StarRating),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRatingChanged));

    public int? Rating
    {
        get => (int?)GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    public static readonly DependencyProperty EditModeProperty = DependencyProperty.Register(
        nameof(EditMode), typeof(bool), typeof(StarRating),
        new FrameworkPropertyMetadata(false, OnRatingChanged));

    /// <summary>When false (default) only filled stars show; when true all 5 show for editing.</summary>
    public bool EditMode
    {
        get => (bool)GetValue(EditModeProperty);
        set => SetValue(EditModeProperty, value);
    }

    public StarRating()
    {
        InitializeComponent();
        FilledBrush.Freeze();
        EmptyBrush.Freeze();

        for (int i = 0; i < 5; i++)
        {
            var star = new TextBlock
            {
                Text = "☆",
                FontSize = 14,
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand,
                Tag = i + 1,
                Foreground = EmptyBrush
            };
            star.MouseLeftButtonDown += Star_MouseLeftButtonDown;
            _stars[i] = star;
            StarPanel.Children.Add(star);
        }
        UpdateStars();
    }

    private void Star_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is int value)
        {
            // Click the current rating to clear; otherwise set to the clicked star.
            Rating = Rating == value ? null : value;
            e.Handled = true;
        }
    }

    private static void OnRatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StarRating)d).UpdateStars();

    private void UpdateStars()
    {
        int rating = Rating ?? 0;
        bool edit = EditMode;
        for (int i = 0; i < 5; i++)
        {
            bool filled = i < rating;
            // Only filled stars show normally; empty stars appear on hover (edit mode).
            _stars[i].Visibility = (filled || edit) ? Visibility.Visible : Visibility.Collapsed;
            _stars[i].Text = filled ? "★" : "☆";
            _stars[i].Foreground = filled ? FilledBrush : EmptyBrush;
        }
    }
}
