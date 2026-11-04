using System.Collections.Generic;

namespace Skua.Shared.Avalonia.ViewModels.Options;

public partial class CBOChoiceOptionItemViewModel : DisplayOptionItemViewModel<int>
{
    public CBOChoiceOptionItemViewModel(string optionTitle, string description, string tag, List<string> options)
        : base(optionTitle, description, tag)
    {
        Options = options;
        Value = 0;
    }

    public CBOChoiceOptionItemViewModel(string optionTitle, string description, string tag, List<string> options, int value)
        : base(optionTitle, description, tag)
    {
        Options = options;
        Value = value;
    }

    public List<string> Options { get; }
}
