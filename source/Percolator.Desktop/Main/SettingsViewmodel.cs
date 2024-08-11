using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Client;
using R3;

namespace Percolator.Desktop.Main;

public class SettingsViewmodel
{
    private ISelfProvider _selfProvider;
    private readonly MainService _mainService;
    private readonly ILogger<SettingsViewmodel> _logger;
    public BindableReactiveProperty<bool> IsBroadcastListening { get; }

    public BindableReactiveProperty<bool> IsAnnouncing { get; }
    
    public BindableReactiveProperty<bool> AllowIntroductions { get; }
    
    public BindableReactiveProperty<bool> AutoReplyIntroductions { get; }
    
    public BindableReactiveProperty<bool> EditSelfNickname { get; } = new();
    public BindableReactiveProperty<string> SelfNickname { get; } = new();
    
    public BaseCommand SaveSelfNicknameCommand { get; }
    public BaseCommand EditSelfNicknameCommand { get; }

    public SettingsViewmodel(
        ISelfProvider selfProvider, 
        MainService mainService,
        ILogger<SettingsViewmodel> logger)
    {
        _selfProvider = selfProvider;
        _mainService = mainService;
        _logger = logger;
        var selfModel = _selfProvider.GetSelf();
        AllowIntroductions = selfModel.IntroduceListen
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.IntroduceListen.Value);
        AllowIntroductions.Subscribe(b =>
        {
            selfModel.IntroduceListen.Value = b;
            if (b)
            {
                _mainService.ListenForIntroductions();
            }
            else
            {
                _mainService.StopListeningForIntroductions();
            }
        });
        AutoReplyIntroductions = selfModel.AutoReplyIntroductions
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.AutoReplyIntroductions.Value);
        AutoReplyIntroductions.Subscribe(b => selfModel.AutoReplyIntroductions.Value = b);
        
        SaveSelfNicknameCommand = new BaseCommand(OnSaveSelfNickname);
        EditSelfNicknameCommand = new BaseCommand(OnEditSelfNickname);
        IsBroadcastListening = selfModel.BroadcastListen
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.BroadcastListen.Value);
        IsBroadcastListening.Subscribe(b =>
        {
            selfModel.BroadcastListen.Value = b;
            if (b)
            {
                _mainService.ListenForBroadcasts();
            }
            else
            {
                _mainService.StopListeningForBroadcasts();
            }
        });
        SelfNickname.Value = selfModel.PreferredNickname.Value;

        IsAnnouncing = selfModel.BroadcastSelf
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.BroadcastSelf.Value);
        IsAnnouncing.Subscribe(b =>
        {
            selfModel.BroadcastSelf.Value = b;
            if (b)
            {
                _mainService.Announce();
            }
            else
            {
                _mainService.StopAnnounce();
            }
        });
    }
    
    
    private void OnEditSelfNickname(object? _)
    {
        EditSelfNickname.Value = true;
    }

    private void OnSaveSelfNickname(object? _)
    {
        EditSelfNickname.Value = false;
        if(string.IsNullOrWhiteSpace(SelfNickname.Value))
        {
            SelfNickname.Value = _selfProvider.GetSelf().PreferredNickname.Value;
            return;
        }

        _selfProvider.GetSelf().PreferredNickname.Value = SelfNickname.Value;
    }
}