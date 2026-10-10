using System.Windows;
using System.Windows.Controls;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Views;

public partial class ServicesView : UserControl
{
    public ServicesView()
    {
        InitializeComponent();
    }

    /// <summary>The passcode is read from the PasswordBox only here and handed straight to the configuration edit.</summary>
    private void ApplyIGate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ServicesViewModel vm) return;
        string pass = PasscodeBox.Password;
        PasscodeBox.Clear();
        vm.ApplyIGate(pass);
    }
}
