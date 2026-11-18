// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using Avalonia.Controls;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Transports.ViewModels;

namespace Percolator.Desktop.Features.Transports.Views;

[DialogFor(typeof(AddConnectionDialogViewModel))]
public partial class AddConnectionDialog : Window
{
    public AddConnectionDialog()
    {
        InitializeComponent();
    }
}
