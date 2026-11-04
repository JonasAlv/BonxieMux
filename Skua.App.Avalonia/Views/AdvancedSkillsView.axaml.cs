using Avalonia.Controls;
using Avalonia.Input;
using Skua.App.Avalonia.ViewModels.AdvancedSkills;

namespace Skua.App.Avalonia.Views;

public partial class AdvancedSkillsView : UserControl
{
    public AdvancedSkillsView() => InitializeComponent();

    private void OnSkillDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AdvancedSkillsViewModel vm && vm.SavedViewModel.SelectedSkill != null)
        {
            vm.SavedViewModel.EditSelectedCommand.Execute(null);
        }
    }
}
