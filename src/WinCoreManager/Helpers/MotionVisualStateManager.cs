using System.Windows;

namespace WinCoreManager.Helpers;

public sealed class MotionVisualStateManager : VisualStateManager
{
    protected override bool GoToStateCore(FrameworkElement control, FrameworkElement stateGroupsRoot, string stateName,
        VisualStateGroup? group, VisualState? state, bool useTransitions) =>
        group is not null && state is not null && base.GoToStateCore(control, stateGroupsRoot, stateName, group, state,
            useTransitions && SystemParameters.ClientAreaAnimation);
}
