using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace TeamLigma.Panel.Controls;

/// <summary>Focusable strip that accepts exactly one input: Ctrl + C.</summary>
public sealed partial class CtrlCCatcher : ContentControl
{
    readonly Border _frame;
    readonly TextBlock _text;
    bool _ctrl;

    public event EventHandler? CtrlC;

    public CtrlCCatcher()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var res = Application.Current.Resources;
        _text = new TextBlock { Text = "Click here, then press Ctrl + C to stop the Authserver", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)res["TlTextSecondary"], FontSize = 13 };
        _frame = new Border
        {
            MinHeight = 32,
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1, 1, 1, 2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x24, 0x23, 0x21)),
            BorderBrush = (Brush)res["TlCardBorder"],
            Child = _text,
        };
        Content = _frame;
        PointerPressed += (_, e) => { Focus(FocusState.Pointer); e.Handled = true; };
        GotFocus += (_, _) => { _frame.BorderBrush = (Brush)res["TlAccent"]; _text.Text = "Listening for Ctrl + C…"; };
        LostFocus += (_, _) => { _frame.BorderBrush = (Brush)res["TlCardBorder"]; _text.Text = "Click here, then press Ctrl + C to stop the Authserver"; _ctrl = false; };
    }

    static bool IsCtrl(VirtualKey k) => k is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl;

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (IsCtrl(e.Key)) { _ctrl = true; return; }
        var ctrl = _ctrl;
        ctrl |= ModifiersHaveCtrl(e);
        if (e.Key == VirtualKey.C && ctrl) CtrlC?.Invoke(this, EventArgs.Empty);
    }

    static System.Reflection.PropertyInfo? _modsProp;
    static bool ModifiersHaveCtrl(KeyRoutedEventArgs e)
    {
        try
        {
            _modsProp ??= typeof(KeyRoutedEventArgs).GetProperty("KeyboardModifiers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (_modsProp?.GetValue(e) is VirtualKeyModifiers m && m.HasFlag(VirtualKeyModifiers.Control)) return true;
        }
        catch { }
        try
        {
            return Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        }
        catch { return false; }
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (IsCtrl(e.Key)) _ctrl = false;
    }
}
