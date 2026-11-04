using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Utils;

namespace Skua.Shared.Avalonia.ViewModels;

public class AboutViewModel : BotControlViewModelBase
{
    private string _markDownContent = "Loading content...";

    public AboutViewModel(IProcessService processService) : base("About")
    {
        _processService = processService;
        _markDownContent = string.Empty;

        Task.Run(async () => await GetAboutContent());

        NavigateCommand = new RelayCommand<string>(NavigateToUrl);
    }

    private readonly IProcessService _processService;

    public string MarkdownDoc
    {
        get => _markDownContent; set => SetProperty(ref _markDownContent, value);
    }

    public IRelayCommand NavigateCommand { get; }

    private void NavigateToUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;

        try
        {
            if (url.StartsWith("./"))
            {
                _processService.OpenLink($"https://github.com/auqw/Skua/blob/master/{url.Substring(2)}");
            }
            else
            {
                _processService.OpenLink(url);
            }
        }
        catch
        {
            /* ignored */
        }
    }

    private async Task GetAboutContent()
    {
        MarkdownDoc = @"# BonxieMux
A Linux-native, tabbed multi-client derivative of Skua, engineered for advanced automation, stability, and streamlined multi-account management.

### Key Features
* **Headless Mode:** Reduced framerate and quality viewport for minimal CPU/GPU usage per instance.
* **Autonomous Scheduling:** Built-in queue to schedule scripts at specific dates and times.
* **Unified Tabbed UI:** Embedded X11 interface with tabbed, multi-client management and Army controls.
* **Engine Optimizations:** Powered by Mugetsu (custom Ruffle build) with background connection stability.

### Disclaimer
**Educational & Personal Use Only.** This project is provided ""as-is"" under the MIT License. Use of this software may violate the Terms of Service of the associated game. By using this tool, you acknowledge that you do so entirely at your own risk.";
        await Task.CompletedTask;
    }
}
