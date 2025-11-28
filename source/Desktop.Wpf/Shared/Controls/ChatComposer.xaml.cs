using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Wpf.Shared.Controls;

public partial class ChatComposer : UserControl
{
    public ChatComposer()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ChatComposer), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty CanSendProperty = DependencyProperty.Register(
        nameof(CanSend), typeof(bool), typeof(ChatComposer), new PropertyMetadata(false));

    public bool CanSend
    {
        get => (bool)GetValue(CanSendProperty);
        set => SetValue(CanSendProperty, value);
    }

    public static readonly DependencyProperty SendCommandProperty = DependencyProperty.Register(
        nameof(SendCommand), typeof(ICommand), typeof(ChatComposer));

    public ICommand? SendCommand
    {
        get => (ICommand?)GetValue(SendCommandProperty);
        set => SetValue(SendCommandProperty, value);
    }

    public static readonly DependencyProperty PlusCommandProperty = DependencyProperty.Register(
        nameof(PlusCommand), typeof(ICommand), typeof(ChatComposer));

    public ICommand? PlusCommand
    {
        get => (ICommand?)GetValue(PlusCommandProperty);
        set => SetValue(PlusCommandProperty, value);
    }

    public static readonly DependencyProperty EmojiCommandProperty = DependencyProperty.Register(
        nameof(EmojiCommand), typeof(ICommand), typeof(ChatComposer));

    public ICommand? EmojiCommand
    {
        get => (ICommand?)GetValue(EmojiCommandProperty);
        set => SetValue(EmojiCommandProperty, value);
    }

    public static readonly DependencyProperty ClipCommandProperty = DependencyProperty.Register(
        nameof(ClipCommand), typeof(ICommand), typeof(ChatComposer));

    public ICommand? ClipCommand
    {
        get => (ICommand?)GetValue(ClipCommandProperty);
        set => SetValue(ClipCommandProperty, value);
    }
}
