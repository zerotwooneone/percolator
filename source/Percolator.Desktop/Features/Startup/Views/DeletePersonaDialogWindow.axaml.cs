// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using Avalonia.Controls;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.ViewModels;

namespace Percolator.Desktop.Features.Startup.Views;

[DialogFor(typeof(DeletePersonaDialogViewModel))]
public partial class DeletePersonaDialogWindow : Window
{
    public DeletePersonaDialogWindow()
    {
        InitializeComponent();
    }
}
