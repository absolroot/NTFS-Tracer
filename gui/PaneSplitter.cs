using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace JournalDesk;

public sealed class PaneSplitter : GridSplitter
{
    private IInputElement? previousFocus;
    private bool pointerDown;

    public PaneSplitter() => DragCompleted += RestoreFocusAfterDrag;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        previousFocus = Keyboard.FocusedElement == this ? null : Keyboard.FocusedElement;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        pointerDown = true;
        try
        {
            base.OnMouseLeftButtonDown(e);
        }
        finally
        {
            pointerDown = false;
        }
    }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        if (pointerDown)
            e.Handled = true;
    }

    private void RestoreFocusAfterDrag(object sender, DragCompletedEventArgs e)
    {
        var previous = previousFocus;
        previousFocus = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsKeyboardFocused)
                return;
            if (previous is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } &&
                Keyboard.Focus(previous) == previous)
                return;
            if (Window.GetWindow(this)?.Content is UIElement surface)
            {
                surface.SetCurrentValue(FocusableProperty, true);
                KeyboardNavigation.SetIsTabStop(surface, false);
                if (Keyboard.Focus(surface) == surface)
                    return;
            }
            Keyboard.ClearFocus();
        });
    }
}
