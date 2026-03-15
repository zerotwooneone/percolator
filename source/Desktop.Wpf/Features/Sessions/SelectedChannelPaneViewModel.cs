using System.Linq;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Mvvm;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public enum SelectedPaneState
{
    None,
    Pending,
    Active,
    Offline,
    Failed
}

public sealed class SelectedChannelPaneViewModel : ViewModelBase
{
    public BindableReactiveProperty<SelectedPaneState> State { get; }
    public BindableReactiveProperty<string?> DisplayName { get; }
    public BindableReactiveProperty<string?> BannerText { get; }
    public BindableReactiveProperty<bool> IsInputEnabled { get; }

    public BindableReactiveProperty<object?> ActiveContent { get; }

    private readonly SelectedChannelModel _selection;
    private readonly ISecureChannelsStore _store;
    private readonly ISessionScopeFactory _sessionFactory;
    private readonly SelectedSecureChannelStateCache _stateCache;

    private DisposableBag _bag;
    private DisposableBag _currentSelectionBag;

    public SelectedChannelPaneViewModel(
        SelectedChannelModel selection,
        ISecureChannelsStore store,
        ISessionScopeFactory sessionFactory,
        SelectedSecureChannelStateCache stateCache)
    {
        _selection = selection;
        _store = store;
        _sessionFactory = sessionFactory;
        _stateCache = stateCache;

        State = new BindableReactiveProperty<SelectedPaneState>(SelectedPaneState.None).AddTo(ref _bag);
        DisplayName = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        BannerText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        IsInputEnabled = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
        ActiveContent = new BindableReactiveProperty<object?>(null).AddTo(ref _bag);

        _selection.SelectedKey
            .DistinctUntilChanged()
            .Subscribe(key => OnSelectionChanged(key))
            .AddTo(ref _bag);

        // Initialize
        OnSelectionChanged(_selection.SelectedKey.Value);
    }

    private void OnSelectionChanged(SecureChannelKey? selectedKey)
    {
        _currentSelectionBag.Dispose();
        _currentSelectionBag = default;

        if (selectedKey is null)
        {
            State.Value = SelectedPaneState.None;
            DisplayName.Value = null;
            BannerText.Value = null;
            IsInputEnabled.Value = false;
            ActiveContent.Value = null;
            return;
        }

        var key = selectedKey.Value;
        var model = _store.Channels.FirstOrDefault(c => c.Key.Equals(key));

        if (model is null)
        {
            State.Value = SelectedPaneState.None;
            DisplayName.Value = null;
            BannerText.Value = null;
            IsInputEnabled.Value = false;
            ActiveContent.Value = null;
            return;
        }

        DisplayName.Value = model.DisplayName.CurrentValue;

        var state = MapState(model);
        State.Value = state;

        switch (state)
        {
            case SelectedPaneState.Pending:
                BannerText.Value = "Establishing…";
                IsInputEnabled.Value = false;
                ActiveContent.Value = null;
                break;

            case SelectedPaneState.Failed:
                BannerText.Value = "Connection failed.";
                IsInputEnabled.Value = false;
                ActiveContent.Value = null;
                break;

            case SelectedPaneState.Offline:
                BannerText.Value = "Offline";
                IsInputEnabled.Value = false;
                ActiveContent.Value = ResolveChatContent(model, key);
                break;

            case SelectedPaneState.Active:
                BannerText.Value = "E2E Encryption Established";
                IsInputEnabled.Value = true;
                ActiveContent.Value = ResolveChatContent(model, key);
                break;

            default:
                BannerText.Value = null;
                IsInputEnabled.Value = false;
                ActiveContent.Value = null;
                break;
        }
    }

    private object? ResolveChatContent(SecureChannelModel model, SecureChannelKey key)
    {
        if (key.Type != SecureChannelKeyType.SecureSession)
        {
            return null;
        }

        var sessionId = key.Value.ToString("N");
        var header = new SessionHeader
        {
            DisplayName = model.DisplayName.CurrentValue,
            Initials = model.Initials.CurrentValue,
            IsOnline = model.IsOnline.CurrentValue
        };

        var resolved = _sessionFactory.GetOrCreate(sessionId, header);

        var overlay = _stateCache.GetOrCreate(key);

        // Bridge draft text from per-channel overlay into per-session SessionContext draft.
        resolved.Context.Draft.Value = overlay.DraftMessageText.Value;

        resolved.Context.Draft
            .Subscribe(text =>
            {
                if (overlay.DraftMessageText.Value != text)
                    overlay.DraftMessageText.Value = text;
            })
            .AddTo(ref _currentSelectionBag);

        overlay.DraftMessageText
            .Subscribe(text =>
            {
                if (resolved.Context.Draft.Value != text)
                    resolved.Context.Draft.Value = text;
            })
            .AddTo(ref _currentSelectionBag);

        return resolved.ViewModel;
    }

    private static SelectedPaneState MapState(SecureChannelModel model)
    {
        return model.Kind.CurrentValue switch
        {
            SecureChannelKind.PendingInbound => SelectedPaneState.Pending,
            SecureChannelKind.PendingOutbound => SelectedPaneState.Pending,
            SecureChannelKind.Failed => SelectedPaneState.Failed,
            SecureChannelKind.Direct => model.IsOnline.CurrentValue ? SelectedPaneState.Active : SelectedPaneState.Offline,
            _ => SelectedPaneState.Active
        };
    }

    protected override void DisposeCore()
    {
        _currentSelectionBag.Dispose();
        _bag.Dispose();
    }
}
