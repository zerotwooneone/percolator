// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using Avalonia.Controls;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Chats.ViewModels;

namespace Percolator.Desktop.Features.Chats.Views;

[DialogFor(typeof(GroupCreationDialogViewModel))]
public partial class GroupCreationDialog : Window
{
    public GroupCreationDialog()
    {
        InitializeComponent();
    }
}
