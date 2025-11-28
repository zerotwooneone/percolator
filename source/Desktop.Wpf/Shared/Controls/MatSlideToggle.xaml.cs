using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Desktop.Wpf.Shared.Controls
{
    public partial class MatSlideToggle : UserControl
    {
        public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
            nameof(IsChecked), typeof(bool), typeof(MatSlideToggle), new PropertyMetadata(false));

        public static readonly DependencyProperty OnContentProperty = DependencyProperty.Register(
            nameof(OnContent), typeof(object), typeof(MatSlideToggle), new PropertyMetadata(null));

        public static readonly DependencyProperty OffContentProperty = DependencyProperty.Register(
            nameof(OffContent), typeof(object), typeof(MatSlideToggle), new PropertyMetadata(null));

        public bool IsChecked
        {
            get => (bool)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

        public object? OnContent
        {
            get => GetValue(OnContentProperty);
            set => SetValue(OnContentProperty, value);
        }

        public object? OffContent
        {
            get => GetValue(OffContentProperty);
            set => SetValue(OffContentProperty, value);
        }

        public MatSlideToggle()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                if (FindName("PART_Toggle") is ToggleButton tb)
                {
                    tb.ClickMode = ClickMode.Press;
                    tb.Checked += (_, __2) => IsChecked = true;
                    tb.Unchecked += (_, __2) => IsChecked = false;
                }
            };
        }
    }
}
