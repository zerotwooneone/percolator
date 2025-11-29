using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Desktop.Wpf.Features.Sessions;

namespace Desktop.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, ISidebarHost
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void SetSidebar(object? view)
    {
        SidebarHost.Content = view;
    }
}