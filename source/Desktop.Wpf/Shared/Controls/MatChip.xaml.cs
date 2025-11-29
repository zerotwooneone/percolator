using System.Windows;
using System.Windows.Controls;

namespace Desktop.Wpf.Shared.Controls
{
    public partial class MatChip : UserControl
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            nameof(Icon), typeof(string), typeof(MatChip), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MatChip), new PropertyMetadata(string.Empty));

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public MatChip()
        {
            InitializeComponent();
        }
    }
}
