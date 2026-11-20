// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/View.spec.md before modifying this file.

using Avalonia.Controls;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Transports.ViewModels;

namespace Percolator.Desktop.Features.Transports.Views;

[DialogFor(typeof(PendingRequestsPanelViewModel))]
public partial class PendingRequestsPanelWindow : Window
{
    public PendingRequestsPanelWindow()
    {
        InitializeComponent();
    }
}
