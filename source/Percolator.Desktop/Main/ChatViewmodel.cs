using System.Collections.ObjectModel;

namespace Percolator.Desktop.Main;

public class ChatViewmodel
{
    public ObservableCollection<MessageViewmodel> Messages { get; } = new();
}

public class MessageViewmodel
{
    
}